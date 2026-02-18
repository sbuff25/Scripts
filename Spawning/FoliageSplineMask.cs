using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>
/// Defines a spline-based mask region for <see cref="FoliageSpawnerVolume"/>.
/// The first spline on the referenced <see cref="SplineContainer"/> is sampled
/// into line segments projected onto the XZ plane. Points within
/// <see cref="radius"/> of the spline path are considered inside the mask.
/// For closed splines, points inside the polygon are also included.
/// </summary>
/// See: https://docs.unity3d.com/Packages/com.unity.splines@2.8/manual/index.html
public class FoliageSplineMask : MonoBehaviour
{
    public enum MaskMode
    {
        Include,  // Only spawn foliage inside this region
        Exclude   // Block foliage from spawning inside this region
    }

    [Header("Spline")]
    [Tooltip("SplineContainer to use as the mask shape. If not set, looks for one on this GameObject.")]
    public SplineContainer splineContainer;

    [Header("Mask Settings")]
    [Tooltip("Include: only spawn foliage inside this spline. Exclude: block foliage inside this spline.")]
    public MaskMode mode = MaskMode.Exclude;

    [Tooltip("Distance from the spline path that is considered inside the mask. For closed splines, the interior is always included regardless of radius.")]
    [Min(0f)]
    public float radius = 2f;

    [Tooltip("Number of line segments used to approximate the spline. Higher = more accurate boundary.")]
    [Range(16, 512)]
    public int resolution = 64;

    [Header("Visualization")]
    [Tooltip("Color used to draw this mask in the scene view.")]
    public Color gizmoColor = new Color(1f, 0.3f, 0.3f, 0.5f);

    // Cached segment vertices projected to XZ (x = world X, y = world Z)
    private Vector2[] _segmentsXZ;

    // Whether the source spline is closed (enables point-in-polygon test)
    private bool _isClosed;

    // Cached AABB expanded by radius for early rejection
    private float _minX, _maxX, _minZ, _maxZ;

    // Track whether cache is valid
    private bool _cacheValid;

    /// <summary>
    /// Returns true if the cached data is valid and ready for queries.
    /// </summary>
    public bool IsValid => _cacheValid && _segmentsXZ != null && _segmentsXZ.Length >= 2;

    /// <summary>
    /// Rebuilds the cached XZ segments from the first spline on the referenced
    /// <see cref="SplineContainer"/>. Call before batch containment queries.
    /// Returns false if no valid spline is found or it has insufficient knots.
    /// </summary>
    /// See: https://docs.unity3d.com/Packages/com.unity.splines@2.8/api/UnityEngine.Splines.SplineContainer.html
    public bool RebuildPolygon()
    {
        SplineContainer container = splineContainer != null
            ? splineContainer
            : GetComponent<SplineContainer>();

        if (container == null)
        {
            _cacheValid = false;
            return false;
        }

        IReadOnlyList<Spline> splines = container.Splines;
        if (splines == null || splines.Count == 0)
        {
            _cacheValid = false;
            return false;
        }

        Spline spline = splines[0];
        if (spline.Count < 2)
        {
            _cacheValid = false;
            return false;
        }

        _isClosed = spline.Closed;

        int segments = Mathf.Max(resolution, 3);
        // For closed splines, we sample segments+1 points to close the loop
        int pointCount = _isClosed ? segments : segments + 1;
        _segmentsXZ = new Vector2[pointCount];

        float rawMinX = float.MaxValue, rawMaxX = float.MinValue;
        float rawMinZ = float.MaxValue, rawMaxZ = float.MinValue;

        for (int i = 0; i < pointCount; i++)
        {
            float t;
            if (_isClosed)
                t = (float)i / segments;
            else
                t = (float)i / segments;

            // EvaluatePosition on SplineContainer returns world-space coordinates
            Unity.Mathematics.float3 worldPos = container.EvaluatePosition(0, t);

            float wx = worldPos.x;
            float wz = worldPos.z;
            _segmentsXZ[i] = new Vector2(wx, wz);

            if (wx < rawMinX) rawMinX = wx;
            if (wx > rawMaxX) rawMaxX = wx;
            if (wz < rawMinZ) rawMinZ = wz;
            if (wz > rawMaxZ) rawMaxZ = wz;
        }

        // Expand AABB by radius for early rejection
        _minX = rawMinX - radius;
        _maxX = rawMaxX + radius;
        _minZ = rawMinZ - radius;
        _maxZ = rawMaxZ + radius;

        _cacheValid = true;
        return true;
    }

