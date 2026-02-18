using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;
using UnityEditor;

/// <summary>
/// Foliage cleanup utilities.
/// </summary>
public static class FoliageCleanup
{
    [MenuItem("Tools/Foliage/Destroy Hidden Foliage Containers")]
    public static void DestroyHiddenFoliageContainers()
    {
        int destroyed = 0;
        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            var scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            foreach (var root in scene.GetRootGameObjects())
                destroyed += DestroyHiddenRecursive(root);
        }

        if (destroyed > 0)
            Debug.Log($"[FoliageCleanup] Destroyed {destroyed} hidden foliage container(s).");
        else
            Debug.Log("[FoliageCleanup] No hidden foliage containers found.");
    }

    private static int DestroyHiddenRecursive(GameObject go)
    {
        int count = 0;
        // Check children first (iterate backwards since we may destroy)
        for (int i = go.transform.childCount - 1; i >= 0; i--)
            count += DestroyHiddenRecursive(go.transform.GetChild(i).gameObject);

        if ((go.hideFlags & HideFlags.HideInHierarchy) != 0 && go.name.EndsWith("_Foliage"))
        {
            Object.DestroyImmediate(go);
            count++;
        }
        return count;
    }

    [MenuItem("Tools/Foliage/Fix Material Keywords (Remove LOD Crossfade)")]
    public static void FixMaterialKeywords()
    {
        string[] guids = AssetDatabase.FindAssets("t:Material");
        int fixedCount = 0;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            // Skip package materials
            if (path.StartsWith("Packages/")) continue;

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null && mat.IsKeywordEnabled("LOD_FADE_CROSSFADE"))
            {
                mat.DisableKeyword("LOD_FADE_CROSSFADE");
                EditorUtility.SetDirty(mat);
                fixedCount++;
                Debug.Log($"  Fixed: {path}");
            }
        }

        if (fixedCount > 0)
        {
            AssetDatabase.SaveAssets();
            Debug.Log($"[FoliageCleanup] Removed LOD_FADE_CROSSFADE keyword from {fixedCount} material(s).");
        }
        else
        {
            Debug.Log("[FoliageCleanup] No materials had the LOD_FADE_CROSSFADE keyword set locally.");
        }
    }
}

/// <summary>
/// Custom inspector for <see cref="FoliageSpawnerVolume"/>.
/// Adds Spawn/Clear buttons, auto-respawn toggle, and slope/noise/mask
/// visualization debug modes that draw colored dots in the scene view.
/// </summary>
/// See: https://docs.unity3d.com/ScriptReference/Editor.html
/// See: https://docs.unity3d.com/ScriptReference/Editor.OnSceneGUI.html
/// See: https://docs.unity3d.com/ScriptReference/Handles.html
[CustomEditor(typeof(FoliageSpawnerVolume))]
public class FoliageSpawnerVolumeEditor : Editor
{
    private bool _autoRespawn;
    private bool _showSlopeDebug;
    private int _slopeResolution = 20;
    private float _dotSize = 0.3f;
    private bool _showNoiseDebug;
    private int _noiseDebugTypeIndex;
    private bool _showMaskDebug;

