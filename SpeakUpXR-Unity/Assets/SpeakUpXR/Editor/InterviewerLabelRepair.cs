using System;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Migrates legacy personality-facing labels to ordinary interview roles and
/// makes the world-space nameplates readable from the candidate seat.
/// Character objects and their authored transforms are left untouched.
/// </summary>
[InitializeOnLoad]
public static class InterviewerLabelRepair
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";

    static InterviewerLabelRepair()
    {
        EditorApplication.delayCall += RepairIfNeeded;
    }

    [MenuItem("SpeakUpXR/Repair Interviewer Labels")]
    public static void RepairNow() => Repair(openScene: true);

    private static void RepairIfNeeded()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode) return;
        Repair(openScene: SceneManager.GetActiveScene().path != ScenePath);
    }

    private static void Repair(bool openScene)
    {
        if (openScene) EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var interviewers = UnityEngine.Object.FindObjectsByType<InterviewerController>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        bool changed = false;
        foreach (var interviewer in interviewers)
        {
            string label = LabelFor(interviewer.PersonaId);
            if (interviewer.DisplayName == label) continue;
            interviewer.DisplayName = label;
            EditorUtility.SetDirty(interviewer);
            changed = true;
        }
        var nameplates = UnityEngine.Object.FindObjectsByType<Canvas>(
            FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(canvas => canvas.name.StartsWith("NamePlate_", StringComparison.Ordinal));
        foreach (var nameplate in nameplates)
        {
            string label = LabelFor(ClosestPersona(nameplate.transform, interviewers));
            if (nameplate.name != "NamePlate_" + label) { nameplate.name = "NamePlate_" + label; changed = true; }
            if (nameplate.transform.localRotation != Quaternion.identity) { nameplate.transform.localRotation = Quaternion.identity; changed = true; }
            var text = nameplate.GetComponentInChildren<Text>(true);
            if (text && text.text != label) { text.text = label; EditorUtility.SetDirty(text); changed = true; }
            EditorUtility.SetDirty(nameplate.transform);
        }
        if (!changed) return;
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        Debug.Log("[SpeakUpXR] Interviewer labels changed to HR / Technical / Executive roles and nameplate mirroring was corrected.");
    }

    private static string ClosestPersona(Transform nameplate, InterviewerController[] interviewers)
    {
        if (interviewers.Length == 0) return "warm";
        return interviewers.OrderBy(value => Mathf.Abs(value.transform.position.x - nameplate.position.x)).First().PersonaId;
    }

    private static string LabelFor(string personaId) => personaId == "analytical" ? "기술 면접관" :
        personaId == "challenging" ? "임원 면접관" : "인사 면접관";
}
