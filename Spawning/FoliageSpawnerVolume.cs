using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns foliage prefabs on the surface of terrain or meshes within a box volume.
/// Scatters random XZ points, raycasts downward, and places prefab instances on hit surfaces.
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
    [Tooltip("Size of the spawning volume in local space.")]
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
    /// Clears existing instances and spawns fresh foliage for every entry.
    /// </summary>
    public void Spawn()
    {
        Clear();

        Transform parent = GetSpawnTarget();
        Random.State prevState = Random.state;
        Random.InitState(seed);

        Vector3 half = volumeSize * 0.5f;
        float area = volumeSize.x * volumeSize.z;
        Vector3 rayDir = -transform.up;

        foreach (FoliageEntry foliage in foliageTypes)
        {
            if (foliage.prefab == null) continue;

            if (pattern == PlacementPattern.Random)
            {
                int count = Mathf.RoundToInt(area * foliage.density);
                for (int i = 0; i < count; i++)
                {
                    float x = Random.Range(-half.x, half.x);
                    float z = Random.Range(-half.z, half.z);
                    TryPlace(foliage, x, z, half, rayDir, parent);
                }
            }
            else
            {
                // Grid spacing derived from density: spacing = 1/sqrt(density)
                float spacing = 1f / Mathf.Sqrt(Mathf.Max(foliage.density, 0.001f));
                int cellsX = Mathf.Max(1, Mathf.FloorToInt(volumeSize.x / spacing));
                int cellsZ = Mathf.Max(1, Mathf.FloorToInt(volumeSize.z / spacing));
                float stepX = volumeSize.x / cellsX;
                float stepZ = volumeSize.z / cellsZ;

                for (int xi = 0; xi < cellsX; xi++)
                {
                    for (int zi = 0; zi < cellsZ; zi++)
                    {
                        float x = -half.x + (xi + 0.5f) * stepX;
                        float z = -half.z + (zi + 0.5f) * stepZ;

                        if (pattern == PlacementPattern.JitteredGrid)
                        {
                            x += Random.Range(-0.5f, 0.5f) * stepX * jitter;
                            z += Random.Range(-0.5f, 0.5f) * stepZ * jitter;
                        }

                        TryPlace(foliage, x, z, half, rayDir, parent);
                    }
                }
            }
        }

        Random.state = prevState;
    }

    /// <summary>
    /// Raycasts from a local XZ candidate and places a prefab if the surface passes all filters.
    /// </summary>
    private void TryPlace(FoliageEntry foliage, float localX, float localZ,
        Vector3 half, Vector3 rayDir, Transform parent)
    {
        Vector3 worldOrigin = transform.TransformPoint(new Vector3(localX, half.y, localZ));

        if (!Physics.Raycast(worldOrigin, rayDir, out RaycastHit hit, maxRayDistance, surfaceLayers))
            return;

        float slope = Vector3.Angle(hit.normal, Vector3.up);
        if (slope < foliage.minSlopeAngle || slope > foliage.maxSlopeAngle)
            return;

        Vector3 pos = hit.point + hit.normal * foliage.surfaceOffset;

        // Bounds check
        Vector3 localPos = transform.InverseTransformPoint(pos);
        if (Mathf.Abs(localPos.x) > half.x ||
            Mathf.Abs(localPos.y) > half.y ||
            Mathf.Abs(localPos.z) > half.z)
            return;

        Quaternion rot = Quaternion.Slerp(
            Quaternion.identity,
            Quaternion.FromToRotation(Vector3.up, hit.normal),
            foliage.alignToNormal);

        if (foliage.randomYaw > 0f)
            rot *= Quaternion.Euler(0f, Random.Range(0f, foliage.randomYaw), 0f);

        float minS = foliage.minScale < 0.01f ? 1f : foliage.minScale;
        float maxS = foliage.maxScale < 0.01f ? 1f : foliage.maxScale;
        if (maxS < minS) maxS = minS;
        float scale = Random.Range(minS, maxS);

#if UNITY_EDITOR
        GameObject instance = UnityEditor.PrefabUtility.InstantiatePrefab(foliage.prefab) as GameObject;
        if (instance == null) return;
        instance.transform.SetParent(parent, worldPositionStays: true);
        instance.transform.SetPositionAndRotation(pos, rot);
        instance.transform.localScale = Vector3.one * scale;
#else
        GameObject instance = Instantiate(foliage.prefab, pos, rot, parent);
        instance.transform.localScale = Vector3.one * scale;
#endif
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
        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.25f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawCube(Vector3.zero, volumeSize);

        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.7f);
        Gizmos.DrawWireCube(Vector3.zero, volumeSize);
    }
}
