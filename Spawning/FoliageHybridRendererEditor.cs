using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[CustomEditor(typeof(FoliageHybridRenderer))]
public class FoliageHybridRendererEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var renderer = (FoliageHybridRenderer)target;

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Capture", EditorStyles.boldLabel);

        // Stats
        int protoCount = renderer.Prototypes?.Count ?? 0;
        int instCount = renderer.Instances?.Count ?? 0;
        EditorGUILayout.HelpBox(
            $"Prototypes: {protoCount}    Instances: {instCount}",
            instCount > 0 ? MessageType.Info : MessageType.None);

        // Per-prototype breakdown
        if (protoCount > 0 && instCount > 0)
        {
            var counts = new int[protoCount];
            for (int i = 0; i < instCount; i++)
            {
                int pi = renderer.Instances[i].prototypeIndex;
                if (pi >= 0 && pi < protoCount) counts[pi]++;
            }

            for (int p = 0; p < protoCount; p++)
            {
                var proto = renderer.Prototypes[p];
                string prefabName = proto.prefab != null
                    ? proto.prefab.name
                    : $"Prototype {p}";
                int lodCount = proto.lods != null ? proto.lods.Length : 0;
                EditorGUILayout.LabelField($"  {prefabName}", $"{counts[p]} instances, {lodCount} LODs");
            }
        }

        EditorGUILayout.Space(4);

        // Validation warnings
        DrawValidationWarnings(renderer);

        // Buttons
        EditorGUILayout.Space(4);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Capture Scene Instances", GUILayout.Height(28)))
            {
                CaptureInstances(renderer);
            }

            GUI.enabled = instCount > 0;
            if (GUILayout.Button("Clear Data", GUILayout.Height(28), GUILayout.Width(100)))
            {
                if (EditorUtility.DisplayDialog(
                    "Clear Hybrid Data",
                    $"Remove all {instCount} captured instances and {protoCount} prototypes?",
                    "Clear", "Cancel"))
                {
                    Undo.RecordObject(renderer, "Clear Hybrid Data");
                    renderer.Prototypes.Clear();
                    renderer.Instances.Clear();
                    EditorUtility.SetDirty(renderer);
                }
            }
            GUI.enabled = true;
        }
    }

    private void DrawValidationWarnings(FoliageHybridRenderer renderer)
    {
        // Check for children that look like combined meshes
        int childCount = renderer.transform.childCount;
        if (childCount > 0)
        {
            bool hasCombined = false;
            for (int i = 0; i < childCount; i++)
            {
                var child = renderer.transform.GetChild(i);
                if (child.name.Contains("Combined"))
                {
                    hasCombined = true;
                    break;
                }
            }
            if (hasCombined)
            {
                EditorGUILayout.HelpBox(
                    "Combined mesh objects detected in children. " +
                    "Disable 'combineMeshes' on foliage types and re-spawn before capturing — " +
                    "combined meshes lose per-instance data.",
                    MessageType.Warning);
            }
        }

        // Check for missing prefab references
        if (renderer.Prototypes != null)
        {
            for (int p = 0; p < renderer.Prototypes.Count; p++)
            {
                if (renderer.Prototypes[p].prefab == null)
                {
                    EditorGUILayout.HelpBox(
                        $"Prototype {p} has no prefab reference. " +
                        "Promoted GameObjects won't be created for these instances. " +
                        "Assign a prefab manually or re-capture from prefab instances.",
                        MessageType.Warning);
                    break;
                }
            }
        }
    }

    //------------------------------------------------------------------
    // Capture logic
    //------------------------------------------------------------------

    /// <summary>
    /// Data collected per spawned instance during scanning.
    /// </summary>
    private struct CapturedInstance
    {
        public int prototypeIndex;
        public Vector3 position;
        public Quaternion rotation;
        public float scale;
    }

    private void CaptureInstances(FoliageHybridRenderer renderer)
    {
        Transform captureRoot = FindSpawnRoot(renderer);

        var prototypes = new List<FoliageHybridRenderer.HybridPrototype>();
        var capturedInstances = new List<CapturedInstance>();

        // Maps prefab → prototype index (group all instances of the same prefab)
        var prefabToProto = new Dictionary<GameObject, int>();

        ScanChildrenForCapture(captureRoot, prototypes, capturedInstances, prefabToProto);

        if (capturedInstances.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "Capture",
                "No mesh instances found.\n\n" +
                "Make sure you've spawned foliage (with combineMeshes OFF).\n\n" +
                "The capture scanned: " + captureRoot.name +
                " (" + captureRoot.childCount + " children)",
                "OK");
            return;
        }

        // Confirm
        if (!EditorUtility.DisplayDialog(
            "Capture Scene Instances",
            $"Found {capturedInstances.Count} instances across {prototypes.Count} prototypes.\n\n" +
            "This will:\n" +
            "1. Store all instance data on this component\n" +
            "2. Destroy the original GameObjects\n\n" +
            "Continue?",
            "Capture", "Cancel"))
        {
            return;
        }

        Undo.RecordObject(renderer, "Capture Hybrid Instances");

        // Convert to HybridInstance list
        var instances = new List<FoliageHybridRenderer.HybridInstance>(capturedInstances.Count);
        for (int i = 0; i < capturedInstances.Count; i++)
        {
            instances.Add(new FoliageHybridRenderer.HybridInstance
            {
                prototypeIndex = capturedInstances[i].prototypeIndex,
                position = capturedInstances[i].position,
                rotation = capturedInstances[i].rotation,
                uniformScale = capturedInstances[i].scale,
            });
        }

        // Store data
        renderer.Prototypes.Clear();
        renderer.Prototypes.AddRange(prototypes);
        renderer.Instances.Clear();
        renderer.Instances.AddRange(instances);

        // Destroy original GameObjects from the capture root
        Transform destroyRoot = FindSpawnRoot(renderer);
        var toDestroy = new List<GameObject>();
        for (int i = destroyRoot.childCount - 1; i >= 0; i--)
            toDestroy.Add(destroyRoot.GetChild(i).gameObject);

        for (int i = 0; i < toDestroy.Count; i++)
            DestroyImmediate(toDestroy[i]);

        // If capture root was a separate hidden container, destroy it too
        if (destroyRoot != renderer.transform)
            DestroyImmediate(destroyRoot.gameObject);

        EditorUtility.SetDirty(renderer);

        // Log summary
        string summary = $"[FoliageHybridRenderer] Captured {instances.Count} instances across {prototypes.Count} prototypes:";
        for (int p = 0; p < prototypes.Count; p++)
        {
            string name = prototypes[p].prefab != null ? prototypes[p].prefab.name : $"Proto {p}";
            summary += $"\n  {name}: {prototypes[p].lods.Length} LODs";
            for (int l = 0; l < prototypes[p].lods.Length; l++)
            {
                float dist = l < prototypes[p].lodDistances.Length
                    ? prototypes[p].lodDistances[l] : float.PositiveInfinity;
                string meshName = prototypes[p].lods[l].mesh != null ? prototypes[p].lods[l].mesh.name : "null";
                summary += $"\n    LOD{l}: {meshName} (up to {dist:F0} units)";
            }
        }
        Debug.Log(summary);
    }

    //------------------------------------------------------------------
    // Spawn root detection
    //------------------------------------------------------------------

    private Transform FindSpawnRoot(FoliageHybridRenderer renderer)
    {
        var spawner = renderer.GetComponent<FoliageSpawnerVolume>();
        if (spawner == null)
            spawner = renderer.GetComponentInParent<FoliageSpawnerVolume>();
        if (spawner == null && renderer.transform.parent != null)
            spawner = renderer.transform.parent.GetComponentInChildren<FoliageSpawnerVolume>();

        if (spawner != null)
        {
            if (spawner.spawnParent != null)
                return spawner.spawnParent;

            string hiddenName = spawner.gameObject.name + "_Foliage";
            Transform searchParent = spawner.transform.parent != null
                ? spawner.transform.parent
                : spawner.transform;

            for (int i = 0; i < searchParent.childCount; i++)
            {
                var child = searchParent.GetChild(i);
                if (child.name == hiddenName)
                    return child;
            }

            var allRoots = searchParent.gameObject.scene.GetRootGameObjects();
            foreach (var root in allRoots)
            {
                if (root.name == hiddenName)
                    return root.transform;
            }
        }

        return renderer.transform;
    }

    //------------------------------------------------------------------
    // Child scanning with LOD capture
    //------------------------------------------------------------------

    private void ScanChildrenForCapture(
        Transform root,
        List<FoliageHybridRenderer.HybridPrototype> prototypes,
        List<CapturedInstance> instances,
        Dictionary<GameObject, int> prefabToProto)
    {
        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);

            // If this object has an LODGroup, capture all LOD levels
            var lodGroup = child.GetComponent<LODGroup>();
            if (lodGroup != null)
            {
                CaptureFromLODGroup(child, lodGroup, prototypes, instances, prefabToProto);
                continue;
            }

            // Single-mesh object (no LODGroup)
            var mf = child.GetComponent<MeshFilter>();
            var mr = child.GetComponent<MeshRenderer>();

            if (mf != null && mr != null && mf.sharedMesh != null)
            {
                var prefab = PrefabUtility.GetCorrespondingObjectFromSource(child.gameObject);
                int protoIdx = GetOrCreatePrototype(prefab, mf, mr, prototypes, prefabToProto);

                instances.Add(new CapturedInstance
                {
                    prototypeIndex = protoIdx,
                    position = child.position,
                    rotation = child.rotation,
                    scale = child.lossyScale.x,
                });
            }

            // Recurse into category groups
            if (child.childCount > 0)
                ScanChildrenForCapture(child, prototypes, instances, prefabToProto);
        }
    }

    /// <summary>
    /// Gets or creates a prototype for a single-mesh object (no LODGroup).
    /// Creates a prototype with a single LOD entry.
    /// </summary>
    private int GetOrCreatePrototype(
        GameObject prefab,
        MeshFilter mf, MeshRenderer mr,
        List<FoliageHybridRenderer.HybridPrototype> prototypes,
        Dictionary<GameObject, int> prefabToProto)
    {
        // Key by prefab if available, otherwise create a new prototype per mesh+material combo
        if (prefab != null && prefabToProto.TryGetValue(prefab, out int existing))
            return existing;

        int idx = prototypes.Count;
        prototypes.Add(new FoliageHybridRenderer.HybridPrototype
        {
            prefab = prefab,
            lods = new FoliageHybridRenderer.LODMesh[]
            {
                new FoliageHybridRenderer.LODMesh
                {
                    mesh = mf.sharedMesh,
                    materials = mr.sharedMaterials,
                }
            },
            lodDistances = new float[] { float.MaxValue },
            shadowCasting = mr.shadowCastingMode,
        });

        if (prefab != null)
            prefabToProto[prefab] = idx;

        return idx;
    }

    /// <summary>
    /// Captures ALL LOD levels from a prefab with an LODGroup.
    /// Computes distance thresholds from the LODGroup's screen-relative heights.
    /// </summary>
    private void CaptureFromLODGroup(
        Transform instance, LODGroup lodGroup,
        List<FoliageHybridRenderer.HybridPrototype> prototypes,
        List<CapturedInstance> instances,
        Dictionary<GameObject, int> prefabToProto)
    {
        var prefab = PrefabUtility.GetCorrespondingObjectFromSource(instance.gameObject);

        // If we've already built a prototype for this prefab, just add the instance
        if (prefab != null && prefabToProto.TryGetValue(prefab, out int existingIdx))
        {
            instances.Add(new CapturedInstance
            {
                prototypeIndex = existingIdx,
                position = instance.position,
                rotation = instance.rotation,
                scale = instance.lossyScale.x,
            });
            return;
        }

        var lods = lodGroup.GetLODs();
        if (lods.Length == 0) return;

        // Build LODMesh array and distance thresholds
        var lodMeshes = new List<FoliageHybridRenderer.LODMesh>();
        var lodDistances = new List<float>();

        // Compute distances from screen-relative heights
        // Formula: distance = objectSize / (2 * screenHeight * tan(fov/2))
        float objectSize = lodGroup.size;
        float referenceFov = 60f * Mathf.Deg2Rad;
        float tanHalfFov = Mathf.Tan(referenceFov * 0.5f);

        ShadowCastingMode shadowMode = ShadowCastingMode.On;

        for (int l = 0; l < lods.Length; l++)
        {
            var lodRenderers = lods[l].renderers;
            if (lodRenderers == null || lodRenderers.Length == 0) continue;

            // Get the first valid MeshRenderer in this LOD
            Mesh lodMesh = null;
            Material[] lodMats = null;

            for (int r = 0; r < lodRenderers.Length; r++)
            {
                var rend = lodRenderers[r] as MeshRenderer;
                if (rend == null) continue;

                var mf = rend.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                lodMesh = mf.sharedMesh;
                lodMats = rend.sharedMaterials;
                if (l == 0) shadowMode = rend.shadowCastingMode;
                break;
            }

            if (lodMesh == null) continue;

            lodMeshes.Add(new FoliageHybridRenderer.LODMesh
            {
                mesh = lodMesh,
                materials = lodMats,
            });

            // Compute max distance for this LOD
            float screenHeight = lods[l].screenRelativeTransitionHeight;
            float maxDist;
            if (screenHeight > 0.001f)
                maxDist = objectSize / (2f * screenHeight * tanHalfFov);
            else
                maxDist = float.MaxValue; // last LOD — render to infinity

            lodDistances.Add(maxDist);
        }

        if (lodMeshes.Count == 0) return;

        // Make sure last LOD goes to infinity
        lodDistances[lodDistances.Count - 1] = float.MaxValue;

        int protoIdx = prototypes.Count;
        prototypes.Add(new FoliageHybridRenderer.HybridPrototype
        {
            prefab = prefab,
            lods = lodMeshes.ToArray(),
            lodDistances = lodDistances.ToArray(),
            shadowCasting = shadowMode,
        });

        if (prefab != null)
            prefabToProto[prefab] = protoIdx;

        instances.Add(new CapturedInstance
        {
            prototypeIndex = protoIdx,
            position = instance.position,
            rotation = instance.rotation,
            scale = instance.lossyScale.x,
        });
    }
}
