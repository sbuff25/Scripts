using UnityEngine;

/// <summary>
/// Defines one sub-type within a <see cref="FoliageCluster"/>.
/// Each entry produces a random count of instances within the cluster radius.
/// </summary>
[System.Serializable]
public class ClusterEntry
{
    [Tooltip("Single prefab for this entry. Ignored if the Prefabs array is populated.")]
    public GameObject prefab;

    [Tooltip("Array of prefab variants. If populated, a random variant is chosen per instance.")]
    public GameObject[] prefabs = new GameObject[0];

    // ── Count ─────────────────────────────────────────
    [Header("Count")]
    [Tooltip("Minimum instances of this entry per cluster.")]
    [Min(0)]
    public int countMin = 1;

    [Tooltip("Maximum instances of this entry per cluster.")]
    [Min(0)]
    public int countMax = 3;

    // ── Scale ─────────────────────────────────────────
    [Header("Scale")]
    [Tooltip("Minimum uniform scale.")]
    [Min(0.01f)]
    public float minScale = 0.8f;

    [Tooltip("Maximum uniform scale.")]
    [Min(0.01f)]
    public float maxScale = 1.2f;

    // ── Rotation ──────────────────────────────────────
    [Header("Rotation")]
    [Tooltip("Random yaw range in degrees.")]
    [Range(0f, 360f)]
    public float randomYaw = 360f;

    [Tooltip("Max random pitch (X tilt) in degrees. Applied as ± this value.")]
    [Range(0f, 45f)]
    public float randomPitch = 0f;

    [Tooltip("Max random roll (Z tilt) in degrees. Applied as ± this value.")]
    [Range(0f, 45f)]
    public float randomRoll = 0f;

    // ── Placement ─────────────────────────────────────
    [Header("Placement")]
    [Tooltip("Blend between world up (0) and surface normal (1).")]
    [Range(0f, 1f)]
    public float alignToNormal = 0f;

    [Tooltip("Vertical offset from the hit surface.")]
    public float surfaceOffset = 0f;

    // ── Radial Placement ──────────────────────────────
    [Header("Radial Placement")]
    [Tooltip("Minimum distance from the cluster center. 0 = can spawn at center.")]
    [Min(0f)]
    public float minRadius = 0f;

    [Tooltip("Maximum distance from the cluster center. Should be <= the parent cluster's clusterRadius.")]
    [Min(0f)]
    public float maxRadius = 5f;

    // ── Intra-Cluster Spacing ─────────────────────────
    [Header("Intra-Cluster Spacing")]
    [Tooltip("Minimum distance between instances of this entry within the cluster. Prevents self-overlap.")]
    [Min(0f)]
    public float minSpacing = 0f;

    /// <summary>
    /// Returns a prefab to instantiate (variant-aware).
    /// </summary>
    public GameObject GetRandomPrefab()
    {
        if (prefabs != null && prefabs.Length > 0)
            return prefabs[Random.Range(0, prefabs.Length)];
        return prefab;
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
