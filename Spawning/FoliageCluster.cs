using UnityEngine;

/// <summary>
/// Defines a cluster type — a group of <see cref="ClusterEntry"/> sub-types
/// that spawn together around a single center point. Processed alongside
/// <see cref="FoliageType"/> in priority order by <see cref="FoliageSpawnerVolume"/>.
/// </summary>
[System.Serializable]
public class FoliageCluster
{
    [Tooltip("Descriptive name for this cluster (shown in inspector).")]
    public string name = "New Cluster";

    // ── Density ───────────────────────────────────────
    [Header("Density")]
    [Tooltip("Cluster center points per square unit within the volume.")]
    [Min(0.001f)]
    public float density = 0.1f;

    // ── Cluster Shape ─────────────────────────────────
    [Header("Cluster Shape")]
    [Tooltip("Maximum radius from the center point in which sub-instances are scattered.")]
    [Min(0.1f)]
    public float clusterRadius = 5f;

    // ── Entries ───────────────────────────────────────
    [Header("Entries")]
    [Tooltip("Sub-types that make up this cluster. Each entry spawns its own count of instances.")]
    public ClusterEntry[] entries = new ClusterEntry[0];

    // ── Slope Filter ─────────────────────────────────
    [Header("Slope Filter")]
    [Tooltip("Minimum slope at the cluster center for the cluster to spawn.")]
    [Range(0f, 90f)]
    public float minSlopeAngle = 0f;

    [Tooltip("Maximum slope at the cluster center for the cluster to spawn.")]
    [Range(0f, 90f)]
    public float maxSlopeAngle = 90f;

    // ── Noise Density ─────────────────────────────────
    [Header("Noise Density")]
    [Tooltip("Perlin noise frequency for cluster density modulation. 0 = disabled.")]
    [Min(0f)]
    public float noiseScale = 0f;

    [Tooltip("Noise values below this threshold reject the cluster center.")]
    [Range(0f, 1f)]
    public float noiseThreshold = 0.5f;

    [Tooltip("World-space offset for noise sampling.")]
    public Vector2 noiseOffset = Vector2.zero;

    // ── Spacing ───────────────────────────────────────
    [Header("Spacing")]
    [Tooltip("Minimum distance between cluster centers of this type. 0 = no spacing.")]
    [Min(0f)]
    public float minSpacing = 0f;

    // ── Exclusion Zone ────────────────────────────────
    [Header("Exclusion Zone")]
    [Tooltip("Radius around each cluster center that suppresses later-priority types. 0 = no exclusion.")]
    [Min(0f)]
    public float exclusionRadius = 0f;

    // ── Priority ──────────────────────────────────────
    [Header("Priority")]
    [Tooltip("Spawn priority. Processed together with FoliageType entries — higher spawns first.")]
    public int spawnPriority = 0;

    // ── Mesh Combining ───────────────────────────────
    [Header("Mesh Combining")]
    [Tooltip("Combine all sub-instances by material to reduce draw calls. Prefab meshes must have Read/Write enabled.")]
    public bool combineMeshes = false;
}
