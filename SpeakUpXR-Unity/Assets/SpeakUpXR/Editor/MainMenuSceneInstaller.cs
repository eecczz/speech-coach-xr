using System;
using System.IO;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[InitializeOnLoad]
public static class MainMenuSceneInstaller
{
    private const string MenuScenePath = "Assets/SpeakUpXR/Scenes/MainMenu.unity";
    private const string InterviewScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private static readonly Color Bg = Hex("F4F6FB");
    private static readonly Color Card = Color.white;
    private static readonly Color Soft = Hex("EEF3F8");
    private static readonly Color Border = Hex("DDE3EE");
    private static readonly Color Ink = Hex("2E2C3A");
    private static readonly Color Muted = Hex("6B6880");
    private static readonly Color Faint = Hex("9896A8");
    private static bool _running;

    static MainMenuSceneInstaller() => EditorApplication.delayCall += TryBuild;

    [MenuItem("SpeakUpXR/Build Web-Style Main Menu")]
    public static void BuildNow() => Build();

    private static void TryBuild()
    {
        if (_running || File.Exists(MenuScenePath) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode) return;
        Build();
    }

    private static void Build()
    {
        _running = true;
        try
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Bg;
            camera.orthographic = false;
            cameraObject.AddComponent<AudioListener>();
            cameraObject.AddComponent<TrackedPoseDriver>();

            var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            eventSystem.GetComponent<InputSystemUIInputModule>().deselectOnBackgroundClick = false;

            var canvasObject = new GameObject("WebStyleMainMenu_EDIT_LAYOUT_HERE", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 0.8f;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            Image page = Image("PageBackground", canvasObject.transform, Bg);
            Stretch(page.rectTransform);
            Image card = Image("SetupCard_EDIT_SIZE_HERE", page.transform, Card);
            Rect(card.rectTransform, Vector2.zero, new Vector2(900f, 1000f), new Vector2(0.5f, 0.5f));
            var outline = card.gameObject.AddComponent<Outline>();
            outline.effectColor = Border;
            outline.effectDistance = new Vector2(1f, -1f);

            Image badge = Image("BrandBadge", card.transform, Ink);
            RectTopLeft(badge.rectTransform, new Vector2(42f, -34f), new Vector2(48f, 48f));
            Text("S", badge.transform, 24, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
            Text brand = Text("SpeakUp", card.transform, 24, Ink, FontStyle.Bold, TextAnchor.UpperLeft);
            RectTopLeft(brand.rectTransform, new Vector2(104f, -34f), new Vector2(240f, 32f));
            Text sub = Text("AI 모의 면접", card.transform, 16, Muted, FontStyle.Normal, TextAnchor.UpperLeft);
            RectTopLeft(sub.rectTransform, new Vector2(104f, -65f), new Vector2(240f, 26f));

            Chip(card.transform, "1  면접 설정", new Vector2(42f, -108f), true);
            Chip(card.transform, "2  XR 면접", new Vector2(178f, -108f), false);
            Chip(card.transform, "3  코칭 리포트", new Vector2(302f, -108f), false);

            Text title = Text("어떤 면접을 연습할까요?", card.transform, 32, Ink, FontStyle.Bold, TextAnchor.UpperLeft);
            RectTopLeft(title.rectTransform, new Vector2(42f, -158f), new Vector2(800f, 44f));
            Text description = Text("상황과 주제를 바탕으로 AI 면접관이 첫 질문부터 꼬리질문까지 구성합니다.", card.transform, 17, Muted, FontStyle.Normal, TextAnchor.UpperLeft);
            RectTopLeft(description.rectTransform, new Vector2(42f, -202f), new Vector2(800f, 30f));

            var project = Field(card.transform, "프로젝트", "예: 상반기 공채 면접 준비", new Vector2(42f, -252f), 816f);
            var role = Field(card.transform, "지원 직무", "예: 백엔드 개발자", new Vector2(42f, -348f), 816f);
            var situation = Field(card.transform, "면접 상황", "예: 신입 기술 및 인성 종합 면접", new Vector2(42f, -444f), 816f);
            var topic = Field(card.transform, "핵심 주제", "예: 지원 동기, 프로젝트 경험, 협업 갈등", new Vector2(42f, -540f), 816f);

            Text("난이도", card.transform, 14, Faint, FontStyle.Bold, TextAnchor.UpperLeft, new Vector2(42f, -638f), new Vector2(380f, 22f));
            Text("질문 수", card.transform, 14, Faint, FontStyle.Bold, TextAnchor.UpperLeft, new Vector2(468f, -638f), new Vector2(390f, 22f));
            var difficulty = Dropdown(card.transform, "Difficulty", new[] { "쉬움", "보통", "어려움" }, new Vector2(42f, -666f), 390f);
            difficulty.value = 1;
            var questionCount = Dropdown(card.transform, "QuestionCount", new[] { "3 질문", "5 질문", "7 질문" }, new Vector2(468f, -666f), 390f);
            questionCount.value = 1;

            Text("집중 포커스", card.transform, 14, Faint, FontStyle.Bold, TextAnchor.UpperLeft, new Vector2(42f, -752f), new Vector2(400f, 22f));
            string[] focusNames = { "답변 구조", "시선 처리", "말 속도", "제스처", "자신감" };
            var toggles = new Toggle[focusNames.Length];
            float x = 42f;
            for (int i = 0; i < focusNames.Length; i++)
            {
                toggles[i] = FocusToggle(card.transform, focusNames[i], new Vector2(x, -782f));
                toggles[i].isOn = i is 0 or 1 or 4;
                x += i == 0 ? 132f : i == 1 ? 130f : i == 2 ? 112f : i == 3 ? 112f : 0f;
            }

            Text provider = Text("AI 면접관 연결 확인 중…", card.transform, 15, Muted, FontStyle.Normal, TextAnchor.MiddleLeft);
            RectTopLeft(provider.rectTransform, new Vector2(42f, -850f), new Vector2(540f, 38f));
            Button start = Button(card.transform, "면접 시작하기  →", new Vector2(606f, -842f), new Vector2(252f, 52f), Ink, Color.white);
            Button previous = Button(card.transform, "이전 설정 불러오기", new Vector2(42f, -910f), new Vector2(210f, 42f), Card, Muted);
            previous.gameObject.name = "LoadPreviousSettingsButton";
            var previousOutline = previous.gameObject.AddComponent<Outline>();
            previousOutline.effectColor = Border;
            previousOutline.effectDistance = new Vector2(1f, -1f);

            var system = new GameObject("MainMenuSystem");
            var api = system.AddComponent<CoachApi>();
            var controller = system.AddComponent<InterviewMenuController>();
            controller.Api = api;
            controller.ProjectInput = project;
            controller.JobRoleInput = role;
            controller.SituationInput = situation;
            controller.TopicInput = topic;
            controller.DifficultyDropdown = difficulty;
            controller.QuestionCountDropdown = questionCount;
            controller.FocusToggles = toggles;
            controller.ProviderStatusText = provider;
            controller.StartButton = start;
            controller.LoadPreviousButton = previous;

            Directory.CreateDirectory(Path.GetDirectoryName(MenuScenePath) ?? "Assets/SpeakUpXR/Scenes");
            EditorSceneManager.SaveScene(scene, MenuScenePath);
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(MenuScenePath, true),
                new EditorBuildSettingsScene(InterviewScenePath, true),
            };
            AssetDatabase.SaveAssets();
            Debug.Log("[SpeakUpXR] Web-style editable MainMenu scene created and set as build scene 0.");
        }
        finally { _running = false; }
    }

