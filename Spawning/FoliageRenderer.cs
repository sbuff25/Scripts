using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Chunk-based background foliage renderer using Graphics.DrawMeshInstanced.
/// Pre-bakes per-chunk instance matrices at init — zero per-instance work at runtime.
/// Foreground trees are rendered by their spawned GameObjects with LODGroup crossfade.
/// See: https://docs.unity3d.com/ScriptReference/Graphics.DrawMeshInstanced.html
/// </summary>
public class FoliageRenderer : MonoBehaviour
{
    // ── Serialized Data (set by editor capture) ──────────────────────────

    [System.Serializable]
    public class SubMeshMaterialSet
    {
        public Material[] materials;
    }

    [System.Serializable]
    public class FoliagePrototype
    {
        public string name;
        public Mesh[] lodMeshes;
        public Material[] lodMaterials;
        public SubMeshMaterialSet[] lodSubMeshMats;
        public Matrix4x4[] lodLocalMatrices;
        public float[] lodScreenHeights;
        public float objectSize;
        public float baseCutoff = 0.5f;
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
    [Tooltip("Maximum distance at which background foliage chunks are rendered.")]
    public float maxRenderDistance = 500f;

    [Tooltip("Chunk cell size in world units for spatial grouping and frustum culling.")]
    public float chunkSize = 32f;

    // ── Runtime State ────────────────────────────────────────────────────

    private struct ChunkProtoData
    {
        public int protoIndex;
        public Matrix4x4[] matrices;
        public int count;
    }

    private struct Chunk
    {
        public Bounds bounds;
        public ChunkProtoData[] protos;
    }

    private Chunk[] _chunks;
    private int _chunkCount;

    private Matrix4x4[] _batchBuffer;
    private MaterialPropertyBlock _mpb;
    private Plane[] _frustumPlanes = new Plane[6];
    private int _batchLimit;
    private bool _initialized;

    // Per-prototype consolidated draw lists (filled from visible chunks each frame)
    private Matrix4x4[][] _drawLists;
    private int[] _drawCounts;

    // Per-prototype background rendering data
    private int[] _bgLOD;
    private float[] _protoWorldSize;
    private Material[][] _bgMats;

    // Crossover (chunk-level LODGroup↔instancing boundary)
    private float _maxCrossoverDistSq;

    private const float FrustumPadding = 10f;
    private int _renderLayer;
    private bool _cameraWarningLogged;
    private bool _firstFrameLogged;

    // Debug (public for overlay)
    [System.NonSerialized] public int debugVisibleChunks;
    [System.NonSerialized] public int debugTotalChunks;
    [System.NonSerialized] public int debugBackgroundCount;

    // ── Initialization ───────────────────────────────────────────────────

    private void OnEnable()
    {
        Initialize();
    }

    /// <summary>
    /// Builds chunks, pre-bakes per-chunk matrices, and resolves materials.
    /// Safe to call multiple times.
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

        _renderLayer = gameObject.layer;
        _cameraWarningLogged = false;
        _firstFrameLogged = false;

        ValidateMaterials();
        ComputeBackgroundLODs();
        AllocateDrawLists();
        BuildChunks();
        SortInstancesByChunk();
        BuildChunkDrawData();

        Debug.Log($"[FoliageRenderer] {instances.Length} instances, {prototypes.Length} prototypes, " +
                  $"{_chunkCount} chunks (size={chunkSize:F0}m), chunk-based rendering");

        for (int p = 0; p < prototypes.Length; p++)
        {
            var proto = prototypes[p];
            int bgLod = _bgLOD[p];
            string meshName = (proto.lodMeshes != null && bgLod < proto.lodMeshes.Length && proto.lodMeshes[bgLod] != null)
                ? proto.lodMeshes[bgLod].name : "null";
            Material[] mats = _bgMats[p];
            string matNames = mats.Length > 0
                ? string.Join(", ", System.Array.ConvertAll(mats, m => m != null ? m.name : "null"))
                : "NONE";
            Debug.Log($"[FoliageRenderer] Proto {p} \"{proto.name}\" — bg LOD {bgLod}: " +
                      $"{meshName}, worldSize={_protoWorldSize[p]:F2}m, mats=[{matNames}]");
        }