    public override void OnInspectorGUI()
    {
        FoliageSpawnerVolume spawner = (FoliageSpawnerVolume)target;

        // Detect any property change in the default inspector
        // See: https://docs.unity3d.com/ScriptReference/EditorGUI.BeginChangeCheck.html
        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        bool changed = EditorGUI.EndChangeCheck();

        // Priority info
        bool hasPriorities = false;
        foreach (FoliageType ft in spawner.foliageTypes)
        {
            if (ft.spawnPriority != 0) { hasPriorities = true; break; }
        }
        if (!hasPriorities)
        {
            foreach (FoliageCluster fc in spawner.foliageClusters)
            {
                if (fc.spawnPriority != 0) { hasPriorities = true; break; }
            }
        }
        if (hasPriorities)
        {
            EditorGUILayout.HelpBox(
                "Spawn order is determined by Priority (highest first). " +
                "Types and clusters with equal priority spawn in list order.",
                MessageType.Info);
        }

        // Cluster validation warnings
        foreach (FoliageCluster fc in spawner.foliageClusters)
        {
            if (fc.entries == null || fc.entries.Length == 0)
            {
                EditorGUILayout.HelpBox($"Cluster '{fc.name}' has no entries.", MessageType.Warning);
                continue;
            }
            foreach (ClusterEntry entry in fc.entries)
            {
                if (entry.maxRadius > fc.clusterRadius)
                    EditorGUILayout.HelpBox(
                        $"Cluster '{fc.name}': entry maxRadius ({entry.maxRadius}) exceeds clusterRadius ({fc.clusterRadius}).",
                        MessageType.Warning);
                if (entry.countMax < entry.countMin)
                    EditorGUILayout.HelpBox(
                        $"Cluster '{fc.name}': entry countMax ({entry.countMax}) < countMin ({entry.countMin}).",
                        MessageType.Warning);
            }
        }

        // Spline mask validation warnings
        if (spawner.splineMasks != null)
        {
            for (int i = 0; i < spawner.splineMasks.Count; i++)
            {
                FoliageSplineMask mask = spawner.splineMasks[i];
                if (mask == null)
                {
                    EditorGUILayout.HelpBox($"Spline Mask [{i}] is null.", MessageType.Warning);
                    continue;
                }
                SplineContainer container = mask.splineContainer != null
                    ? mask.splineContainer
                    : mask.GetComponent<SplineContainer>();
                if (container == null || container.Splines == null || container.Splines.Count == 0)
                {
                    EditorGUILayout.HelpBox($"Spline Mask '{mask.name}' has no spline.", MessageType.Warning);
                }
                else if (!container[0].Closed && mask.radius <= 0f)
                {
                    EditorGUILayout.HelpBox($"Spline Mask '{mask.name}': open spline with radius 0 won't match anything.", MessageType.Warning);
                }
            }
        }

        EditorGUILayout.Space(10);

        // Count spawned instances (grandchildren inside category groups)
        Transform spawnTarget = ResolveSpawnTarget(spawner);
        int categoryCount = spawnTarget != null ? spawnTarget.childCount : 0;
        int instanceCount = 0;
        for (int i = 0; i < categoryCount; i++)
            instanceCount += spawnTarget.GetChild(i).childCount;
        EditorGUILayout.HelpBox($"Spawned instances: {instanceCount}  ({categoryCount} categories)", MessageType.Info);

        EditorGUILayout.Space(5);

        _autoRespawn = EditorGUILayout.Toggle("Auto Respawn", _autoRespawn);

        EditorGUILayout.Space(5);

        EditorGUILayout.BeginHorizontal();

        GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
        if (GUILayout.Button("Spawn Foliage", GUILayout.Height(30)))
        {
            RegisterUndoForSpawn(spawner, "Spawn Foliage");
            spawner.Spawn();
            RegisterCreatedContainer(spawner);
        }

        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
        if (GUILayout.Button("Clear All", GUILayout.Height(30)))
        {
            RegisterUndoForSpawn(spawner, "Clear Foliage");
            spawner.Clear();
        }

        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();

        // Auto-respawn when a property was modified and toggle is on
        if (_autoRespawn && changed)
        {
            RegisterUndoForSpawn(spawner, "Auto Respawn Foliage");
            spawner.Spawn();
            RegisterCreatedContainer(spawner);
        }

        // ── Slope Debug ──
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Slope Visualization", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        _showSlopeDebug = EditorGUILayout.Toggle("Show Slope Debug", _showSlopeDebug);
        if (_showSlopeDebug)
        {
            _slopeResolution = EditorGUILayout.IntSlider("Grid Resolution", _slopeResolution, 5, 60);
            _dotSize = EditorGUILayout.Slider("Dot Size", _dotSize, 0.05f, 2f);
        }
        // ── Noise Debug ──
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Noise Visualization", EditorStyles.boldLabel);

        _showNoiseDebug = EditorGUILayout.Toggle("Show Noise Debug", _showNoiseDebug);
        int totalNoiseItems = spawner.foliageTypes.Count + spawner.foliageClusters.Count;
        if (_showNoiseDebug && totalNoiseItems > 0)
        {
            string[] noiseNames = new string[totalNoiseItems];
            for (int i = 0; i < spawner.foliageTypes.Count; i++)
            {
                FoliageType ft = spawner.foliageTypes[i];
                noiseNames[i] = ft.prefab != null ? ft.prefab.name : $"Type {i} (no prefab)";
            }
            for (int i = 0; i < spawner.foliageClusters.Count; i++)
            {
                FoliageCluster fc = spawner.foliageClusters[i];
                noiseNames[spawner.foliageTypes.Count + i] = $"[C] {fc.name}";
            }
            _noiseDebugTypeIndex = Mathf.Clamp(_noiseDebugTypeIndex, 0, noiseNames.Length - 1);
            _noiseDebugTypeIndex = EditorGUILayout.Popup("Preview Type", _noiseDebugTypeIndex, noiseNames);
        }

        // ── Mask Debug ──
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Mask Visualization", EditorStyles.boldLabel);

        _showMaskDebug = EditorGUILayout.Toggle("Show Mask Debug", _showMaskDebug);

        if (EditorGUI.EndChangeCheck())
        {
            // Force scene view repaint when debug settings change
            SceneView.RepaintAll();
        }
    }

