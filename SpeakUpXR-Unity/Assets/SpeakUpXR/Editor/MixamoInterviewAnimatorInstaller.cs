using System;
using System.IO;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Imports the downloaded Mixamo clips as Humanoid animation and builds the
/// scene-authored three-person interview panel Animator. Nothing is spawned at runtime.
/// </summary>
[InitializeOnLoad]
public static class MixamoInterviewAnimatorInstaller
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private const string Folder = "Assets/SpeakUpXR/Animations/Mixamo";
    private const string IdlePath = Folder + "/Mixamo_SittingIdle.fbx";
    private const string TalkingPath = Folder + "/Mixamo_SittingTalking.fbx";
    private const string AskingPath = Folder + "/Mixamo_AskingQuestion.fbx";
    private const string AngryPath = Folder + "/Mixamo_AngryGesture.fbx";
    private const string ControllerPath = Folder + "/InterviewPanel_Mixamo.controller";
    private const string MarkerPath = Folder + "/mixamo-interview-animator-installed-v1.txt";

    private static bool _running;

    static MixamoInterviewAnimatorInstaller()
    {
        EditorApplication.delayCall += TryInstall;
    }

    [MenuItem("SpeakUpXR/Install Mixamo Interview Animations")]
    public static void InstallNow()
    {
        Install();
    }

    private static void TryInstall()
    {
        if (_running || File.Exists(MarkerPath) || EditorApplication.isCompiling ||
            EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        if (!RequiredFilesExist()) return;
        Install();
    }

    private static void Install()
    {
        if (_running) return;
        _running = true;
        try
        {
            if (!RequiredFilesExist())
                throw new InvalidOperationException("The four downloaded Mixamo FBX files are not all present.");

            // First replace the three authored slots with the correct Formal model.
            FormalCharacterInstaller.InstallNow();

            ConfigureMixamoClip(IdlePath);
            ConfigureMixamoClip(TalkingPath);
            ConfigureMixamoClip(AskingPath);
            ConfigureMixamoClip(AngryPath);

            AnimationClip idle = FindClip(IdlePath);
            AnimationClip talking = FindClip(TalkingPath);
            AnimationClip asking = FindClip(AskingPath);
            AnimationClip angry = FindClip(AngryPath);
            var controller = BuildController(idle, talking, asking, angry);

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var interviewers = UnityEngine.Object.FindObjectsByType<InterviewerController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (interviewers.Length != 3)
                throw new InvalidOperationException($"Expected 3 interviewer slots, found {interviewers.Length}.");

            foreach (var interviewer in interviewers)
            {
                if (!interviewer.AvatarRoot)
                    throw new InvalidOperationException($"{interviewer.PersonaId} has no scene-authored AvatarRoot.");

                var animator = interviewer.AvatarRoot.GetComponentInChildren<Animator>(true);
                if (!animator || !animator.avatar || !animator.avatar.isHuman)
                    throw new InvalidOperationException($"{interviewer.PersonaId} does not have a valid Humanoid Animator.");

                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                interviewer.CharacterAnimator = animator;
                interviewer.UseFullBodySpeakingGesture = false;
                interviewer.SpeakingGesture.Clip = interviewer.Personality == InterviewerPersonality.Challenging
                    ? angry
                    : interviewer.Personality == InterviewerPersonality.Analytical ? asking : talking;
                interviewer.SpeakingGesture.StateName = interviewer.Personality == InterviewerPersonality.Challenging
                    ? "Challenging - Angry Gesture"
                    : interviewer.Personality == InterviewerPersonality.Analytical
                        ? "Analytical - Asking Question"
                        : "Warm - Sitting Talking";
                interviewer.SpeakingGesture.SourceFrameRate = interviewer.SpeakingGesture.Clip.frameRate;
                EditorUtility.SetDirty(animator);
                EditorUtility.SetDirty(interviewer);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            // These were only a temporary generated pose. The panel now uses Mixamo clips exclusively.
            AssetDatabase.DeleteAsset("Assets/SpeakUpXR/Animations/FormalCharacter/SeatedIdle.anim");
            AssetDatabase.DeleteAsset("Assets/SpeakUpXR/Animations/FormalCharacter/SeatedIdle.controller");

            File.WriteAllText(MarkerPath,
                "Mixamo interview Animator installed.\n" +
                "Idle: Sitting Idle (loop)\n" +
                "Warm: Sitting Talking\n" +
                "Analytical: Asking Question\n" +
                "Challenging: Angry Gesture\n");
            AssetDatabase.ImportAsset(MarkerPath);
            AssetDatabase.SaveAssets();
            Debug.Log("[SpeakUpXR] Mixamo seated idle and personality-routed speaking animations installed on all three scene-authored interviewers.");
        }
        finally
        {
            _running = false;
        }
    }

    private static bool RequiredFilesExist()
    {
        return File.Exists(IdlePath) && File.Exists(TalkingPath) &&
               File.Exists(AskingPath) && File.Exists(AngryPath);
    }

    private static void ConfigureMixamoClip(string path)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        if (AssetImporter.GetAtPath(path) is not ModelImporter importer)
            throw new InvalidOperationException("Mixamo animation is not an FBX model: " + path);

        bool rigChanged = importer.animationType != ModelImporterAnimationType.Human ||
                          importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel;
        if (rigChanged)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = true;
            importer.SaveAndReimport();
        }

        var clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0) clips = importer.defaultClipAnimations;
        foreach (var clip in clips)
        {
            clip.loopTime = true;
            clip.loopPose = true;
        }
        importer.clipAnimations = clips;
        importer.SaveAndReimport();

        var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
        if (!avatar || !avatar.isHuman)
            throw new InvalidOperationException("Unity could not build a Humanoid Avatar for " + path);
    }

    private static AnimationClip FindClip(string path)
    {
        var clip = AssetDatabase.LoadAllAssetsAtPath(path)
            .OfType<AnimationClip>()
            .FirstOrDefault(value => !value.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase));
        if (!clip) throw new InvalidOperationException("No animation clip was found in " + path);
        return clip;
    }

    private static AnimatorController BuildController(
        AnimationClip idleClip, AnimationClip talkingClip, AnimationClip askingClip, AnimationClip angryClip)
    {
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath))
            AssetDatabase.DeleteAsset(ControllerPath);

        var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.AddParameter("IsSpeaking", AnimatorControllerParameterType.Bool);
        controller.AddParameter("GestureStyle", AnimatorControllerParameterType.Int);

        var machine = controller.layers[0].stateMachine;
        var idle = AddState(machine, "Seated Idle", idleClip, new Vector3(250f, 20f));
        var talking = AddState(machine, "Warm - Sitting Talking", talkingClip, new Vector3(520f, -80f));
        var asking = AddState(machine, "Analytical - Asking Question", askingClip, new Vector3(520f, 20f));
        var angry = AddState(machine, "Challenging - Angry Gesture", angryClip, new Vector3(520f, 120f));
        machine.defaultState = idle;

        AddSpeakingTransition(idle, talking, 0);
        AddSpeakingTransition(idle, asking, 1);
        AddSpeakingTransition(idle, angry, 2);
        AddIdleTransition(talking, idle);
        AddIdleTransition(asking, idle);
        AddIdleTransition(angry, idle);

        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static AnimatorState AddState(
        AnimatorStateMachine machine, string name, Motion motion, Vector3 position)
    {
        var state = machine.AddState(name, position);
        state.motion = motion;
        state.writeDefaultValues = true;
        return state;
    }

    private static void AddSpeakingTransition(AnimatorState from, AnimatorState to, int style)
    {
        var transition = from.AddTransition(to);
        ConfigureTransition(transition);
        transition.AddCondition(AnimatorConditionMode.If, 0f, "IsSpeaking");
        transition.AddCondition(AnimatorConditionMode.Equals, style, "GestureStyle");
    }

    private static void AddIdleTransition(AnimatorState from, AnimatorState idle)
    {
        var transition = from.AddTransition(idle);
        ConfigureTransition(transition);
        transition.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsSpeaking");
    }

    private static void ConfigureTransition(AnimatorStateTransition transition)
    {
        transition.hasExitTime = false;
        transition.hasFixedDuration = true;
        transition.duration = 0.18f;
        transition.canTransitionToSelf = false;
    }
}
