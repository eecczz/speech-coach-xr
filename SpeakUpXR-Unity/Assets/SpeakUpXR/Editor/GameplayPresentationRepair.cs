using System.IO;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// One-time, non-destructive scene repair: leaves only one bottom dialogue container
/// during gameplay and moves the first-person camera in front of the face mesh.
/// </summary>
[InitializeOnLoad]
public static class GameplayPresentationRepair
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private const string MarkerPath = "Assets/SpeakUpXR/UI/gameplay-presentation-repaired-v1.txt";
    private static bool _running;

    static GameplayPresentationRepair() => EditorApplication.delayCall += TryRepair;

    [MenuItem("SpeakUpXR/Repair Gameplay HUD And First-Person Camera")]
    public static void RepairNow() => Repair();

    private static void TryRepair()
    {
        if (_running || File.Exists(MarkerPath) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (File.Exists(ScenePath)) Repair();
    }

    private static void Repair()
    {
        if (_running) return;
        _running = true;
        try
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            RepairHud();
            RepairCamera();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath) ?? "Assets/SpeakUpXR/UI");
            File.WriteAllText(MarkerPath,
                "Gameplay HUD repaired: only DialogueBox_BOTTOM_ONLY has a background.\n" +
                "Status is plain text. First-person camera is positioned in front of the face mesh.\n");
            AssetDatabase.ImportAsset(MarkerPath);
            AssetDatabase.SaveAssets();
            Debug.Log("[SpeakUpXR] Removed full-screen gameplay UI containers; kept only the bottom dialogue box and moved the head camera in front of the face.");
        }
        finally { _running = false; }
    }

    private static void RepairHud()
    {
        var hud = Object.FindFirstObjectByType<InterviewHud>(FindObjectsInactive.Include);
        if (!hud) return;

        var dialogueBox = hud.transform.Find("DialogueBox_BOTTOM_ONLY") as RectTransform;
        if (!dialogueBox)
        {
            dialogueBox = new GameObject("DialogueBox_BOTTOM_ONLY", typeof(RectTransform)).GetComponent<RectTransform>();
            dialogueBox.SetParent(hud.transform, false);
        }
        dialogueBox.anchorMin = dialogueBox.anchorMax = new Vector2(0.5f, 0f);
        dialogueBox.pivot = new Vector2(0.5f, 0f);
        dialogueBox.anchoredPosition = new Vector2(0f, 28f);
        dialogueBox.sizeDelta = new Vector2(1160f, 230f);

        // Preserve the status text, then remove its former colored pill container.
        Image oldPill = hud.StatusPill;
        if (hud.StatusText) hud.StatusText.transform.SetParent(dialogueBox, false);
        hud.StatusPill = null;
        if (oldPill) Object.DestroyImmediate(oldPill.gameObject);

        Image background = hud.GetComponentsInChildren<Image>(true)
            .FirstOrDefault(value => value && value.gameObject.name.StartsWith("Background"));
        if (!background)
            background = new GameObject("Background_DIALOGUE_ONLY", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image))
                .GetComponent<Image>();
        background.gameObject.name = "Background_DIALOGUE_ONLY";
        background.transform.SetParent(dialogueBox, false);
        background.transform.SetAsFirstSibling();
        background.color = new Color(0.035f, 0.045f, 0.065f, 0.88f);
        Stretch(background.rectTransform);
        var outline = background.GetComponent<Outline>() ?? background.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0.58f, 0.66f, 0.76f, 0.72f);
        outline.effectDistance = new Vector2(2f, -2f);

        // No other gameplay Image/container is allowed under this HUD.
        foreach (Image image in hud.GetComponentsInChildren<Image>(true))
            if (image && image != background) Object.DestroyImmediate(image.gameObject);

        MoveText(hud.SpeakerText, dialogueBox, new Vector2(30f, -20f), new Vector2(760f, 36f), 24, FontStyle.Bold);
        MoveText(hud.QuestionText, dialogueBox, new Vector2(30f, -66f), new Vector2(1100f, 100f), 36, FontStyle.Normal);
        MoveText(hud.InterimText, dialogueBox, new Vector2(30f, -180f), new Vector2(1100f, 34f), 21, FontStyle.Italic);
        MoveText(hud.StatusText, dialogueBox, new Vector2(-26f, -20f), new Vector2(330f, 34f), 20, FontStyle.Bold);
        if (hud.StatusText)
        {
            hud.StatusText.alignment = TextAnchor.UpperRight;
            hud.StatusText.rectTransform.anchorMin = hud.StatusText.rectTransform.anchorMax = new Vector2(1f, 1f);
            hud.StatusText.rectTransform.pivot = new Vector2(1f, 1f);
            hud.StatusText.color = new Color(0.43f, 0.66f, 1f);
        }

        var adaptive = hud.GetComponent<AdaptiveWorldCanvas>();
        if (adaptive)
        {
            adaptive.DesktopAnchor = adaptive.DesktopPivot = new Vector2(0.5f, 0f);
            adaptive.DesktopPosition = Vector2.zero;
            adaptive.DesktopSize = new Vector2(1920f, 1080f);
            adaptive.AttachToHeadInXr = true;
            adaptive.XrLocalPosition = new Vector3(0f, -0.31f, 0.78f);
            adaptive.XrWorldScale = 0.00072f;
            EditorUtility.SetDirty(adaptive);
        }
        EditorUtility.SetDirty(hud);
    }

    private static void RepairCamera()
    {
        var player = Object.FindFirstObjectByType<FirstPersonAvatarController>(FindObjectsInactive.Include);
        if (!player) return;
        player.CameraAtHeadOffset = new Vector3(0f, 0.03f, 0.18f);
        if (player.HeadCamera) player.HeadCamera.nearClipPlane = 0.05f;
        EditorUtility.SetDirty(player);
    }

    private static void MoveText(Text text, RectTransform parent, Vector2 position, Vector2 size, int fontSize, FontStyle style)
    {
        if (!text) return;
        text.transform.SetParent(parent, false);
        text.rectTransform.anchorMin = text.rectTransform.anchorMax = new Vector2(0f, 1f);
        text.rectTransform.pivot = new Vector2(0f, 1f);
        text.rectTransform.anchoredPosition = position;
        text.rectTransform.sizeDelta = size;
        text.fontSize = fontSize;
        text.fontStyle = style;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }
}
