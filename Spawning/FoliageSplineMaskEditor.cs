using UnityEngine;
using UnityEngine.Splines;
using UnityEditor;

/// <summary>
/// Custom inspector for <see cref="FoliageSplineMask"/>.
/// Validates spline configuration and shows mode with color coding.
/// </summary>
/// See: https://docs.unity3d.com/ScriptReference/Editor.html
[CustomEditor(typeof(FoliageSplineMask))]
public class FoliageSplineMaskEditor : Editor
{
    public override void OnInspectorGUI()
    {
        FoliageSplineMask mask = (FoliageSplineMask)target;

        DrawDefaultInspector();

        EditorGUILayout.Space(5);

        // Resolve which SplineContainer is in use
        SplineContainer container = mask.splineContainer != null
            ? mask.splineContainer
            : mask.GetComponent<SplineContainer>();

        if (container == null)
        {
            EditorGUILayout.HelpBox(
                "No SplineContainer assigned or found on this GameObject. " +
                "Assign one in the Spline Container field, or add a SplineContainer component here.",
                MessageType.Error);
            return;
        }

        if (container.Splines == null || container.Splines.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "SplineContainer has no splines. Add at least one spline.",
                MessageType.Warning);
            return;
        }

        Spline spline = container[0];

        if (spline.Count < 2)
        {
            EditorGUILayout.HelpBox(
                "Spline needs at least 2 knots.",
                MessageType.Warning);
        }
        else if (!spline.Closed && mask.radius <= 0f)
        {
            EditorGUILayout.HelpBox(
                "Open spline with radius 0 will not match any points. " +
                "Increase the radius or close the spline.",
                MessageType.Warning);
        }
        else if (!spline.Closed)
        {
            EditorGUILayout.HelpBox(
                "Open spline — mask covers a corridor of radius " + mask.radius + " around the path.",
                MessageType.Info);
        }

        // Show mode with colored label
        EditorGUILayout.Space(5);
        Color prevColor = GUI.color;
        GUI.color = mask.mode == FoliageSplineMask.MaskMode.Include
            ? new Color(0.3f, 0.9f, 0.3f)
            : new Color(0.9f, 0.3f, 0.3f);
        EditorGUILayout.LabelField($"Mode: {mask.mode}", EditorStyles.boldLabel);
        GUI.color = prevColor;
    }
}
