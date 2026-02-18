using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Hybrid foliage LOD system.
/// Far instances are rendered via GPU instancing (zero GameObjects) with distance-based LOD selection.
/// Near instances are "promoted" to real pooled GameObjects with colliders/scripts.
/// When the camera moves away, promoted objects are demoted back to instanced and returned to the pool.
/// </summary>
public class FoliageHybridRenderer : MonoBehaviour
{
    //------------------------------------------------------------------
    // Serialized data structures
    //------------------------------------------------------------------

    [System.Serializable]
    public struct LODMesh
    {
        [Tooltip("Mesh for this LOD level.")]
        public Mesh mesh;

        [Tooltip("Materials for this LOD (one per submesh).")]
        public Material[] materials;
    }

    [System.Serializable]
    public class HybridPrototype
    {
        [Tooltip("Original prefab — pooled instances are created from this (has colliders, scripts, etc.).")]
        public GameObject prefab;

        [Tooltip("LOD meshes from highest to lowest detail. LOD0 = closest to camera.")]
        public LODMesh[] lods;

        [Tooltip("Max render distance for each LOD level. Last LOD renders to infinity. " +
                 "These are auto-computed from the LODGroup during capture but can be tweaked.")]
        public float[] lodDistances;

        public ShadowCastingMode shadowCasting = ShadowCastingMode.On;
    }

    [System.Serializable]
    public struct HybridInstance
    {
        public int prototypeIndex;
        public Vector3 position;
        public Quaternion rotation;
        public float uniformScale;
    }

    //------------------------------------------------------------------
    // Settings
    //------------------------------------------------------------------

    [Header("Data")]
    [SerializeField] private List<HybridPrototype> prototypes = new List<HybridPrototype>();
    [SerializeField] private List<HybridInstance> instances = new List<HybridInstance>();

    [Header("LOD")]
    [Tooltip("Distance within which instances are promoted to real GameObjects.")]
    [SerializeField] private float promoteDistance = 50f;

    [Tooltip("Extra distance beyond promoteDistance before demoting (prevents flicker at the boundary).")]
    [SerializeField] private float hysteresis = 5f;

    [Header("Pooling")]
    [Tooltip("Pre-instantiated pool objects per prototype.")]
    [SerializeField] private int poolWarmupCount = 10;

    [Header("Chunking")]
    [Tooltip("Spatial grid cell size in world units. Affects frustum culling granularity.")]
    [SerializeField] private float chunkSize = 32f;

    [Header("Tracking")]
    [Tooltip("Transform to measure distance from. If null, uses Camera.main.")]
    [SerializeField] private Transform trackTarget;

    //------------------------------------------------------------------
    // Public accessors for editor
    //------------------------------------------------------------------

    public List<HybridPrototype> Prototypes => prototypes;
    public List<HybridInstance> Instances => instances;

    //------------------------------------------------------------------
    // Runtime state
    //------------------------------------------------------------------

    // Pre-computed TRS matrices for every instance
    private Matrix4x4[] _cachedMatrices;

    // Spatial chunks keyed by packed (chunkX, chunkZ)
    private Dictionary<long, Chunk> _chunks;

    // Promoted instances: instance index → active pooled GameObject
    private Dictionary<int, GameObject> _promoted;

    // Object pools: one queue per prototype
    private Queue<GameObject>[] _pools;
    private Transform _poolParent;

    // Reusable draw buffer (max 1023 per DrawMeshInstanced call)
    private Matrix4x4[] _drawBuffer;
    private const int MaxInstancesPerDraw = 1023;

    // Frustum planes (reusable array)
    private Plane[] _frustumPlanes;

    // Chunk distance classification thresholds (squared)
    private float _promoteSq;
    private float _demoteSq;

    // Pre-squared LOD distances per prototype for fast comparison
    private float[][] _lodDistSq;

    private bool _initialized;
    private bool _quitting;

    //------------------------------------------------------------------
    // Internal types
    //------------------------------------------------------------------

    private class Chunk
    {
        public Bounds bounds;
        public List<int> instanceIndices; // indices into the instances list
    }

    //------------------------------------------------------------------
    // Lifecycle
    //------------------------------------------------------------------

    private void Awake()
    {
        if (instances == null || instances.Count == 0) return;

        Initialize();
    }

    private void OnApplicationQuit()
    {
        _quitting = true;
    }

