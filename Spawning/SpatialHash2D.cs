using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lightweight 2D spatial hash for fast radius queries on the XZ plane.
/// Used by <see cref="FoliageSpawnerVolume"/> for per-type minimum spacing checks.
/// </summary>
/// See: https://docs.unity3d.com/ScriptReference/Vector3.html
public class SpatialHash2D
{
    private readonly float _invCellSize;
    private readonly Dictionary<long, List<Vector3>> _cells;

    public SpatialHash2D(float cellSize)
    {
        cellSize = Mathf.Max(cellSize, 0.01f);
        _invCellSize = 1f / cellSize;
        _cells = new Dictionary<long, List<Vector3>>(256);
    }

    /// <summary>
    /// Inserts a world-space position into the grid.
    /// </summary>
    public void Insert(Vector3 position)
    {
        long key = CellKey(position.x, position.z);
        if (!_cells.TryGetValue(key, out List<Vector3> list))
        {
            list = new List<Vector3>(4);
            _cells[key] = list;
        }
        list.Add(position);
    }

    /// <summary>
    /// Returns true if any stored point is within <paramref name="radius"/>
    /// of <paramref name="position"/> on the XZ plane.
    /// </summary>
    public bool HasNeighborWithin(Vector3 position, float radius)
    {
        float radiusSq = radius * radius;
        int cx = Mathf.FloorToInt(position.x * _invCellSize);
        int cz = Mathf.FloorToInt(position.z * _invCellSize);
        int range = Mathf.CeilToInt(radius * _invCellSize);

        for (int dx = -range; dx <= range; dx++)
        {
            for (int dz = -range; dz <= range; dz++)
            {
                long key = PackKey(cx + dx, cz + dz);
                if (!_cells.TryGetValue(key, out List<Vector3> list)) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    float ox = list[i].x - position.x;
                    float oz = list[i].z - position.z;
                    if (ox * ox + oz * oz < radiusSq)
                        return true;
                }
            }
        }
        return false;
    }

    private long CellKey(float wx, float wz)
    {
        return PackKey(
            Mathf.FloorToInt(wx * _invCellSize),
            Mathf.FloorToInt(wz * _invCellSize));
    }

    private static long PackKey(int cx, int cz)
    {
        return ((long)cx << 32) | (uint)cz;
    }
}

/// <summary>
/// Spatial hash that stores positions with per-point exclusion radii.
/// Used by <see cref="FoliageSpawnerVolume"/> for cross-type exclusion zones
/// (e.g. trees suppressing grass around their trunks).
/// </summary>
public class ExclusionGrid
{
    private struct Entry
    {
        public Vector3 position;
        public float radiusSq;
    }

    private readonly float _invCellSize;
    private readonly float _maxRadius;
    private readonly Dictionary<long, List<Entry>> _cells;

    public ExclusionGrid(float maxRadius)
    {
        _maxRadius = Mathf.Max(maxRadius, 0.01f);
        _invCellSize = 1f / _maxRadius;
        _cells = new Dictionary<long, List<Entry>>(256);
    }

    /// <summary>
    /// Inserts a position with the given exclusion radius.
    /// </summary>
    public void Insert(Vector3 position, float exclusionRadius)
    {
        long key = CellKey(position.x, position.z);
        if (!_cells.TryGetValue(key, out List<Entry> list))
        {
            list = new List<Entry>(4);
            _cells[key] = list;
        }
        list.Add(new Entry { position = position, radiusSq = exclusionRadius * exclusionRadius });
    }

    /// <summary>
    /// Returns true if <paramref name="position"/> falls within any stored
    /// point's exclusion radius on the XZ plane.
    /// </summary>
    public bool IsExcluded(Vector3 position)
    {
        int cx = Mathf.FloorToInt(position.x * _invCellSize);
        int cz = Mathf.FloorToInt(position.z * _invCellSize);
        int range = Mathf.CeilToInt(_maxRadius * _invCellSize);

        for (int dx = -range; dx <= range; dx++)
        {
            for (int dz = -range; dz <= range; dz++)
            {
                long key = PackKey(cx + dx, cz + dz);
                if (!_cells.TryGetValue(key, out List<Entry> list)) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    float ox = list[i].position.x - position.x;
                    float oz = list[i].position.z - position.z;
                    if (ox * ox + oz * oz < list[i].radiusSq)
                        return true;
                }
            }
        }
        return false;
    }

    private long CellKey(float wx, float wz)
    {
        return PackKey(
            Mathf.FloorToInt(wx * _invCellSize),
            Mathf.FloorToInt(wz * _invCellSize));
    }

    private static long PackKey(int cx, int cz)
    {
        return ((long)cx << 32) | (uint)cz;
    }
}
