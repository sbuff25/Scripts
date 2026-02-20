using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// GPU instanced foliage renderer. Stores captured prototype and instance data,
/// builds spatial chunks, performs frustum culling and distance-based LOD selection,
/// and draws via <see cref="Graphics.DrawMeshInstanced"/>.
/// See: https://docs.unity3d.com/ScriptReference/Graphics.DrawMeshInstanced.html
/// </summary>
public class FoliageRenderer : MonoBehaviour
{
    // ── Serialized Data (set by editor capture) ──────────────────────────

    [System.Serializable]
    public class FoliagePrototype
    {
        public string name;
        public Mesh[] lodMeshes;
        public Material[] lodMaterials;
        public float[] lodDistancesSq; // pre-squared distance thresholds per LOD
    }

    [System.Serializable]
    public struct FoliageInstance
    {
        public int prototypeIndex;
        public Vector3 position;
        public Quaternion rotation;
        public float uniformScale;
    }

    [Header("Data")]
    public FoliagePrototype[] prototypes = new FoliagePrototype[0];
    public FoliageInstance[] instances = new FoliageInstance[0];

    [Header("Settings")]
    [Tooltip("Maximum distance at which foliage is rendered.")]
    public float maxRenderDistance = 500f;

    [Tooltip("Size of spatial chunks for frustum culling.")]
    public float chunkSize = 32f;

    [Tooltip("Width of crossfade zone between LOD levels (world units). 0 = disabled.")]
    public float crossfadeWidth = 10f;

    // ── Runtime State ────────────────────────────────────────────────────

    private class FoliageChunk
    {
        public Bounds bounds;
        public int[] instanceIndices;
    }

    private class DrawState
    {
        public Matrix4x4[][] lodLists; // [lodIndex] → matrix array
        public int[] lodCounts;        // [lodIndex] → current fill count
    }

    private FoliageChunk[] _chunks;
    private DrawState[] _drawStates;
    private Matrix4x4[] _batchBuffer;
    private Matrix4x4[] _cachedMatrices; // pre-computed TRS per instance
    private MaterialPropertyBlock _mpb;  // breaks SRP Batcher → enables GPU instancing
    private Plane[] _frustumPlanes = new Plane[6];
    private int _batchLimit;
    private bool _initialized;

    // Crossfade
    private Vector4[][] _crossfadeVectors; // [protoIndex][lodIndex] → _LODCrossfade value
    private float[][] _crossfadeStartSq;   // [protoIndex][lodIndex] → squared distance where fade-out begins
    private static readonly int _LODCrossfadeID = Shader.PropertyToID("_LODCrossfade");

    // ── Initialization ───────────────────────────────────────────────────

    private void OnEnable()
    {
        Initialize();
    }

    /// <summary>
    /// Builds spatial chunks and allocates draw lists. Safe to call multiple times.
    /// </summary>
    public void Initialize()
    {
        _initialized = false;

        if (prototypes == null || prototypes.Length == 0 ||
            instances == null || instances.Length == 0)
            return;

        _batchLimit = DetectBatchLimit();
        _batchBuffer = new Matrix4x4[_batchLimit];
        _mpb = new MaterialPropertyBlock();

        PrecomputeMatrices();
        BuildChunks();
        AllocateDrawStates();
        PrecomputeCrossfade();

        _initialized = true;
    }

    private int DetectBatchLimit()
    {
        // See: https://docs.unity3d.com/ScriptReference/SystemInfo-graphicsDeviceType.html
        switch (SystemInfo.graphicsDeviceType)
        {
            case GraphicsDeviceType.OpenGLES3:
                return 125; // 16KB cbuffer / 128 bytes per matrix
            case GraphicsDeviceType.Vulkan:
                return 500;
            default:
                return 1023;
        }
    }

    private void PrecomputeMatrices()
    {
        _cachedMatrices = new Matrix4x4[instances.Length];
        for (int i = 0; i < instances.Length; i++)
        {
            ref FoliageInstance inst = ref instances[i];
            _cachedMatrices[i] = Matrix4x4.TRS(
                inst.position, inst.rotation, Vector3.one * inst.uniformScale);
        }
    }

