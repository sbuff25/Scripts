using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

/// <summary>
/// Defines a box volume that spawns instanced foliage on surfaces below it.
/// Points are scattered randomly within the XZ footprint of the volume,
/// then raycasted downward to place instances on whatever geometry they hit.
/// Supports individual foliage types and multi-object clusters.
/// </summary>
public class FoliageSpawnerVolume : MonoBehaviour
{
    [Header("Volume")]
    [Tooltip("Size of the spawning volume in local space.")]
    public Vector3 volumeSize = new Vector3(50f, 30f, 50f);

    [Header("Raycasting")]
    [Tooltip("Layers that foliage can be placed on.")]
    public LayerMask surfaceLayers = ~0;

    [Tooltip("Max raycast distance downward from the top of the volume.")]
    public float maxRayDistance = 100f;

    [Header("Foliage")]
    public List<FoliageType> foliageTypes = new List<FoliageType>();

    [Header("Clusters")]
    public List<FoliageCluster> foliageClusters = new List<FoliageCluster>();

    [Header("Settings")]
    [Tooltip("Seed for reproducible results. Change to get a different layout.")]
    public int seed = 12345;

    [Tooltip("Parent transform for spawned instances. If null, a hidden sibling container is auto-created so selecting this spawner doesn't outline all foliage.")]
    public Transform spawnParent;

    [Tooltip("Mark spawned instances as static for batching. Can cause culling issues — disable if objects are invisible.")]
    public bool markStatic = false;

    [Header("Spline Masks")]
    [Tooltip("Spline-based include/exclude masks. Exclude masks block spawning inside the spline. Include masks restrict spawning to inside the spline. Applied to all types and clusters.")]
    public List<FoliageSplineMask> splineMasks = new List<FoliageSplineMask>();

    [Header("Performance")]
    [Tooltip("Use batch raycasting (Jobs system) for faster spawning. Produces slightly different results than sequential mode for the same seed.")]
    public bool batchRaycasts = true;

    // Auto-created sibling container — keeps spawned objects out of the spawner's
    // child hierarchy so selecting the spawner doesn't outline all foliage.
    [SerializeField, HideInInspector]
    private Transform _spawnContainer;

    // Internal struct to unify foliage types and clusters for priority sorting
    private struct SpawnItem
    {
        public enum Kind { FoliageType, Cluster }
        public Kind kind;
        public int index;
        public int spawnPriority;
        public int listOrder;
    }

