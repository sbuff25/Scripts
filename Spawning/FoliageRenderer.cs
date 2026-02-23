using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Hybrid chunk-based foliage renderer.
/// Foreground: spawned GameObjects with LODGroup handle close-range LOD transitions.
/// Background: Graphics.DrawMeshInstanced with per-chunk LOD selection for distant foliage.
/// Pre-bakes per-chunk per-LOD instance matrices at init — zero per-instance work at runtime.
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

    [Tooltip("Distance beyond which the instanced renderer takes over from spawned prefabs. " +
             "Prefab LODGroups handle transitions inside this range.")]
    public float crossoverDistance = 50f;

    [Tooltip("Root transform of spawned prefab instances. LODGroups under this are adjusted " +
             "at runtime to cull at crossover distance, preventing double-rendering.")]
    public Transform foregroundRoot;

    // ── Runtime State ────────────────────────────────────────────────────

    private struct ChunkProtoData
    {
        public int protoIndex;
        public Matrix4x4[][] lodMatrices; // [lodIndex][instanceIndex]
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

    // Per-prototype per-LOD consolidated draw lists (filled from visible chunks each frame)
    private Matrix4x4[][][] _drawLists;  // [protoIdx][lodIdx][instanceIdx]
    private int[][] _drawCounts;          // [protoIdx][lodIdx]

    // Per-prototype rendering data
    private float[] _protoWorldSize;
    private float[][] _lodDistBase;       // [protoIdx][lodIdx] = worldSize / (2 * SRTH)
    private Material[][][] _lodMats;      // [protoIdx][lodIdx][subMeshIdx]

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
    /// Builds chunks, pre-bakes per-chunk per-LOD matrices, and resolves materials.
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
        ComputePrototypeData();
        AllocateDrawLists();
        BuildChunks();
        SortInstancesByChunk();
        BuildChunkDrawData();
        AdjustForegroundLODGroups();

        Debug.Log($"[FoliageRenderer] {instances.Length} instances, {prototypes.Length} prototypes, " +
                  $"{_chunkCount} chunks (size={chunkSize:F0}m), hybrid rendering (crossover={crossoverDistance:F0}m)");

        for (int p = 0; p < prototypes.Length; p++)
        {
            var proto = prototypes[p];
            int lodCount = proto.lodMeshes != null ? proto.lodMeshes.Length : 0;
            var sb = new System.Text.StringBuilder();
            sb.Append($"[FoliageRenderer] Proto {p} \"{proto.name}\" — {lodCount} LODs, " +
                      $"worldSize={_protoWorldSize[p]:F2}m:");
            for (int l = 0; l < lodCount; l++)
            {
                string meshName = (proto.lodMeshes[l] != null) ? proto.lodMeshes[l].name : "null";
                Material[] mats = _lodMats[p][l];
                string matNames = mats.Length > 0
                    ? string.Join(", ", System.Array.ConvertAll(mats, m => m != null ? m.name : "null"))
                    : "NONE";
                sb.Append($"\n    LOD {l}: {meshName}, distBase={_lodDistBase[p][l]:F1}, mats=[{matNames}]");
            }
            Debug.Log(sb.ToString());
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
    /// Adjusts foreground LODGroups so they cull at crossoverDistance.
    /// Without this, trees with large lodGroup.size (like Oak-01 at 16.5m) keep their
    /// LODGroups rendering out to ~276m while the instanced renderer starts at crossoverDistance,
    /// causing double-rendering and high batch counts.
    /// See: https://docs.unity3d.com/ScriptReference/LODGroup.SetLODs.html
    /// </summary>
    private void AdjustForegroundLODGroups()
    {
        if (foregroundRoot == null) return;

        // Reference FOV — use main camera if available, otherwise 60°
        Camera cam = Camera.main;
        float halfTan = (cam != null)
            ? Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)
            : Mathf.Tan(30f * Mathf.Deg2Rad);

        LODGroup[] lodGroups = foregroundRoot.GetComponentsInChildren<LODGroup>();
        int adjusted = 0;

        foreach (LODGroup lg in lodGroups)
        {
            LOD[] lods = lg.GetLODs();
            if (lods.Length == 0) continue;

            // Compute the SRTH that makes Unity cull this LODGroup at crossoverDistance.
            // Unity's formula: screenHeight = lodGroup.size * worldScale / (2 * dist * halfTan)
            // Cull when screenHeight < lastLOD.SRTH
            // Required: SRTH = lodGroup.size * worldScale / (2 * crossoverDistance * halfTan)
            float worldScale = Mathf.Max(
                lg.transform.lossyScale.x,
                Mathf.Max(lg.transform.lossyScale.y, lg.transform.lossyScale.z));
            float requiredSRTH = lg.size * worldScale / (2f * crossoverDistance * halfTan);

            float currentSRTH = lods[lods.Length - 1].screenRelativeTransitionHeight;

            // Only increase SRTH (= pull cull distance closer) if it's currently too far
            if (requiredSRTH > currentSRTH)
            {
                lods[lods.Length - 1].screenRelativeTransitionHeight = requiredSRTH;
                lg.SetLODs(lods);
                adjusted++;
            }
        }

        if (adjusted > 0)
            Debug.Log($"[FoliageRenderer] Adjusted {adjusted}/{lodGroups.Length} LODGroups " +
                $"to cull at crossover distance ({crossoverDistance:F0}m).");
    }

    /// <summary>
    /// Computes average world size, per-LOD distance bases, and resolved materials per prototype.
    /// Distance base = worldSize / (2 * SRTH) — multiply by 1/halfTan at runtime for FOV-correct thresholds.
    /// See: https://docs.unity3d.com/ScriptReference/LODGroup.html
    /// </summary>
    private void ComputePrototypeData()
    {
        int protoCount = prototypes.Length;
        _protoWorldSize = new float[protoCount];
        _lodDistBase = new float[protoCount][];
        _lodMats = new Material[protoCount][][];

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
            float avgScale = scaleCount[p] > 0 ? scaleSum[p] / scaleCount[p] : 1f;
            _protoWorldSize[p] = proto.objectSize * avgScale;

            int lodCount = proto.lodMeshes != null ? proto.lodMeshes.Length : 0;

            // Distance bases: worldSize / (2 * SRTH) per LOD
            _lodDistBase[p] = new float[lodCount];
            for (int l = 0; l < lodCount; l++)
            {
                float srth = (proto.lodScreenHeights != null && l < proto.lodScreenHeights.Length)
                    ? proto.lodScreenHeights[l] : 0f;
                _lodDistBase[p][l] = (srth > 0.001f)
                    ? _protoWorldSize[p] / (2f * srth)
                    : float.MaxValue;
            }

            // Resolve materials per LOD
            _lodMats[p] = new Material[lodCount][];
            for (int l = 0; l < lodCount; l++)
            {
                if (proto.lodSubMeshMats != null && l < proto.lodSubMeshMats.Length
                    && proto.lodSubMeshMats[l] != null
                    && proto.lodSubMeshMats[l].materials != null
                    && proto.lodSubMeshMats[l].materials.Length > 0)
                {
                    _lodMats[p][l] = proto.lodSubMeshMats[l].materials;
                }
                else if (proto.lodMaterials != null && l < proto.lodMaterials.Length
                    && proto.lodMaterials[l] != null)
                {
                    _lodMats[p][l] = new Material[] { proto.lodMaterials[l] };
                }
                else
                {
                    _lodMats[p][l] = System.Array.Empty<Material>();
                }
            }
        }
    }

    /// <summary>
    /// Pre-allocates per-prototype per-LOD draw lists sized to total instance count.
    /// Visible chunk matrices are copied into these each frame before drawing.
    /// </summary>
    private void AllocateDrawLists()
    {
        int protoCount = prototypes.Length;
        _drawLists = new Matrix4x4[protoCount][][];
        _drawCounts = new int[protoCount][];

        int[] protoCounts = new int[protoCount];
        for (int i = 0; i < instances.Length; i++)
        {
            int pi = instances[i].prototypeIndex;
            if (pi >= 0 && pi < protoCount)
                protoCounts[pi]++;
        }

        for (int p = 0; p < protoCount; p++)
        {
            int lodCount = prototypes[p].lodMeshes != null ? prototypes[p].lodMeshes.Length : 0;
            _drawLists[p] = new Matrix4x4[lodCount][];
            _drawCounts[p] = new int[lodCount];
            for (int l = 0; l < lodCount; l++)
                _drawLists[p][l] = new Matrix4x4[protoCounts[p]];
        }
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
    /// Pre-bakes per-chunk, per-prototype, per-LOD instance matrices.
    /// At runtime, the selected LOD's matrices are sent directly to DrawMeshInstanced.
    /// </summary>
    private void BuildChunkDrawData()
    {
        for (int c = 0; c < _chunkCount; c++)
        {
            int start = _tempChunkStarts[c];
            int count = _tempChunkCounts[c];

            // Group instances by prototype, pre-compute matrices for ALL LODs
            var protoGroups = new Dictionary<int, List<Matrix4x4[]>>();

            for (int i = start; i < start + count; i++)
            {
                ref FoliageInstance inst = ref instances[i];
                int pi = inst.prototypeIndex;
                if (pi < 0 || pi >= prototypes.Length) continue;

                FoliagePrototype proto = prototypes[pi];
                int lodCount = proto.lodMeshes != null ? proto.lodMeshes.Length : 0;

                Matrix4x4 rootMatrix = Matrix4x4.TRS(
                    inst.position, inst.rotation, Vector3.one * inst.uniformScale);

                // One matrix per LOD (different lodLocalMatrices offsets)
                Matrix4x4[] perLod = new Matrix4x4[lodCount];
                for (int l = 0; l < lodCount; l++)
                {
                    perLod[l] = (proto.lodLocalMatrices != null && l < proto.lodLocalMatrices.Length)
                        ? rootMatrix * proto.lodLocalMatrices[l] : rootMatrix;
                }

                if (!protoGroups.TryGetValue(pi, out var list))
                {
                    list = new List<Matrix4x4[]>();
                    protoGroups[pi] = list;
                }
                list.Add(perLod);
            }

            var chunkProtos = new ChunkProtoData[protoGroups.Count];
            int idx = 0;
            foreach (var kvp in protoGroups)
            {
                int pi = kvp.Key;
                var instanceLodMatrices = kvp.Value;
                int instCount = instanceLodMatrices.Count;
                int lodCount = prototypes[pi].lodMeshes != null ? prototypes[pi].lodMeshes.Length : 0;

                // Transpose from [instance][lod] to [lod][instance] for cache-friendly access
                Matrix4x4[][] byLod = new Matrix4x4[lodCount][];
                for (int l = 0; l < lodCount; l++)
                {
                    byLod[l] = new Matrix4x4[instCount];
                    for (int j = 0; j < instCount; j++)
                        byLod[l][j] = instanceLodMatrices[j][l];
                }

                chunkProtos[idx++] = new ChunkProtoData
                {
                    protoIndex = pi,
                    lodMatrices = byLod,
                    count = instCount
                };
            }

            _chunks[c].protos = chunkProtos;
        }

        // Clear temp data
        _tempChunkStarts = null;
        _tempChunkCounts = null;
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
        float invHalfTan = 1f / halfTan;
        float maxDistSq = maxRenderDistance * maxRenderDistance;
        float crossoverDistSq = crossoverDistance * crossoverDistance;

        Vector3 camPos = cam.transform.position;

        // See: https://docs.unity3d.com/ScriptReference/GeometryUtility.CalculateFrustumPlanes.html
        GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);
        for (int i = 0; i < 6; i++)
            _frustumPlanes[i].distance += FrustumPadding;

        // Clear consolidated draw lists
        for (int p = 0; p < prototypes.Length; p++)
            for (int l = 0; l < _drawCounts[p].Length; l++)
                _drawCounts[p][l] = 0;

        int visibleChunks = 0;
        int drawnCount = 0;

        // ── Cull chunks, select LODs, collect matrices ──
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

            // Skip chunks in foreground territory (prefab LODGroups handle these)
            if (chunkDistSq < crossoverDistSq) continue;
            if (chunkDistSq > maxDistSq) continue;

            visibleChunks++;

            if (chunk.protos == null) continue;
            float chunkDist = Mathf.Sqrt(chunkDistSq);

            for (int p = 0; p < chunk.protos.Length; p++)
            {
                ref ChunkProtoData cpd = ref chunk.protos[p];
                int pi = cpd.protoIndex;
                if (pi < 0 || pi >= prototypes.Length) continue;

                // Per-chunk LOD selection: compare chunk distance to LOD thresholds
                int lodCount = _lodDistBase[pi].Length;
                int selectedLOD = lodCount - 1; // default to last (cheapest) LOD
                for (int l = 0; l < lodCount - 1; l++)
                {
                    float threshold = _lodDistBase[pi][l] * invHalfTan;
                    if (chunkDist < threshold)
                    {
                        selectedLOD = l;
                        break;
                    }
                }

                // Copy this chunk's matrices for the selected LOD into the draw list
                if (selectedLOD < cpd.lodMatrices.Length)
                {
                    int dst = _drawCounts[pi][selectedLOD];
                    int copyCount = Mathf.Min(cpd.count,
                        _drawLists[pi][selectedLOD].Length - dst);
                    if (copyCount > 0)
                    {
                        System.Array.Copy(cpd.lodMatrices[selectedLOD], 0,
                            _drawLists[pi][selectedLOD], dst, copyCount);
                        _drawCounts[pi][selectedLOD] = dst + copyCount;
                        drawnCount += copyCount;
                    }
                }
            }
        }

        debugVisibleChunks = visibleChunks;
        debugBackgroundCount = drawnCount;

        // First-frame diagnostics
        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            Debug.Log($"[FoliageRenderer] === First frame ===");
            Debug.Log($"  Camera: \"{cam.name}\", FOV={cam.fieldOfView:F1}°, halfTan={halfTan:F4}");
            Debug.Log($"  crossoverDistance={crossoverDistance:F0}m, maxRenderDistance={maxRenderDistance:F0}m");
            Debug.Log($"  Chunks: {visibleChunks} visible / {_chunkCount} total");
            Debug.Log($"  Background instances: {drawnCount} / {instances.Length} total");

            for (int p = 0; p < prototypes.Length; p++)
            {
                var proto = prototypes[p];
                int lodCount = proto.lodMeshes != null ? proto.lodMeshes.Length : 0;
                var sb = new System.Text.StringBuilder();
                sb.Append($"  Proto {p} \"{proto.name}\": objectSize={proto.objectSize:F2}, " +
                    $"worldSize={_protoWorldSize[p]:F2}m");
                for (int l = 0; l < lodCount; l++)
                {
                    float threshold = _lodDistBase[p][l] * invHalfTan;
                    int lodInstCount = _drawCounts[p][l];
                    sb.Append($"\n    LOD {l}: threshold={threshold:F0}m, instances={lodInstCount}");
                }
                Debug.Log(sb.ToString());
            }
        }

        DrawConsolidated();
    }

    // ── Draw Calls ───────────────────────────────────────────────────────

    /// <summary>
    /// Issues consolidated draw calls — one set per prototype per LOD from all visible chunks.
    /// Minimizes batch count by drawing all instances of the same prototype+LOD together.
    /// </summary>
    private void DrawConsolidated()
    {
        for (int p = 0; p < prototypes.Length; p++)
        {
            FoliagePrototype proto = prototypes[p];
            int lodCount = proto.lodMeshes != null ? proto.lodMeshes.Length : 0;

            for (int l = 0; l < lodCount; l++)
            {
                int count = _drawCounts[p][l];
                if (count == 0) continue;

                Mesh mesh = proto.lodMeshes[l];
                if (mesh == null) continue;

                Material[] mats = _lodMats[p][l];
                if (mats.Length == 0) continue;

                DrawBatched(mesh, mats, _drawLists[p][l], count);
            }
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
