using System;
using System.IO;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Authors a head-only speaking layer and assigns it to three distinct scene Animators.</summary>
[InitializeOnLoad]
public static class RestrainedInterviewerAnimatorUpgrade
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private const string Folder = "Assets/SpeakUpXR/Animations/Mixamo";
    private const string IdlePath = Folder + "/Mixamo_SittingIdle.fbx";
    private const string NodClipPath = Folder + "/RestrainedHeadNod.anim";
    private const string MaskPath = Folder + "/HeadOnly.mask";
    private const string ControllerPath = Folder + "/InterviewPanel_Restrained.controller";
    private const string MarkerPath = Folder + "/restrained-interviewer-animator-v1.txt";
    private static bool _running;

    static RestrainedInterviewerAnimatorUpgrade() => EditorApplication.delayCall += TryUpgrade;

    [MenuItem("SpeakUpXR/Install Restrained Head-Only Interviewer Speaking Animation")]
    public static void UpgradeNow() => Upgrade();

    private static void TryUpgrade()
    {
        if (_running || File.Exists(MarkerPath) || EditorApplication.isCompiling ||
            EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (File.Exists(ScenePath) && File.Exists(IdlePath)) Upgrade();
    }

    private static void Upgrade()
    {
        _running = true;
        try
        {
            AnimationClip idle = AssetDatabase.LoadAllAssetsAtPath(IdlePath).OfType<AnimationClip>()
                .First(value => !value.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase));
            AnimationClip nod = BuildNodClip();
            AvatarMask mask = BuildHeadMask();
            AnimatorController controller = BuildController(idle, nod, mask);

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var panel = UnityEngine.Object.FindFirstObjectByType<InterviewerPanel>(FindObjectsInactive.Include);
            if (!panel || panel.Members == null || panel.Members.Count(value => value) != 3)
                throw new InvalidOperationException("Exactly three scene-authored interviewers are required.");

            foreach (var member in panel.Members)
            {
                var animator = member.CharacterAnimator ??
                    (member.AvatarRoot ? member.AvatarRoot.GetComponentInChildren<Animator>(true) : null);
                if (!animator || !animator.isHuman)
                    throw new InvalidOperationException(member.DisplayName + " has no Humanoid Animator.");
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                member.CharacterAnimator = animator;
                member.UseFullBodySpeakingGesture = false;
                EditorUtility.SetDirty(animator);
                EditorUtility.SetDirty(member);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            File.WriteAllText(MarkerPath,
                "Three independent Animator components. Base: seated idle. Speaking Head layer: restrained Humanoid head nod only.\n");
            AssetDatabase.ImportAsset(MarkerPath);
            Debug.Log("[SpeakUpXR] Head-only speaking Animator layer assigned independently to all three interviewers.");
        }
        finally { _running = false; }
    }

    private static AnimationClip BuildNodClip()
    {
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>(NodClipPath)) AssetDatabase.DeleteAsset(NodClipPath);
        var clip = new AnimationClip { name = "Restrained Head Nod", frameRate = 30f, wrapMode = WrapMode.Loop };
        string muscle = HumanTrait.MuscleName.FirstOrDefault(value => value == "Head Nod Down-Up")
            ?? HumanTrait.MuscleName.First(value => value.Contains("Head Nod"));
        var curve = new AnimationCurve(
            new Keyframe(0f, 0f), new Keyframe(0.34f, 0.045f), new Keyframe(0.68f, 0f),
            new Keyframe(1.12f, -0.022f), new Keyframe(1.55f, 0f));
        for (int i = 0; i < curve.length; i++) AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Auto);
        clip.SetCurve(string.Empty, typeof(Animator), muscle, curve);
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = true;
        settings.loopBlend = true;
        AnimationUtility.SetAnimationClipSettings(clip, settings);
        AssetDatabase.CreateAsset(clip, NodClipPath);
        return clip;
    }

    private static AvatarMask BuildHeadMask()
    {
        if (AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath)) AssetDatabase.DeleteAsset(MaskPath);
        var mask = new AvatarMask { name = "Head Only" };
        foreach (AvatarMaskBodyPart part in Enum.GetValues(typeof(AvatarMaskBodyPart)))
            if (part != AvatarMaskBodyPart.LastBodyPart) mask.SetHumanoidBodyPartActive(part, part == AvatarMaskBodyPart.Head);
        AssetDatabase.CreateAsset(mask, MaskPath);
        return mask;
    }

    private static AnimatorController BuildController(AnimationClip idle, AnimationClip nod, AvatarMask mask)
    {
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath)) AssetDatabase.DeleteAsset(ControllerPath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.AddParameter("IsSpeaking", AnimatorControllerParameterType.Bool);
        var baseMachine = controller.layers[0].stateMachine;
        var seated = baseMachine.AddState("Seated Idle");
        seated.motion = idle;
        baseMachine.defaultState = seated;

        controller.AddLayer("Speaking Head");
        var layers = controller.layers;
        layers[1].defaultWeight = 1f;
        layers[1].blendingMode = AnimatorLayerBlendingMode.Override;
        layers[1].avatarMask = mask;
        controller.layers = layers;
        var machine = controller.layers[1].stateMachine;
        var quiet = machine.AddState("Quiet");
        var speaking = machine.AddState("Restrained Head Nod");
        speaking.motion = nod;
        machine.defaultState = quiet;
        AddTransition(quiet, speaking, true);
        AddTransition(speaking, quiet, false);
        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static void AddTransition(AnimatorState from, AnimatorState to, bool speaking)
    {
        var transition = from.AddTransition(to);
        transition.hasExitTime = false;
        transition.hasFixedDuration = true;
        transition.duration = 0.14f;
        transition.AddCondition(speaking ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f, "IsSpeaking");
    }
}