        _initialized = true;
    }

    private int DetectBatchLimit()
    {
        // See: https://docs.unity3d.com/ScriptReference/SystemInfo-graphicsDeviceType.html
        switch (SystemInfo.graphicsDeviceType)
        {
            case GraphicsDeviceType.OpenGLES3: return 125;
            case GraphicsDeviceType.Vulkan: return 500;
            default: return 1023;
        }
    }

    /// <summary>
    /// Validates and auto-fixes materials for GPU instancing compatibility.
    /// See: https://docs.unity3d.com/ScriptReference/Material-enableInstancing.html
    /// </summary>
    private void ValidateMaterials()
    {
        for (int p = 0; p < prototypes.Length; p++)
        {
            FoliagePrototype proto = prototypes[p];
            ValidateMaterialArray(proto.lodMaterials, proto.name);
            if (proto.lodSubMeshMats != null)
            {
                for (int l = 0; l < proto.lodSubMeshMats.Length; l++)
                {
                    if (proto.lodSubMeshMats[l] != null)
                        ValidateMaterialArray(proto.lodSubMeshMats[l].materials, proto.name);
                }
            }
        }
    }

    private void ValidateMaterialArray(Material[] mats, string protoName)
    {
        if (mats == null) return;
        for (int i = 0; i < mats.Length; i++)
        {
            Material mat = mats[i];
            if (mat == null) continue;
            if (!mat.enableInstancing)
            {
                Debug.LogWarning($"[FoliageRenderer] Enabling GPU Instancing on \"{mat.name}\" " +
                    $"(proto \"{protoName}\"). Enable it on the material asset for builds.");
                mat.enableInstancing = true;
            }
        }
    }

    /// <summary>
    /// Determines background LOD index, average world size, and resolved materials per prototype.
    /// The background LOD is the last captured LOD.
    /// </summary>
    private void ComputeBackgroundLODs()
    {
        int protoCount = prototypes.Length;
        _bgLOD = new int[protoCount];
        _protoWorldSize = new float[protoCount];
        _bgMats = new Material[protoCount][];

        float[] scaleSum = new float[protoCount];
        int[] scaleCount = new int[protoCount];
        for (int i = 0; i < instances.Length; i++)
        {
            int pi = instances[i].prototypeIndex;
            if (pi >= 0 && pi < protoCount)
            {
                scaleSum[pi] += instances[i].uniformScale;
                scaleCount[pi]++;
            }
        }

        for (int p = 0; p < protoCount; p++)
        {
            var proto = prototypes[p];
            int lodCount = proto.lodMeshes != null ? proto.lodMeshes.Length : 0;
            _bgLOD[p] = Mathf.Max(0, lodCount - 1);

            float avgScale = scaleCount[p] > 0 ? scaleSum[p] / scaleCount[p] : 1f;
            _protoWorldSize[p] = proto.objectSize * avgScale;

            // Resolve background materials
            int bgLod = _bgLOD[p];
            if (proto.lodSubMeshMats != null && bgLod < proto.lodSubMeshMats.Length
                && proto.lodSubMeshMats[bgLod] != null
                && proto.lodSubMeshMats[bgLod].materials != null
                && proto.lodSubMeshMats[bgLod].materials.Length > 0)
            {
                _bgMats[p] = proto.lodSubMeshMats[bgLod].materials;
            }
            else if (proto.lodMaterials != null && bgLod < proto.lodMaterials.Length
                && proto.lodMaterials[bgLod] != null)
            {
                _bgMats[p] = new Material[] { proto.lodMaterials[bgLod] };
            }
            else
            {
                _bgMats[p] = System.Array.Empty<Material>();
            }
        }
    }

