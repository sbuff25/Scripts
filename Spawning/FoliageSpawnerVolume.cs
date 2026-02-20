using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns foliage prefabs on the surface of terrain or meshes within a box volume.
/// Scatters random XZ points, raycasts downward, and places prefab instances on hit surfaces.
/// Optionally derives the spawn area from a surface object's world-space bounds (accounts for scale).
/// See: https://docs.unity3d.com/ScriptReference/Physics.Raycast.html
/// </summary>
public class FoliageSpawnerVolume : MonoBehaviour
{
    public enum PlacementPattern
    {
        [Tooltip("Pure random scatter.")]
        Random,
        [Tooltip("Uniform grid derived from density.")]
        Grid,
        [Tooltip("Grid with per-cell random offset.")]
        JitteredGrid
    }

    [System.Serializable]
    public class FoliageEntry
    {
        [Tooltip("Prefab to spawn.")]
        public GameObject prefab;

        [Tooltip("Instances per square unit within the volume.")]
        [Min(0.001f)]
        public float density = 1f;

        [Header("Scale")]
        [Tooltip("Minimum uniform scale.")]
        [Min(0.01f)]
        public float minScale = 0.8f;

        [Tooltip("Maximum uniform scale.")]
        [Min(0.01f)]
        public float maxScale = 1.2f;

        [Header("Rotation")]
        [Tooltip("Random yaw range in degrees. 360 = full random rotation around Y.")]
        [Range(0f, 360f)]
        public float randomYaw = 360f;

        [Header("Placement")]
        [Tooltip("Blend between world up (0) and surface normal (1) for instance orientation.")]
        [Range(0f, 1f)]
        public float alignToNormal = 0f;

        [Tooltip("Vertical offset from the hit surface (useful for partially burying meshes).")]
        public float surfaceOffset = 0f;

        [Header("Slope Filter")]
        [Tooltip("Minimum surface slope in degrees. 0 = flat ground.")]
        [Range(0f, 90f)]
        public float minSlopeAngle = 0f;

        [Tooltip("Maximum surface slope in degrees. 90 = vertical wall.")]
        [Range(0f, 90f)]
        public float maxSlopeAngle = 90f;
    }

    [Header("Volume")]
    [Tooltip("Optional surface object. When set, the spawn area is derived from this object's world-space bounds (accounts for scale). When null, uses manual Volume Size.")]
    public Renderer surfaceRenderer;

    [Tooltip("Size of the spawning volume in local space. Ignored when Surface Renderer is set.")]
    public Vector3 volumeSize = new Vector3(50f, 30f, 50f);

    [Header("Raycasting")]
    [Tooltip("Layers that foliage can be placed on.")]
    public LayerMask surfaceLayers = ~0;

    [Tooltip("Max raycast distance downward from the top of the volume.")]
    public float maxRayDistance = 100f;

    [Header("Foliage")]
    public List<FoliageEntry> foliageTypes = new List<FoliageEntry>();

    [Header("Pattern")]
    [Tooltip("How candidate points are distributed across the volume.")]
    public PlacementPattern pattern = PlacementPattern.JitteredGrid;

    [Tooltip("How much each grid point is randomized within its cell. 0 = perfect grid, 1 = full cell randomization. Only used by Jittered Grid.")]
    [Range(0f, 1f)]
    public float jitter = 0.75f;

    [Header("Settings")]
    [Tooltip("Seed for reproducible results. Change to get a different layout.")]
    public int seed = 12345;

    [Tooltip("Parent transform for spawned instances. If null, a sibling container is auto-created.")]
    public Transform spawnParent;

    [SerializeField, HideInInspector]
    private Transform _spawnContainer;

    /// <summary>
    /// Returns the world-space spawn area. When surfaceRenderer is set, uses its bounds.
    /// Otherwise derives from the spawner's transform and volumeSize.
    /// </summary>
    private void GetSpawnArea(out Vector3 center, out Vector3 halfExtents, out float area)
    {
        if (surfaceRenderer != null)
        {
            // See: https://docs.unity3d.com/ScriptReference/Renderer-bounds.html
            Bounds b = surfaceRenderer.bounds;
            center = b.center;
            halfExtents = b.extents;
            area = b.size.x * b.size.z;
        }
        else
        {
            center = transform.position;
            halfExtents = volumeSize * 0.5f;
            area = volumeSize.x * volumeSize.z;
        }
    }