    private void BuildChunks()
    {
        var chunkMap = new Dictionary<long, List<int>>();
        var chunkBounds = new Dictionary<long, Bounds>();
        float cs = chunkSize;

        for (int i = 0; i < instances.Length; i++)
        {
            Vector3 pos = instances[i].position;
            int cx = Mathf.FloorToInt(pos.x / cs);
            int cz = Mathf.FloorToInt(pos.z / cs);
            long key = ((long)cx << 32) | (uint)cz;

            if (!chunkMap.TryGetValue(key, out List<int> list))
            {
                list = new List<int>();
                chunkMap[key] = list;
                chunkBounds[key] = new Bounds(pos, Vector3.zero);
            }

            list.Add(i);

            Bounds b = chunkBounds[key];
            b.Encapsulate(pos);
            chunkBounds[key] = b;
        }

        // Convert to arrays and pad bounds vertically
        _chunks = new FoliageChunk[chunkMap.Count];
        int idx = 0;
        foreach (var kvp in chunkMap)
        {
            Bounds b = chunkBounds[kvp.Key];
            // Pad vertically for tall objects and horizontally for scale
            b.Expand(new Vector3(2f, 50f, 2f));

            _chunks[idx++] = new FoliageChunk
            {
                bounds = b,
                instanceIndices = kvp.Value.ToArray()
            };
        }
    }

    private void AllocateDrawStates()
    {
        // Count instances per prototype
        int[] protoCounts = new int[prototypes.Length];
        for (int i = 0; i < instances.Length; i++)
        {
            int pi = instances[i].prototypeIndex;
            if (pi >= 0 && pi < prototypes.Length)
                protoCounts[pi]++;
        }

        _drawStates = new DrawState[prototypes.Length];
        for (int p = 0; p < prototypes.Length; p++)
        {
            int lodCount = prototypes[p].lodMeshes != null ? prototypes[p].lodMeshes.Length : 0;
            if (lodCount == 0) lodCount = 1;

            var state = new DrawState
            {
                lodLists = new Matrix4x4[lodCount][],
                lodCounts = new int[lodCount]
            };

            for (int l = 0; l < lodCount; l++)
                state.lodLists[l] = new Matrix4x4[protoCounts[p]];

            _drawStates[p] = state;
        }
    }

    private void PrecomputeCrossfade()
    {
        int protoCount = prototypes.Length;
        _crossfadeVectors = new Vector4[protoCount][];
        _crossfadeStartSq = new float[protoCount][];

        float W = Mathf.Max(crossfadeWidth, 0f);

        for (int p = 0; p < protoCount; p++)
        {
            FoliagePrototype proto = prototypes[p];
            int lodCount = (proto.lodMeshes != null && proto.lodMeshes.Length > 0)
                ? proto.lodMeshes.Length : 1;

            // Convert squared distances to linear for shader
            float[] dist = new float[lodCount];
            for (int l = 0; l < lodCount; l++)
            {
                dist[l] = (proto.lodDistancesSq != null && l < proto.lodDistancesSq.Length)
                    ? Mathf.Sqrt(proto.lodDistancesSq[l])
                    : maxRenderDistance;
            }

            _crossfadeVectors[p] = new Vector4[lodCount];
            _crossfadeStartSq[p] = new float[lodCount];

            for (int l = 0; l < lodCount; l++)
            {
                float fiStart = 0f, fiEnd = 0f;
                float foStart = 0f, foEnd = 0f;

                // Fade-in from previous LOD boundary
                if (l > 0 && W > 0f)
                {
                    fiEnd = dist[l - 1];
                    fiStart = Mathf.Max(fiEnd - W, 0f);
                }

                // Fade-out toward next LOD or max distance
                if (W > 0f)
                {
                    if (l < lodCount - 1)
                    {
                        foEnd = dist[l];
                        foStart = Mathf.Max(foEnd - W, 0f);
                    }
                    else
                    {
                        foEnd = maxRenderDistance;
                        foStart = Mathf.Max(maxRenderDistance - W, 0f);
                    }
                }

                _crossfadeVectors[p][l] = new Vector4(fiStart, fiEnd, foStart, foEnd);
                _crossfadeStartSq[p][l] = foStart * foStart;
            }
        }
    }

    // ── Render Loop ──────────────────────────────────────────────────────

    private void LateUpdate()
    {
        if (!_initialized) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 camPos = cam.transform.position;
        float maxDistSq = maxRenderDistance * maxRenderDistance;

        // See: https://docs.unity3d.com/ScriptReference/GeometryUtility.CalculateFrustumPlanes.html
        GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

        // Clear draw counts
        for (int p = 0; p < _drawStates.Length; p++)
        {
            int[] counts = _drawStates[p].lodCounts;
            for (int l = 0; l < counts.Length; l++)
                counts[l] = 0;
        }

        // Cull chunks → select LOD → accumulate matrices
        for (int c = 0; c < _chunks.Length; c++)
        {
            FoliageChunk chunk = _chunks[c];

            // See: https://docs.unity3d.com/ScriptReference/GeometryUtility.TestPlanesAABB.html
            if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, chunk.bounds))
                continue;

