using System;
using System.IO;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Author-time conversion to a scene-authored first-person candidate. The Formal character,
/// camera references, waypoints, HUD and all three interviewers remain editable in the scene.
/// </summary>
[InitializeOnLoad]
public static class FirstPersonInterviewInstaller
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private const string ModelPath = "Assets/FORMAL_AVATAR/Model/Formal_Without Mustache_Base.fbx";
    private const string WalkPath = "Assets/FORMAL_AVATAR/Animations/F@Walk_F.fbx";
    private const string StandingIdlePath = "Assets/FORMAL_AVATAR/Animations/F@Pose_Idle.fbx";
    private const string SitPath = "Assets/SpeakUpXR/Animations/Mixamo/Mixamo_StandToSit.fbx";
    private const string IdlePath = "Assets/SpeakUpXR/Animations/Mixamo/Mixamo_SittingIdle.fbx";
    private const string TalkingPath = "Assets/SpeakUpXR/Animations/Mixamo/Mixamo_SittingTalking.fbx";
    private const string AskingPath = "Assets/SpeakUpXR/Animations/Mixamo/Mixamo_AskingQuestion.fbx";
    private const string AngryPath = "Assets/SpeakUpXR/Animations/Mixamo/Mixamo_AngryGesture.fbx";
    private const string OutputFolder = "Assets/SpeakUpXR/Animations/FirstPersonCandidate";
    private const string ControllerPath = OutputFolder + "/FirstPersonCandidate.controller";
    private const string MarkerPath = OutputFolder + "/first-person-interview-installed-v1.txt";
    private static bool _running;

    static FirstPersonInterviewInstaller() => EditorApplication.delayCall += TryInstall;

    [MenuItem("SpeakUpXR/Install First-Person Player And Dialogue UI")]
    public static void InstallNow() => Install();

    private static void TryInstall()
    {
        if (_running || File.Exists(MarkerPath) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (RequiredAssetsExist()) Install();
    }

    private static bool RequiredAssetsExist()
        => File.Exists(ModelPath) && File.Exists(WalkPath) && File.Exists(StandingIdlePath) && File.Exists(SitPath) && File.Exists(IdlePath) && File.Exists(ScenePath);

    private static void Install()
    {
        if (_running) return;
        _running = true;
        try
        {
            if (!RequiredAssetsExist()) throw new InvalidOperationException("First-person player source assets are missing.");
            EnsureFolder(OutputFolder);
            ConfigureClip(StandingIdlePath, true, true);
            ConfigureClip(WalkPath, true, false);
            ConfigureClip(SitPath, false, true);
            ConfigureClip(IdlePath, true, true);

            AnimationClip standingIdle = FindClip(StandingIdlePath);
            AnimationClip walk = FindClip(WalkPath);
            AnimationClip sit = FindClip(SitPath);
            AnimationClip idle = FindClip(IdlePath);
            RuntimeAnimatorController controller = BuildController(standingIdle, walk, sit, idle);

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (!model) throw new InvalidOperationException("Formal player model could not be loaded.");

            var old = GameObject.Find("PLAYER_CANDIDATE_EDIT_TRANSFORM_HERE");
            if (old) UnityEngine.Object.DestroyImmediate(old);
            var avatar = PrefabUtility.InstantiatePrefab(model) as GameObject;
            if (!avatar) throw new InvalidOperationException("Formal player model could not be instantiated.");
            avatar.name = "PLAYER_CANDIDATE_EDIT_TRANSFORM_HERE";

            var animator = avatar.GetComponentInChildren<Animator>(true) ?? avatar.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var player = animator.gameObject.AddComponent<FirstPersonAvatarController>();
            player.Animator = animator;
            player.Walk.Clip = walk;
            player.SitDown.Clip = sit;
            player.SeatedIdle.Clip = idle;
            player.SitDown.StartFrame = 0;
            player.SitDown.SourceFrameRate = sit.frameRate;
            player.SeatedIdle.SourceFrameRate = idle.frameRate;
            var upperBody = animator.gameObject.AddComponent<XrUpperBodyAvatarDriver>();
            upperBody.Animator = animator;

            var entrance = UnityEngine.Object.FindFirstObjectByType<InterviewEntranceSequence>(FindObjectsInactive.Include);
            if (!entrance) throw new InvalidOperationException("Interview entrance sequence was not found.");
            entrance.PlayerAvatar = player;
            entrance.WalkSeconds = 5f;
            entrance.SeatPoint.position = new Vector3(0f, 0f, 0.45f);
            entrance.SeatPoint.rotation = Quaternion.identity;
            avatar.transform.SetPositionAndRotation(entrance.EntrancePoint.position, entrance.EntrancePoint.rotation);

            var headCamera = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Include);
            if (!headCamera) throw new InvalidOperationException("Main camera was not found.");
            player.HeadCamera = headCamera;
            upperBody.HeadCamera = headCamera;
            player.CameraOffset = headCamera.transform.parent;
            player.XrOrigin = entrance.XrOrigin;
            player.CameraAtHeadOffset = new Vector3(0f, 0.03f, 0.18f);
            headCamera.fieldOfView = 72f;
            headCamera.nearClipPlane = 0.05f;

            UpdatePanelFraming(headCamera.transform);
            UpdateDialogueHud();
            InstallVoiceCasting();

            EditorUtility.SetDirty(player);
            EditorUtility.SetDirty(entrance);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            File.WriteAllText(MarkerPath,
                "Scene-authored first-person Formal candidate installed.\n" +
                "Camera: humanoid head bone at runtime.\n" +
                "Entrance: root-motion Walk -> Stand To Sit -> Sitting Idle.\n" +
                "HUD: bottom dialogue layout in desktop and head-locked world-space in XR.\n");
            AssetDatabase.ImportAsset(MarkerPath);
            Debug.Log("[SpeakUpXR] First-person candidate, root-motion entrance, Stand To Sit, close panel framing, bottom dialogue HUD and voice casting installed.");
        }
        finally { _running = false; }
    }

    private static void UpdatePanelFraming(Transform lookTarget)
    {
        var panel = UnityEngine.Object.FindFirstObjectByType<InterviewerPanel>(FindObjectsInactive.Include);
        if (!panel || panel.Members == null) return;
        float[] x = { -1.25f, 0f, 1.25f };
        var ordered = panel.Members.OrderBy(value => value.Personality).ToArray();
        for (int i = 0; i < ordered.Length && i < x.Length; i++)
        {
            ordered[i].transform.localPosition = new Vector3(x[i], 0f, 2.5f);
            ordered[i].LookTarget = lookTarget;
            var animator = ordered[i].AvatarRoot ? ordered[i].AvatarRoot.GetComponentInChildren<Animator>(true) : null;
            if (animator)
            {
                var tracker = animator.GetComponent<InterviewerHeadTracker>() ?? animator.gameObject.AddComponent<InterviewerHeadTracker>();
                tracker.Animator = animator;
                tracker.Target = lookTarget;
                tracker.AvatarFacingRoot = ordered[i].AvatarRoot.transform;
                EditorUtility.SetDirty(tracker);
            }
            string clipPath = ordered[i].Personality == InterviewerPersonality.Challenging
                ? AngryPath
                : ordered[i].Personality == InterviewerPersonality.Analytical ? AskingPath : TalkingPath;
            ordered[i].SpeakingGesture.Clip = FindClip(clipPath);
            ordered[i].SpeakingGesture.StateName = ordered[i].Personality == InterviewerPersonality.Challenging
                ? "Challenging - Angry Gesture"
                : ordered[i].Personality == InterviewerPersonality.Analytical
                    ? "Analytical - Asking Question"
                    : "Warm - Sitting Talking";
            ordered[i].SpeakingGesture.SourceFrameRate = ordered[i].SpeakingGesture.Clip.frameRate;
            EditorUtility.SetDirty(ordered[i]);
        }

        for (int i = 0; i < x.Length; i++)
        {
            var chair = panel.transform.Find("Chair_" + (i + 1));
            if (chair) chair.localPosition = new Vector3(x[i], 0.48f, 2.72f);
        }
        foreach (Transform child in panel.transform)
        {
            if (!child.name.StartsWith("NamePlate_", StringComparison.Ordinal)) continue;
            if (child.name.Contains("인사")) child.localPosition = new Vector3(x[0], 0.92f, 1.65f);
            else if (child.name.Contains("기술")) child.localPosition = new Vector3(x[1], 0.92f, 1.65f);
            else if (child.name.Contains("임원")) child.localPosition = new Vector3(x[2], 0.92f, 1.65f);
            child.localRotation = Quaternion.identity;
        }

        var casting = UnityEngine.Object.FindFirstObjectByType<VoiceCastingController>(FindObjectsInactive.Include);
        if (casting) casting.Panel = panel;
    }

    private static void UpdateDialogueHud()
    {
        var hud = UnityEngine.Object.FindFirstObjectByType<InterviewHud>(FindObjectsInactive.Include);
        if (!hud) return;
        var rect = (RectTransform)hud.transform;
        rect.sizeDelta = new Vector2(1200f, 280f);
        var adaptive = hud.GetComponent<AdaptiveWorldCanvas>() ?? hud.gameObject.AddComponent<AdaptiveWorldCanvas>();
        adaptive.DesktopAnchor = adaptive.DesktopPivot = new Vector2(0.5f, 0f);
        adaptive.DesktopPosition = new Vector2(0f, 32f);
        adaptive.DesktopSize = new Vector2(1180f, 270f);
        adaptive.AttachToHeadInXr = true;
        adaptive.XrLocalPosition = new Vector3(0f, -0.31f, 0.78f);
        adaptive.XrWorldScale = 0.00072f;

        SetTextRect(hud.SpeakerText, new Vector2(36f, -24f), new Vector2(1128f, 38f), 25, FontStyle.Bold);
        SetTextRect(hud.QuestionText, new Vector2(36f, -73f), new Vector2(1128f, 118f), 38, FontStyle.Normal);
        SetTextRect(hud.InterimText, new Vector2(36f, -207f), new Vector2(1128f, 40f), 22, FontStyle.Italic);
        var background = hud.transform.Find("Background")?.GetComponent<Image>();
        if (background)
        {
            background.color = new Color(0.035f, 0.045f, 0.065f, 0.94f);
            var outline = background.GetComponent<Outline>() ?? background.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0.58f, 0.66f, 0.76f, 0.75f);
            outline.effectDistance = new Vector2(2f, -2f);
        }
        EditorUtility.SetDirty(adaptive);
        EditorUtility.SetDirty(hud);
    }

    private static void SetTextRect(Text text, Vector2 position, Vector2 size, int fontSize, FontStyle style)
    {
        if (!text) return;
        text.rectTransform.anchorMin = text.rectTransform.anchorMax = new Vector2(0f, 1f);
        text.rectTransform.pivot = new Vector2(0f, 1f);
        text.rectTransform.anchoredPosition = position;
        text.rectTransform.sizeDelta = size;
        text.fontSize = fontSize;
        text.fontStyle = style;
    }

    private static void InstallVoiceCasting()
    {
        var system = GameObject.Find("InterviewSystem");
        if (!system) return;
        var casting = system.GetComponent<VoiceCastingController>() ?? system.AddComponent<VoiceCastingController>();
        casting.Panel = UnityEngine.Object.FindFirstObjectByType<InterviewerPanel>(FindObjectsInactive.Include);
        casting.ApplySelection();
        EditorUtility.SetDirty(casting);
    }

    private static RuntimeAnimatorController BuildController(AnimationClip standingIdle, AnimationClip walk, AnimationClip sit, AnimationClip idle)
    {
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath)) AssetDatabase.DeleteAsset(ControllerPath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        var layers = controller.layers;
        layers[0].iKPass = true;
        controller.layers = layers;
        var machine = controller.layers[0].stateMachine;
        var standingState = machine.AddState("Standing Idle", new Vector3(0f, 0f));
        standingState.motion = standingIdle;
        var walkState = machine.AddState("Walk", new Vector3(240f, 0f));
        walkState.motion = walk;
        var sitState = machine.AddState("Sit Down", new Vector3(480f, 0f));
        sitState.motion = sit;
        var idleState = machine.AddState("Seated Idle", new Vector3(720f, 0f));
        idleState.motion = idle;
        machine.defaultState = standingState;
        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static void ConfigureClip(string path, bool loop, bool bakePlanarRoot)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        if (AssetImporter.GetAtPath(path) is not ModelImporter importer)
            throw new InvalidOperationException("Animation source is not an FBX: " + path);
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = true;
        importer.SaveAndReimport();

        var clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0) clips = importer.defaultClipAnimations;
        foreach (var clip in clips)
        {
            clip.loopTime = loop;
            clip.loopPose = loop;
            clip.lockRootHeightY = true;
            clip.lockRootPositionXZ = bakePlanarRoot;
            clip.lockRootRotation = true;
        }
        importer.clipAnimations = clips;
        importer.SaveAndReimport();
    }

    private static AnimationClip FindClip(string path)
    {
        var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
            .FirstOrDefault(value => !value.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase));
        if (!clip) throw new InvalidOperationException("No animation clip found in " + path);
        return clip;
    }

    private static void EnsureFolder(string path)
    {
        string current = "Assets";
        foreach (string segment in path.Split('/').Skip(1))
        {
            string next = current + "/" + segment;
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, segment);
            current = next;
        }
    }
}