    /// <summary>
    /// Destroys all previously spawned children, then spawns fresh instances
    /// for every foliage type and cluster in the lists. Items are processed in
    /// <see cref="FoliageType.spawnPriority"/> / <see cref="FoliageCluster.spawnPriority"/>
    /// order (highest first).
    /// </summary>
    public void Spawn()
    {
        Clear();

        Transform parent = GetSpawnTarget();
        Random.State prevState = Random.state;
        Random.InitState(seed);

        Vector3 half = volumeSize * 0.5f;
        float area = volumeSize.x * volumeSize.z;

        // Build unified spawn order from both types and clusters
        List<SpawnItem> items = new List<SpawnItem>();
        int listOrder = 0;
        for (int i = 0; i < foliageTypes.Count; i++)
        {
            items.Add(new SpawnItem
            {
                kind = SpawnItem.Kind.FoliageType,
                index = i,
                spawnPriority = foliageTypes[i].spawnPriority,
                listOrder = listOrder++
            });
        }
        for (int i = 0; i < foliageClusters.Count; i++)
        {
            items.Add(new SpawnItem
            {
                kind = SpawnItem.Kind.Cluster,
                index = i,
                spawnPriority = foliageClusters[i].spawnPriority,
                listOrder = listOrder++
            });
        }

        // Stable sort by spawnPriority descending (ties preserve list order)
        items.Sort((a, b) =>
        {
            int cmp = b.spawnPriority.CompareTo(a.spawnPriority);
            return cmp != 0 ? cmp : a.listOrder.CompareTo(b.listOrder);
        });

        // Global exclusion grid (shared across all types and clusters)
        float maxExclusion = 0f;
        foreach (FoliageType ft in foliageTypes)
            if (ft.exclusionRadius > maxExclusion) maxExclusion = ft.exclusionRadius;
        foreach (FoliageCluster fc in foliageClusters)
            if (fc.exclusionRadius > maxExclusion) maxExclusion = fc.exclusionRadius;
        ExclusionGrid globalExclusion = maxExclusion > 0f ? new ExclusionGrid(maxExclusion) : null;

        Vector3 rayDir = -transform.up;

        // Rebuild spline mask polygons and partition by mode
        List<FoliageSplineMask> globalIncludeMasks = null;
        List<FoliageSplineMask> globalExcludeMasks = null;
        RebuildAndPartitionMasks(splineMasks, ref globalIncludeMasks, ref globalExcludeMasks);

        foreach (SpawnItem item in items)
        {
            if (item.kind == SpawnItem.Kind.FoliageType)
            {
                FoliageType foliage = foliageTypes[item.index];
                if (!foliage.HasAnyPrefab()) continue;

                // Merge global masks with per-type masks
                List<FoliageSplineMask> effectiveInclude = globalIncludeMasks;
                List<FoliageSplineMask> effectiveExclude = globalExcludeMasks;
                if (foliage.splineMasks != null && foliage.splineMasks.Count > 0)
                {
                    effectiveInclude = MergeMaskLists(globalIncludeMasks, null);
                    effectiveExclude = MergeMaskLists(globalExcludeMasks, null);
                    RebuildAndPartitionMasks(foliage.splineMasks, ref effectiveInclude, ref effectiveExclude);
                }

                // Create category parent for this type
                Transform category = CreateCategory(foliage.GetCombineName(), parent);

                int count = Mathf.RoundToInt(area * foliage.density);
                SpatialHash2D spacingGrid = foliage.minSpacing > 0f
                    ? new SpatialHash2D(foliage.minSpacing)
                    : null;

                if (batchRaycasts)
                    SpawnTypeBatched(foliage, count, half, rayDir, category, spacingGrid, globalExclusion, effectiveInclude, effectiveExclude);
                else
                    SpawnTypeSequential(foliage, count, half, rayDir, category, spacingGrid, globalExclusion, effectiveInclude, effectiveExclude);
            }
            else
            {
                FoliageCluster cluster = foliageClusters[item.index];
                if (cluster.entries == null || cluster.entries.Length == 0) continue;

                // Merge global masks with per-cluster masks
                List<FoliageSplineMask> effectiveInclude = globalIncludeMasks;
                List<FoliageSplineMask> effectiveExclude = globalExcludeMasks;
                if (cluster.splineMasks != null && cluster.splineMasks.Count > 0)
                {
                    effectiveInclude = MergeMaskLists(globalIncludeMasks, null);
                    effectiveExclude = MergeMaskLists(globalExcludeMasks, null);
                    RebuildAndPartitionMasks(cluster.splineMasks, ref effectiveInclude, ref effectiveExclude);
                }

                // Create category parent for this cluster
                Transform category = CreateCategory(cluster.name, parent);

                int count = Mathf.RoundToInt(area * cluster.density);
                SpatialHash2D spacingGrid = cluster.minSpacing > 0f
                    ? new SpatialHash2D(cluster.minSpacing)
                    : null;

                if (batchRaycasts)
                    SpawnClusterBatched(cluster, count, half, rayDir, category, spacingGrid, globalExclusion, effectiveInclude, effectiveExclude);
                else
                    SpawnClusterSequential(cluster, count, half, rayDir, category, spacingGrid, globalExclusion, effectiveInclude, effectiveExclude);
            }
        }

        Random.state = prevState;
    }

    // ── Foliage Type Spawning ─────────────────────────────────────────────