    /// <summary>
    /// Pre-allocates per-prototype draw lists sized to total instance count.
    /// Visible chunk matrices are copied into these each frame before drawing.
    /// </summary>
    private void AllocateDrawLists()
    {
        int protoCount = prototypes.Length;
        _drawLists = new Matrix4x4[protoCount][];
        _drawCounts = new int[protoCount];

        int[] protoCounts = new int[protoCount];
        for (int i = 0; i < instances.Length; i++)
        {
            int pi = instances[i].prototypeIndex;
            if (pi >= 0 && pi < protoCount)
                protoCounts[pi]++;
        }

        for (int p = 0; p < protoCount; p++)
            _drawLists[p] = new Matrix4x4[protoCounts[p]];
    }

    // ── Chunk Building ───────────────────────────────────────────────────

    private int[] _tempChunkIndices;
    private int[] _tempChunkStarts;
    private int[] _tempChunkCounts;

    private void BuildChunks()
    {
        float cs = Mathf.Max(chunkSize, 1f);
        int n = instances.Length;

        _tempChunkIndices = new int[n];
        var cellMap = new Dictionary<long, int>();
        var cellBounds = new List<Bounds>();
        var cellCounts = new List<int>();

        float maxObjRadius = 2f;
        for (int i = 0; i < n; i++)
        {
            int pi = instances[i].prototypeIndex;
            if (pi >= 0 && pi < prototypes.Length)
            {
                float r = instances[i].uniformScale * prototypes[pi].objectSize * 0.5f;
                if (r > maxObjRadius) maxObjRadius = r;
            }
        }

        for (int i = 0; i < n; i++)
        {
            Vector3 pos = instances[i].position;
            int cx = Mathf.FloorToInt(pos.x / cs);
            int cz = Mathf.FloorToInt(pos.z / cs);
            long key = ((long)cx << 32) | (uint)cz;

            if (!cellMap.TryGetValue(key, out int cellIdx))
            {
                cellIdx = cellBounds.Count;
                cellMap[key] = cellIdx;
                cellBounds.Add(new Bounds(pos, Vector3.zero));
                cellCounts.Add(0);
            }

            _tempChunkIndices[i] = cellIdx;

            Bounds b = cellBounds[cellIdx];
            b.Encapsulate(pos);
            cellBounds[cellIdx] = b;
            cellCounts[cellIdx]++;
        }

        _chunkCount = cellBounds.Count;
        _chunks = new Chunk[_chunkCount];
        _tempChunkCounts = new int[_chunkCount];

        for (int c = 0; c < _chunkCount; c++)
        {
            Bounds b = cellBounds[c];
            b.Expand(new Vector3(maxObjRadius, 50f, maxObjRadius));

            _chunks[c] = new Chunk { bounds = b, protos = null };
            _tempChunkCounts[c] = cellCounts[c];
        }

        debugTotalChunks = _chunkCount;
    }

    /// <summary>
    /// Sorts instances so those in the same chunk are contiguous.
    /// </summary>
    private void SortInstancesByChunk()
    {
        int n = instances.Length;

        var sortKeys = new int[n];
        for (int i = 0; i < n; i++) sortKeys[i] = i;
        int[] chunkIndices = _tempChunkIndices;
        System.Array.Sort(sortKeys, (a, b) => chunkIndices[a].CompareTo(chunkIndices[b]));

        var sortedInst = new FoliageInstance[n];
        for (int i = 0; i < n; i++)
            sortedInst[i] = instances[sortKeys[i]];
        instances = sortedInst;

        _tempChunkStarts = new int[_chunkCount];
        int cursor = 0;
        for (int c = 0; c < _chunkCount; c++)
        {
            _tempChunkStarts[c] = cursor;
            cursor += _tempChunkCounts[c];
        }

        _tempChunkIndices = null;
    }

