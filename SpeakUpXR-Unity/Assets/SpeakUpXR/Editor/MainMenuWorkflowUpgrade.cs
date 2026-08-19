using System.IO;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[InitializeOnLoad]
public static class MainMenuWorkflowUpgrade
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/MainMenu.unity";
    private const string MarkerPath = "Assets/SpeakUpXR/UI/main-menu-required-setup-v1.txt";
    private static bool _running;

    static MainMenuWorkflowUpgrade() => EditorApplication.delayCall += TryUpgrade;

    [MenuItem("SpeakUpXR/Apply Required Main Menu Workflow")]
    public static void UpgradeNow() => Upgrade();

    private static void TryUpgrade()
    {
        if (_running || File.Exists(MarkerPath) || !File.Exists(ScenePath) ||
            EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
        Upgrade();
    }

    private static void Upgrade()
    {
        _running = true;
        try
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var controller = Object.FindFirstObjectByType<InterviewMenuController>(FindObjectsInactive.Include);
            if (!controller) throw new MissingReferenceException("MainMenu의 InterviewMenuController를 찾지 못했습니다.");

            Button previous = controller.LoadPreviousButton;
            if (!previous)
            {
                var card = GameObject.Find("SetupCard_EDIT_SIZE_HERE")?.transform;
                if (!card) throw new MissingReferenceException("MainMenu 설정 카드를 찾지 못했습니다.");

                var root = new GameObject("LoadPreviousSettingsButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                root.transform.SetParent(card, false);
                var rect = (RectTransform)root.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(42f, -910f);
                rect.sizeDelta = new Vector2(210f, 42f);

                var image = root.GetComponent<Image>();
                image.color = Color.white;
                image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd");
                image.type = Image.Type.Sliced;
                var outline = root.AddComponent<Outline>();
                outline.effectColor = Hex("DDE3EE");
                outline.effectDistance = new Vector2(1f, -1f);

                var labelObject = new GameObject("Label_이전 설정 불러오기", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                labelObject.transform.SetParent(root.transform, false);
                var labelRect = (RectTransform)labelObject.transform;
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
                var label = labelObject.GetComponent<Text>();
                label.text = "이전 설정 불러오기";
                label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                label.fontSize = 15;
                label.color = Hex("6B6880");
                label.alignment = TextAnchor.MiddleCenter;
                previous = root.GetComponent<Button>();
            }

            controller.LoadPreviousButton = previous;
            EditorUtility.SetDirty(controller);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath) ?? "Assets/SpeakUpXR/UI");
            File.WriteAllText(MarkerPath, "Main menu requires explicit interview setup; previous values load only on request.\n");
            AssetDatabase.ImportAsset(MarkerPath);
            AssetDatabase.SaveAssets();
            Debug.Log("[SpeakUpXR] Required main-menu setup workflow applied.");
        }
        finally { _running = false; }
    }

    private static Color Hex(string value)
    {
        ColorUtility.TryParseHtmlString("#" + value, out Color color);
        return color;
    }
}