    /// <summary>
    /// Sequential raycast path — one <see cref="Physics.Raycast"/> per candidate.
    /// </summary>
    private void SpawnTypeSequential(
        FoliageType foliage, int count, Vector3 half, Vector3 rayDir,
        Transform parent, SpatialHash2D spacingGrid, ExclusionGrid globalExclusion,
        List<FoliageSplineMask> includeMasks, List<FoliageSplineMask> excludeMasks)
    {
        List<GameObject> spawnedInstances = foliage.combineMeshes ? new List<GameObject>(count / 2) : null;

        for (int i = 0; i < count; i++)
        {
            float x = Random.Range(-half.x, half.x);
            float z = Random.Range(-half.z, half.z);

            Vector3 localPoint = new Vector3(x, half.y, z);
            Vector3 worldOrigin = transform.TransformPoint(localPoint);

            // Noise density check (before raycast for early-out performance)
            // See: https://docs.unity3d.com/ScriptReference/Mathf.PerlinNoise.html
            if (foliage.noiseScale > 0f)
            {
                float nx = (worldOrigin.x + 10000f) * foliage.noiseScale + foliage.noiseOffset.x;
                float nz = (worldOrigin.z + 10000f) * foliage.noiseScale + foliage.noiseOffset.y;
                if (Mathf.PerlinNoise(nx, nz) < foliage.noiseThreshold)
                    continue;
            }

            // Spline mask check (pre-raycast, XZ-only)
            if (!PassesSplineMasks(worldOrigin.x, worldOrigin.z, includeMasks, excludeMasks))
                continue;

            if (!Physics.Raycast(worldOrigin, rayDir, out RaycastHit hit, maxRayDistance, surfaceLayers))
                continue;

            GameObject instance = ProcessHitAndInstantiate(
                foliage, hit, half, parent, spacingGrid, globalExclusion);
            if (instance != null)
                spawnedInstances?.Add(instance);
        }

        if (foliage.combineMeshes && spawnedInstances != null && spawnedInstances.Count > 0)
            CombineInstances(spawnedInstances, foliage.GetCombineName(), parent);
    }

    /// <summary>
    /// Batched raycast path via <see cref="RaycastCommand.ScheduleBatch"/>.
    /// </summary>
    private void SpawnTypeBatched(
        FoliageType foliage, int count, Vector3 half, Vector3 rayDir,
        Transform parent, SpatialHash2D spacingGrid, ExclusionGrid globalExclusion,
        List<FoliageSplineMask> includeMasks, List<FoliageSplineMask> excludeMasks)
    {
        // Phase 1: Generate candidates and noise-filter
        List<Vector3> origins = new List<Vector3>(count);

        for (int i = 0; i < count; i++)
        {
            float x = Random.Range(-half.x, half.x);
            float z = Random.Range(-half.z, half.z);

            Vector3 localPoint = new Vector3(x, half.y, z);
            Vector3 worldOrigin = transform.TransformPoint(localPoint);

            if (foliage.noiseScale > 0f)
            {
                float nx = (worldOrigin.x + 10000f) * foliage.noiseScale + foliage.noiseOffset.x;
                float nz = (worldOrigin.z + 10000f) * foliage.noiseScale + foliage.noiseOffset.y;
                if (Mathf.PerlinNoise(nx, nz) < foliage.noiseThreshold)
                    continue;
            }

            // Spline mask check (pre-raycast, XZ-only)
            if (!PassesSplineMasks(worldOrigin.x, worldOrigin.z, includeMasks, excludeMasks))
                continue;

            origins.Add(worldOrigin);
        }

        if (origins.Count == 0) return;

        // Phase 2: Batch raycast
        // See: https://docs.unity3d.com/ScriptReference/QueryParameters-ctor.html
        var queryParams = new QueryParameters(surfaceLayers, false, QueryTriggerInteraction.UseGlobal, false);
        int rayCount = origins.Count;

        var commands = new NativeArray<RaycastCommand>(rayCount, Allocator.TempJob);
        var results = new NativeArray<RaycastHit>(rayCount, Allocator.TempJob);

        for (int i = 0; i < rayCount; i++)
            commands[i] = new RaycastCommand(origins[i], rayDir, queryParams, maxRayDistance);

        RaycastCommand.ScheduleBatch(commands, results, 32, 1, default).Complete();

        // Phase 3: Process results sequentially
        List<GameObject> spawnedInstances = foliage.combineMeshes ? new List<GameObject>(rayCount / 2) : null;

        for (int i = 0; i < rayCount; i++)
        {
            RaycastHit hit = results[i];
            if (hit.collider == null) continue;

            GameObject instance = ProcessHitAndInstantiate(
                foliage, hit, half, parent, spacingGrid, globalExclusion);
            if (instance != null)
                spawnedInstances?.Add(instance);
        }

        commands.Dispose();
        results.Dispose();

        if (foliage.combineMeshes && spawnedInstances != null && spawnedInstances.Count > 0)
            CombineInstances(spawnedInstances, foliage.GetCombineName(), parent);
    }