    private void OnDisable()
    {
        if (!_initialized) return;

        // During scene teardown the hierarchy is mid-destruction —
        // don't touch any GameObjects, just clear tracking data.
        _promoted.Clear();
    }

    private void OnDestroy()
    {
        _quitting = true;
        if (_poolParent != null)
            Destroy(_poolParent.gameObject);
    }

    private void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _promoteSq = promoteDistance * promoteDistance;
        _demoteSq = (promoteDistance + hysteresis) * (promoteDistance + hysteresis);

        // Pre-square LOD distances for fast runtime comparison
        int protoCount = prototypes.Count;
        _lodDistSq = new float[protoCount][];
        for (int p = 0; p < protoCount; p++)
        {
            var proto = prototypes[p];
            int lodCount = proto.lods != null ? proto.lods.Length : 0;
            _lodDistSq[p] = new float[lodCount];
            for (int l = 0; l < lodCount; l++)
            {
                float d = (proto.lodDistances != null && l < proto.lodDistances.Length)
                    ? proto.lodDistances[l]
                    : float.MaxValue;
                _lodDistSq[p][l] = d * d;
            }
        }

        // Pre-compute matrices
        _cachedMatrices = new Matrix4x4[instances.Count];
        for (int i = 0; i < instances.Count; i++)
        {
            var inst = instances[i];
            _cachedMatrices[i] = Matrix4x4.TRS(
                inst.position, inst.rotation, Vector3.one * inst.uniformScale);
        }

        // Build spatial chunks
        BuildChunks();

        // Initialize pools
        _promoted = new Dictionary<int, GameObject>();
        _pools = new Queue<GameObject>[protoCount];
        _poolParent = new GameObject("_FoliagePool").transform;
        _poolParent.SetParent(transform, false);
        _poolParent.gameObject.SetActive(false);

        for (int p = 0; p < protoCount; p++)
        {
            _pools[p] = new Queue<GameObject>();
            if (prototypes[p].prefab == null) continue;
            for (int w = 0; w < poolWarmupCount; w++)
            {
                var go = CreatePoolInstance(p);
                go.SetActive(false);
                _pools[p].Enqueue(go);
            }
        }

        // Allocate draw buffer
        _drawBuffer = new Matrix4x4[MaxInstancesPerDraw];
        _frustumPlanes = new Plane[6];

