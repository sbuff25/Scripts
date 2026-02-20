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

        float cameraFOV = Camera.main != null ? Camera.main.fieldOfView : 60f;

        // Collect spawned instance roots — these are either:
        // - Prefab instances with LODGroups (treat as one instance with multiple LOD levels)
        // - Simple objects with MeshFilter/MeshRenderer (single LOD)
        var instanceRoots = new List<Transform>();
        CollectInstanceRoots(spawnRoot, instanceRoots);

        foreach (Transform instanceRoot in instanceRoots)
        {
            // Get prefab root for prototype keying
            // See: https://docs.unity3d.com/ScriptReference/PrefabUtility.GetCorrespondingObjectFromSource.html
            GameObject prefabRoot = PrefabUtility.GetCorrespondingObjectFromOriginalSource(instanceRoot.gameObject);
            string protoKey = GetPrototypeKey(instanceRoot.gameObject, prefabRoot);

            if (!protoMap.TryGetValue(protoKey, out int protoIndex))
            {
                protoIndex = protoList.Count;
                protoMap[protoKey] = protoIndex;

                FoliageRenderer.FoliagePrototype proto = BuildPrototype(
                    instanceRoot.gameObject, prefabRoot, cameraFOV, renderer.maxRenderDistance);
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

        EditorUtility.SetDirty(renderer);

        // Destroy originals
        Undo.RegisterFullObjectHierarchyUndo(spawnRoot.gameObject, "Capture Foliage (Destroy Originals)");
        for (int i = spawnRoot.childCount - 1; i >= 0; i--)
            DestroyImmediate(spawnRoot.GetChild(i).gameObject);

        Debug.Log($"[FoliageRenderer] Captured {instanceList.Count} instances across {protoList.Count} prototypes.");
    }

    /// <summary>
    /// Collects spawned instance root transforms. Each collected transform represents
    /// one foliage instance. Objects with LODGroups are collected as-is (their children
    /// are LOD levels, not separate instances). Simple MeshRenderer objects are collected
    /// directly. Empty group objects (categories) are recursed into.
    /// </summary>
    private void CollectInstanceRoots(Transform root, List<Transform> results)
    {
        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);

            // Has LODGroup → this is one instance with multiple LOD levels
            if (child.GetComponent<LODGroup>() != null)
            {
                results.Add(child);
            }
            // Has MeshRenderer but no LODGroup → simple single-LOD instance
            else if (child.GetComponent<MeshRenderer>() != null)
            {
                results.Add(child);
            }
            // No renderer, no LODGroup → category group, recurse
            else if (child.childCount > 0)
            {
                CollectInstanceRoots(child, results);
            }
        }
    }

    /// <summary>
    /// Returns a stable key for grouping instances by prototype.
    /// Uses the prefab original source for consistency.
    /// </summary>
    private string GetPrototypeKey(GameObject sceneInstance, GameObject prefabRoot)
    {
        if (prefabRoot != null)
            return prefabRoot.GetInstanceID().ToString();

        // Fallback: mesh + material identity from the first renderer found
        MeshRenderer mr = sceneInstance.GetComponent<MeshRenderer>();
        MeshFilter mf = sceneInstance.GetComponent<MeshFilter>();
        if (mr == null) mr = sceneInstance.GetComponentInChildren<MeshRenderer>();
        if (mf == null) mf = sceneInstance.GetComponentInChildren<MeshFilter>();

        int meshId = mf != null && mf.sharedMesh != null ? mf.sharedMesh.GetInstanceID() : 0;
        int matId = mr != null && mr.sharedMaterial != null ? mr.sharedMaterial.GetInstanceID() : 0;
        return $"{meshId}_{matId}";
    }

    /// <summary>
    /// Builds a prototype from a scene instance. Checks the scene instance first
    /// for a LODGroup (instantiated prefabs keep their LODGroups), then falls back
    /// to the prefab source, then to a single-LOD prototype.
    /// </summary>
    private FoliageRenderer.FoliagePrototype BuildPrototype(
        GameObject sceneInstance, GameObject prefabRoot, float cameraFOV, float maxDist)
    {
        // Check scene instance for LODGroup (instantiated prefabs have it)
        LODGroup lodGroup = sceneInstance.GetComponent<LODGroup>();

        if (lodGroup != null)
            return BuildPrototypeFromLODGroup(lodGroup, cameraFOV, maxDist);

        // Check prefab source for LODGroup
        if (prefabRoot != null)
        {
            LODGroup prefabLOD = prefabRoot.GetComponent<LODGroup>();
            if (prefabLOD != null)
                return BuildPrototypeFromLODGroup(prefabLOD, cameraFOV, maxDist);
        }

        // No LODGroup — single LOD
        MeshFilter mf = sceneInstance.GetComponent<MeshFilter>();
        MeshRenderer mr = sceneInstance.GetComponent<MeshRenderer>();

        string name = prefabRoot != null ? prefabRoot.name : sceneInstance.name;

        return new FoliageRenderer.FoliagePrototype
        {
            name = name,
            lodMeshes = new Mesh[] { mf.sharedMesh },
            lodMaterials = new Material[] { mr.sharedMaterial },
            lodDistancesSq = new float[] { maxDist * maxDist }
        };
    }

    /// <summary>
    /// Extracts per-LOD mesh, material, and distance data from a LODGroup.
    /// See: https://docs.unity3d.com/ScriptReference/LODGroup.GetLODs.html
    /// </summary>
    private FoliageRenderer.FoliagePrototype BuildPrototypeFromLODGroup(
        LODGroup lodGroup, float cameraFOV, float maxDist)
    {
        LOD[] lods = lodGroup.GetLODs();
        float[] distancesSq = FoliageRenderer.ConvertLODDistances(lodGroup, cameraFOV, maxDist);

        var meshes = new List<Mesh>();
        var materials = new List<Material>();
        var distances = new List<float>();

        for (int i = 0; i < lods.Length; i++)
        {
            Renderer[] renderers = lods[i].renderers;
            if (renderers == null || renderers.Length == 0) continue;

            // Use the first renderer with a valid mesh
            Mesh mesh = null;
            Material mat = null;
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    mesh = mf.sharedMesh;
                    mat = r.sharedMaterial;
                    break;
                }
            }

            if (mesh == null) continue;

            meshes.Add(mesh);
            materials.Add(mat);
            distances.Add(distancesSq[i]);
        }

        // Clamp last distance to maxRenderDistance
        if (distances.Count > 0)
            distances[distances.Count - 1] = maxDist * maxDist;

        return new FoliageRenderer.FoliagePrototype
        {
            name = lodGroup.gameObject.name,
            lodMeshes = meshes.ToArray(),
            lodMaterials = materials.ToArray(),
            lodDistancesSq = distances.ToArray()
        };
    }

    /// <summary>
    /// Finds the spawn root by looking for a FoliageSpawnerVolume and resolving
    /// its spawn parent or auto-created container.
    /// </summary>
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