    /// <summary>
    /// Shared FoliageType hit processing: slope/bounds/exclusion/spacing filters,
    /// rotation/scale, prefab variant selection, and instantiation.
    /// </summary>
    private GameObject ProcessHitAndInstantiate(
        FoliageType foliage, RaycastHit hit, Vector3 half,
        Transform parent, SpatialHash2D spacingGrid, ExclusionGrid globalExclusion)
    {
        float slopeAngle = Vector3.Angle(hit.normal, Vector3.up);
        if (slopeAngle < foliage.minSlopeAngle || slopeAngle > foliage.maxSlopeAngle)
            return null;

        Vector3 position = hit.point + hit.normal * foliage.surfaceOffset;

        Vector3 localPos = transform.InverseTransformPoint(position);
        if (Mathf.Abs(localPos.x) > half.x ||
            Mathf.Abs(localPos.y) > half.y ||
            Mathf.Abs(localPos.z) > half.z)
            return null;

        if (globalExclusion != null && globalExclusion.IsExcluded(position))
            return null;

        if (spacingGrid != null && spacingGrid.HasNeighborWithin(position, foliage.minSpacing))
            return null;

        Quaternion normalAlign = Quaternion.FromToRotation(Vector3.up, hit.normal);
        Quaternion rotation = Quaternion.Slerp(Quaternion.identity, normalAlign, foliage.alignToNormal);

        float yaw   = foliage.randomYaw   > 0f ? Random.Range(0f, foliage.randomYaw)     : 0f;
        float pitch = foliage.randomPitch  > 0f ? Random.Range(-foliage.randomPitch, foliage.randomPitch) : 0f;
        float roll  = foliage.randomRoll   > 0f ? Random.Range(-foliage.randomRoll, foliage.randomRoll)   : 0f;
        rotation *= Quaternion.Euler(pitch, yaw, roll);

        float minS = foliage.minScale < 0.01f ? 1f : foliage.minScale;
        float maxS = foliage.maxScale < 0.01f ? 1f : foliage.maxScale;
        if (maxS < minS) maxS = minS;
        float uniformScale = Random.Range(minS, maxS);
        Vector3 finalScale = Vector3.one * uniformScale;

        GameObject chosenPrefab = foliage.GetRandomPrefab();
        if (chosenPrefab == null) return null;

        GameObject instance = InstantiatePrefab(chosenPrefab, position, rotation, finalScale, parent);
        if (instance == null) return null;

        spacingGrid?.Insert(position);
        if (foliage.exclusionRadius > 0f)
            globalExclusion?.Insert(position, foliage.exclusionRadius);

        return instance;
    }

    // ── Cluster Spawning ──────────────────────────────────────────────────

    /// <summary>
    /// Sequential cluster center raycast path.
    /// </summary>
    private void SpawnClusterSequential(
        FoliageCluster cluster, int count, Vector3 half, Vector3 rayDir,
        Transform parent, SpatialHash2D spacingGrid, ExclusionGrid globalExclusion,
        List<FoliageSplineMask> includeMasks, List<FoliageSplineMask> excludeMasks)
    {
        List<GameObject> allClusterInstances = cluster.combineMeshes ? new List<GameObject>() : null;

        for (int i = 0; i < count; i++)
        {
            float x = Random.Range(-half.x, half.x);
            float z = Random.Range(-half.z, half.z);

            Vector3 localPoint = new Vector3(x, half.y, z);
            Vector3 worldOrigin = transform.TransformPoint(localPoint);

            // Noise density check on cluster center
            if (cluster.noiseScale > 0f)
            {
                float nx = (worldOrigin.x + 10000f) * cluster.noiseScale + cluster.noiseOffset.x;
                float nz = (worldOrigin.z + 10000f) * cluster.noiseScale + cluster.noiseOffset.y;
                if (Mathf.PerlinNoise(nx, nz) < cluster.noiseThreshold)
                    continue;
            }

            // Spline mask check on cluster center (pre-raycast, XZ-only)
            if (!PassesSplineMasks(worldOrigin.x, worldOrigin.z, includeMasks, excludeMasks))
                continue;

            if (!Physics.Raycast(worldOrigin, rayDir, out RaycastHit hit, maxRayDistance, surfaceLayers))
                continue;

            ProcessClusterCenter(cluster, hit, half, rayDir, parent, spacingGrid, globalExclusion, allClusterInstances, includeMasks, excludeMasks);
        }

        if (cluster.combineMeshes && allClusterInstances != null && allClusterInstances.Count > 0)
            CombineInstances(allClusterInstances, cluster.name, parent);
    }