    /// <summary>
    /// Draws slope and noise debug visualizations in the scene view.
    /// Slope: colored discs — green (0°) → yellow (45°) → red (90°).
    /// Noise: colored squares — green (accept) / red (reject) for the selected type.
    /// </summary>
    private void OnSceneGUI()
    {
        if (!_showSlopeDebug && !_showNoiseDebug && !_showMaskDebug) return;

        FoliageSpawnerVolume spawner = (FoliageSpawnerVolume)target;
        Vector3 half = spawner.volumeSize * 0.5f;
        int res = _slopeResolution;

        // Rebuild mask polygons for debug visualization
        bool hasMaskPreview = false;
        if (_showMaskDebug && spawner.splineMasks != null && spawner.splineMasks.Count > 0)
        {
            foreach (FoliageSplineMask mask in spawner.splineMasks)
            {
                if (mask != null && mask.gameObject.activeInHierarchy && mask.RebuildPolygon())
                    hasMaskPreview = true;
            }
        }

        // Resolve noise parameters for this frame (supports both types and clusters)
        float noiseScale = 0f, noiseThreshold = 0f;
        Vector2 noiseOffset = Vector2.zero;
        bool hasNoisePreview = false;

        if (_showNoiseDebug)
        {
            if (_noiseDebugTypeIndex >= 0 && _noiseDebugTypeIndex < spawner.foliageTypes.Count)
            {
                FoliageType ft = spawner.foliageTypes[_noiseDebugTypeIndex];
                if (ft.noiseScale > 0f)
                {
                    noiseScale = ft.noiseScale;
                    noiseThreshold = ft.noiseThreshold;
                    noiseOffset = ft.noiseOffset;
                    hasNoisePreview = true;
                }
            }
            else
            {
                int clusterIdx = _noiseDebugTypeIndex - spawner.foliageTypes.Count;
                if (clusterIdx >= 0 && clusterIdx < spawner.foliageClusters.Count)
                {
                    FoliageCluster fc = spawner.foliageClusters[clusterIdx];
                    if (fc.noiseScale > 0f)
                    {
                        noiseScale = fc.noiseScale;
                        noiseThreshold = fc.noiseThreshold;
                        noiseOffset = fc.noiseOffset;
                        hasNoisePreview = true;
                    }
                }
            }
        }

        for (int xi = 0; xi <= res; xi++)
        {
            for (int zi = 0; zi <= res; zi++)
            {
                float tx = (float)xi / res;
                float tz = (float)zi / res;
                float x = Mathf.Lerp(-half.x, half.x, tx);
                float z = Mathf.Lerp(-half.z, half.z, tz);

                Vector3 localOrigin = new Vector3(x, half.y, z);
                Vector3 worldOrigin = spawner.transform.TransformPoint(localOrigin);
                Vector3 rayDir = -spawner.transform.up;

                if (!Physics.Raycast(worldOrigin, rayDir, out RaycastHit hit, spawner.maxRayDistance, spawner.surfaceLayers))
                    continue;

                // Slope debug: colored disc by slope angle
                if (_showSlopeDebug)
                {
                    float slopeAngle = Vector3.Angle(hit.normal, Vector3.up);
                    float t = Mathf.Clamp01(slopeAngle / 90f);

                    Color slopeColor;
                    if (t < 0.5f)
                        slopeColor = Color.Lerp(Color.green, Color.yellow, t * 2f);
                    else
                        slopeColor = Color.Lerp(Color.yellow, Color.red, (t - 0.5f) * 2f);

                    Handles.color = slopeColor;
                    Handles.DrawSolidDisc(hit.point + hit.normal * 0.01f, hit.normal, _dotSize);
                }

                // Noise debug: green/red overlay for accept/reject
                if (hasNoisePreview)
                {
                    float nx = (worldOrigin.x + 10000f) * noiseScale + noiseOffset.x;
                    float nz = (worldOrigin.z + 10000f) * noiseScale + noiseOffset.y;
                    float noiseValue = Mathf.PerlinNoise(nx, nz);
                    bool accepted = noiseValue >= noiseThreshold;

                    Color noiseColor = accepted
                        ? new Color(0f, 0.8f, 0f, 0.4f)
                        : new Color(0.8f, 0f, 0f, 0.4f);

                    Handles.color = noiseColor;
                    float offset = _showSlopeDebug ? 0.02f : 0.01f;
                    Handles.DrawSolidDisc(hit.point + hit.normal * offset, hit.normal, _dotSize * 0.8f);
                }

                // Mask debug: purple overlay for masked-out points
                if (hasMaskPreview)
                {
                    bool masked = false;
                    bool hasInclude = false;
                    bool insideInclude = false;

                    foreach (FoliageSplineMask mask in spawner.splineMasks)
                    {
                        if (mask == null || !mask.IsValid) continue;

                        bool inside = mask.ContainsPointXZ(worldOrigin.x, worldOrigin.z);

                        if (mask.mode == FoliageSplineMask.MaskMode.Exclude && inside)
                        {
                            masked = true;
                            break;
                        }
                        if (mask.mode == FoliageSplineMask.MaskMode.Include)
                        {
                            hasInclude = true;
                            if (inside) insideInclude = true;
                        }
                    }

                    if (!masked && hasInclude && !insideInclude)
                        masked = true;

                    if (masked)
                    {
                        Handles.color = new Color(0.5f, 0f, 0.5f, 0.5f);
                        float offset = (_showSlopeDebug ? 0.01f : 0f) + (hasNoisePreview ? 0.01f : 0f) + 0.01f;
                        Handles.DrawSolidDisc(hit.point + hit.normal * offset, hit.normal, _dotSize * 0.6f);
                    }
                }
            }
        }

        // Draw legend in scene view
        Handles.BeginGUI();
        float legendY = 10f;

        if (_showSlopeDebug)
        {
            Rect rect = new Rect(10, legendY, 160, 70);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(15, legendY + 2, 150, 20), "Slope Debug", EditorStyles.boldLabel);
            GUI.color = Color.green;
            GUI.Label(new Rect(15, legendY + 22, 150, 16), "■ 0° (flat)");
            GUI.color = Color.yellow;
            GUI.Label(new Rect(15, legendY + 38, 150, 16), "■ 45°");
            GUI.color = Color.red;
            GUI.Label(new Rect(15, legendY + 54, 150, 16), "■ 90° (vertical)");
            GUI.color = Color.white;
            legendY += 80f;
        }