    private static InputField Field(Transform parent, string label, string placeholder, Vector2 position, float width)
    {
        Text(label, parent, 14, Faint, FontStyle.Bold, TextAnchor.UpperLeft, position, new Vector2(width, 22f));
        var root = new GameObject(label + "Input_EDIT", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField));
        root.transform.SetParent(parent, false);
        RectTopLeft((RectTransform)root.transform, position + new Vector2(0f, -28f), new Vector2(width, 52f));
        var image = root.GetComponent<Image>(); image.color = Hex("FAFBFF"); image.sprite = Builtin("UI/Skin/InputFieldBackground.psd"); image.type = UnityEngine.UI.Image.Type.Sliced;
        var text = Text("Text", root.transform, 17, Ink, FontStyle.Normal, TextAnchor.MiddleLeft);
        StretchInset(text.rectTransform, 16f, 12f);
        var hint = Text(placeholder, root.transform, 17, Hex("C8D0E0"), FontStyle.Normal, TextAnchor.MiddleLeft);
        StretchInset(hint.rectTransform, 16f, 12f);
        var field = root.GetComponent<InputField>(); field.textComponent = text; field.placeholder = hint; field.lineType = InputField.LineType.SingleLine;
        return field;
    }

    private static Dropdown Dropdown(Transform parent, string name, string[] values, Vector2 position, float width)
    {
        var root = new GameObject(name + "_EDIT", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Dropdown));
        root.transform.SetParent(parent, false); RectTopLeft((RectTransform)root.transform, position, new Vector2(width, 52f));
        var image = root.GetComponent<Image>(); image.color = Hex("FAFBFF"); image.sprite = Builtin("UI/Skin/InputFieldBackground.psd"); image.type = UnityEngine.UI.Image.Type.Sliced;
        var label = Text("Label", root.transform, 17, Ink, FontStyle.Normal, TextAnchor.MiddleLeft); StretchInset(label.rectTransform, 16f, 40f);
        var arrow = Text("⌄", root.transform, 20, Muted, FontStyle.Bold, TextAnchor.MiddleCenter); RectRight(arrow.rectTransform, 10f, 36f);
        var template = new GameObject("Template", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect)); template.transform.SetParent(root.transform, false); template.SetActive(false);
        RectTopLeft((RectTransform)template.transform, new Vector2(0f, -54f), new Vector2(width, 150f)); template.GetComponent<Image>().color = Card;
        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Mask), typeof(Image)); viewport.transform.SetParent(template.transform, false); Stretch((RectTransform)viewport.transform); viewport.GetComponent<Image>().color = Card;
        var content = new GameObject("Content", typeof(RectTransform)); content.transform.SetParent(viewport.transform, false); Stretch((RectTransform)content.transform);
        var item = new GameObject("Item", typeof(RectTransform), typeof(Toggle)); item.transform.SetParent(content.transform, false); RectTopLeft((RectTransform)item.transform, Vector2.zero, new Vector2(width, 34f));
        var check = Image("Item Checkmark", item.transform, Soft); RectTopLeft(check.rectTransform, Vector2.zero, new Vector2(28f, 28f));
        var itemLabel = Text("Item Label", item.transform, 16, Ink, FontStyle.Normal, TextAnchor.MiddleLeft); StretchInset(itemLabel.rectTransform, 36f, 8f);
        var toggle = item.GetComponent<Toggle>(); toggle.targetGraphic = check; toggle.graphic = check;
        var scroll = template.GetComponent<ScrollRect>(); scroll.viewport = (RectTransform)viewport.transform; scroll.content = (RectTransform)content.transform;
        var dropdown = root.GetComponent<Dropdown>(); dropdown.targetGraphic = image; dropdown.template = (RectTransform)template.transform; dropdown.captionText = label; dropdown.itemText = itemLabel;
        dropdown.options = values.Select(v => new Dropdown.OptionData(v)).ToList(); dropdown.RefreshShownValue();
        return dropdown;
    }

    private static Toggle FocusToggle(Transform parent, string value, Vector2 position)
    {
        float width = Mathf.Max(104f, value.Length * 19f + 42f);
        var root = new GameObject("Focus_" + value, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Toggle)); root.transform.SetParent(parent, false);
        RectTopLeft((RectTransform)root.transform, position, new Vector2(width, 38f));
        var bg = root.GetComponent<Image>(); bg.color = Soft; bg.sprite = Builtin("UI/Skin/Background.psd"); bg.type = UnityEngine.UI.Image.Type.Sliced;
        var check = Text("✓", root.transform, 15, Ink, FontStyle.Bold, TextAnchor.MiddleCenter); RectTopLeft(check.rectTransform, new Vector2(8f, -7f), new Vector2(22f, 24f));
        var label = Text(value, root.transform, 15, Muted, FontStyle.Normal, TextAnchor.MiddleLeft); RectTopLeft(label.rectTransform, new Vector2(30f, -5f), new Vector2(width - 34f, 28f));
        var toggle = root.GetComponent<Toggle>(); toggle.targetGraphic = bg; toggle.graphic = check;
        var colors = toggle.colors; colors.normalColor = Soft; colors.highlightedColor = Border; colors.selectedColor = Hex("C8D0E0"); toggle.colors = colors;
        return toggle;
    }

    private static void Chip(Transform parent, string value, Vector2 position, bool active)
    {
        float width = value.Length * 14f + 28f;
        Image bg = Image("Step_" + value, parent, active ? Ink : Soft); bg.sprite = Builtin("UI/Skin/Background.psd"); bg.type = UnityEngine.UI.Image.Type.Sliced;
        RectTopLeft(bg.rectTransform, position, new Vector2(width, 30f));
        Text(value, bg.transform, 13, active ? Color.white : Faint, FontStyle.Normal, TextAnchor.MiddleCenter);
    }

    private static Button Button(Transform parent, string value, Vector2 position, Vector2 size, Color bgColor, Color textColor)
    {
        var root = new GameObject("StartInterviewButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button)); root.transform.SetParent(parent, false);
        RectTopLeft((RectTransform)root.transform, position, size); var image = root.GetComponent<Image>(); image.color = bgColor; image.sprite = Builtin("UI/Skin/Background.psd"); image.type = UnityEngine.UI.Image.Type.Sliced;
        Text(value, root.transform, 17, textColor, FontStyle.Bold, TextAnchor.MiddleCenter);
        return root.GetComponent<Button>();
    }

    private static Text Text(string value, Transform parent, int size, Color color, FontStyle style, TextAnchor alignment, Vector2? position = null, Vector2? dimensions = null)
    {
        var text = new GameObject(value == "Text" ? "Text" : "Label_" + value, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text)).GetComponent<Text>(); text.transform.SetParent(parent, false);
        text.text = value; text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); text.fontSize = size; text.color = color; text.fontStyle = style; text.alignment = alignment; text.supportRichText = false;
        if (position.HasValue) RectTopLeft(text.rectTransform, position.Value, dimensions ?? new Vector2(300f, 30f)); else Stretch(text.rectTransform);
        return text;
    }

    private static Image Image(string name, Transform parent, Color color) { var image = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<Image>(); image.transform.SetParent(parent, false); image.color = color; return image; }
    private static Sprite Builtin(string path) => AssetDatabase.GetBuiltinExtraResource<Sprite>(path);
    private static Color Hex(string value) { ColorUtility.TryParseHtmlString("#" + value, out Color color); return color; }
    private static void Rect(RectTransform r, Vector2 position, Vector2 size, Vector2 anchor) { r.anchorMin = r.anchorMax = anchor; r.pivot = anchor; r.anchoredPosition = position; r.sizeDelta = size; }
    private static void RectTopLeft(RectTransform r, Vector2 position, Vector2 size) { r.anchorMin = r.anchorMax = new Vector2(0f, 1f); r.pivot = new Vector2(0f, 1f); r.anchoredPosition = position; r.sizeDelta = size; }
    private static void RectRight(RectTransform r, float right, float width) { r.anchorMin = new Vector2(1f, 0f); r.anchorMax = Vector2.one; r.pivot = new Vector2(1f, 0.5f); r.offsetMin = new Vector2(-right - width, 0f); r.offsetMax = new Vector2(-right, 0f); }
    private static void Stretch(RectTransform r) { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero; }
    private static void StretchInset(RectTransform r, float left, float right) { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = new Vector2(left, 7f); r.offsetMax = new Vector2(-right, -7f); }
}
