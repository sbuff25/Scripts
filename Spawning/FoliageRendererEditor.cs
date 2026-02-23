using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Custom inspector for <see cref="FoliageRenderer"/>.
/// Provides Capture and Clear buttons for converting spawned GameObjects
/// into GPU instanced rendering data.
/// See: https://docs.unity3d.com/ScriptReference/Editor.html
/// </summary>
[CustomEditor(typeof(FoliageRenderer))]
public class FoliageRendererEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        FoliageRenderer renderer = (FoliageRenderer)target;

        EditorGUILayout.Space(10);

        // Stats
        int protoCount = renderer.prototypes != null ? renderer.prototypes.Length : 0;
        int instCount = renderer.instances != null ? renderer.instances.Length : 0;
        EditorGUILayout.HelpBox(
            $"Prototypes: {protoCount}   Instances: {instCount}",
            MessageType.Info);

        EditorGUILayout.Space(5);

        EditorGUILayout.BeginHorizontal();

        GUI.backgroundColor = new Color(0.4f, 0.7f, 1f);
        if (GUILayout.Button("Capture", GUILayout.Height(30)))
        {
            CaptureInstances(renderer);
        }

        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
        if (GUILayout.Button("Clear", GUILayout.Height(30)))
        {
            Undo.RecordObject(renderer, "Clear Foliage Renderer");
            renderer.prototypes = new FoliageRenderer.FoliagePrototype[0];
            renderer.instances = new FoliageRenderer.FoliageInstance[0];
            EditorUtility.SetDirty(renderer);
        }

        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();
    }

    /// <summary>
    /// Scans spawned GameObjects, extracts prototype and instance data,
    /// stores on the renderer, and destroys the originals.
    /// </summary>
    private void CaptureInstances(FoliageRenderer renderer)
    {
        Transform spawnRoot = FindSpawnRoot(renderer);
        if (spawnRoot == null)
        {
            EditorUtility.DisplayDialog("Capture Failed",
                "Could not find spawned foliage. Make sure a FoliageSpawnerVolume " +
                "has spawned instances, and this component can find the spawn container.",
                "OK");
            return;
        }

        if (spawnRoot.childCount == 0)
        {
            EditorUtility.DisplayDialog("Capture Failed",
                "Spawn root has no children. Spawn foliage first.", "OK");
            return;
        }

        Undo.RecordObject(renderer, "Capture Foliage");

        var protoMap = new Dictionary<string, int>();
        var protoList = new List<FoliageRenderer.FoliagePrototype>();
        var instanceList = new List<FoliageRenderer.FoliageInstance>();

        var instanceRoots = new List<Transform>();
        CollectInstanceRoots(spawnRoot, instanceRoots);

        foreach (Transform instanceRoot in instanceRoots)
        {
            GameObject prefabRoot = PrefabUtility.GetCorrespondingObjectFromOriginalSource(instanceRoot.gameObject);
            string protoKey = GetPrototypeKey(instanceRoot.gameObject, prefabRoot);

            if (!protoMap.TryGetValue(protoKey, out int protoIndex))
            {
                protoIndex = protoList.Count;
                protoMap[protoKey] = protoIndex;

                FoliageRenderer.FoliagePrototype proto = BuildPrototype(
                    instanceRoot.gameObject, prefabRoot, renderer.maxRenderDistance);
                protoList.Add(proto);
            }

            instanceList.Add(new FoliageRenderer.FoliageInstance
            {
                prototypeIndex = protoIndex,
                position = instanceRoot.position,
                rotation = instanceRoot.rotation,
                uniformScale = instanceRoot.lossyScale.x
            });
        }

        renderer.prototypes = protoList.ToArray();
        renderer.instances = instanceList.ToArray();
        renderer.foregroundRoot = spawnRoot;

        EditorUtility.SetDirty(renderer);

        // Spawned GameObjects are kept alive — their LODGroups handle foreground rendering.
        // The instanced renderer handles background chunks beyond crossoverDistance.

        // Log capture summary
        Debug.Log($"[FoliageRenderer] Captured {instanceList.Count} instances across {protoList.Count} prototypes. " +
                  "Originals kept for LODGroup foreground rendering.");
        for (int p = 0; p < protoList.Count; p++)
        {
            var proto = protoList[p];
            var sb = new System.Text.StringBuilder();
            sb.Append($"  Prototype {p} \"{proto.name}\" — {proto.lodMeshes.Length} LODs, objectSize={proto.objectSize:F2}m:");
            for (int l = 0; l < proto.lodMeshes.Length; l++)
            {
                string meshName = proto.lodMeshes[l] != null ? proto.lodMeshes[l].name : "null";
                int tris = proto.lodMeshes[l] != null ? (int)proto.lodMeshes[l].GetIndexCount(0) / 3 : 0;
                float srth = proto.lodScreenHeights[l];
                sb.Append($"\n    LOD {l}: {meshName} ({tris} tris), SRTH={srth:F4}");
            }
            Debug.Log(sb.ToString());
        }
    }

    private void CollectInstanceRoots(Transform root, List<Transform> results)
    {
        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);

            if (child.GetComponent<LODGroup>() != null)
            {
                results.Add(child);
            }
            else if (child.GetComponent<MeshRenderer>() != null)
            {
                results.Add(child);
            }
            else if (child.childCount > 0)
            {
                CollectInstanceRoots(child, results);
            }
        }
    }

    private string GetPrototypeKey(GameObject sceneInstance, GameObject prefabRoot)
    {
        if (prefabRoot != null)
            return prefabRoot.GetInstanceID().ToString();

        MeshRenderer mr = sceneInstance.GetComponent<MeshRenderer>();
        MeshFilter mf = sceneInstance.GetComponent<MeshFilter>();
        if (mr == null) mr = sceneInstance.GetComponentInChildren<MeshRenderer>();
        if (mf == null) mf = sceneInstance.GetComponentInChildren<MeshFilter>();

        int meshId = mf != null && mf.sharedMesh != null ? mf.sharedMesh.GetInstanceID() : 0;
        int matId = mr != null && mr.sharedMaterial != null ? mr.sharedMaterial.GetInstanceID() : 0;
        return $"{meshId}_{matId}";
    }

    private FoliageRenderer.FoliagePrototype BuildPrototype(
        GameObject sceneInstance, GameObject prefabRoot, float maxDist)
    {
        // Check scene instance for LODGroup (instantiated prefabs have it)
        LODGroup lodGroup = sceneInstance.GetComponent<LODGroup>();

        if (lodGroup != null)
            return BuildPrototypeFromLODGroup(lodGroup, maxDist);

        // Check prefab source for LODGroup
        if (prefabRoot != null)
        {
            LODGroup prefabLOD = prefabRoot.GetComponent<LODGroup>();
            if (prefabLOD != null)
                return BuildPrototypeFromLODGroup(prefabLOD, maxDist);
        }

        // No LODGroup — single LOD with full screen height
        MeshFilter mf = sceneInstance.GetComponent<MeshFilter>();
        MeshRenderer mr = sceneInstance.GetComponent<MeshRenderer>();
        string name = prefabRoot != null ? prefabRoot.name : sceneInstance.name;

        // Compute local-space objectSize from mesh bounds
        float objSize = 1f;
        if (mf != null && mf.sharedMesh != null)
        {
            Vector3 s = mf.sharedMesh.bounds.size;
            objSize = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
        }

        Material mat0 = mr != null ? mr.sharedMaterial : null;
        Material[] allMats = mr != null ? mr.sharedMaterials : new Material[] { mat0 };

        return new FoliageRenderer.FoliagePrototype
        {
            name = name,
            lodMeshes = new Mesh[] { mf.sharedMesh },
            lodMaterials = new Material[] { mr.sharedMaterial },
            lodSubMeshMats = new FoliageRenderer.SubMeshMaterialSet[]
            {
                new FoliageRenderer.SubMeshMaterialSet { materials = allMats }
            },
            lodLocalMatrices = new Matrix4x4[] { Matrix4x4.identity },
            lodScreenHeights = new float[] { 0f },
            objectSize = objSize,
            baseCutoff = ReadBaseCutoff(mat0)
        };
    }

    /// <summary>
    /// Extracts per-LOD mesh, material, screen heights, and object size from a LODGroup.
    /// Stores the raw screenRelativeTransitionHeight values and lodGroup.size so the
    /// renderer can evaluate LODs per-frame using the actual camera FOV — matching
    /// Unity's LODGroup exactly.
    /// See: https://docs.unity3d.com/ScriptReference/LODGroup.GetLODs.html
    /// </summary>
    private FoliageRenderer.FoliagePrototype BuildPrototypeFromLODGroup(
        LODGroup lodGroup, float maxDist)
    {
        LOD[] lods = lodGroup.GetLODs();

        // Use lodGroup.size — this is the exact value Unity's LODGroup uses internally
        // for screen-ratio evaluation. It's in local space; at runtime the renderer
        // multiplies by per-instance scale, matching Unity's behavior.
        // See: https://docs.unity3d.com/ScriptReference/LODGroup-size.html
        float objectSizeAtScale1 = lodGroup.size;

        Debug.Log($"[Capture] \"{lodGroup.gameObject.name}\" lodGroup.size={lodGroup.size:F3}, " +
                  $"lossyScale={lodGroup.transform.lossyScale}, {lods.Length} LOD levels");

        Matrix4x4 rootWorldToLocal = lodGroup.transform.worldToLocalMatrix;

        var meshes = new List<Mesh>();
        var materials = new List<Material>();
        var subMeshMats = new List<FoliageRenderer.SubMeshMaterialSet>();
        var screenHeights = new List<float>();
        var localMatrices = new List<Matrix4x4>();

        for (int i = 0; i < lods.Length; i++)
        {
            Renderer[] renderers = lods[i].renderers;
            if (renderers == null || renderers.Length == 0)
            {
                Debug.LogWarning($"[Capture] LOD {i} skipped — no renderers assigned.");
                continue;
            }

            // Count valid renderers for diagnostics
            int validRendererCount = 0;
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
                if ((mf != null && mf.sharedMesh != null) || (smr != null && smr.sharedMesh != null))
                    validRendererCount++;
            }

            if (validRendererCount > 1)
            {
                Debug.LogWarning($"[Capture] LOD {i} has {validRendererCount} renderers — only the FIRST " +
                    $"mesh will be used for background instancing. The others will only render in " +
                    $"foreground (LODGroup) mode, causing extra draw calls.");
            }

            Mesh mesh = null;
            Material mat = null;
            Material[] allMats = null;
            Matrix4x4 localMatrix = Matrix4x4.identity;
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;

                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    mesh = mf.sharedMesh;
                    mat = r.sharedMaterial;
                    allMats = r.sharedMaterials;
                    localMatrix = rootWorldToLocal * r.transform.localToWorldMatrix;
                    break;
                }

                SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
                if (smr != null && smr.sharedMesh != null)
                {
                    mesh = smr.sharedMesh;
                    mat = r.sharedMaterial;
                    allMats = r.sharedMaterials;
                    localMatrix = rootWorldToLocal * r.transform.localToWorldMatrix;
                    break;
                }
            }

            if (mesh == null)
            {
                Debug.LogWarning($"[Capture] LOD {i} skipped — {renderers.Length} renderer(s) but no valid mesh. " +
                    $"Types: {string.Join(", ", System.Array.ConvertAll(renderers, r => r != null ? r.GetType().Name : "null"))}");
                continue;
            }

            meshes.Add(mesh);
            materials.Add(mat);
            subMeshMats.Add(new FoliageRenderer.SubMeshMaterialSet { materials = allMats });
            screenHeights.Add(lods[i].screenRelativeTransitionHeight);
            localMatrices.Add(localMatrix);

            int subCount = mesh.subMeshCount;
            int matCount = allMats != null ? allMats.Length : 0;
            int tris = (int)mesh.GetIndexCount(0) / 3;
            int totalTris = 0;
            for (int s = 0; s < subCount; s++)
                totalTris += (int)mesh.GetIndexCount(s) / 3;
            Debug.Log($"[Capture]   LOD {i}: {mesh.name} ({subCount} sub-mesh, {matCount} mat, {totalTris} tris), " +
                $"SRTH={lods[i].screenRelativeTransitionHeight:F4}, renderers={validRendererCount}");
        }

        // Last LOD's SRTH is kept as-is — it defines the crossover distance
        // where LODGroup culling ends and the background instanced renderer starts.

        // Read base alpha cutoff from LOD 0 material
        Material lod0Mat = materials.Count > 0 ? materials[0] : null;

        return new FoliageRenderer.FoliagePrototype
        {
            name = lodGroup.gameObject.name,
            lodMeshes = meshes.ToArray(),
            lodMaterials = materials.ToArray(),
            lodSubMeshMats = subMeshMats.ToArray(),
            lodLocalMatrices = localMatrices.ToArray(),
            lodScreenHeights = screenHeights.ToArray(),
            objectSize = objectSizeAtScale1,
            baseCutoff = ReadBaseCutoff(lod0Mat)
        };
    }

    /// <summary>
    /// Reads the alpha cutoff base value from a material. Checks common property names.
    /// </summary>
    private static float ReadBaseCutoff(Material mat)
    {
        if (mat == null) return 0.5f;
        if (mat.HasProperty("_Cutoff"))
            return mat.GetFloat("_Cutoff");
        if (mat.HasProperty("_AlphaClipThreshold"))
            return mat.GetFloat("_AlphaClipThreshold");
        return 0.5f;
    }

    private Transform FindSpawnRoot(FoliageRenderer renderer)
    {
        FoliageSpawnerVolume spawner = renderer.GetComponent<FoliageSpawnerVolume>();
        if (spawner == null)
            spawner = renderer.GetComponentInParent<FoliageSpawnerVolume>();
        if (spawner == null)
            spawner = Object.FindFirstObjectByType<FoliageSpawnerVolume>();

        if (spawner == null) return null;

        if (spawner.spawnParent != null)
            return spawner.spawnParent;

        var so = new SerializedObject(spawner);
        SerializedProperty containerProp = so.FindProperty("_spawnContainer");
        if (containerProp != null && containerProp.objectReferenceValue != null)
            return (Transform)containerProp.objectReferenceValue;

        return null;
    }
}
