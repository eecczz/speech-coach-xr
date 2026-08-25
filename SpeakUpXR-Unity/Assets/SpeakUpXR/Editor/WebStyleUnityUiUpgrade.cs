using System.IO;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

[InitializeOnLoad]
public static class WebStyleUnityUiUpgrade
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private const string MarkerPath = "Assets/SpeakUpXR/UI/web-style-unity-ui-v2.txt";
    private static bool _running;
    private static readonly Color Card = Color.white;
    private static readonly Color Border = Hex("DDE3EE");
    private static readonly Color Ink = Hex("2E2C3A");
    private static readonly Color Muted = Hex("6B6880");
    private static readonly Color Soft = Hex("EEF3F8");

    static WebStyleUnityUiUpgrade() => EditorApplication.delayCall += TryUpgrade;
    [MenuItem("SpeakUpXR/Apply Web Style To Interview UI")]
    public static void UpgradeNow() => Upgrade();

    private static void TryUpgrade()
    {
        if (_running || File.Exists(MarkerPath) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (File.Exists(ScenePath)) Upgrade();
    }

    private static void Upgrade()
    {
        _running = true;
        try
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var hud = Object.FindFirstObjectByType<InterviewHud>(FindObjectsInactive.Include);
            if (hud)
            {
                var background = hud.GetComponentsInChildren<Image>(true).FirstOrDefault(value => value.name.Contains("Background_DIALOGUE"));
                if (background)
                {
                    background.color = new Color(Card.r, Card.g, Card.b, 0.96f);
                    var outline = background.GetComponent<Outline>() ?? background.gameObject.AddComponent<Outline>();
                    outline.effectColor = Border; outline.effectDistance = new Vector2(1f, -1f);
                }
                if (hud.SpeakerText) { hud.SpeakerText.color = Ink; hud.SpeakerText.fontStyle = FontStyle.Bold; }
                if (hud.QuestionText) hud.QuestionText.color = Ink;
                if (hud.InterimText) hud.InterimText.color = Muted;
                if (hud.StatusText) hud.StatusText.color = Muted;
                EditorUtility.SetDirty(hud);
            }
            var report = Object.FindFirstObjectByType<InterviewReportView>(FindObjectsInactive.Include);
            if (report)
            {
                var backdrop = report.GetComponentsInChildren<Image>(true).FirstOrDefault(value => value.name == "Backdrop");
                if (backdrop) backdrop.color = new Color(0.957f, 0.965f, 0.984f, 0.98f);
                if (report.TitleText) report.TitleText.color = Ink;
                if (report.SummaryText) report.SummaryText.color = Ink;
                if (report.DetailText) report.DetailText.color = Muted;
                var loadingText = report.GetComponentsInChildren<Text>(true).FirstOrDefault(value => value.name == "LoadingText");
                if (loadingText) loadingText.color = Ink;
                EditorUtility.SetDirty(report);
            }
            var panel = Object.FindFirstObjectByType<InterviewerPanel>(FindObjectsInactive.Include);
            if (panel)
            {
                foreach (Transform child in panel.transform)
                {
                    if (!child.name.StartsWith("NamePlate_", System.StringComparison.Ordinal)) continue;
                    var image = child.GetComponentInChildren<Image>(true);
                    var text = child.GetComponentInChildren<Text>(true);
                    if (image)
                    {
                        image.color = new Color(Card.r, Card.g, Card.b, 0.96f);
                        var outline = image.GetComponent<Outline>() ?? image.gameObject.AddComponent<Outline>();
                        outline.effectColor = Border; outline.effectDistance = new Vector2(1f, -1f);
                    }
                    if (text) text.color = Ink;
                }
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath) ?? "Assets/SpeakUpXR/UI");
            File.WriteAllText(MarkerPath, "SpeakUp web design tokens applied to dialogue and report UI.\n");
            AssetDatabase.ImportAsset(MarkerPath);
            AssetDatabase.SaveAssets();
            Debug.Log("[SpeakUpXR] Web design tokens applied to Interview dialogue and report UI.");
        }
        finally { _running = false; }
    }

    private static Color Hex(string value) { ColorUtility.TryParseHtmlString("#" + value, out Color color); return color; }
}