            int[] indices = chunk.instanceIndices;
            for (int i = 0; i < indices.Length; i++)
            {
                ref FoliageInstance inst = ref instances[indices[i]];

                // XZ distance for LOD (ignore vertical)
                float dx = inst.position.x - camPos.x;
                float dz = inst.position.z - camPos.z;
                float distSq = dx * dx + dz * dz;

                if (distSq > maxDistSq) continue;

                // Per-instance frustum cull (point test with padding for mesh extent)
                if (!PointInFrustum(inst.position, inst.uniformScale)) continue;

                int pi = inst.prototypeIndex;
                FoliagePrototype proto = prototypes[pi];
                DrawState state = _drawStates[pi];
                Matrix4x4 matrix = _cachedMatrices[indices[i]];

                // Select primary LOD level
                int lod = SelectLOD(proto, distSq);

                // Add to primary LOD draw list
                int count = state.lodCounts[lod];
                if (count < state.lodLists[lod].Length)
                {
                    state.lodLists[lod][count] = matrix;
                    state.lodCounts[lod] = count + 1;
                }

                // Crossfade: if in fade-out zone, also add to next LOD
                if (crossfadeWidth > 0f && lod < state.lodCounts.Length - 1
                    && distSq >= _crossfadeStartSq[pi][lod])
                {
                    int nextCount = state.lodCounts[lod + 1];
                    if (nextCount < state.lodLists[lod + 1].Length)
                    {
                        state.lodLists[lod + 1][nextCount] = matrix;
                        state.lodCounts[lod + 1] = nextCount + 1;
                    }
                }
            }
        }

        // Issue draw calls
        for (int p = 0; p < prototypes.Length; p++)
        {
            FoliagePrototype proto = prototypes[p];
            DrawState state = _drawStates[p];

            for (int l = 0; l < state.lodCounts.Length; l++)
            {
                int count = state.lodCounts[l];
                if (count == 0) continue;

                Mesh mesh = (proto.lodMeshes != null && l < proto.lodMeshes.Length)
                    ? proto.lodMeshes[l] : null;
                Material mat = (proto.lodMaterials != null && l < proto.lodMaterials.Length)
                    ? proto.lodMaterials[l] : null;

                if (mesh == null || mat == null) continue;

                _mpb.SetVector(_LODCrossfadeID, _crossfadeVectors[p][l]);
                DrawBatched(mesh, mat, state.lodLists[l], count);
            }
        }
    }

    private bool PointInFrustum(Vector3 point, float radius)
    {
        for (int i = 0; i < 6; i++)
        {
            if (_frustumPlanes[i].GetDistanceToPoint(point) < -radius)
                return false;
        }
        return true;
    }

    private int SelectLOD(FoliagePrototype proto, float distSq)
    {
        if (proto.lodDistancesSq == null || proto.lodDistancesSq.Length == 0)
            return 0;

        for (int l = 0; l < proto.lodDistancesSq.Length; l++)
        {
            if (distSq < proto.lodDistancesSq[l])
                return l;
        }

        // Beyond all LOD thresholds — use last LOD
        return proto.lodDistancesSq.Length - 1;
    }

    private void DrawBatched(Mesh mesh, Material mat, Matrix4x4[] matrices, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int batch = Mathf.Min(count - offset, _batchLimit);
            System.Array.Copy(matrices, offset, _batchBuffer, 0, batch);

            for (int sub = 0; sub < mesh.subMeshCount; sub++)
                Graphics.DrawMeshInstanced(mesh, sub, mat, _batchBuffer, batch, _mpb);

            offset += batch;
        }
    }

    // ── Utility ──────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a LODGroup's screen-relative transition heights to world-space
    /// squared distances, using the given reference camera FOV.
    /// See: https://docs.unity3d.com/ScriptReference/LODGroup.html
    /// </summary>
    public static float[] ConvertLODDistances(LODGroup lodGroup, float cameraFOV, float maxDist)
    {
        LOD[] lods = lodGroup.GetLODs();
        float objectSize = lodGroup.size;
        float halfTan = Mathf.Tan(cameraFOV * 0.5f * Mathf.Deg2Rad);

        float[] distancesSq = new float[lods.Length];
        for (int i = 0; i < lods.Length; i++)
        {
            float screenHeight = lods[i].screenRelativeTransitionHeight;
            if (screenHeight > 0.001f)
            {
                float dist = objectSize / (2f * screenHeight * halfTan);
                distancesSq[i] = dist * dist;
            }
            else
            {
                distancesSq[i] = maxDist * maxDist;
            }
        }

        return distancesSq;
    }
}