    /// <summary>
    /// Pre-bakes per-chunk, per-prototype instance matrices.
    /// At runtime, these are sent directly to DrawMeshInstanced — no per-instance work.
    /// </summary>
    private void BuildChunkDrawData()
    {
        for (int c = 0; c < _chunkCount; c++)
        {
            int start = _tempChunkStarts[c];
            int count = _tempChunkCounts[c];

            // Group instances by prototype and pre-compute background LOD matrices
            var protoGroups = new Dictionary<int, List<Matrix4x4>>();

            for (int i = start; i < start + count; i++)
            {
                ref FoliageInstance inst = ref instances[i];
                int pi = inst.prototypeIndex;
                if (pi < 0 || pi >= prototypes.Length) continue;

                int bgLod = _bgLOD[pi];
                FoliagePrototype proto = prototypes[pi];

                Matrix4x4 rootMatrix = Matrix4x4.TRS(
                    inst.position, inst.rotation, Vector3.one * inst.uniformScale);
                Matrix4x4 matrix = (proto.lodLocalMatrices != null && bgLod < proto.lodLocalMatrices.Length)
                    ? rootMatrix * proto.lodLocalMatrices[bgLod] : rootMatrix;

                if (!protoGroups.TryGetValue(pi, out var list))
                {
                    list = new List<Matrix4x4>();
                    protoGroups[pi] = list;
                }
                list.Add(matrix);
            }

            var chunkProtos = new ChunkProtoData[protoGroups.Count];
            int idx = 0;
            foreach (var kvp in protoGroups)
            {
                chunkProtos[idx++] = new ChunkProtoData
                {
                    protoIndex = kvp.Key,
                    matrices = kvp.Value.ToArray(),
                    count = kvp.Value.Count
                };
            }

            _chunks[c].protos = chunkProtos;
        }

        // Clear temp data
        _tempChunkStarts = null;
        _tempChunkCounts = null;
    }

    // ── Crossover Distance ───────────────────────────────────────────────

    /// <summary>
    /// Computes the maximum crossover distance across all prototypes.
    /// Chunks closer than this are in LODGroup territory and skipped.
    /// See: https://docs.unity3d.com/ScriptReference/LODGroup.html
    /// </summary>
    private void UpdateMaxCrossoverDistance(float halfTan)
    {
        _maxCrossoverDistSq = 0f;
        for (int p = 0; p < prototypes.Length; p++)
        {
            var proto = prototypes[p];
            int bgLod = _bgLOD[p];
            float cullSRTH = (proto.lodScreenHeights != null && bgLod < proto.lodScreenHeights.Length)
                ? proto.lodScreenHeights[bgLod] : 0f;
            float worldSize = _protoWorldSize[p];

            if (cullSRTH > 0.001f && worldSize > 0.01f)
            {
                float d = worldSize / (2f * cullSRTH * halfTan);
                d *= 0.9f; // slight overlap so there's no gap at the crossover boundary
                float dSq = d * d;
                if (dSq > _maxCrossoverDistSq)
                    _maxCrossoverDistSq = dSq;
            }
        }
    }

    // ── Render Loop ──────────────────────────────────────────────────────

    private void LateUpdate()
    {
        if (!_initialized) return;

        Camera cam = Camera.main;
        if (cam == null)
        {
            if (!_cameraWarningLogged)
            {
                Debug.LogWarning("[FoliageRenderer] Camera.main is null — nothing will render. " +
                    "Tag your camera with 'MainCamera'.");
                _cameraWarningLogged = true;
            }
            return;
        }

        float halfTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        UpdateMaxCrossoverDistance(halfTan);

        // Auto-extend render distance if crossover exceeds maxRenderDistance.
        float crossoverDist = Mathf.Sqrt(_maxCrossoverDistSq);
        float effectiveMaxDist = Mathf.Max(maxRenderDistance, crossoverDist + 100f);
        float maxDistSq = effectiveMaxDist * effectiveMaxDist;

        Vector3 camPos = cam.transform.position;

        // See: https://docs.unity3d.com/ScriptReference/GeometryUtility.CalculateFrustumPlanes.html
        GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);
        for (int i = 0; i < 6; i++)
            _frustumPlanes[i].distance += FrustumPadding;

        // Clear consolidated draw lists
        for (int p = 0; p < _drawCounts.Length; p++)
            _drawCounts[p] = 0;

