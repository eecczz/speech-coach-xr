using System;
using System.IO;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class XrInterviewRuntimeUpgrade
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private const string IdlePath = "Assets/FORMAL_AVATAR/Animations/F@Pose_Idle.fbx";
    private const string ControllerPath = "Assets/SpeakUpXR/Animations/FirstPersonCandidate/FirstPersonCandidate.controller";
    private const string MarkerPath = "Assets/SpeakUpXR/Animations/FirstPersonCandidate/xr-runtime-upgrade-v5.txt";
    private static bool _running;

    static XrInterviewRuntimeUpgrade()
    {
        EditorApplication.delayCall += TryUpgrade;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredEditMode) EditorApplication.delayCall += TryUpgrade;
        };
    }

    [MenuItem("SpeakUpXR/Repair Walk, Head Tracking, TTS And Live XR Feedback")]
    public static void UpgradeNow() => Upgrade();

    private static void TryUpgrade()
    {
        if (_running || File.Exists(MarkerPath) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (File.Exists(ScenePath) && File.Exists(IdlePath) && File.Exists(ControllerPath)) Upgrade();
    }

    private static void Upgrade()
    {
        _running = true;
        try
        {
            ConfigureStandingIdle();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Transform head = Camera.main ? Camera.main.transform : null;
            var candidate = UnityEngine.Object.FindFirstObjectByType<FirstPersonAvatarController>(FindObjectsInactive.Include);
            if (candidate)
            {
                candidate.DriveHeadFromHeadset = true;
                candidate.HeadsetRotationWeight = 1f;
                candidate.ClampHeadsetRotation = false;
                var upperBody = candidate.GetComponent<XrUpperBodyAvatarDriver>()
                    ?? candidate.gameObject.AddComponent<XrUpperBodyAvatarDriver>();
                upperBody.Animator = candidate.Animator;
                upperBody.HeadCamera = candidate.HeadCamera;
                upperBody.DriveHands = true;
                upperBody.DriveUpperBody = true;
                EditorUtility.SetDirty(upperBody);
                EditorUtility.SetDirty(candidate);
            }
            var panel = UnityEngine.Object.FindFirstObjectByType<InterviewerPanel>(FindObjectsInactive.Include);
            if (panel?.Members != null)
            {
                string[] voices = { "ko-KR-InJoonNeural", "ko-KR-HyunsuMultilingualNeural", "en-US-AndrewMultilingualNeural" };
                for (int i = 0; i < panel.Members.Length; i++)
                {
                    var member = panel.Members[i];
                    if (!member) continue;
                    member.Voice.VoiceName = voices[Mathf.Min(i, voices.Length - 1)];
                    member.LookTarget = head;
                    var animator = member.AvatarRoot ? member.AvatarRoot.GetComponentInChildren<Animator>(true) : null;
                    if (animator)
                    {
                        var tracker = animator.GetComponent<InterviewerHeadTracker>() ?? animator.gameObject.AddComponent<InterviewerHeadTracker>();
                        tracker.Animator = animator;
                        tracker.Target = head;
                        tracker.AvatarFacingRoot = member.AvatarRoot.transform;
                        EditorUtility.SetDirty(tracker);
                    }
                    EditorUtility.SetDirty(member);
                }
            }

            var system = GameObject.Find("InterviewSystem");
            var session = system ? system.GetComponent<InterviewSession>() : null;
            var signals = system ? system.GetComponent<CandidateSignalTracker>() : null;
            var microphone = system ? system.GetComponent<MicrophoneRecorder>() : null;
            if (signals) microphone = microphone ? microphone : system.AddComponent<MicrophoneRecorder>();
            if (signals) { signals.Microphone = microphone; EditorUtility.SetDirty(signals); }
            if (system && session && signals)
            {
                var live = system.GetComponent<XrRealtimeFeedbackController>() ?? system.AddComponent<XrRealtimeFeedbackController>();
                live.Session = session;
                live.Signals = signals;
                live.Api = session.Api;
                live.Panel = session.Panel;
                live.Hud = session.Hud;
                EditorUtility.SetDirty(live);
            }
            var casting = system ? system.GetComponent<VoiceCastingController>() : null;
            if (casting) { casting.HrMaleIndex = 0; casting.TechnicalMaleIndex = 1; casting.ExecutiveMaleIndex = 2; EditorUtility.SetDirty(casting); }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            File.WriteAllText(MarkerPath, "Standing idle before entrance; three persistent head trackers; distinct male voices; XR realtime feedback.\n");
            AssetDatabase.ImportAsset(MarkerPath);
            Debug.Log("[SpeakUpXR] XR runtime upgrade applied to the editable Interview scene.");
        }
        finally { _running = false; }
    }

    private static void ConfigureStandingIdle()
    {
        AssetDatabase.ImportAsset(IdlePath, ImportAssetOptions.ForceSynchronousImport);
        if (AssetImporter.GetAtPath(IdlePath) is ModelImporter importer)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            importer.importAnimation = true;
            var settings = importer.clipAnimations;
            if (settings == null || settings.Length == 0) settings = importer.defaultClipAnimations;
            foreach (var clip in settings)
            {
                clip.loopTime = clip.loopPose = true;
                clip.lockRootHeightY = clip.lockRootPositionXZ = clip.lockRootRotation = true;
            }
            importer.clipAnimations = settings;
            importer.SaveAndReimport();
        }
        var idle = AssetDatabase.LoadAllAssetsAtPath(IdlePath).OfType<AnimationClip>()
            .First(value => !value.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase));
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        var layers = controller.layers;
        layers[0].iKPass = true;
        controller.layers = layers;
        var machine = controller.layers[0].stateMachine;
        var state = machine.states.Select(value => value.state).FirstOrDefault(value => value.name == "Standing Idle")
                    ?? machine.AddState("Standing Idle", new Vector3(0f, 0f));
        state.motion = idle;
        machine.defaultState = state;
        EditorUtility.SetDirty(controller);
    }
}