    /// <summary>
    /// Batched cluster center raycast path — center raycasts are batched,
    /// sub-instance raycasts are sequential (small count per cluster).
    /// </summary>
    private void SpawnClusterBatched(
        FoliageCluster cluster, int count, Vector3 half, Vector3 rayDir,
        Transform parent, SpatialHash2D spacingGrid, ExclusionGrid globalExclusion,
        List<FoliageSplineMask> includeMasks, List<FoliageSplineMask> excludeMasks)
    {
        // Phase 1: Generate center candidates and noise-filter
        List<Vector3> origins = new List<Vector3>(count);

        for (int i = 0; i < count; i++)
        {
            float x = Random.Range(-half.x, half.x);
            float z = Random.Range(-half.z, half.z);

            Vector3 localPoint = new Vector3(x, half.y, z);
            Vector3 worldOrigin = transform.TransformPoint(localPoint);

            if (cluster.noiseScale > 0f)
            {
                float nx = (worldOrigin.x + 10000f) * cluster.noiseScale + cluster.noiseOffset.x;
                float nz = (worldOrigin.z + 10000f) * cluster.noiseScale + cluster.noiseOffset.y;
                if (Mathf.PerlinNoise(nx, nz) < cluster.noiseThreshold)
                    continue;
            }

            // Spline mask check on cluster center (pre-raycast, XZ-only)
            if (!PassesSplineMasks(worldOrigin.x, worldOrigin.z, includeMasks, excludeMasks))
                continue;

            origins.Add(worldOrigin);
        }

        if (origins.Count == 0) return;

        // Phase 2: Batch raycast centers
        var queryParams = new QueryParameters(surfaceLayers, false, QueryTriggerInteraction.UseGlobal, false);
        int rayCount = origins.Count;

        var commands = new NativeArray<RaycastCommand>(rayCount, Allocator.TempJob);
        var results = new NativeArray<RaycastHit>(rayCount, Allocator.TempJob);

        for (int i = 0; i < rayCount; i++)
            commands[i] = new RaycastCommand(origins[i], rayDir, queryParams, maxRayDistance);

        RaycastCommand.ScheduleBatch(commands, results, 32, 1, default).Complete();

        // Phase 3: Process center hits and spawn sub-instances
        List<GameObject> allClusterInstances = cluster.combineMeshes ? new List<GameObject>() : null;

        for (int i = 0; i < rayCount; i++)
        {
            RaycastHit hit = results[i];
            if (hit.collider == null) continue;

            ProcessClusterCenter(cluster, hit, half, -transform.up, parent, spacingGrid, globalExclusion, allClusterInstances, includeMasks, excludeMasks);
        }

        commands.Dispose();
        results.Dispose();

        if (cluster.combineMeshes && allClusterInstances != null && allClusterInstances.Count > 0)
            CombineInstances(allClusterInstances, cluster.name, parent);
    }

    /// <summary>
    /// Validates a cluster center hit, then spawns all sub-instances around it.
    /// </summary>
    private void ProcessClusterCenter(
        FoliageCluster cluster, RaycastHit centerHit, Vector3 half, Vector3 rayDir,
        Transform parent, SpatialHash2D spacingGrid, ExclusionGrid globalExclusion,
        List<GameObject> allClusterInstances,
        List<FoliageSplineMask> includeMasks, List<FoliageSplineMask> excludeMasks)
    {
        // Slope filter on center
        float slopeAngle = Vector3.Angle(centerHit.normal, Vector3.up);
        if (slopeAngle < cluster.minSlopeAngle || slopeAngle > cluster.maxSlopeAngle)
            return;

        Vector3 centerPos = centerHit.point;

        // Bounds check on center
        Vector3 localCenter = transform.InverseTransformPoint(centerPos);
        if (Mathf.Abs(localCenter.x) > half.x ||
            Mathf.Abs(localCenter.y) > half.y ||
            Mathf.Abs(localCenter.z) > half.z)
            return;

        // Global exclusion check on center
        if (globalExclusion != null && globalExclusion.IsExcluded(centerPos))
            return;

        // Cluster-type spacing check
        if (spacingGrid != null && spacingGrid.HasNeighborWithin(centerPos, cluster.minSpacing))
            return;

        // Center accepted — spawn sub-instances
        SpawnClusterInstances(cluster, centerPos, half, rayDir, parent, allClusterInstances, includeMasks, excludeMasks);

        // Register center in grids
        spacingGrid?.Insert(centerPos);
        if (cluster.exclusionRadius > 0f)
            globalExclusion?.Insert(centerPos, cluster.exclusionRadius);
    }