    /// <summary>
    /// Clears existing instances and spawns fresh foliage for every entry.
    /// </summary>
    public void Spawn()
    {
        Clear();

        Transform parent = GetSpawnTarget();
        Random.State prevState = Random.state;
        Random.InitState(seed);

        // Ensure physics colliders are up-to-date in edit mode.
        // Physics.autoSyncTransforms is off by default in Unity 6.
        // See: https://docs.unity3d.com/ScriptReference/Physics.SyncTransforms.html
        Physics.SyncTransforms();

        GetSpawnArea(out Vector3 spawnCenter, out Vector3 spawnHalf, out float spawnArea);

        // When spawning on a scaled surface, normalize foliage scale so it looks correct.
        // e.g. surface at scale 100 → foliage scale divided by 100.
        // See: https://docs.unity3d.com/ScriptReference/Transform-lossyScale.html
        float scaleMultiplier = 1f;
        if (surfaceRenderer != null)
        {
            Vector3 s = surfaceRenderer.transform.lossyScale;
            float avgScale = (Mathf.Abs(s.x) + Mathf.Abs(s.z)) * 0.5f;
            if (avgScale > 0.001f && Mathf.Abs(avgScale - 1f) > 0.01f)
                scaleMultiplier = 1f / avgScale;
        }

        if (foliageTypes == null || foliageTypes.Count == 0)
        {
            Debug.LogWarning("[FoliageSpawner] No foliage entries defined. Add entries to the Foliage Types list.");
            Random.state = prevState;
            return;
        }

        float sizeX = spawnHalf.x * 2f;
        float sizeZ = spawnHalf.z * 2f;

        int totalPlaced = 0;
        foreach (FoliageEntry foliage in foliageTypes)
        {
            if (foliage.prefab == null)
            {
                Debug.LogWarning("[FoliageSpawner] Skipping entry with null prefab.");
                continue;
            }

            if (pattern == PlacementPattern.Random)
            {
                int count = Mathf.RoundToInt(spawnArea * foliage.density);
                for (int i = 0; i < count; i++)
                {
                    float x = Random.Range(-spawnHalf.x, spawnHalf.x);
                    float z = Random.Range(-spawnHalf.z, spawnHalf.z);
                    if (TryPlace(foliage, x, z, spawnCenter, spawnHalf, scaleMultiplier, parent))
                        totalPlaced++;
                }
            }
            else
            {
                // Grid spacing derived from density: spacing = 1/sqrt(density)
                float spacing = 1f / Mathf.Sqrt(Mathf.Max(foliage.density, 0.001f));
                int cellsX = Mathf.Max(1, Mathf.FloorToInt(sizeX / spacing));
                int cellsZ = Mathf.Max(1, Mathf.FloorToInt(sizeZ / spacing));
                float stepX = sizeX / cellsX;
                float stepZ = sizeZ / cellsZ;

                for (int xi = 0; xi < cellsX; xi++)
                {
                    for (int zi = 0; zi < cellsZ; zi++)
                    {
                        float x = -spawnHalf.x + (xi + 0.5f) * stepX;
                        float z = -spawnHalf.z + (zi + 0.5f) * stepZ;

                        if (pattern == PlacementPattern.JitteredGrid)
                        {
                            x += Random.Range(-0.5f, 0.5f) * stepX * jitter;
                            z += Random.Range(-0.5f, 0.5f) * stepZ * jitter;
                        }

                        if (TryPlace(foliage, x, z, spawnCenter, spawnHalf, scaleMultiplier, parent))
                            totalPlaced++;
                    }
                }
            }
        }

        if (totalPlaced == 0)
            Debug.LogWarning($"[FoliageSpawner] 0 instances placed. Check: volume position over surface, Surface Layers mask, and volume size. " +
                $"Center: {spawnCenter}, Half: {spawnHalf}, Layers: {surfaceLayers.value}");
        else
            Debug.Log($"[FoliageSpawner] Placed {totalPlaced} instances.");

        Random.state = prevState;
    }