        // Destroy any leftover scene GameObjects (from capture)
        DestroySceneChildren();
    }

    //------------------------------------------------------------------
    // Update loop
    //------------------------------------------------------------------

    private void Update()
    {
        if (!_initialized || _quitting) return;

        Vector3 camPos = GetTrackPosition();
        if (float.IsNaN(camPos.x)) return;

        Camera cam = trackTarget != null ? null : Camera.main;
        if (cam != null)
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);
        bool doFrustumCull = cam != null;

        // Per-prototype, per-LOD draw lists
        int protoCount = prototypes.Count;
        var drawLists = new List<int>[protoCount][];
        for (int p = 0; p < protoCount; p++)
        {
            int lodCount = prototypes[p].lods != null ? prototypes[p].lods.Length : 0;
            drawLists[p] = new List<int>[lodCount];
            for (int l = 0; l < lodCount; l++)
                drawLists[p][l] = new List<int>();
        }

        // Track which instances should be promoted this frame
        var shouldBePromoted = new HashSet<int>();

        // Process each chunk
        foreach (var kvp in _chunks)
        {
            var chunk = kvp.Value;

            // Frustum cull entire chunk
            if (doFrustumCull && !GeometryUtility.TestPlanesAABB(_frustumPlanes, chunk.bounds))
            {
                for (int j = 0; j < chunk.instanceIndices.Count; j++)
                {
                    int idx = chunk.instanceIndices[j];
                    if (_promoted.ContainsKey(idx))
                        DemoteInstance(idx);
                }
                continue;
            }

            // Distance from camera to chunk center (XZ)
            Vector3 chunkCenter = chunk.bounds.center;
            float chunkDistSq = SqrDistanceXZ(camPos, chunkCenter);

            float chunkHalfDiag = chunkSize * 0.7071f;
            float nearEdgeDist = Mathf.Sqrt(chunkDistSq) - chunkHalfDiag;
            float farEdgeDist = Mathf.Sqrt(chunkDistSq) + chunkHalfDiag;

            if (farEdgeDist * farEdgeDist < _promoteSq)
            {
                // Entire chunk is near — promote all
                for (int j = 0; j < chunk.instanceIndices.Count; j++)
                {
                    int idx = chunk.instanceIndices[j];
                    shouldBePromoted.Add(idx);
                    PromoteInstance(idx);
                }
            }
            else if (nearEdgeDist * nearEdgeDist > _demoteSq)
            {
                // Entire chunk is far — all instanced
                for (int j = 0; j < chunk.instanceIndices.Count; j++)
                {
                    int idx = chunk.instanceIndices[j];
                    if (_promoted.ContainsKey(idx))
                        DemoteInstance(idx);

                    float distSq = SqrDistanceXZ(camPos, instances[idx].position);
                    int pi = instances[idx].prototypeIndex;
                    int lod = GetLODLevel(pi, distSq);
                    if (lod >= 0)
                        drawLists[pi][lod].Add(idx);
                }
            }
            else
            {
                // Mixed chunk — per-instance distance check
                for (int j = 0; j < chunk.instanceIndices.Count; j++)
                {
                    int idx = chunk.instanceIndices[j];
                    float distSq = SqrDistanceXZ(camPos, instances[idx].position);

                    if (distSq < _promoteSq)
                    {
                        shouldBePromoted.Add(idx);
                        PromoteInstance(idx);
                    }
                    else if (distSq > _demoteSq && _promoted.ContainsKey(idx))
                    {
                        DemoteInstance(idx);
                        int pi = instances[idx].prototypeIndex;
                        int lod = GetLODLevel(pi, distSq);
                        if (lod >= 0)
                            drawLists[pi][lod].Add(idx);
                    }
                    else if (!_promoted.ContainsKey(idx))
                    {
                        int pi = instances[idx].prototypeIndex;
                        int lod = GetLODLevel(pi, distSq);
                        if (lod >= 0)
                            drawLists[pi][lod].Add(idx);
                    }
                    // else: still promoted, within hysteresis band — keep as GO
                }
            }
        }

        // Demote any promoted instances no longer in shouldBePromoted
        var toRemove = new List<int>();
        foreach (var kvp in _promoted)
        {
            if (!shouldBePromoted.Contains(kvp.Key))
            {
                float distSq = SqrDistanceXZ(camPos, instances[kvp.Key].position);
                if (distSq > _demoteSq)
                    toRemove.Add(kvp.Key);
            }
        }
        for (int i = 0; i < toRemove.Count; i++)
            DemoteInstance(toRemove[i]);

        // Draw instanced batches — per prototype, per LOD
        int layer = gameObject.layer;
        for (int p = 0; p < protoCount; p++)
        {
            var proto = prototypes[p];
            if (proto.lods == null) continue;

            for (int l = 0; l < proto.lods.Length; l++)
            {
                var lodMesh = proto.lods[l];
                if (lodMesh.mesh == null || lodMesh.materials == null || lodMesh.materials.Length == 0)
                    continue;

                var list = drawLists[p][l];
                if (list.Count == 0) continue;

                DrawInstancedBatch(lodMesh, proto.shadowCasting, list, layer);
            }
        }
    }

    //------------------------------------------------------------------
    // LOD selection
    //------------------------------------------------------------------

    /// <summary>
    /// Returns the LOD index for the given squared distance.
    /// Returns -1 if the prototype has no LODs.
    /// </summary>
    private int GetLODLevel(int prototypeIndex, float distSq)
    {
        var dists = _lodDistSq[prototypeIndex];
        if (dists.Length == 0) return -1;

        for (int l = 0; l < dists.Length; l++)
        {
            if (distSq < dists[l])
                return l;
        }

        return dists.Length - 1;
    }

    //------------------------------------------------------------------
    // Promote / Demote
    //------------------------------------------------------------------

    private void PromoteInstance(int instanceIndex)
    {
        if (_promoted.ContainsKey(instanceIndex)) return;

        var inst = instances[instanceIndex];
        var go = Rent(inst.prototypeIndex);
        if (go == null) return;

        go.transform.SetParent(transform, false);
        go.transform.SetPositionAndRotation(inst.position, inst.rotation);
        go.transform.localScale = Vector3.one * inst.uniformScale;
        go.SetActive(true);
        _promoted[instanceIndex] = go;
    }

    private void DemoteInstance(int instanceIndex)
    {
        if (!_promoted.TryGetValue(instanceIndex, out var go)) return;

        _promoted.Remove(instanceIndex);

        // During teardown the hierarchy is mid-destruction — skip GO manipulation
        if (_quitting || go == null) return;

        go.SetActive(false);
        go.transform.SetParent(_poolParent, false);
        ReturnToPool(instances[instanceIndex].prototypeIndex, go);
    }

    //------------------------------------------------------------------
    // Object pool
    //------------------------------------------------------------------

    private GameObject Rent(int prototypeIndex)
    {
        if (prototypeIndex < 0 || prototypeIndex >= _pools.Length) return null;
        if (_pools[prototypeIndex].Count > 0)
            return _pools[prototypeIndex].Dequeue();

        return CreatePoolInstance(prototypeIndex);
    }

    private void ReturnToPool(int prototypeIndex, GameObject go)
    {
        if (prototypeIndex < 0 || prototypeIndex >= _pools.Length) return;
        go.transform.SetParent(_poolParent, false);
        _pools[prototypeIndex].Enqueue(go);
    }

    private GameObject CreatePoolInstance(int prototypeIndex)
    {
        var prefab = prototypes[prototypeIndex].prefab;
        if (prefab == null) return null;

        var go = Instantiate(prefab, _poolParent);
        go.SetActive(false);
        return go;
    }

    //------------------------------------------------------------------
    // GPU instanced rendering
    //------------------------------------------------------------------

    private void DrawInstancedBatch(LODMesh lodMesh, ShadowCastingMode shadowCasting,
        List<int> instanceIndices, int layer)
    {
        int total = instanceIndices.Count;
        int submeshCount = lodMesh.mesh.subMeshCount;

        for (int offset = 0; offset < total; offset += MaxInstancesPerDraw)
        {
            int count = Mathf.Min(MaxInstancesPerDraw, total - offset);

            for (int i = 0; i < count; i++)
                _drawBuffer[i] = _cachedMatrices[instanceIndices[offset + i]];

            for (int sub = 0; sub < submeshCount; sub++)
            {
                Material mat = sub < lodMesh.materials.Length
                    ? lodMesh.materials[sub]
                    : lodMesh.materials[lodMesh.materials.Length - 1];

                Graphics.DrawMeshInstanced(
                    lodMesh.mesh,
                    sub,
                    mat,
                    _drawBuffer,
                    count,
                    null,
                    shadowCasting,
                    true,
                    layer);
            }
        }
    }

    //------------------------------------------------------------------
    // Spatial chunking
    //------------------------------------------------------------------

    private void BuildChunks()
    {
        _chunks = new Dictionary<long, Chunk>();

        for (int i = 0; i < instances.Count; i++)
        {
            var pos = instances[i].position;
            int cx = Mathf.FloorToInt(pos.x / chunkSize);
            int cz = Mathf.FloorToInt(pos.z / chunkSize);
            long key = PackChunkKey(cx, cz);

            if (!_chunks.TryGetValue(key, out var chunk))
            {
                chunk = new Chunk
                {
                    instanceIndices = new List<int>(),
                    bounds = new Bounds(
                        new Vector3((cx + 0.5f) * chunkSize, 0f, (cz + 0.5f) * chunkSize),
                        new Vector3(chunkSize, 0f, chunkSize))
                };
                _chunks[key] = chunk;
            }

            chunk.instanceIndices.Add(i);
            chunk.bounds.Encapsulate(pos);
        }

        // Add vertical padding so frustum culling works for tall objects
        foreach (var kvp in _chunks)
        {
            var b = kvp.Value.bounds;
            b.Expand(new Vector3(0f, 20f, 0f));
            kvp.Value.bounds = b;
        }
    }

    private static long PackChunkKey(int cx, int cz)
    {
        return ((long)cx << 32) | (uint)cz;
    }

    //------------------------------------------------------------------
    // Helpers
    //------------------------------------------------------------------

    private Vector3 GetTrackPosition()
    {
        if (trackTarget != null) return trackTarget.position;
        var cam = Camera.main;
        return cam != null ? cam.transform.position : new Vector3(float.NaN, 0f, 0f);
    }

    private static float SqrDistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    private void DestroySceneChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            if (child == _poolParent) continue;
            Destroy(child.gameObject);
        }
    }
}