    /// <summary>
    /// Tests whether a world-space XZ point is inside the mask region.
    /// A point is inside if it is within <see cref="radius"/> of any spline
    /// segment, or (for closed splines) inside the polygon area.
    /// Must call <see cref="RebuildPolygon"/> before using this method.
    /// </summary>
    public bool ContainsPointXZ(float worldX, float worldZ)
    {
        if (!_cacheValid || _segmentsXZ == null || _segmentsXZ.Length < 2)
            return false;

        // AABB early rejection (already expanded by radius)
        if (worldX < _minX || worldX > _maxX || worldZ < _minZ || worldZ > _maxZ)
            return false;

        // For closed splines, check if point is inside the polygon (always counts)
        if (_isClosed && _segmentsXZ.Length >= 3 && PointInPolygon(worldX, worldZ))
            return true;

        // Check distance to nearest spline segment
        if (radius > 0f)
        {
            float radiusSq = radius * radius;
            Vector2 point = new Vector2(worldX, worldZ);
            int segCount = _isClosed ? _segmentsXZ.Length : _segmentsXZ.Length - 1;

            for (int i = 0; i < segCount; i++)
            {
                int j = (i + 1) % _segmentsXZ.Length;
                if (DistanceToSegmentSq(point, _segmentsXZ[i], _segmentsXZ[j]) <= radiusSq)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ray-casting (odd-even) point-in-polygon test on the XZ plane.
    /// </summary>
    private bool PointInPolygon(float worldX, float worldZ)
    {
        bool inside = false;
        int count = _segmentsXZ.Length;

        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            float zi = _segmentsXZ[i].y;
            float zj = _segmentsXZ[j].y;

            if ((zi > worldZ) != (zj > worldZ))
            {
                float xi = _segmentsXZ[i].x;
                float xj = _segmentsXZ[j].x;
                float intersectX = xi + (worldZ - zi) / (zj - zi) * (xj - xi);
                if (worldX < intersectX)
                    inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Returns the squared XZ distance from point P to line segment AB.
    /// </summary>
    private static float DistanceToSegmentSq(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lengthSq = ab.sqrMagnitude;

        // Degenerate segment (A == B)
        if (lengthSq < 0.0001f)
            return (p - a).sqrMagnitude;

        // Project P onto the line, clamped to [0,1] to stay on the segment
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lengthSq);
        Vector2 projection = a + ab * t;
        return (p - projection).sqrMagnitude;
    }

    private void OnDrawGizmosSelected()
    {
        DrawMaskGizmo(1.0f);
    }

    private void OnDrawGizmos()
    {
        DrawMaskGizmo(0.3f);
    }

    private void DrawMaskGizmo(float alphaMultiplier)
    {
        if (!_cacheValid) RebuildPolygon();
        if (!_cacheValid || _segmentsXZ == null || _segmentsXZ.Length < 2) return;

        Color wireColor = gizmoColor;
        wireColor.a *= alphaMultiplier;
        Gizmos.color = wireColor;

        float y = transform.position.y;
        int segCount = _isClosed ? _segmentsXZ.Length : _segmentsXZ.Length - 1;

        // Draw spline path
        for (int i = 0; i < segCount; i++)
        {
            int j = (i + 1) % _segmentsXZ.Length;
            Vector3 a = new Vector3(_segmentsXZ[i].x, y, _segmentsXZ[i].y);
            Vector3 b = new Vector3(_segmentsXZ[j].x, y, _segmentsXZ[j].y);
            Gizmos.DrawLine(a, b);
        }

        // Draw radius boundary (offset lines on both sides of each segment)
        if (radius > 0f)
        {
            Color radiusColor = gizmoColor;
            radiusColor.a *= alphaMultiplier * 0.5f;
            Gizmos.color = radiusColor;

            for (int i = 0; i < segCount; i++)
            {
                int j = (i + 1) % _segmentsXZ.Length;
                Vector2 dir = _segmentsXZ[j] - _segmentsXZ[i];
                if (dir.sqrMagnitude < 0.0001f) continue;

                // Perpendicular offset in XZ
                Vector2 perp = new Vector2(-dir.y, dir.x).normalized * radius;

                Vector3 a1 = new Vector3(_segmentsXZ[i].x + perp.x, y, _segmentsXZ[i].y + perp.y);
                Vector3 b1 = new Vector3(_segmentsXZ[j].x + perp.x, y, _segmentsXZ[j].y + perp.y);
                Vector3 a2 = new Vector3(_segmentsXZ[i].x - perp.x, y, _segmentsXZ[i].y - perp.y);
                Vector3 b2 = new Vector3(_segmentsXZ[j].x - perp.x, y, _segmentsXZ[j].y - perp.y);

                Gizmos.DrawLine(a1, b1);
                Gizmos.DrawLine(a2, b2);
            }
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        _cacheValid = false;
    }
#endif
}
