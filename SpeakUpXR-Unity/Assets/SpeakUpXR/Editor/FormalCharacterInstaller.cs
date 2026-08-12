using System;
using System.IO;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Replaces all three authored interviewer avatars with the rigged Formal
/// Character Asset Store model. The generated seated idle is an Animator clip,
/// loops from frame zero, and is saved as a normal editable project asset.
/// </summary>
[InitializeOnLoad]
public static class FormalCharacterInstaller
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private const string OutputFolder = "Assets/SpeakUpXR/Animations/FormalCharacter";
    private const string InstallMarker = OutputFolder + "/formal-character-installed-v2.txt";
    private const string ClipPath = OutputFolder + "/SeatedIdle.anim";
    private const string ControllerPath = OutputFolder + "/SeatedIdle.controller";

    static FormalCharacterInstaller()
    {
        EditorApplication.update -= TryInstallWhenImported;
        EditorApplication.update += TryInstallWhenImported;
    }

    [MenuItem("SpeakUpXR/Replace Panel With Formal Character")]
    public static void InstallNow()
    {
        string modelPath = FindFormalCharacterModel();
        if (string.IsNullOrEmpty(modelPath))
            throw new InvalidOperationException(
                "Formal Character is not imported yet. In Package Manager > My Assets, download and import Formal character first.");
        EnsureHumanoid(modelPath);
        Install(modelPath);
    }

    private static void TryInstallWhenImported()
    {
        if (File.Exists(InstallMarker) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        string modelPath = FindFormalCharacterModel();
        if (string.IsNullOrEmpty(modelPath)) return;

        EditorApplication.update -= TryInstallWhenImported;
        EnsureHumanoid(modelPath);
        Install(modelPath);
    }

    private static string FindFormalCharacterModel()
    {
        return AssetDatabase.FindAssets("t:Model")
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            .Where(path => path.IndexOf("FORMAL_AVATAR/Model/", StringComparison.OrdinalIgnoreCase) >= 0)
            .Where(path => Path.GetFileName(path).StartsWith("Formal_", StringComparison.OrdinalIgnoreCase))
            .Where(path => path.IndexOf("_Base.fbx", StringComparison.OrdinalIgnoreCase) >= 0)
            .Where(path => AssetDatabase.LoadAssetAtPath<GameObject>(path)
                ?.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
            .OrderByDescending(path => path.IndexOf("Without Mustache_Base.fbx", StringComparison.OrdinalIgnoreCase) >= 0)
            .FirstOrDefault();
    }

    private static void EnsureHumanoid(string path)
    {
        if (AssetImporter.GetAtPath(path) is not ModelImporter importer)
            throw new InvalidOperationException("Formal Character source is not an FBX model: " + path);
        if (importer.animationType != ModelImporterAnimationType.Human)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.SaveAndReimport();
        }
        if (!AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().Any(avatar => avatar && avatar.isHuman))
            throw new InvalidOperationException("Unity could not build a valid Humanoid Avatar for " + path);
    }

    private static void Install(string modelPath)
    {
        EnsureFolder(OutputFolder);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (!model) throw new InvalidOperationException("Could not load Formal Character model at " + modelPath);

        var clip = CreateSeatedIdle(model);
        var controller = CreateController(clip);
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var interviewers = UnityEngine.Object.FindObjectsByType<InterviewerController>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (interviewers.Length != 3)
            throw new InvalidOperationException($"Expected 3 interviewer slots, found {interviewers.Length}.");

        foreach (var interviewer in interviewers)
        {
            if (interviewer.AvatarRoot)
                UnityEngine.Object.DestroyImmediate(interviewer.AvatarRoot);

            var avatar = PrefabUtility.InstantiatePrefab(model, interviewer.transform) as GameObject;
            if (!avatar) throw new InvalidOperationException("Could not instantiate Formal Character.");
            avatar.name = $"FormalCharacter_{interviewer.PersonaId}_EDIT_TRANSFORM_HERE";
            avatar.transform.localPosition = Vector3.zero;
            avatar.transform.localRotation = Quaternion.identity;
            avatar.transform.localScale = Vector3.one;

            var animator = avatar.GetComponent<Animator>() ?? avatar.AddComponent<Animator>();
            if (!animator.avatar)
                animator.avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().FirstOrDefault(value => value.isHuman);
            if (!animator.avatar || !animator.avatar.isHuman)
                throw new InvalidOperationException("Formal Character does not expose a valid Humanoid Avatar.");
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            interviewer.AvatarRoot = avatar;
            interviewer.PlaceholderMouth = null;
            EditorUtility.SetDirty(interviewer);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        File.WriteAllText(InstallMarker,
            $"Formal Character source: {modelPath}\nThree scene-authored instances use looping SeatedIdle.controller.\n");
        AssetDatabase.ImportAsset(InstallMarker);
        Debug.Log("[SpeakUpXR] Formal Character now fills all three seats with an Animator-driven looping seated idle.");
    }

    private static AnimationClip CreateSeatedIdle(GameObject model)
    {
        var temporary = PrefabUtility.InstantiatePrefab(model) as GameObject;
        if (!temporary) throw new InvalidOperationException("Could not prepare Formal Character seated pose.");
        temporary.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            var animator = temporary.GetComponent<Animator>() ?? temporary.AddComponent<Animator>();
            if (!animator.avatar)
                animator.avatar = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(model))
                    .OfType<Avatar>().FirstOrDefault(value => value.isHuman);
            if (!animator.avatar || !animator.avatar.isHuman)
                throw new InvalidOperationException("Formal Character must be imported as Humanoid.");

            var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            var pose = new HumanPose();
            poseHandler.GetHumanPose(ref pose);
            SetMuscle(ref pose, "Left Upper Leg Front-Back", -0.78f);
            SetMuscle(ref pose, "Right Upper Leg Front-Back", -0.78f);
            SetMuscle(ref pose, "Left Leg Stretch", -0.82f);
            SetMuscle(ref pose, "Right Leg Stretch", -0.82f);
            SetMuscle(ref pose, "Left Foot Up-Down", 0.18f);
            SetMuscle(ref pose, "Right Foot Up-Down", 0.18f);
            SetMuscle(ref pose, "Left Arm Down-Up", -0.34f);
            SetMuscle(ref pose, "Right Arm Down-Up", 0.34f);
            SetMuscle(ref pose, "Left Arm Front-Back", -0.22f);
            SetMuscle(ref pose, "Right Arm Front-Back", -0.22f);
            SetMuscle(ref pose, "Left Forearm Stretch", -0.48f);
            SetMuscle(ref pose, "Right Forearm Stretch", -0.48f);
            SetMuscle(ref pose, "Spine Front-Back", 0.08f);
            SetMuscle(ref pose, "Chest Front-Back", 0.05f);
            poseHandler.SetHumanPose(ref pose);

            var clip = new AnimationClip { name = "SeatedIdle", frameRate = 30f, wrapMode = WrapMode.Loop };
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;
                var transform = animator.GetBoneTransform(bone);
                if (!transform) continue;
                string path = AnimationUtility.CalculateTransformPath(transform, animator.transform);
                Quaternion value = transform.localRotation;
                SetConstantCurve(clip, path, "m_LocalRotation.x", value.x);
                SetConstantCurve(clip, path, "m_LocalRotation.y", value.y);
                SetConstantCurve(clip, path, "m_LocalRotation.z", value.z);
                SetConstantCurve(clip, path, "m_LocalRotation.w", value.w);
            }

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            settings.loopBlend = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath)) AssetDatabase.DeleteAsset(ClipPath);
            AssetDatabase.CreateAsset(clip, ClipPath);
            return clip;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(temporary);
        }
    }

    private static RuntimeAnimatorController CreateController(AnimationClip clip)
    {
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath)) AssetDatabase.DeleteAsset(ControllerPath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        var stateMachine = controller.layers[0].stateMachine;
        var state = stateMachine.AddState("Seated Idle");
        state.motion = clip;
        state.speed = 1f;
        state.writeDefaultValues = true;
        stateMachine.defaultState = state;
        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static void SetMuscle(ref HumanPose pose, string muscleName, float value)
    {
        int index = Array.IndexOf(HumanTrait.MuscleName, muscleName);
        if (index >= 0) pose.muscles[index] = value;
    }

    private static void SetConstantCurve(AnimationClip clip, string path, string property, float value)
    {
        var curve = AnimationCurve.Constant(0f, 1f, value);
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), property), curve);
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
