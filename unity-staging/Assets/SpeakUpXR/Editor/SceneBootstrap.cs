// Editor automation — builds the panel-interview scene with one menu click.
//
// Layout (metres, y=0 floor, candidate row centred on the user):
//   user (XR camera) at origin, middle seat of the candidate row, looking +Z
//   candidate desk z=0.55, side candidates at x=±1.5 (same VRM, idle anims)
//   interviewer desk z=2.2, four role interviewers at x=-1.8…1.8 facing -Z
//   open room 10×6.8m, 3.2m ceiling, window wall behind the panel + right side
//   HUD above the interviewer desk at (0, 2.1, 2.16)

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.InputSystem.XR;
using SpeakUpXR;

public static class SceneBootstrap
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";

    private static readonly Color WallColor = new(0.90f, 0.91f, 0.93f);
    private static readonly Color ChairColor = new(0.18f, 0.21f, 0.25f);
    private static readonly Color DeskTopColor = new(0.46f, 0.35f, 0.26f);
    private static readonly Color DeskPanelColor = new(0.38f, 0.29f, 0.21f);
    private static readonly Color SkyGlassColor = new(0.64f, 0.80f, 0.94f);

    private static readonly float[] PanelX = { -1.8f, -0.6f, 0.6f, 1.8f };
    private static readonly string[] PanelRoles = { "인사 담당", "직무 면접관", "기술 면접관", "임원 면접관" };
    private static readonly float[] CandidateX = { -1.5f, 1.5f };

    // 같은 VRM을 쓰는 동안의 역할별 외형 변주 (머리 틴트 / 정장 틴트 / 체격)
    private static readonly Color[] PanelHair =
        { new(0.28f, 0.20f, 0.13f), new(0.06f, 0.06f, 0.07f), new(0.38f, 0.30f, 0.19f), new(0.72f, 0.72f, 0.75f) };
    private static readonly Color[] PanelOutfit =
        { new(0.17f, 0.23f, 0.40f), new(0.21f, 0.21f, 0.23f), new(0.17f, 0.31f, 0.25f), new(0.10f, 0.10f, 0.12f) };
    private static readonly float[] PanelScale = { 1.0f, 1.04f, 0.97f, 1.06f };
    private static readonly Color[] CandidateHair = { new(0.10f, 0.08f, 0.08f), new(0.45f, 0.33f, 0.20f) };
    private static readonly Color[] CandidateOutfit = { new(0.55f, 0.55f, 0.58f), new(0.36f, 0.31f, 0.28f) };
    private static readonly float[] CandidateScale = { 0.98f, 1.03f };

    [MenuItem("SpeakUpXR/Build Interview Scene")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // ── Lighting: bright daylight so the room reads open ──
        var sun = new GameObject("Key Light").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.25f;
        sun.transform.rotation = Quaternion.Euler(50f, -35f, 0);
        var fill = new GameObject("Fill Light").AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.intensity = 0.35f;
        fill.shadows = LightShadows.None;
        fill.transform.rotation = Quaternion.Euler(30f, 150f, 0);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.60f, 0.63f, 0.68f);

        // ── Room shell 10×6.8m, ceiling 3.2m (centre z=0.8) ──
        var room = new GameObject("Room");
        Box(room, "Floor", new Vector3(0, -0.05f, 0.8f), new Vector3(10f, 0.1f, 6.8f), new Color(0.58f, 0.60f, 0.63f));
        Box(room, "Ceiling", new Vector3(0, 3.25f, 0.8f), new Vector3(10f, 0.1f, 6.8f), new Color(0.95f, 0.96f, 0.97f));
        Box(room, "WallBack", new Vector3(0, 1.6f, 4.2f), new Vector3(10f, 3.2f, 0.1f), WallColor);
        Box(room, "WallFront", new Vector3(0, 1.6f, -2.6f), new Vector3(10f, 3.2f, 0.1f), WallColor);
        Box(room, "WallLeft", new Vector3(-5f, 1.6f, 0.8f), new Vector3(0.1f, 3.2f, 6.8f), WallColor);
        Box(room, "WallRight", new Vector3(5f, 1.6f, 0.8f), new Vector3(0.1f, 3.2f, 6.8f), WallColor);

        // ── Windows: big daylight panes behind the panel + right wall ──
        foreach (var wx in new[] { -3f, 0f, 3f })
            Glass(room, $"WindowBack{wx}", new Vector3(wx, 1.8f, 4.14f), new Vector3(2.6f, 1.7f, 0.05f));
        Glass(room, "WindowRight", new Vector3(4.94f, 1.8f, 1.0f), new Vector3(0.05f, 1.7f, 4.2f));

        // ── Interviewer panel desk ──
        var desk = new GameObject("PanelDesk");
        Box(desk, "Top", new Vector3(0, 0.74f, 2.2f), new Vector3(5.0f, 0.04f, 0.8f), DeskTopColor);
        Box(desk, "Front", new Vector3(0, 0.36f, 1.82f), new Vector3(5.0f, 0.72f, 0.03f), DeskPanelColor);

        // ── XR Origin (user = middle seat of the candidate row) ──
        var origin = new GameObject("XR Origin");
        var offset = new GameObject("Camera Offset");
        offset.transform.SetParent(origin.transform, false);
        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        camGo.transform.SetParent(offset.transform, false);
        camGo.transform.localPosition = new Vector3(0, 1.2f, 0); // desktop preview eye; XR overrides
        var cam = camGo.AddComponent<Camera>();
        cam.nearClipPlane = 0.05f;
        camGo.AddComponent<AudioListener>();
        camGo.AddComponent<TrackedPoseDriver>(); // head tracking under OpenXR
        camGo.AddComponent<DesktopLook>();       // editor preview: right-drag to look around

        // ── Interviewer panel: 4 role interviewers behind the desk ──
        var panelRoot = new GameObject("InterviewerPanel");
        var panel = panelRoot.AddComponent<InterviewerPanel>();
        var members = new InterviewerController[PanelX.Length];
        for (int i = 0; i < PanelX.Length; i++)
        {
            float x = PanelX[i];
            var chair = new GameObject($"PanelChair{i}");
            chair.transform.SetParent(panelRoot.transform, false);
            Box(chair, "Seat", new Vector3(x, 0.48f, 2.62f), new Vector3(0.5f, 0.06f, 0.5f), ChairColor);
            Box(chair, "Back", new Vector3(x, 0.78f, 2.84f), new Vector3(0.5f, 0.6f, 0.06f), ChairColor);

            // Standing rig sunk -0.34m so it reads as seated behind the desk (legs occluded).
            var go = new GameObject($"Interviewer_{PanelRoles[i]}");
            go.transform.SetParent(panelRoot.transform, false);
            go.transform.position = new Vector3(x, -0.34f, 2.62f);
            go.transform.rotation = Quaternion.Euler(0, 180f, 0); // face the user (-Z)
            members[i] = go.AddComponent<InterviewerController>();
            members[i].LookTarget = camGo.transform;
            members[i].HairTint = PanelHair[i];
            members[i].OutfitTint = PanelOutfit[i];
            members[i].BodyScale = PanelScale[i];

            NamePlate(desk.transform, new Vector3(x, 0.90f, 1.81f), PanelRoles[i]);
        }
        panel.Members = members;

        // ── Candidate row: desk + side candidates, user seat in the middle ──
        var candRow = new GameObject("CandidateRow");
        Box(candRow, "DeskTop", new Vector3(0, 0.74f, 0.55f), new Vector3(4.6f, 0.04f, 0.7f), DeskTopColor);
        Box(candRow, "DeskFront", new Vector3(0, 0.36f, 0.88f), new Vector3(4.6f, 0.72f, 0.03f), DeskPanelColor);
        Box(candRow, "UserChairSeat", new Vector3(0, 0.48f, 0), new Vector3(0.5f, 0.06f, 0.5f), ChairColor);
        Box(candRow, "UserChairBack", new Vector3(0, 0.78f, -0.22f), new Vector3(0.5f, 0.6f, 0.06f), ChairColor);
        for (int c = 0; c < CandidateX.Length; c++)
        {
            float x = CandidateX[c];
            Box(candRow, $"CandChairSeat{x}", new Vector3(x, 0.48f, 0), new Vector3(0.5f, 0.06f, 0.5f), ChairColor);
            Box(candRow, $"CandChairBack{x}", new Vector3(x, 0.78f, -0.22f), new Vector3(0.5f, 0.6f, 0.06f), ChairColor);

            var go = new GameObject($"Candidate{x}");
            go.transform.SetParent(candRow.transform, false);
            go.transform.position = new Vector3(x, -0.34f, 0);
            // Faces +Z like the user; idle breathing/blink come from the same controller.
            var cand = go.AddComponent<InterviewerController>();
            cand.HairTint = CandidateHair[c];
            cand.OutfitTint = CandidateOutfit[c];
            cand.BodyScale = CandidateScale[c];
        }

        // ── HUD (world-space canvas above the panel desk) ──
        var hud = BuildHud();

        // ── Session wiring ──
        var sessionGo = new GameObject("InterviewSession");
        var api = sessionGo.AddComponent<CoachApi>();
        var session = sessionGo.AddComponent<InterviewSession>();
        session.Api = api;
        session.Panel = panel;
        session.Hud = hud;
        var dev = sessionGo.AddComponent<DevKeyboardDriver>();
        dev.Session = session;

        System.IO.Directory.CreateDirectory("Assets/SpeakUpXR/Scenes");
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[SpeakUpXR] Interview scene built → {ScenePath}");
    }

    private static void Box(GameObject parent, string name, Vector3 pos, Vector3 size, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = size;
        var mat = new Material(Shader.Find("Standard")) { color = color };
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    /// <summary>Sky-lit window pane: emissive so it reads as daylight, not a wall.</summary>
    private static void Glass(GameObject parent, string name, Vector3 pos, Vector3 size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = size;
        var mat = new Material(Shader.Find("Standard")) { color = SkyGlassColor };
        mat.EnableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor", SkyGlassColor * 0.9f);
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    /// <summary>Small role plate on the desk front, readable from the user's seat.</summary>
    private static void NamePlate(Transform parent, Vector3 pos, string role)
    {
        var canvasGo = new GameObject($"Plate_{role}");
        canvasGo.transform.SetParent(parent, false);
        canvasGo.transform.position = pos;
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.GetComponent<RectTransform>().sizeDelta = new Vector2(360, 90);
        canvasGo.transform.localScale = Vector3.one * 0.001f;

        var bg = new GameObject("Bg").AddComponent<Image>();
        bg.transform.SetParent(canvasGo.transform, false);
        bg.rectTransform.anchorMin = Vector2.zero;
        bg.rectTransform.anchorMax = Vector2.one;
        bg.rectTransform.offsetMin = bg.rectTransform.offsetMax = Vector2.zero;
        bg.color = new Color(0.10f, 0.13f, 0.18f, 0.92f);

        var label = MakeText(canvasGo.transform, "Role", Vector2.zero, 40, FontStyle.Bold, new Color(0.93f, 0.95f, 0.97f), 360, 90);
        label.alignment = TextAnchor.MiddleCenter;
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
        label.text = role;
    }

    private static InterviewHud BuildHud()
    {
        var canvasGo = new GameObject("InterviewHud");
        canvasGo.transform.position = new Vector3(0, 2.1f, 2.16f);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = canvas.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(1400, 340);
        canvasGo.transform.localScale = Vector3.one * 0.001f; // 1400px → 1.4m
        canvasGo.AddComponent<GraphicRaycaster>();

        // Card background
        var bg = new GameObject("Bg").AddComponent<Image>();
        bg.transform.SetParent(canvasGo.transform, false);
        bg.rectTransform.anchorMin = Vector2.zero;
        bg.rectTransform.anchorMax = Vector2.one;
        bg.rectTransform.offsetMin = bg.rectTransform.offsetMax = Vector2.zero;
        bg.color = new Color(0.06f, 0.09f, 0.13f, 0.85f);

        var hud = canvasGo.AddComponent<InterviewHud>();
        hud.StatusPill = MakePill(canvasGo.transform, out var statusText);
        hud.StatusText = statusText;
        hud.QuestionText = MakeText(canvasGo.transform, "Question", new Vector2(40, -110), 48, FontStyle.Bold, new Color(0.96f, 0.97f, 0.98f), 1320, 150);
        hud.InterimText = MakeText(canvasGo.transform, "Interim", new Vector2(40, -280), 26, FontStyle.Normal, new Color(0.58f, 0.64f, 0.72f), 1320, 50);
        hud.SetQuestion("면접 시작 버튼을 누르면 면접관이 질문을 시작합니다.");
        hud.SetStatus("대기 중", HudTone.Ask);
        return hud;
    }

    private static Image MakePill(Transform parent, out Text label)
    {
        var pill = new GameObject("StatusPill").AddComponent<Image>();
        pill.transform.SetParent(parent, false);
        pill.rectTransform.anchorMin = pill.rectTransform.anchorMax = new Vector2(0, 1);
        pill.rectTransform.pivot = new Vector2(0, 1);
        pill.rectTransform.anchoredPosition = new Vector2(36, -26);
        pill.rectTransform.sizeDelta = new Vector2(360, 52);
        label = MakeText(pill.transform, "Label", Vector2.zero, 28, FontStyle.Bold, new Color(0.04f, 0.06f, 0.09f), 340, 52);
        label.alignment = TextAnchor.MiddleCenter;
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
        return pill;
    }

    private static Text MakeText(Transform parent, string name, Vector2 pos, int size, FontStyle style, Color color, float w, float h)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = size;
        text.fontStyle = style;
        text.color = color;
        text.alignment = TextAnchor.UpperLeft;
        var rt = text.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(w, h);
        return text;
    }
}
