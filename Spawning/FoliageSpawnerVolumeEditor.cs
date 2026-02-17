using UnityEngine;
using UnityEditor;

/// <summary>
/// Custom inspector for <see cref="FoliageSpawnerVolume"/>.
/// Adds Spawn/Clear buttons, auto-respawn toggle, and a slope
/// visualization debug mode that draws colored dots in the scene view.
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
        if (hasPriorities)
        {
            EditorGUILayout.HelpBox(
                "Spawn order is determined by Priority (highest first). " +
                "Types with equal priority spawn in list order.",
                MessageType.Info);
        }

        EditorGUILayout.Space(10);

        // Count current children
        Transform parent = spawner.spawnParent != null ? spawner.spawnParent : spawner.transform;
        int childCount = parent.childCount;
        EditorGUILayout.HelpBox($"Spawned instances: {childCount}", MessageType.Info);

        EditorGUILayout.Space(5);

        _autoRespawn = EditorGUILayout.Toggle("Auto Respawn", _autoRespawn);

        EditorGUILayout.Space(5);

        EditorGUILayout.BeginHorizontal();

        GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
        if (GUILayout.Button("Spawn Foliage", GUILayout.Height(30)))
        {
            Undo.RegisterFullObjectHierarchyUndo(
                spawner.spawnParent != null ? spawner.spawnParent.gameObject : spawner.gameObject,
                "Spawn Foliage");
            spawner.Spawn();
        }

        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
        if (GUILayout.Button("Clear All", GUILayout.Height(30)))
        {
            Undo.RegisterFullObjectHierarchyUndo(
                spawner.spawnParent != null ? spawner.spawnParent.gameObject : spawner.gameObject,
                "Clear Foliage");
            spawner.Clear();
        }

        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();

        // Auto-respawn when a property was modified and toggle is on
        if (_autoRespawn && changed)
        {
            Undo.RegisterFullObjectHierarchyUndo(
                spawner.spawnParent != null ? spawner.spawnParent.gameObject : spawner.gameObject,
                "Auto Respawn Foliage");
            spawner.Spawn();
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
        if (_showNoiseDebug && spawner.foliageTypes.Count > 0)
        {
            string[] typeNames = new string[spawner.foliageTypes.Count];
            for (int i = 0; i < typeNames.Length; i++)
            {
                FoliageType ft = spawner.foliageTypes[i];
                typeNames[i] = ft.prefab != null ? ft.prefab.name : $"Type {i} (no prefab)";
            }
            _noiseDebugTypeIndex = Mathf.Clamp(_noiseDebugTypeIndex, 0, typeNames.Length - 1);
            _noiseDebugTypeIndex = EditorGUILayout.Popup("Preview Type", _noiseDebugTypeIndex, typeNames);
        }

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
        if (!_showSlopeDebug && !_showNoiseDebug) return;

        FoliageSpawnerVolume spawner = (FoliageSpawnerVolume)target;
        Vector3 half = spawner.volumeSize * 0.5f;
        int res = _slopeResolution;

        // Resolve noise type for this frame
        FoliageType noiseType = null;
        if (_showNoiseDebug &&
            _noiseDebugTypeIndex >= 0 &&
            _noiseDebugTypeIndex < spawner.foliageTypes.Count)
        {
            FoliageType candidate = spawner.foliageTypes[_noiseDebugTypeIndex];
            if (candidate.noiseScale > 0f)
                noiseType = candidate;
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
                if (noiseType != null)
                {
                    // See: https://docs.unity3d.com/ScriptReference/Mathf.PerlinNoise.html
                    float nx = (worldOrigin.x + 10000f) * noiseType.noiseScale + noiseType.noiseOffset.x;
                    float nz = (worldOrigin.z + 10000f) * noiseType.noiseScale + noiseType.noiseOffset.y;
                    float noiseValue = Mathf.PerlinNoise(nx, nz);
                    bool accepted = noiseValue >= noiseType.noiseThreshold;

                    Color noiseColor = accepted
                        ? new Color(0f, 0.8f, 0f, 0.4f)
                        : new Color(0.8f, 0f, 0f, 0.4f);

                    Handles.color = noiseColor;
                    float offset = _showSlopeDebug ? 0.02f : 0.01f;
                    Handles.DrawSolidDisc(hit.point + hit.normal * offset, hit.normal, _dotSize * 0.8f);
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

        if (noiseType != null)
        {
            Rect rect = new Rect(10, legendY, 160, 50);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(15, legendY + 2, 150, 20), "Noise Debug", EditorStyles.boldLabel);
            GUI.color = new Color(0f, 0.8f, 0f);
            GUI.Label(new Rect(15, legendY + 22, 150, 16), "■ Accept");
            GUI.color = new Color(0.8f, 0f, 0f);
            GUI.Label(new Rect(15, legendY + 38, 150, 16), "■ Reject");
            GUI.color = Color.white;
        }

        Handles.EndGUI();
    }
}
