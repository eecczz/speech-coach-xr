using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class InterviewSceneRepair
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private const string RepairMarkerPath = "Assets/ThirdParty/Sketchfab/playability-fixed-v4.txt";

    [InitializeOnLoadMethod]
    private static void QueueRepairOnce()
    {
        if (System.IO.File.Exists(RepairMarkerPath)) return;
        EditorApplication.update -= PollUntilEditable;
        EditorApplication.update += PollUntilEditable;
    }

    private static void PollUntilEditable()
    {
        if (System.IO.File.Exists(RepairMarkerPath))
        {
            EditorApplication.update -= PollUntilEditable;
            return;
        }
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            return;
        EditorApplication.update -= PollUntilEditable;
        TryAutomaticRepair();
    }

    private static void TryAutomaticRepair()
    {
        if (System.IO.File.Exists(RepairMarkerPath)) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            return;
        }

        ReinstallCharactersAndApply();
        System.IO.File.WriteAllText(RepairMarkerPath, "Desktop UI, seated presentation, and static-avatar motion fixes were applied.\n");
        AssetDatabase.ImportAsset(RepairMarkerPath);
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.delayCall += TryAutomaticRepair;
    }

    [MenuItem("SpeakUpXR/Fix Desktop UI And Seating")]
    public static void Apply()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var camera = Camera.main;
        if (camera) camera.transform.localPosition = new Vector3(0f, 1.65f, 0f);

        var hud = Object.FindFirstObjectByType<InterviewHud>(FindObjectsInactive.Include);
        if (hud)
        {
            hud.transform.position = new Vector3(0f, 1.2f, 1.25f);
            ConfigureCanvas(hud.gameObject, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -24f), new Vector2(1100f, 300f));
        }

        var report = Object.FindFirstObjectByType<InterviewReportView>(FindObjectsInactive.Include);
        if (report)
            ConfigureCanvas(report.gameObject, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1500f, 900f));

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[SpeakUpXR] Desktop UI and seated presentation fixes saved in Interview.unity.");
    }

    public static void ReinstallCharactersAndApply()
    {
        SketchfabCharacterInstaller.PlaceDownloadedCharacters();
        Apply();
    }

    private static void ConfigureCanvas(GameObject root, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
    {
        var adaptive = root.GetComponent<AdaptiveWorldCanvas>() ?? root.AddComponent<AdaptiveWorldCanvas>();
        adaptive.DesktopAnchor = anchor;
        adaptive.DesktopPivot = pivot;
        adaptive.DesktopPosition = position;
        adaptive.DesktopSize = size;
        if (!root.GetComponent<CanvasScaler>()) root.AddComponent<CanvasScaler>();
        EditorUtility.SetDirty(adaptive);
    }
}
