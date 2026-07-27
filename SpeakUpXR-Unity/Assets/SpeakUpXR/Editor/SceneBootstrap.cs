// Editor automation — builds the 1:1 interview room scene with one menu click,
// so no manual hierarchy work is needed. Port of interview-room.ts geometry.
//
// Layout (metres, user's seat at origin, y=0 floor):
//   user (XR camera) ~ (0, seated, 0) looking +Z
//   desk centre z=1.05, interviewer chair z=1.55 facing -Z (toward user)
//   panel above desk at (0, 1.72, 1.0)

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

    [MenuItem("SpeakUpXR/Build Interview Scene")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // ── Lighting ──
        var sun = new GameObject("Key Light").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.1f;
        sun.transform.rotation = Quaternion.Euler(45f, -30f, 0);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.57f, 0.62f);

        // ── Room shell (floor/ceiling/walls as scaled cubes) ──
        var room = new GameObject("Room");
        Box(room, "Floor", new Vector3(0, -0.05f, 1.1f), new Vector3(4.2f, 0.1f, 4.4f), new Color(0.54f, 0.56f, 0.60f));
        Box(room, "Ceiling", new Vector3(0, 2.75f, 1.1f), new Vector3(4.2f, 0.1f, 4.4f), new Color(0.95f, 0.96f, 0.97f));
        Box(room, "WallBack", new Vector3(0, 1.35f, 3.3f), new Vector3(4.2f, 2.7f, 0.1f), WallColor);
        Box(room, "WallFront", new Vector3(0, 1.35f, -1.1f), new Vector3(4.2f, 2.7f, 0.1f), WallColor);
        Box(room, "WallLeft", new Vector3(-2.1f, 1.35f, 1.1f), new Vector3(0.1f, 2.7f, 4.4f), WallColor);
        Box(room, "WallRight", new Vector3(2.1f, 1.35f, 1.1f), new Vector3(0.1f, 2.7f, 4.4f), WallColor);

        // ── Desk (top + modesty panel occluding the interviewer's legs) ──
        var desk = new GameObject("Desk");
        Box(desk, "Top", new Vector3(0, 0.74f, 1.05f), new Vector3(1.6f, 0.04f, 0.7f), new Color(0.42f, 0.31f, 0.23f));
        Box(desk, "Panel", new Vector3(0, 0.36f, 0.72f), new Vector3(1.6f, 0.72f, 0.03f), new Color(0.35f, 0.26f, 0.19f));

        // ── Interviewer chair ──
        var chair = new GameObject("Chair");
        Box(chair, "Seat", new Vector3(0, 0.48f, 1.55f), new Vector3(0.5f, 0.06f, 0.5f), ChairColor);
        Box(chair, "Back", new Vector3(0, 0.78f, 1.77f), new Vector3(0.5f, 0.6f, 0.06f), ChairColor);

        // ── XR Origin (camera rig) ──
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

        // ── Interviewer (VRM loads at runtime from StreamingAssets) ──
        var interviewerGo = new GameObject("InterviewerRoot");
        // Sunk -0.34m so the standing-rig VRM reads as seated behind the desk (legs occluded).
        interviewerGo.transform.position = new Vector3(0, -0.34f, 1.55f);
        interviewerGo.transform.rotation = Quaternion.Euler(0, 180f, 0); // face the user (-Z)
        var interviewer = interviewerGo.AddComponent<InterviewerController>();
        interviewer.LookTarget = camGo.transform;

        // ── HUD (world-space canvas above the desk) ──
        var hud = BuildHud(camGo.transform);

        // ── Session wiring ──
        var sessionGo = new GameObject("InterviewSession");
        var api = sessionGo.AddComponent<CoachApi>();
        var session = sessionGo.AddComponent<InterviewSession>();
        session.Api = api;
        session.Interviewer = interviewer;
        session.Hud = hud;
        var dev = sessionGo.AddComponent<DevKeyboardDriver>();
        dev.Session = session;

        System.IO.Directory.CreateDirectory("Assets/SpeakUpXR/Scenes");
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[SpeakUpXR] Interview scene built → {ScenePath}");
    }

    private static readonly Color WallColor = new(0.87f, 0.89f, 0.92f);
    private static readonly Color ChairColor = new(0.18f, 0.21f, 0.25f);

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

    private static InterviewHud BuildHud(Transform head)
    {
        var canvasGo = new GameObject("InterviewHud");
        canvasGo.transform.position = new Vector3(0, 1.72f, 1.0f);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = canvas.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(1050, 330);
        canvasGo.transform.localScale = Vector3.one * 0.001f; // 1050px → 1.05m
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
        hud.QuestionText = MakeText(canvasGo.transform, "Question", new Vector2(40, -110), 44, FontStyle.Bold, new Color(0.96f, 0.97f, 0.98f), 970, 150);
        hud.InterimText = MakeText(canvasGo.transform, "Interim", new Vector2(40, -270), 26, FontStyle.Normal, new Color(0.58f, 0.64f, 0.72f), 970, 50);
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
