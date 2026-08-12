// One-time authoring utility. The generated scene is the source of truth afterward:
// characters, seats, door, waypoints and UI remain ordinary scene objects.

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;
using SpeakUpXR;

public static class SceneBootstrap
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private static readonly Color Wall = new(0.88f, 0.90f, 0.93f);
    private static readonly Color Floor = new(0.42f, 0.45f, 0.49f);
    private static readonly Color Wood = new(0.36f, 0.26f, 0.18f);
    private static readonly Color Dark = new(0.10f, 0.13f, 0.18f);

    [MenuItem("SpeakUpXR/Create Editable Interview Scene")]
    [MenuItem("SpeakUpXR/Build Interview Scene")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        BuildLighting();
        BuildRoom(out var door);
        var origin = BuildXrRig(out var head);
        var panel = BuildPanel(head);
        var hud = BuildHud();
        var entrance = BuildEntrance(origin.transform, door.transform);
        var report = BuildReport();

        var system = new GameObject("InterviewSystem");
        var api = system.AddComponent<CoachApi>();
        var microphone = system.AddComponent<MicrophoneRecorder>();
        var signals = system.AddComponent<CandidateSignalTracker>();
        signals.Head = head;
        signals.Panel = panel;
        var session = system.AddComponent<InterviewSession>();
        session.Api = api;
        session.Panel = panel;
        session.Hud = hud;
        session.Microphone = microphone;
        session.Signals = signals;
        session.Entrance = entrance;
        session.ReportView = report;
        session.MaxQuestions = 5;
        session.AutoStart = true;
        system.AddComponent<DevKeyboardDriver>().Session = session;
        system.AddComponent<XrAnswerInput>().Session = session;

        System.IO.Directory.CreateDirectory("Assets/SpeakUpXR/Scenes");
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("[SpeakUpXR] Editable three-person interview scene created. Edit this scene directly; rebuilding is optional.");
    }

    private static void BuildLighting()
    {
        var key = new GameObject("Key Light").AddComponent<Light>();
        key.type = LightType.Directional;
        key.intensity = 1.15f;
        key.transform.rotation = Quaternion.Euler(48f, -30f, 0f);
        var fill = new GameObject("Fill Light").AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.intensity = 0.3f;
        fill.shadows = LightShadows.None;
        fill.transform.rotation = Quaternion.Euler(25f, 150f, 0f);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.58f, 0.61f, 0.66f);
    }

    private static void BuildRoom(out GameObject doorPivot)
    {
        var room = new GameObject("InterviewRoom_EDIT_ME");
        Box(room.transform, "Floor", new Vector3(0, -0.05f, 0.5f), new Vector3(8f, 0.1f, 7f), Floor);
        Box(room.transform, "Ceiling", new Vector3(0, 3.15f, 0.5f), new Vector3(8f, 0.1f, 7f), Wall);
        Box(room.transform, "BackWall", new Vector3(0, 1.55f, 4f), new Vector3(8f, 3.2f, 0.1f), Wall);
        Box(room.transform, "LeftWall", new Vector3(-4f, 1.55f, 0.5f), new Vector3(0.1f, 3.2f, 7f), Wall);
        Box(room.transform, "RightWall", new Vector3(4f, 1.55f, 0.5f), new Vector3(0.1f, 3.2f, 7f), Wall);
        Box(room.transform, "FrontWallLeft", new Vector3(-2.4f, 1.55f, -3f), new Vector3(3.2f, 3.2f, 0.1f), Wall);
        Box(room.transform, "FrontWallRight", new Vector3(2.4f, 1.55f, -3f), new Vector3(3.2f, 3.2f, 0.1f), Wall);
        Box(room.transform, "FrontWallTop", new Vector3(0, 2.8f, -3f), new Vector3(1.6f, 0.7f, 0.1f), Wall);

        doorPivot = new GameObject("DoorPivot_EDIT_ME");
        doorPivot.transform.SetParent(room.transform, false);
        doorPivot.transform.localPosition = new Vector3(-0.78f, 0f, -2.94f);
        Box(doorPivot.transform, "Door", new Vector3(0.78f, 1.2f, 0f), new Vector3(1.5f, 2.4f, 0.08f), new Color(0.25f, 0.18f, 0.12f));

        // Daylight panels behind the interviewers.
        for (int i = -1; i <= 1; i++)
            Box(room.transform, "Window_" + i, new Vector3(i * 2.2f, 1.9f, 3.93f), new Vector3(1.8f, 1.5f, 0.04f), new Color(0.62f, 0.78f, 0.92f));
    }

    private static GameObject BuildXrRig(out Transform head)
    {
        var origin = new GameObject("XR Origin_EDIT_POSITION_WITH_WAYPOINTS");
        var offset = new GameObject("Camera Offset");
        offset.transform.SetParent(origin.transform, false);
        var cameraObject = new GameObject("Main Camera");
        cameraObject.tag = "MainCamera";
        cameraObject.transform.SetParent(offset.transform, false);
        cameraObject.transform.localPosition = new Vector3(0, 1.65f, 0);
        var camera = cameraObject.AddComponent<Camera>();
        camera.nearClipPlane = 0.05f;
        cameraObject.AddComponent<AudioListener>();
        cameraObject.AddComponent<TrackedPoseDriver>();
        cameraObject.AddComponent<DesktopLook>();
        head = cameraObject.transform;
        return origin;
    }

    private static InterviewerPanel BuildPanel(Transform lookTarget)
    {
        var root = new GameObject("InterviewerPanel_EDIT_CHARACTERS_HERE");
        Box(root.transform, "DeskTop", new Vector3(0, 0.76f, 2.05f), new Vector3(4.8f, 0.06f, 0.8f), Wood);
        Box(root.transform, "DeskFront", new Vector3(0, 0.38f, 1.68f), new Vector3(4.8f, 0.72f, 0.05f), Wood * 0.8f);

        string[] ids = { "warm", "analytical", "challenging" };
        string[] names = { "따뜻한 인사 담당", "분석적인 실무 담당", "압박형 임원 담당" };
        string[] voices = { "ko-KR-SunHiNeural", "ko-KR-HyunsuNeural", "ko-KR-InJoonNeural" };
        int[] rates = { -4, 0, -6 };
        int[] pitches = { 2, 0, -4 };
        Color[] colors = { new(0.25f, 0.39f, 0.58f), new(0.20f, 0.32f, 0.28f), new(0.18f, 0.18f, 0.20f) };
        float[] xs = { -1.45f, 0f, 1.45f };
        var members = new InterviewerController[3];
        for (int i = 0; i < 3; i++)
        {
            var slot = new GameObject($"SLOT_{i + 1}_{names[i]}_SWAP_AVATAR_ROOT");
            slot.transform.SetParent(root.transform, false);
            slot.transform.localPosition = new Vector3(xs[i], 0f, 2.5f);
            slot.transform.localRotation = Quaternion.Euler(0, 180f, 0);
            var avatar = BuildPlaceholder(slot.transform, names[i], colors[i], out var mouth);
            var controller = slot.AddComponent<InterviewerController>();
            controller.PersonaId = ids[i];
            controller.DisplayName = names[i];
            controller.Personality = (InterviewerPersonality)i;
            controller.Voice.VoiceName = voices[i];
            controller.Voice.RatePercent = rates[i];
            controller.Voice.PitchPercent = pitches[i];
            controller.AvatarRoot = avatar;
            controller.PlaceholderMouth = mouth;
            controller.LookTarget = lookTarget;
            controller.VoiceSource = slot.AddComponent<AudioSource>();
            members[i] = controller;

            Box(root.transform, "Chair_" + (i + 1), new Vector3(xs[i], 0.48f, 2.72f), new Vector3(0.55f, 0.08f, 0.55f), Dark);
            BuildNamePlate(root.transform, new Vector3(xs[i], 0.92f, 1.65f), names[i]);
        }
        var panel = root.AddComponent<InterviewerPanel>();
        panel.Members = members;
        return panel;
    }

    private static GameObject BuildPlaceholder(Transform parent, string name, Color suit, out Transform mouth)
    {
        var root = new GameObject("AvatarRoot_PLACEHOLDER_Replace_With_VRM_or_FBX");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = new Vector3(0, -0.15f, 0);
        Capsule(root.transform, "Torso", new Vector3(0, 1.15f, 0), new Vector3(0.48f, 0.62f, 0.30f), suit);
        Sphere(root.transform, "Head", new Vector3(0, 1.83f, 0), new Vector3(0.27f, 0.31f, 0.25f), new Color(0.80f, 0.65f, 0.55f));
        var mouthObject = Box(root.transform, "Mouth_LipSync", new Vector3(0, 1.76f, -0.245f), new Vector3(0.11f, 0.025f, 0.02f), new Color(0.25f, 0.06f, 0.06f));
        mouth = mouthObject.transform;
        return root;
    }

    private static InterviewEntranceSequence BuildEntrance(Transform xrOrigin, Transform door)
    {
        var root = new GameObject("EntranceCutscene_EDIT_WAYPOINTS_HERE");
        var entrance = new GameObject("Waypoint_Entrance").transform;
        entrance.SetParent(root.transform, false);
        entrance.position = new Vector3(0, 0, -2.65f);
        var seat = new GameObject("Waypoint_Seat").transform;
        seat.SetParent(root.transform, false);
        seat.position = Vector3.zero;
        var sequence = root.AddComponent<InterviewEntranceSequence>();
        sequence.XrOrigin = xrOrigin;
        sequence.Door = door;
        sequence.EntrancePoint = entrance;
        sequence.SeatPoint = seat;
        sequence.PlayOnStart = true;
        return sequence;
    }

    private static InterviewHud BuildHud()
    {
        var canvasObject = new GameObject("InterviewHud_EDIT_POSITION");
        canvasObject.transform.position = new Vector3(0, 1.2f, 1.25f);
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.GetComponent<RectTransform>().sizeDelta = new Vector2(1200, 320);
        canvasObject.transform.localScale = Vector3.one * 0.001f;
        canvasObject.AddComponent<GraphicRaycaster>();
        var adaptive = canvasObject.AddComponent<AdaptiveWorldCanvas>();
        adaptive.DesktopAnchor = new Vector2(0.5f, 1f);
        adaptive.DesktopPivot = new Vector2(0.5f, 1f);
        adaptive.DesktopPosition = new Vector2(0f, -24f);
        adaptive.DesktopSize = new Vector2(1100f, 300f);
        Fill(canvasObject.transform, "Background", new Color(0.04f, 0.06f, 0.09f, 0.88f));

        var hud = canvasObject.AddComponent<InterviewHud>();
        hud.SpeakerText = Text(canvasObject.transform, "Speaker", new Vector2(35, -28), 27, FontStyle.Bold, new Color(0.55f, 0.78f, 1f), 1130, 42);
        hud.QuestionText = Text(canvasObject.transform, "Question", new Vector2(35, -82), 43, FontStyle.Bold, Color.white, 1130, 140);
        hud.InterimText = Text(canvasObject.transform, "Interim", new Vector2(35, -240), 23, FontStyle.Normal, new Color(0.68f, 0.72f, 0.78f), 1130, 45);
        var pill = new GameObject("StatusPill").AddComponent<Image>();
        pill.transform.SetParent(canvasObject.transform, false);
        pill.rectTransform.anchorMin = pill.rectTransform.anchorMax = new Vector2(1, 1);
        pill.rectTransform.pivot = new Vector2(1, 1);
        pill.rectTransform.anchoredPosition = new Vector2(-28, -24);
        pill.rectTransform.sizeDelta = new Vector2(350, 44);
        hud.StatusPill = pill;
        hud.StatusText = Text(pill.transform, "Status", Vector2.zero, 22, FontStyle.Bold, Dark, 350, 44);
        Stretch(hud.StatusText.rectTransform);
        hud.SetSpeaker("면접 패널");
        hud.SetQuestion("잠시 후 면접을 시작합니다.");
        hud.SetStatus("대기 중", HudTone.Ask);
        return hud;
    }

    private static InterviewReportView BuildReport()
    {
        var canvasObject = new GameObject("InterviewReport_EDIT_LAYOUT");
        canvasObject.transform.position = new Vector3(0, 1.5f, 1.1f);
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.GetComponent<RectTransform>().sizeDelta = new Vector2(1500, 900);
        canvasObject.transform.localScale = Vector3.one * 0.001f;
        var adaptive = canvasObject.AddComponent<AdaptiveWorldCanvas>();
        adaptive.DesktopAnchor = adaptive.DesktopPivot = new Vector2(0.5f, 0.5f);
        adaptive.DesktopPosition = Vector2.zero;
        adaptive.DesktopSize = new Vector2(1500f, 900f);
        Fill(canvasObject.transform, "Backdrop", new Color(0.025f, 0.035f, 0.055f, 0.97f));

        var loading = new GameObject("Loading");
        loading.transform.SetParent(canvasObject.transform, false);
        var loadingText = Text(loading.transform, "LoadingText", Vector2.zero, 48, FontStyle.Bold, Color.white, 1200, 120);
        loadingText.text = "면접 리포트를 분석하고 있습니다…";
        loadingText.alignment = TextAnchor.MiddleCenter;
        loadingText.rectTransform.anchorMin = loadingText.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        loadingText.rectTransform.pivot = new Vector2(0.5f, 0.5f);

        var reportRoot = new GameObject("ReportContent");
        reportRoot.transform.SetParent(canvasObject.transform, false);
        var title = Text(reportRoot.transform, "Title", new Vector2(70, -60), 58, FontStyle.Bold, new Color(0.58f, 0.78f, 1f), 1360, 80);
        var summary = Text(reportRoot.transform, "Summary", new Vector2(70, -175), 34, FontStyle.Normal, Color.white, 1360, 260);
        var details = Text(reportRoot.transform, "Details", new Vector2(70, -500), 29, FontStyle.Normal, new Color(0.76f, 0.82f, 0.90f), 1360, 280);
        var view = canvasObject.AddComponent<InterviewReportView>();
        view.Root = canvasObject;
        view.LoadingRoot = loading;
        view.ReportRoot = reportRoot;
        view.TitleText = title;
        view.SummaryText = summary;
        view.DetailText = details;
        return view;
    }

    private static GameObject Box(Transform parent, string name, Vector3 position, Vector3 scale, Color color) =>
        Primitive(parent, name, PrimitiveType.Cube, position, scale, color);
    private static GameObject Sphere(Transform parent, string name, Vector3 position, Vector3 scale, Color color) =>
        Primitive(parent, name, PrimitiveType.Sphere, position, scale, color);
    private static GameObject Capsule(Transform parent, string name, Vector3 position, Vector3 scale, Color color) =>
        Primitive(parent, name, PrimitiveType.Capsule, position, scale, color);

    private static GameObject Primitive(Transform parent, string name, PrimitiveType type, Vector3 position, Vector3 scale, Color color)
    {
        var value = GameObject.CreatePrimitive(type);
        value.name = name;
        value.transform.SetParent(parent, false);
        value.transform.localPosition = position;
        value.transform.localScale = scale;
        value.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Standard")) { color = color };
        return value;
    }

    private static void BuildNamePlate(Transform parent, Vector3 position, string label)
    {
        var canvasObject = new GameObject("NamePlate_" + label);
        canvasObject.transform.SetParent(parent, false);
        canvasObject.transform.localPosition = position;
        canvasObject.transform.localRotation = Quaternion.Euler(0, 180f, 0);
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.GetComponent<RectTransform>().sizeDelta = new Vector2(420, 90);
        canvasObject.transform.localScale = Vector3.one * 0.001f;
        Fill(canvasObject.transform, "Background", Dark);
        var text = Text(canvasObject.transform, "Role", Vector2.zero, 32, FontStyle.Bold, Color.white, 420, 90);
        text.text = label;
        text.alignment = TextAnchor.MiddleCenter;
        Stretch(text.rectTransform);
    }

    private static void Fill(Transform parent, string name, Color color)
    {
        var image = new GameObject(name).AddComponent<Image>();
        image.transform.SetParent(parent, false);
        image.color = color;
        Stretch(image.rectTransform);
    }

    private static Text Text(Transform parent, string name, Vector2 position, int size, FontStyle style, Color color, float width, float height)
    {
        var value = new GameObject(name).AddComponent<Text>();
        value.transform.SetParent(parent, false);
        value.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        value.fontSize = size;
        value.fontStyle = style;
        value.color = color;
        value.alignment = TextAnchor.UpperLeft;
        value.horizontalOverflow = HorizontalWrapMode.Wrap;
        value.verticalOverflow = VerticalWrapMode.Overflow;
        var rt = value.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = position;
        rt.sizeDelta = new Vector2(width, height);
        return value;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
