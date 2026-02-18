using UnityEngine;

/// <summary>
/// Serializable data describing one foliage type and how it should be
/// randomly placed by <see cref="FoliageSpawnerVolume"/>.
/// </summary>
[System.Serializable]
public class FoliageType
{
    [Tooltip("Prefab to spawn. Enable GPU Instancing on its material for best performance.")]
    public GameObject prefab;

    [Tooltip("Array of prefab variants. If populated, a random variant is chosen per instance — overrides the single prefab above. Leave empty to use the single prefab.")]
    public GameObject[] prefabs = new GameObject[0];

    [Tooltip("Instances per square unit within the volume.")]
    [Min(0.001f)]
    public float density = 1f;

    // ── Scale ──────────────────────────────────────────
    [Header("Scale")]
    [Tooltip("Minimum uniform scale.")]
    [Min(0.01f)]
    public float minScale = 0.8f;

    [Tooltip("Maximum uniform scale.")]
    [Min(0.01f)]
    public float maxScale = 1.2f;

    // ── Rotation ───────────────────────────────────────
    [Header("Rotation")]
    [Tooltip("Random yaw range in degrees. 360 = full random rotation around Y.")]
    [Range(0f, 360f)]
    public float randomYaw = 360f;

    [Tooltip("Max random pitch (X tilt) in degrees. Applied as ± this value.")]
    [Range(0f, 45f)]
    public float randomPitch = 0f;

    [Tooltip("Max random roll (Z tilt) in degrees. Applied as ± this value.")]
    [Range(0f, 45f)]
    public float randomRoll = 0f;

    // ── Placement ──────────────────────────────────────
    [Header("Placement")]
    [Tooltip("Blend between world up (0) and surface normal (1) for instance orientation.")]
    [Range(0f, 1f)]
    public float alignToNormal = 0f;

    [Tooltip("Vertical offset from the hit surface (useful for partially burying meshes).")]
    public float surfaceOffset = 0f;

    // ── Slope Filter ─────────────────────────────────
    [Header("Slope Filter")]
    [Tooltip("Minimum surface slope in degrees where this type can spawn. 0 = flat ground.")]
    [Range(0f, 90f)]
    public float minSlopeAngle = 0f;

    [Tooltip("Maximum surface slope in degrees where this type can spawn. 90 = vertical wall.")]
    [Range(0f, 90f)]
    public float maxSlopeAngle = 90f;

    // ── Noise Density ─────────────────────────────────
    [Header("Noise Density")]
    [Tooltip("Perlin noise frequency for density modulation. Larger = smaller patches. 0 = disabled (uniform distribution).")]
    [Min(0f)]
    public float noiseScale = 0f;

    [Tooltip("Noise values below this threshold reject the candidate. 0 = accept all. Only used when noiseScale > 0.")]
    [Range(0f, 1f)]
    public float noiseThreshold = 0.5f;

    [Tooltip("World-space offset applied to noise sampling. Shifts the noise pattern without changing the seed.")]
    public Vector2 noiseOffset = Vector2.zero;

    // ── Spacing ───────────────────────────────────────
    [Header("Spacing")]
    [Tooltip("Minimum distance between instances of this type. 0 = no spacing check. Important for trees to prevent overlap.")]
    [Min(0f)]
    public float minSpacing = 0f;

    // ── Exclusion Zone ────────────────────────────────
    [Header("Exclusion Zone")]
    [Tooltip("Radius around each placed instance that suppresses later-priority types. 0 = no exclusion. Use on trees to keep grass away from trunks.")]
    [Min(0f)]
    public float exclusionRadius = 0f;

    // ── Priority ──────────────────────────────────────
    [Header("Priority")]
    [Tooltip("Spawn priority. Higher values spawn first and establish exclusion zones before lower-priority types. Equal priority preserves list order.")]
    public int spawnPriority = 0;

    // ── Mesh Combining ───────────────────────────────
    [Header("Mesh Combining")]
    [Tooltip("Combine spawned meshes by material to reduce draw calls. Best for simple objects (grass, small rocks). Prefab meshes must have Read/Write enabled in import settings.")]
    public bool combineMeshes = false;

    /// <summary>
    /// Returns a prefab to instantiate. If the <see cref="prefabs"/> array is
    /// populated, picks a random entry; otherwise falls back to <see cref="prefab"/>.
    /// </summary>
    public GameObject GetRandomPrefab()
    {
        if (prefabs != null && prefabs.Length > 0)
            return prefabs[Random.Range(0, prefabs.Length)];
        return prefab;
    }

    /// <summary>
    /// Returns a display name for combined meshes.
    /// </summary>
    public string GetCombineName()
    {
        if (prefabs != null && prefabs.Length > 0 && prefabs[0] != null)
            return prefabs[0].name;
        return prefab != null ? prefab.name : "Unknown";
    }

    /// <summary>
    /// Returns true if at least one valid prefab is assigned.
    /// </summary>
    public bool HasAnyPrefab()
    {
        if (prefabs != null && prefabs.Length > 0)
            return System.Array.Exists(prefabs, p => p != null);
        return prefab != null;
    }
}