        if (hasNoisePreview)
        {
            Rect rect = new Rect(10, legendY, 160, 50);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(15, legendY + 2, 150, 20), "Noise Debug", EditorStyles.boldLabel);
            GUI.color = new Color(0f, 0.8f, 0f);
            GUI.Label(new Rect(15, legendY + 22, 150, 16), "■ Accept");
            GUI.color = new Color(0.8f, 0f, 0f);
            GUI.Label(new Rect(15, legendY + 38, 150, 16), "■ Reject");
            GUI.color = Color.white;
            legendY += 60f;
        }

        if (hasMaskPreview)
        {
            Rect rect = new Rect(10, legendY, 160, 36);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(15, legendY + 2, 150, 20), "Mask Debug", EditorStyles.boldLabel);
            GUI.color = new Color(0.5f, 0f, 0.5f);
            GUI.Label(new Rect(15, legendY + 22, 150, 16), "■ Masked out");
            GUI.color = Color.white;
        }

        Handles.EndGUI();
    }

    /// <summary>
    /// Resolves the spawn target by reading the serialized <c>_spawnContainer</c> field.
    /// Falls back to <c>spawnParent</c> or the spawner transform itself.
    /// </summary>
    private Transform ResolveSpawnTarget(FoliageSpawnerVolume spawner)
    {
        if (spawner.spawnParent != null)
            return spawner.spawnParent;

        SerializedProperty containerProp = serializedObject.FindProperty("_spawnContainer");
        if (containerProp != null && containerProp.objectReferenceValue != null)
            return (Transform)containerProp.objectReferenceValue;

        return spawner.transform;
    }

    /// <summary>
    /// Registers Undo for the spawner and its spawn container before spawn/clear.
    /// </summary>
    private void RegisterUndoForSpawn(FoliageSpawnerVolume spawner, string undoName)
    {
        Undo.RegisterFullObjectHierarchyUndo(spawner.gameObject, undoName);

        Transform target = ResolveSpawnTarget(spawner);
        if (target != null && target != spawner.transform)
            Undo.RegisterFullObjectHierarchyUndo(target.gameObject, undoName);
    }

    /// <summary>
    /// If Spawn() created a new container, register it for Undo.
    /// </summary>
    private void RegisterCreatedContainer(FoliageSpawnerVolume spawner)
    {
        serializedObject.Update();
        SerializedProperty containerProp = serializedObject.FindProperty("_spawnContainer");
        if (containerProp != null && containerProp.objectReferenceValue != null)
        {
            Transform container = (Transform)containerProp.objectReferenceValue;
            if (container != spawner.transform)
                Undo.RegisterCreatedObjectUndo(container.gameObject, "Create Spawn Container");
        }
    }
}
