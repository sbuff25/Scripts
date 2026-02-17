using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

/// <summary>
/// Defines a box volume that spawns instanced foliage on surfaces below it.
/// Points are scattered randomly within the XZ footprint of the volume,
/// then raycasted downward to place instances on whatever geometry they hit.
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

    [Header("Settings")]
    [Tooltip("Seed for reproducible results. Change to get a different layout.")]
    public int seed = 12345;

    [Tooltip("Parent transform for spawned instances. If null, this transform is used.")]
    public Transform spawnParent;

    [Tooltip("Mark spawned instances as static for batching. Can cause culling issues — disable if objects are invisible.")]
    public bool markStatic = false;

    [Header("Performance")]
    [Tooltip("Use batch raycasting (Jobs system) for faster spawning. Produces slightly different results than sequential mode for the same seed.")]
    public bool batchRaycasts = true;

    /// <summary>
    /// Destroys all previously spawned children, then spawns fresh instances
    /// for every foliage type in the list. Types are processed in
    /// <see cref="FoliageType.spawnPriority"/> order (highest first).
    /// Noise modulation, minimum spacing, and cross-type exclusion zones
    /// are applied when configured on individual foliage types.
    /// </summary>
    public void Spawn()
    {
        Clear();

        Transform parent = spawnParent != null ? spawnParent : transform;
        Random.State prevState = Random.state;
        Random.InitState(seed);

        Vector3 half = volumeSize * 0.5f;
        float area = volumeSize.x * volumeSize.z;
        int typeCount = foliageTypes.Count;

        // Stable sort by spawnPriority descending (ties preserve list order)
        int[] order = new int[typeCount];
        for (int i = 0; i < typeCount; i++) order[i] = i;
        System.Array.Sort(order, (a, b) =>
        {
            int cmp = foliageTypes[b].spawnPriority.CompareTo(foliageTypes[a].spawnPriority);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });

        // Global exclusion grid (shared across all types)
        float maxExclusion = 0f;
        foreach (FoliageType ft in foliageTypes)
            if (ft.exclusionRadius > maxExclusion) maxExclusion = ft.exclusionRadius;
        ExclusionGrid globalExclusion = maxExclusion > 0f ? new ExclusionGrid(maxExclusion) : null;

        Vector3 rayDir = -transform.up;

        foreach (int idx in order)
        {
            FoliageType foliage = foliageTypes[idx];
            if (foliage.prefab == null) continue;

            int count = Mathf.RoundToInt(area * foliage.density);

            // Per-type spacing grid
            SpatialHash2D spacingGrid = foliage.minSpacing > 0f
                ? new SpatialHash2D(foliage.minSpacing)
                : null;

            if (batchRaycasts)
                SpawnTypeBatched(foliage, count, half, rayDir, parent, spacingGrid, globalExclusion);
            else
                SpawnTypeSequential(foliage, count, half, rayDir, parent, spacingGrid, globalExclusion);
        }

        Random.state = prevState;
    }

    /// <summary>
    /// Sequential raycast path — one <see cref="Physics.Raycast"/> per candidate.
    /// Preserves exact Random.state sequence from prior versions.
    /// </summary>
    private void SpawnTypeSequential(
        FoliageType foliage, int count, Vector3 half, Vector3 rayDir,
        Transform parent, SpatialHash2D spacingGrid, ExclusionGrid globalExclusion)
    {
        List<GameObject> spawnedInstances = foliage.combineMeshes ? new List<GameObject>(count / 2) : null;

        for (int i = 0; i < count; i++)
        {
            // Random point within the XZ footprint
            float x = Random.Range(-half.x, half.x);
            float z = Random.Range(-half.z, half.z);

            // Transform to world space (respects volume rotation)
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

            if (!Physics.Raycast(worldOrigin, rayDir, out RaycastHit hit, maxRayDistance, surfaceLayers))
                continue;

            GameObject instance = ProcessHitAndInstantiate(
                foliage, hit, half, parent, spacingGrid, globalExclusion);
            if (instance != null)
                spawnedInstances?.Add(instance);
        }

        if (foliage.combineMeshes && spawnedInstances != null && spawnedInstances.Count > 0)
            CombineInstances(spawnedInstances, foliage.prefab.name, parent);
    }

    /// <summary>
    /// Batched raycast path — generates all candidates, noise-filters, then
    /// batch-raycasts survivors via <see cref="RaycastCommand.ScheduleBatch"/>.
    /// </summary>
    /// See: https://docs.unity3d.com/ScriptReference/RaycastCommand.ScheduleBatch.html
    private void SpawnTypeBatched(
        FoliageType foliage, int count, Vector3 half, Vector3 rayDir,
        Transform parent, SpatialHash2D spacingGrid, ExclusionGrid globalExclusion)
    {
        // Phase 1: Generate candidates and noise-filter
        List<Vector3> origins = new List<Vector3>(count);

        for (int i = 0; i < count; i++)
        {
            float x = Random.Range(-half.x, half.x);
            float z = Random.Range(-half.z, half.z);

            Vector3 localPoint = new Vector3(x, half.y, z);
            Vector3 worldOrigin = transform.TransformPoint(localPoint);

            // Noise density check
            if (foliage.noiseScale > 0f)
            {
                float nx = (worldOrigin.x + 10000f) * foliage.noiseScale + foliage.noiseOffset.x;
                float nz = (worldOrigin.z + 10000f) * foliage.noiseScale + foliage.noiseOffset.y;
                if (Mathf.PerlinNoise(nx, nz) < foliage.noiseThreshold)
                    continue;
            }

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

        // Phase 3: Process results sequentially (spacing/exclusion are order-dependent)
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
            CombineInstances(spawnedInstances, foliage.prefab.name, parent);
    }

    /// <summary>
    /// Shared logic: given a raycast hit, apply slope/bounds/exclusion/spacing
    /// filters, then build rotation/scale and instantiate. Returns the placed
    /// instance or null if the candidate was rejected.
    /// </summary>
    private GameObject ProcessHitAndInstantiate(
        FoliageType foliage, RaycastHit hit, Vector3 half,
        Transform parent, SpatialHash2D spacingGrid, ExclusionGrid globalExclusion)
    {
        // Slope filter — angle between surface normal and world up
        // See: https://docs.unity3d.com/ScriptReference/Vector3.Angle.html
        float slopeAngle = Vector3.Angle(hit.normal, Vector3.up);
        if (slopeAngle < foliage.minSlopeAngle || slopeAngle > foliage.maxSlopeAngle)
            return null;

        // Final placement (surface offset can shift along the normal)
        Vector3 position = hit.point + hit.normal * foliage.surfaceOffset;

        // Verify position is inside the full volume (XYZ)
        Vector3 localPos = transform.InverseTransformPoint(position);
        if (Mathf.Abs(localPos.x) > half.x ||
            Mathf.Abs(localPos.y) > half.y ||
            Mathf.Abs(localPos.z) > half.z)
            return null;

        // Cross-type exclusion check
        if (globalExclusion != null && globalExclusion.IsExcluded(position))
            return null;

        // Same-type minimum spacing check
        if (spacingGrid != null && spacingGrid.HasNeighborWithin(position, foliage.minSpacing))
            return null;

        // Build rotation
        // See: https://docs.unity3d.com/ScriptReference/Quaternion.Slerp.html
        Quaternion normalAlign = Quaternion.FromToRotation(Vector3.up, hit.normal);
        Quaternion rotation = Quaternion.Slerp(Quaternion.identity, normalAlign, foliage.alignToNormal);

        // Apply random yaw/pitch/roll
        // See: https://docs.unity3d.com/ScriptReference/Quaternion.Euler.html
        float yaw   = foliage.randomYaw   > 0f ? Random.Range(0f, foliage.randomYaw)     : 0f;
        float pitch = foliage.randomPitch  > 0f ? Random.Range(-foliage.randomPitch, foliage.randomPitch) : 0f;
        float roll  = foliage.randomRoll   > 0f ? Random.Range(-foliage.randomRoll, foliage.randomRoll)   : 0f;
        rotation *= Quaternion.Euler(pitch, yaw, roll);

        // Build scale — treat uninitialized values (< 0.01) as 1.0
        float minS = foliage.minScale < 0.01f ? 1f : foliage.minScale;
        float maxS = foliage.maxScale < 0.01f ? 1f : foliage.maxScale;
        if (maxS < minS) maxS = minS;
        float uniformScale = Random.Range(minS, maxS);

        Vector3 finalScale = Vector3.one * uniformScale;

#if UNITY_EDITOR
        // Use single-arg overload (documented) then parent manually
        // See: https://docs.unity3d.com/ScriptReference/PrefabUtility.InstantiatePrefab.html
        GameObject instance = UnityEditor.PrefabUtility.InstantiatePrefab(foliage.prefab) as GameObject;
        if (instance == null) return null;
        instance.transform.SetParent(parent, worldPositionStays: true);
        instance.transform.SetPositionAndRotation(position, rotation);
        instance.transform.localScale = finalScale;
#else
        // See: https://docs.unity3d.com/ScriptReference/Object.Instantiate.html
        GameObject instance = Instantiate(foliage.prefab, position, rotation, parent);
        instance.transform.localScale = finalScale;
#endif
        if (markStatic)
            instance.isStatic = true;

        // Register in spatial grids
        spacingGrid?.Insert(position);
        if (foliage.exclusionRadius > 0f)
            globalExclusion?.Insert(position, foliage.exclusionRadius);

        return instance;
    }

    /// <summary>
    /// Combines spawned instances by material into fewer GameObjects to reduce
    /// draw calls. Each unique material produces one combined mesh object.
    /// Prefab meshes must have Read/Write enabled in import settings.
    /// </summary>
    /// See: https://docs.unity3d.com/ScriptReference/Mesh.CombineMeshes.html
    private void CombineInstances(List<GameObject> instances, string prefabName, Transform parent)
    {
        // Group by material
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

            // One CombineInstance per submesh
            // See: https://docs.unity3d.com/ScriptReference/CombineInstance.html
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

        // Create combined mesh per material
        int groupIndex = 0;
        foreach (var kvp in groups)
        {
            Mesh combinedMesh = new Mesh();
            combinedMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            combinedMesh.CombineMeshes(kvp.Value.ToArray(), mergeSubMeshes: true, useMatrices: true);
            combinedMesh.RecalculateBounds();

            GameObject combined = new GameObject($"{prefabName}_Combined_{groupIndex}");
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

        // Destroy the individual instances
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
        Transform parent = spawnParent != null ? spawnParent : transform;

#if UNITY_EDITOR
        // DestroyImmediate needed in edit mode
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

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.25f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawCube(Vector3.zero, volumeSize);

        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.7f);
        Gizmos.DrawWireCube(Vector3.zero, volumeSize);
    }
}