        int visibleChunks = 0;
        int bgCount = 0;

        // ── Cull chunks and collect visible matrices into per-prototype lists ──
        for (int c = 0; c < _chunkCount; c++)
        {
            ref Chunk chunk = ref _chunks[c];

            // Frustum cull
            if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, chunk.bounds))
                continue;

            // Chunk-level distance cull (XZ, using chunk center)
            float dx = chunk.bounds.center.x - camPos.x;
            float dz = chunk.bounds.center.z - camPos.z;
            float chunkDistSq = dx * dx + dz * dz;

            // Skip chunks in LODGroup territory (prevents double-rendering)
            if (chunkDistSq < _maxCrossoverDistSq) continue;
            if (chunkDistSq > maxDistSq) continue;

            visibleChunks++;

            // Copy this chunk's matrices into per-prototype consolidated lists
            if (chunk.protos == null) continue;
            for (int p = 0; p < chunk.protos.Length; p++)
            {
                ref ChunkProtoData cpd = ref chunk.protos[p];
                int pi = cpd.protoIndex;
                if (pi < 0 || pi >= prototypes.Length) continue;

                int dst = _drawCounts[pi];
                int copyCount = Mathf.Min(cpd.count, _drawLists[pi].Length - dst);
                if (copyCount > 0)
                {
                    System.Array.Copy(cpd.matrices, 0, _drawLists[pi], dst, copyCount);
                    _drawCounts[pi] = dst + copyCount;
                    bgCount += copyCount;
                }
            }
        }

        debugVisibleChunks = visibleChunks;
        debugBackgroundCount = bgCount;

        // First-frame diagnostics
        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            Debug.Log($"[FoliageRenderer] === First frame ===");
            Debug.Log($"  Camera: \"{cam.name}\", FOV={cam.fieldOfView:F1}°");
            Debug.Log($"  maxRenderDistance={maxRenderDistance:F0}m, crossover={crossoverDist:F0}m, " +
                      $"effectiveMax={effectiveMaxDist:F0}m");
            Debug.Log($"  Chunks: {visibleChunks} visible / {_chunkCount} total");
            Debug.Log($"  Background instances: {bgCount}");
        }

        // Issue consolidated draw calls (one set per prototype, not per chunk)
        DrawConsolidated();
    }

    // ── Draw Calls ───────────────────────────────────────────────────────

    /// <summary>
    /// Issues consolidated draw calls — one set per prototype from all visible chunks.
    /// Minimizes batch count by drawing all instances of the same prototype together.
    /// </summary>
    private void DrawConsolidated()
    {
        for (int p = 0; p < prototypes.Length; p++)
        {
            int count = _drawCounts[p];
            if (count == 0) continue;

            int bgLod = _bgLOD[p];
            FoliagePrototype proto = prototypes[p];
            Mesh mesh = (proto.lodMeshes != null && bgLod < proto.lodMeshes.Length)
                ? proto.lodMeshes[bgLod] : null;
            if (mesh == null) continue;

            Material[] mats = _bgMats[p];
            if (mats.Length == 0) continue;

            DrawBatched(mesh, mats, _drawLists[p], count);
        }
    }

    /// <summary>
    /// Issues batched DrawMeshInstanced calls with per-sub-mesh materials.
    /// See: https://docs.unity3d.com/ScriptReference/Graphics.DrawMeshInstanced.html
    /// </summary>
    private void DrawBatched(Mesh mesh, Material[] mats, Matrix4x4[] matrices, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int batch = Mathf.Min(count - offset, _batchLimit);
            System.Array.Copy(matrices, offset, _batchBuffer, 0, batch);

            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                Material mat = sub < mats.Length ? mats[sub] : mats[0];
                if (mat == null) continue;
                Graphics.DrawMeshInstanced(mesh, sub, mat, _batchBuffer, batch, _mpb,
                    ShadowCastingMode.On, true, _renderLayer);
            }

            offset += batch;
        }
    }
}