    /// <summary>
    /// Spawns all sub-instances for one accepted cluster center point.
    /// Each entry gets a random count of instances placed within its radial band.
    /// A shared intra-cluster spatial hash prevents clipping between sub-instances.
    /// </summary>
    private void SpawnClusterInstances(
        FoliageCluster cluster, Vector3 centerPos, Vector3 half, Vector3 rayDir,
        Transform parent, List<GameObject> allClusterInstances,
        List<FoliageSplineMask> includeMasks, List<FoliageSplineMask> excludeMasks)
    {
        // Shared intra-cluster spacing grid
        float maxEntrySpacing = 0f;
        foreach (ClusterEntry entry in cluster.entries)
            if (entry.minSpacing > maxEntrySpacing) maxEntrySpacing = entry.minSpacing;
        SpatialHash2D intraClusterGrid = maxEntrySpacing > 0f ? new SpatialHash2D(maxEntrySpacing) : null;

        Vector3 localCenter = transform.InverseTransformPoint(centerPos);

        foreach (ClusterEntry entry in cluster.entries)
        {
            if (!entry.HasAnyPrefab()) continue;

            int spawnCount = Random.Range(entry.countMin, entry.countMax + 1);
            int maxAttempts = spawnCount * 4;
            int placed = 0;

            for (int attempt = 0; attempt < maxAttempts && placed < spawnCount; attempt++)
            {
                // Random offset within the entry's radial band
                float angle = Random.Range(0f, Mathf.PI * 2f);
                float radius = Random.Range(entry.minRadius, Mathf.Max(entry.minRadius, entry.maxRadius));
                float offsetX = Mathf.Cos(angle) * radius;
                float offsetZ = Mathf.Sin(angle) * radius;

                // Cast from the top of the volume at the offset position
                Vector3 localOffset = localCenter + new Vector3(offsetX, 0f, offsetZ);
                localOffset.y = half.y;
                Vector3 worldRayOrigin = transform.TransformPoint(localOffset);

                // Spline mask check on sub-instance (pre-raycast, XZ-only)
                if (!PassesSplineMasks(worldRayOrigin.x, worldRayOrigin.z, includeMasks, excludeMasks))
                    continue;

                if (!Physics.Raycast(worldRayOrigin, rayDir, out RaycastHit hit, maxRayDistance, surfaceLayers))
                    continue;

                Vector3 instancePos = hit.point + hit.normal * entry.surfaceOffset;

                // Bounds check
                Vector3 localInstancePos = transform.InverseTransformPoint(instancePos);
                if (Mathf.Abs(localInstancePos.x) > half.x ||
                    Mathf.Abs(localInstancePos.y) > half.y ||
                    Mathf.Abs(localInstancePos.z) > half.z)
                    continue;

                // Intra-cluster spacing check
                if (intraClusterGrid != null && intraClusterGrid.HasNeighborWithin(instancePos, entry.minSpacing))
                    continue;

                // Build rotation
                Quaternion normalAlign = Quaternion.FromToRotation(Vector3.up, hit.normal);
                Quaternion rotation = Quaternion.Slerp(Quaternion.identity, normalAlign, entry.alignToNormal);

                float yaw   = entry.randomYaw   > 0f ? Random.Range(0f, entry.randomYaw)     : 0f;
                float pitch = entry.randomPitch  > 0f ? Random.Range(-entry.randomPitch, entry.randomPitch) : 0f;
                float roll  = entry.randomRoll   > 0f ? Random.Range(-entry.randomRoll, entry.randomRoll)   : 0f;
                rotation *= Quaternion.Euler(pitch, yaw, roll);

                // Build scale
                float minS = entry.minScale < 0.01f ? 1f : entry.minScale;
                float maxS = entry.maxScale < 0.01f ? 1f : entry.maxScale;
                if (maxS < minS) maxS = minS;
                float uniformScale = Random.Range(minS, maxS);
                Vector3 finalScale = Vector3.one * uniformScale;

                // Select prefab variant
                GameObject chosenPrefab = entry.GetRandomPrefab();
                if (chosenPrefab == null) continue;

                GameObject instance = InstantiatePrefab(chosenPrefab, instancePos, rotation, finalScale, parent);
                if (instance == null) continue;

                intraClusterGrid?.Insert(instancePos);
                allClusterInstances?.Add(instance);
                placed++;
            }
        }
    }

