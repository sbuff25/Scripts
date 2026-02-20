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
        public Matrix4x4[] lodLocalMatrices; // per-LOD local transform relative to instance root
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

    [Tooltip("Multiplier on effective distance for LOD selection. >1 = earlier transitions (less geo), <1 = later (more geo). 2 = LODs kick in at half the real distance.")]
    [Range(0.5f, 4f)]
    public float lodDistanceScale = 1f;

    [Header("Distance Thinning")]
    [Tooltip("Overall fraction of instances to render. 1 = all, 0.5 = half, 0 = none. Stacks with distance thinning.")]
    [Range(0f, 1f)]
    public float renderDensity = 1f;

    [Tooltip("Distance at which instance thinning begins. Beyond this, fewer instances are drawn.")]
    public float thinningStartDistance = 200f;

    [Tooltip("Fraction of instances still rendered at max render distance. 0 = none, 1 = all (no thinning).")]
    [Range(0f, 1f)]
    public float thinningMinDensity = 0.1f;

    [Tooltip("Curve power for thinning falloff. 1 = linear, higher = more aggressive (density drops faster after start distance).")]
    [Range(1f, 4f)]
    public float thinningCurve = 1f;

    [Tooltip("Scale multiplier for surviving distant instances. Compensates for thinned-out neighbors. 1 = no compensation.")]
    [Range(1f, 5f)]
    public float thinningScaleCompensation = 1.5f;

    [Tooltip("Width of the fade zone for thinning transitions (world units). Instances dither-fade instead of popping. 0 = hard cutoff.")]
    public float thinningFadeWidth = 30f;

    [Header("Triangle Budget")]
    [Tooltip("Target maximum triangle count. Renderer dynamically reduces density when over budget. 0 = unlimited.")]
    public int triangleBudget = 500000;

    [Tooltip("How fast the budget density adjusts per frame. Lower = smoother but slower to react.")]
    [Range(0.01f, 1f)]
    public float budgetAdaptSpeed = 0.1f;

    // ── Runtime State ────────────────────────────────────────────────────

    private class FoliageChunk
    {
        public Bounds bounds;
        public int[] instanceIndices;
    }

    private class DrawState
    {
        public Matrix4x4[][] lodLists; // [lodIndex] → matrix array
        public float[][] lodFades;     // [lodIndex] → per-instance thinning fade (parallel to lodLists)
        public int[] lodCounts;        // [lodIndex] → current fill count
    }

    private FoliageChunk[] _chunks;
    private DrawState[] _drawStates;
    private Matrix4x4[] _batchBuffer;
    private float[] _fadeBatchBuffer;          // per-instance fade values for current batch
    private Matrix4x4[] _cachedMatrices;       // pre-computed TRS per instance
    private MaterialPropertyBlock _mpb;        // breaks SRP Batcher → enables GPU instancing
    private Plane[] _frustumPlanes = new Plane[6];
    private int _batchLimit;
    private bool _initialized;

    // Crossfade
    private Vector4[][] _crossfadeVectors; // [protoIndex][lodIndex] → _LODCrossfade value
    private float[][] _crossfadeStartSq;   // [protoIndex][lodIndex] → squared distance where fade-out begins
    private static readonly int _LODCrossfadeID = Shader.PropertyToID("_LODCrossfade");

    // Distance thinning
    private float[] _instanceHash;        // pre-computed [0,1) hash per instance for stable thinning
    private float _thinningStartSq;       // squared start distance (for early-out check)
    private float _thinningStartDist;     // linear start distance
    private float _thinningRangeInv;      // 1 / (maxRenderDistance - thinningStartDistance)
    private float _thinningDensityRange;  // 1 - thinningMinDensity
    private float _thinningScaleRange;    // thinningScaleCompensation - 1
    private float _thinFadeMargin;        // density units over which instances dither-fade

    // Per-instance shader property for dithered thinning fade
    // See: https://docs.unity3d.com/ScriptReference/MaterialPropertyBlock.SetFloatArray.html
    private static readonly int _ThinFadeID = Shader.PropertyToID("_ThinFade");

    // Triangle budget
    private float _budgetDensity = 1f;  // dynamic multiplier [0-1], adapts over frames
    private int _lastTriCount;          // triangle count from previous frame's draw calls

    // Frustum edge fade
    private const float FrustumPadding = 15f;
    private float _frustumFadeInv;      // 1 / fade width for frustum edge dithering

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
        _fadeBatchBuffer = new float[_batchLimit];
        _mpb = new MaterialPropertyBlock();

        PrecomputeMatrices();
        BuildChunks();
        AllocateDrawStates();
        PrecomputeCrossfade();
        PrecomputeThinning();

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
                lodFades = new float[lodCount][],
                lodCounts = new int[lodCount]
            };

            for (int l = 0; l < lodCount; l++)
            {
                state.lodLists[l] = new Matrix4x4[protoCounts[p]];
                state.lodFades[l] = new float[protoCounts[p]];
            }

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

    private void PrecomputeThinning()
    {
        // Deterministic hash per instance — stable so trees don't flicker.
        // Only needs to be computed once; thresholds are updated each frame
        // so settings can be tweaked in real time during play mode.
        _instanceHash = new float[instances.Length];
        for (int i = 0; i < instances.Length; i++)
        {
            uint h = (uint)i;
            h ^= h >> 16;
            h *= 0x45d9f3b;
            h ^= h >> 16;
            _instanceHash[i] = (h & 0xFFFF) / 65535f;
        }
    }

    private void UpdateThinningThresholds()
    {
        _thinningStartDist = Mathf.Clamp(thinningStartDistance, 0f, maxRenderDistance);
        _thinningStartSq = _thinningStartDist * _thinningStartDist;
        float range = maxRenderDistance - _thinningStartDist;
        _thinningRangeInv = range > 0.001f ? 1f / range : 0f;
        _thinningDensityRange = 1f - Mathf.Clamp01(thinningMinDensity);
        _thinningScaleRange = Mathf.Max(thinningScaleCompensation, 1f) - 1f;

        // Fade margin: density change over thinningFadeWidth distance.
        // Instances whose hash is within this margin of the density threshold
        // dither-fade instead of popping.
        float fadeW = Mathf.Max(thinningFadeWidth, 0f);
        if (range > 0.001f && fadeW > 0f)
            _thinFadeMargin = renderDensity * _thinningDensityRange * (fadeW / range);
        else
            _thinFadeMargin = 0f;
    }

    // ── Render Loop ──────────────────────────────────────────────────────

    private void LateUpdate()
    {
        if (!_initialized) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // Recompute thinning thresholds each frame for real-time inspector tweaking
        UpdateThinningThresholds();

        // Adapt budget density based on previous frame's triangle count
        if (triangleBudget > 0 && _lastTriCount > 0)
        {
            float ratio = (float)triangleBudget / _lastTriCount;
            // Target density: current * ratio, clamped [0.05, 1]
            float target = Mathf.Clamp(_budgetDensity * ratio, 0.05f, 1f);
            _budgetDensity = Mathf.Lerp(_budgetDensity, target, budgetAdaptSpeed);
        }
        else
        {
            _budgetDensity = 1f;
        }

        Vector3 camPos = cam.transform.position;
        float maxDistSq = maxRenderDistance * maxRenderDistance;

        // See: https://docs.unity3d.com/ScriptReference/GeometryUtility.CalculateFrustumPlanes.html
        GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

        // Pad frustum outward to create a dither-fade zone at screen edges.
        // Instances in the padding zone fade in/out smoothly instead of popping.
        // The triangle budget compensates for the extra instances this includes.
        for (int i = 0; i < 6; i++)
            _frustumPlanes[i].distance += FrustumPadding;
        _frustumFadeInv = 1f / FrustumPadding;

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

                // 3D distance for LOD, thinning, and max-distance cull
                float dx = inst.position.x - camPos.x;
                float dy = inst.position.y - camPos.y;
                float dz = inst.position.z - camPos.z;
                float distSq = dx * dx + dy * dy + dz * dz;

                if (distSq > maxDistSq) continue;

                // Combined density: renderDensity × budget × distance-based thinning falloff
                float density = renderDensity * _budgetDensity;
                float thinT = 0f;
                if (_thinningRangeInv > 0f && distSq > _thinningStartSq)
                {
                    float linear = (Mathf.Sqrt(distSq) - _thinningStartDist) * _thinningRangeInv;
                    thinT = thinningCurve > 1.01f ? 1f - Mathf.Pow(1f - linear, thinningCurve) : linear;
                    density *= 1f - thinT * _thinningDensityRange;
                }

                // Density culling with dithered fade
                float thinFade = 1f;
                if (density < 1f)
                {
                    float hash = _instanceHash[indices[i]];
                    if (hash > density) continue; // fully culled

                    // Instances near cull threshold get a fade value (0–1) for shader dithering
                    if (_thinFadeMargin > 0f)
                    {
                        float fadeThreshold = density - _thinFadeMargin;
                        if (hash > fadeThreshold)
                            thinFade = (density - hash) / _thinFadeMargin;
                    }
                }

                // Per-instance frustum cull + edge fade (dither across the padding zone)
                float frustumDist = FrustumMinDistance(inst.position, inst.uniformScale);
                if (frustumDist < 0f) continue;
                thinFade *= Mathf.Clamp01(frustumDist * _frustumFadeInv);

                int pi = inst.prototypeIndex;
                FoliagePrototype proto = prototypes[pi];
                DrawState state = _drawStates[pi];
                Matrix4x4 rootMatrix = _cachedMatrices[indices[i]];

                // Select primary LOD level (lodDistanceScale > 1 pushes transitions closer)
                float lodDistSq = distSq * (lodDistanceScale * lodDistanceScale);
                int lod = SelectLOD(proto, lodDistSq);

                // Apply per-LOD local transform offset (handles child transforms in LODGroup prefabs)
                Matrix4x4 matrix = (proto.lodLocalMatrices != null && lod < proto.lodLocalMatrices.Length)
                    ? rootMatrix * proto.lodLocalMatrices[lod] : rootMatrix;

                // Scale compensation — grow surviving distant instances to fill visual gaps
                if (thinT > 0f && _thinningScaleRange > 0.001f)
                {
                    float sf = 1f + thinT * _thinningScaleRange;
                    matrix.m00 *= sf; matrix.m01 *= sf; matrix.m02 *= sf;
                    matrix.m10 *= sf; matrix.m11 *= sf; matrix.m12 *= sf;
                    matrix.m20 *= sf; matrix.m21 *= sf; matrix.m22 *= sf;
                }

                // Add to primary LOD draw list with fade value
                int count = state.lodCounts[lod];
                if (count < state.lodLists[lod].Length)
                {
                    state.lodLists[lod][count] = matrix;
                    state.lodFades[lod][count] = thinFade;
                    state.lodCounts[lod] = count + 1;
                }

                // Crossfade: if in fade-out zone, also add to next LOD
                if (crossfadeWidth > 0f && lod < state.lodCounts.Length - 1
                    && lodDistSq >= _crossfadeStartSq[pi][lod])
                {
                    int nextLod = lod + 1;
                    Matrix4x4 nextMatrix = (proto.lodLocalMatrices != null && nextLod < proto.lodLocalMatrices.Length)
                        ? rootMatrix * proto.lodLocalMatrices[nextLod] : rootMatrix;

                    if (thinT > 0f && _thinningScaleRange > 0.001f)
                    {
                        float sf = 1f + thinT * _thinningScaleRange;
                        nextMatrix.m00 *= sf; nextMatrix.m01 *= sf; nextMatrix.m02 *= sf;
                        nextMatrix.m10 *= sf; nextMatrix.m11 *= sf; nextMatrix.m12 *= sf;
                        nextMatrix.m20 *= sf; nextMatrix.m21 *= sf; nextMatrix.m22 *= sf;
                    }

                    int nextCount = state.lodCounts[nextLod];
                    if (nextCount < state.lodLists[nextLod].Length)
                    {
                        state.lodLists[nextLod][nextCount] = nextMatrix;
                        state.lodFades[nextLod][nextCount] = thinFade;
                        state.lodCounts[nextLod] = nextCount + 1;
                    }
                }
            }
        }

        // Issue draw calls and count triangles for budget adaptation
        int frameTris = 0;
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

                // See: https://docs.unity3d.com/ScriptReference/Mesh.GetIndexCount.html
                frameTris += count * ((int)mesh.GetIndexCount(0) / 3);

                _mpb.SetVector(_LODCrossfadeID, _crossfadeVectors[p][l]);
                DrawBatched(mesh, mat, state.lodLists[l], state.lodFades[l], count);
            }
        }
        _lastTriCount = frameTris;
    }

    /// <summary>
    /// Returns the minimum signed distance from a point to any frustum plane,
    /// or -1 if outside the frustum. Used both for culling and edge fade.
    /// See: https://docs.unity3d.com/ScriptReference/Plane.GetDistanceToPoint.html
    /// </summary>
    private float FrustumMinDistance(Vector3 point, float radius)
    {
        float minDist = float.MaxValue;
        for (int i = 0; i < 6; i++)
        {
            float d = _frustumPlanes[i].GetDistanceToPoint(point);
            if (d < -radius) return -1f;
            if (d < minDist) minDist = d;
        }
        return minDist;
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

    /// <summary>
    /// Issues batched DrawMeshInstanced calls with per-instance fade values.
    /// See: https://docs.unity3d.com/ScriptReference/Graphics.DrawMeshInstanced.html
    /// </summary>
    private void DrawBatched(Mesh mesh, Material mat, Matrix4x4[] matrices, float[] fades, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int batch = Mathf.Min(count - offset, _batchLimit);
            System.Array.Copy(matrices, offset, _batchBuffer, 0, batch);
            System.Array.Copy(fades, offset, _fadeBatchBuffer, 0, batch);

            _mpb.SetFloatArray(_ThinFadeID, _fadeBatchBuffer);

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