    /// <summary>
    /// Raycasts from a world-space XZ offset and places a prefab if the surface passes all filters.
    /// Returns true if an instance was placed.
    /// </summary>
    private bool TryPlace(FoliageEntry foliage, float offsetX, float offsetZ,
        Vector3 spawnCenter, Vector3 spawnHalf, float scaleMultiplier, Transform parent)
    {
        // Ray origin: center + XZ offset, at the top of the volume
        Vector3 worldOrigin = spawnCenter + new Vector3(offsetX, spawnHalf.y, offsetZ);

        if (!Physics.Raycast(worldOrigin, Vector3.down, out RaycastHit hit, maxRayDistance, surfaceLayers))
            return false;

        float slope = Vector3.Angle(hit.normal, Vector3.up);
        if (slope < foliage.minSlopeAngle || slope > foliage.maxSlopeAngle)
            return false;

        Vector3 pos = hit.point + hit.normal * foliage.surfaceOffset;

        // Bounds check — ensure hit point is within the spawn area
        Vector3 delta = pos - spawnCenter;
        if (Mathf.Abs(delta.x) > spawnHalf.x ||
            Mathf.Abs(delta.y) > spawnHalf.y ||
            Mathf.Abs(delta.z) > spawnHalf.z)
            return false;

        Quaternion rot = Quaternion.Slerp(
            Quaternion.identity,
            Quaternion.FromToRotation(Vector3.up, hit.normal),
            foliage.alignToNormal);

        if (foliage.randomYaw > 0f)
            rot *= Quaternion.Euler(0f, Random.Range(0f, foliage.randomYaw), 0f);

        float minS = foliage.minScale < 0.01f ? 1f : foliage.minScale;
        float maxS = foliage.maxScale < 0.01f ? 1f : foliage.maxScale;
        if (maxS < minS) maxS = minS;
        float scale = Random.Range(minS, maxS) * scaleMultiplier;

#if UNITY_EDITOR
        GameObject instance = UnityEditor.PrefabUtility.InstantiatePrefab(foliage.prefab) as GameObject;
        if (instance == null) return false;
        instance.transform.SetParent(parent, worldPositionStays: true);
        instance.transform.SetPositionAndRotation(pos, rot);
        instance.transform.localScale = Vector3.one * scale;
#else
        GameObject instance = Instantiate(foliage.prefab, pos, rot, parent);
        instance.transform.localScale = Vector3.one * scale;
#endif
        return true;
    }

    /// <summary>
    /// Destroys all spawned instances under the spawn target.
    /// </summary>
    public void Clear()
    {
        Transform parent = GetSpawnTarget();
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
#if UNITY_EDITOR
            DestroyImmediate(parent.GetChild(i).gameObject);
#else
            Destroy(parent.GetChild(i).gameObject);
#endif
        }
    }

    private Transform GetSpawnTarget()
    {
        if (spawnParent != null) return spawnParent;
        if (_spawnContainer != null) return _spawnContainer;

        GameObject container = new GameObject(gameObject.name + "_Foliage");
        if (transform.parent != null)
            container.transform.SetParent(transform.parent, worldPositionStays: false);
        container.transform.SetPositionAndRotation(transform.position, transform.rotation);
        _spawnContainer = container.transform;
        return _spawnContainer;
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

    private void OnDrawGizmosSelected()
    {
        // Show the actual spawn area — from surface bounds or manual volume
        if (surfaceRenderer != null)
        {
            Bounds b = surfaceRenderer.bounds;
            Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.25f);
            Gizmos.DrawCube(b.center, b.size);
            Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.7f);
            Gizmos.DrawWireCube(b.center, b.size);
        }
        else
        {
            Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.25f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawCube(Vector3.zero, volumeSize);
            Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.7f);
            Gizmos.DrawWireCube(Vector3.zero, volumeSize);
        }
    }
}