    // ── Shared Utilities ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a category GameObject under the given parent to group spawned instances.
    /// </summary>
    private Transform CreateCategory(string name, Transform parent)
    {
        GameObject category = new GameObject(name);
        category.transform.SetParent(parent, worldPositionStays: false);
        return category.transform;
    }

    /// <summary>
    /// Returns the transform to parent spawned instances under. If
    /// <see cref="spawnParent"/> is set, uses that. Otherwise auto-creates
    /// a hidden sibling container so spawned objects are NOT children of this
    /// transform (prevents selection outline on all instances when the spawner
    /// is selected in the hierarchy).
    /// </summary>
    private Transform GetSpawnTarget()
    {
        if (spawnParent != null)
            return spawnParent;

        if (_spawnContainer != null)
            return _spawnContainer;

        // Create sibling container (hidden from hierarchy so user can't click it)
        GameObject container = new GameObject(gameObject.name + "_Foliage");
        container.hideFlags = HideFlags.HideInHierarchy;
        if (transform.parent != null)
            container.transform.SetParent(transform.parent, worldPositionStays: false);
        container.transform.SetPositionAndRotation(transform.position, transform.rotation);

        _spawnContainer = container.transform;
        return _spawnContainer;
    }

    /// <summary>
    /// Instantiates a prefab at the given transform. Uses
    /// <see cref="UnityEditor.PrefabUtility.InstantiatePrefab"/> in editor,
    /// <see cref="Object.Instantiate"/> at runtime.
    /// </summary>
    private GameObject InstantiatePrefab(
        GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale, Transform parent)
    {
#if UNITY_EDITOR
        // See: https://docs.unity3d.com/ScriptReference/PrefabUtility.InstantiatePrefab.html
        GameObject instance = UnityEditor.PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        if (instance == null) return null;
        instance.transform.SetParent(parent, worldPositionStays: true);
        instance.transform.SetPositionAndRotation(position, rotation);
        instance.transform.localScale = scale;
#else
        // See: https://docs.unity3d.com/ScriptReference/Object.Instantiate.html
        GameObject instance = Instantiate(prefab, position, rotation, parent);
        instance.transform.localScale = scale;
#endif
        if (markStatic)
            instance.isStatic = true;

        return instance;
    }

    /// <summary>
    /// Combines spawned instances by material into fewer GameObjects to reduce
    /// draw calls. Each unique material produces one combined mesh object.
    /// </summary>
    /// See: https://docs.unity3d.com/ScriptReference/Mesh.CombineMeshes.html
    private void CombineInstances(List<GameObject> instances, string combineName, Transform parent)
    {
        var groups = new Dictionary<Material, List<CombineInstance>>();
        int layer = instances[0].layer;

        foreach (GameObject go in instances)
        {
            MeshFilter mf = go.GetComponent<MeshFilter>();
            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mf == null || mf.sharedMesh == null || mr == null) continue;

            Material mat = mr.sharedMaterial;
            if (!groups.TryGetValue(mat, out List<CombineInstance> list))
            {
                list = new List<CombineInstance>();
                groups[mat] = list;
            }

            Mesh mesh = mf.sharedMesh;
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                list.Add(new CombineInstance
                {
                    mesh = mesh,
                    subMeshIndex = sub,
                    transform = go.transform.localToWorldMatrix
                });
            }
        }

        int groupIndex = 0;
        foreach (var kvp in groups)
        {
            Mesh combinedMesh = new Mesh();
            combinedMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            combinedMesh.CombineMeshes(kvp.Value.ToArray(), mergeSubMeshes: true, useMatrices: true);
            combinedMesh.RecalculateBounds();

            GameObject combined = new GameObject($"{combineName}_Combined_{groupIndex}");
            combined.transform.SetParent(parent, worldPositionStays: false);
            combined.layer = layer;

            MeshFilter cmf = combined.AddComponent<MeshFilter>();
            cmf.sharedMesh = combinedMesh;

            MeshRenderer cmr = combined.AddComponent<MeshRenderer>();
            cmr.sharedMaterial = kvp.Key;

            if (markStatic)
                combined.isStatic = true;

            groupIndex++;
        }

        foreach (GameObject go in instances)
        {
#if UNITY_EDITOR
            DestroyImmediate(go);
#else
            Destroy(go);
#endif
        }
    }

    /// <summary>
    /// Destroys all child objects under the spawn parent.
    /// </summary>
    public void Clear()
    {
        Transform parent = GetSpawnTarget();

#if UNITY_EDITOR
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            DestroyImmediate(parent.GetChild(i).gameObject);
        }
