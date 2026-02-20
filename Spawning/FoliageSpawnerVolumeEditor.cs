using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;

/// <summary>
/// Custom inspector for <see cref="FoliageSpawnerVolume"/>.
/// Adds Spawn/Clear buttons with Undo support.
/// See: https://docs.unity3d.com/ScriptReference/Editor.html
/// </summary>
[CustomEditor(typeof(FoliageSpawnerVolume))]
public class FoliageSpawnerVolumeEditor : Editor
{
    private bool _autoRespawn;

    public override void OnInspectorGUI()
    {
        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        bool changed = EditorGUI.EndChangeCheck();

        FoliageSpawnerVolume spawner = (FoliageSpawnerVolume)target;

        EditorGUILayout.Space(10);

        // Count spawned instances
        Transform spawnTarget = ResolveSpawnTarget(spawner);
        int count = spawnTarget != null ? spawnTarget.childCount : 0;
        EditorGUILayout.HelpBox($"Spawned instances: {count}", MessageType.Info);

        EditorGUILayout.Space(5);

        _autoRespawn = EditorGUILayout.Toggle("Auto Respawn", _autoRespawn);

        EditorGUILayout.Space(5);

        EditorGUILayout.BeginHorizontal();

        GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
        if (GUILayout.Button("Spawn", GUILayout.Height(30)))
        {
            RegisterUndo(spawner, "Spawn Foliage");
            spawner.Spawn();
            RegisterCreatedContainer(spawner);
        }

        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
        if (GUILayout.Button("Clear", GUILayout.Height(30)))
        {
            RegisterUndo(spawner, "Clear Foliage");
            spawner.Clear();
        }

        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();

        // Auto-respawn when any property changes
        if (_autoRespawn && changed)
        {
            RegisterUndo(spawner, "Auto Respawn Foliage");
            spawner.Spawn();
            RegisterCreatedContainer(spawner);
        }
    }

    private Transform ResolveSpawnTarget(FoliageSpawnerVolume spawner)
    {
        if (spawner.spawnParent != null)
            return spawner.spawnParent;

        SerializedProperty containerProp = serializedObject.FindProperty("_spawnContainer");
        if (containerProp != null && containerProp.objectReferenceValue != null)
            return (Transform)containerProp.objectReferenceValue;

        return spawner.transform;
    }

    private void RegisterUndo(FoliageSpawnerVolume spawner, string undoName)
    {
        Undo.RegisterFullObjectHierarchyUndo(spawner.gameObject, undoName);

        Transform target = ResolveSpawnTarget(spawner);
        if (target != null && target != spawner.transform)
            Undo.RegisterFullObjectHierarchyUndo(target.gameObject, undoName);
    }

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

/// <summary>
/// Menu utilities for cleaning up foliage artifacts.
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
        for (int i = go.transform.childCount - 1; i >= 0; i--)
            count += DestroyHiddenRecursive(go.transform.GetChild(i).gameObject);

        if ((go.hideFlags & HideFlags.HideInHierarchy) != 0 && go.name.EndsWith("_Foliage"))
        {
            Object.DestroyImmediate(go);
            count++;
        }
        return count;
    }
}