#else
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Destroy(parent.GetChild(i).gameObject);
        }
#endif
    }

    private void OnDestroy()
    {
        if (_spawnContainer != null)
        {
#if UNITY_EDITOR
            DestroyImmediate(_spawnContainer.gameObject);
#else
            Destroy(_spawnContainer.gameObject);
#endif
        }
    }

    // ── Spline Mask Helpers ─────────────────────────────────────────────

    /// <summary>
    /// Rebuilds polygons for all valid masks in the list and partitions them
    /// into include/exclude lists. Appends to existing lists if non-null.
    /// </summary>
    private static void RebuildAndPartitionMasks(
        List<FoliageSplineMask> masks,
        ref List<FoliageSplineMask> includeMasks,
        ref List<FoliageSplineMask> excludeMasks)
    {
        if (masks == null) return;

        for (int i = 0; i < masks.Count; i++)
        {
            FoliageSplineMask mask = masks[i];
            if (mask == null || !mask.gameObject.activeInHierarchy) continue;
            if (!mask.RebuildPolygon()) continue;

            if (mask.mode == FoliageSplineMask.MaskMode.Include)
            {
                if (includeMasks == null) includeMasks = new List<FoliageSplineMask>();
                includeMasks.Add(mask);
            }
            else
            {
                if (excludeMasks == null) excludeMasks = new List<FoliageSplineMask>();
                excludeMasks.Add(mask);
            }
        }
    }

    /// <summary>
    /// Creates a new list containing all items from both input lists.
    /// Returns null if both inputs are null or empty.
    /// </summary>
    private static List<FoliageSplineMask> MergeMaskLists(
        List<FoliageSplineMask> a, List<FoliageSplineMask> b)
    {
        int countA = a != null ? a.Count : 0;
        int countB = b != null ? b.Count : 0;

        if (countA == 0 && countB == 0) return null;
        if (countA == 0) return new List<FoliageSplineMask>(b);
        if (countB == 0) return new List<FoliageSplineMask>(a);

        var merged = new List<FoliageSplineMask>(countA + countB);
        merged.AddRange(a);
        merged.AddRange(b);
        return merged;
    }

    /// <summary>
    /// Tests a world-space XZ point against all spline masks.
    /// Returns true if the point is allowed (passes all mask checks).
    /// Logic: if ANY exclude mask contains the point → reject.
    /// If include masks exist but NONE contain the point → reject.
    /// </summary>
    private static bool PassesSplineMasks(
        float worldX, float worldZ,
        List<FoliageSplineMask> includeMasks,
        List<FoliageSplineMask> excludeMasks)
    {
        // Check exclude masks first (any match = reject)
        if (excludeMasks != null)
        {
            for (int i = 0; i < excludeMasks.Count; i++)
            {
                if (excludeMasks[i].ContainsPointXZ(worldX, worldZ))
                    return false;
            }
        }

        // Check include masks (must be inside at least one, if any exist)
        if (includeMasks != null && includeMasks.Count > 0)
        {
            for (int i = 0; i < includeMasks.Count; i++)
            {
                if (includeMasks[i].ContainsPointXZ(worldX, worldZ))
                    return true;
            }
            return false; // Include masks exist but point is in none of them
        }

        return true; // No masks, or only exclude masks that didn't trigger
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.25f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawCube(Vector3.zero, volumeSize);

        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.7f);
        Gizmos.DrawWireCube(Vector3.zero, volumeSize);
    }
}
