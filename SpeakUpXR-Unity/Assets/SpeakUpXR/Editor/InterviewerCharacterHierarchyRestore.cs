using System;
using System.IO;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Restores the Formal Character interviewers as the active, scene-authored
/// avatars. The three Blender-rigged Sketchfab models remain available as
/// disabled children so they can be inspected or swapped from the Inspector.
/// </summary>
internal static class InterviewerCharacterHierarchyRestore
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private const string FormalModelPath =
        "Assets/FORMAL_AVATAR/Model/Formal_Without Mustache_Base.fbx";
    private const string ReportPath =
        "Assets/SpeakUpXR/UI/interviewer-character-hierarchy-v2.txt";
    private const string ConvaiSceneAvatarReportPath =
        "Assets/SpeakUpXR/UI/convai-scene-avatar-install-v1.txt";

    [InitializeOnLoadMethod]
    private static void ScheduleRestore()
    {
        EditorApplication.delayCall += RestoreIfNeeded;
    }

    [MenuItem("SpeakUpXR/Restore Formal Interviewers And Keep Rigged Alternates")]
    private static void RestoreFromMenu()
    {
        Restore(force: true);
    }

    private static void RestoreIfNeeded()
    {
        // A later user-authorized migration deliberately replaces the active Formal
        // avatars with face-rigged Convai scene avatars. Do not undo that migration
        // on every script reload; the menu item remains available for manual rollback.
        if (File.Exists(ConvaiSceneAvatarReportPath)) return;
        Restore(force: false);
    }

    private static void Restore(bool force)
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        GameObject formalModel = AssetDatabase.LoadAssetAtPath<GameObject>(FormalModelPath);
        if (!formalModel)
        {
            Debug.LogWarning("[SpeakUpXR] Formal Character model is missing; interviewer hierarchy was not changed.");
            return;
        }

        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        bool openedForRestore = !scene.IsValid() || !scene.isLoaded;
        if (openedForRestore)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);

        try
        {
            InterviewerController[] interviewers = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<InterviewerController>(true))
                .OrderBy(controller => controller.PersonaId)
                .ToArray();

            if (interviewers.Length != 3)
                throw new InvalidOperationException($"Expected 3 interviewer slots, found {interviewers.Length}.");

            bool needsRestore = interviewers.Any(controller =>
                !controller.AvatarRoot ||
                !controller.AvatarRoot.name.StartsWith("FormalCharacter_", StringComparison.Ordinal));
            if (!force && !needsRestore)
                return;

            foreach (InterviewerController interviewer in interviewers)
                RestoreInterviewer(interviewer, formalModel);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            WriteReport(interviewers);
            Debug.Log("[SpeakUpXR] Formal interviewers restored. Rigged alternates are disabled children in Interview.unity.");
        }
        finally
        {
            if (openedForRestore && scene.IsValid() && scene.isLoaded)
                EditorSceneManager.CloseScene(scene, removeScene: true);
        }
    }

    private static void RestoreInterviewer(InterviewerController interviewer, GameObject formalModel)
    {
        GameObject currentAvatar = interviewer.AvatarRoot;
        RuntimeAnimatorController runtimeController = interviewer.CharacterAnimator
            ? interviewer.CharacterAnimator.runtimeAnimatorController
            : currentAvatar
                ? currentAvatar.GetComponentInChildren<Animator>(true)?.runtimeAnimatorController
                : null;

        GameObject formalAvatar = PrefabUtility.InstantiatePrefab(formalModel, interviewer.transform) as GameObject;
        if (!formalAvatar)
            throw new InvalidOperationException($"Could not instantiate Formal Character for {interviewer.PersonaId}.");

        formalAvatar.name = $"FormalCharacter_{interviewer.PersonaId}_EDIT_TRANSFORM_HERE";
        formalAvatar.transform.localPosition = Vector3.zero;
        formalAvatar.transform.localRotation = Quaternion.identity;
        formalAvatar.transform.localScale = Vector3.one;
        formalAvatar.SetActive(true);

        Animator animator = formalAvatar.GetComponentInChildren<Animator>(true);
        if (!animator || !animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman)
            throw new InvalidOperationException($"Formal Character is not a valid Humanoid for {interviewer.PersonaId}.");

        animator.runtimeAnimatorController = runtimeController;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        InterviewerHeadTracker tracker = animator.GetComponent<InterviewerHeadTracker>() ??
                                         animator.gameObject.AddComponent<InterviewerHeadTracker>();
        tracker.Animator = animator;
        tracker.Target = interviewer.LookTarget;
        tracker.AvatarFacingRoot = formalAvatar.transform;
        tracker.LockEveryFrame = true;
        tracker.Rebind();

        if (currentAvatar && currentAvatar != formalAvatar)
        {
            currentAvatar.name = RemoveAlternateSuffix(currentAvatar.name) + "_ALT_DISABLED";
            currentAvatar.transform.SetParent(formalAvatar.transform, false);
            currentAvatar.transform.localPosition = Vector3.zero;
            currentAvatar.transform.localRotation = Quaternion.identity;
            currentAvatar.transform.localScale = Vector3.one;
            currentAvatar.SetActive(false);
            EditorUtility.SetDirty(currentAvatar);
        }

        interviewer.AvatarRoot = formalAvatar;
        interviewer.CharacterAnimator = animator;
        interviewer.PlaceholderMouth = null;
        EditorUtility.SetDirty(interviewer);
        EditorUtility.SetDirty(animator);
        EditorUtility.SetDirty(tracker);
    }

    private static string RemoveAlternateSuffix(string value)
    {
        const string suffix = "_ALT_DISABLED";
        return value.EndsWith(suffix, StringComparison.Ordinal)
            ? value.Substring(0, value.Length - suffix.Length)
            : value;
    }

    private static void WriteReport(InterviewerController[] interviewers)
    {
        string[] lines = interviewers.Select(interviewer =>
        {
            GameObject active = interviewer.AvatarRoot;
            Transform alternate = active
                ? active.transform.Cast<Transform>().FirstOrDefault(child =>
                    child.name.EndsWith("_ALT_DISABLED", StringComparison.Ordinal))
                : null;
            return $"{interviewer.PersonaId}: active={active?.name ?? "MISSING"}; " +
                   $"alternate={alternate?.name ?? "MISSING"}; " +
                   $"alternateActive={alternate?.gameObject.activeSelf.ToString() ?? "N/A"}";
        }).Prepend(
            "Interview.unity character hierarchy: Formal Character active; Blender-rigged alternate disabled")
          .ToArray();

        File.WriteAllLines(ReportPath, lines);
        AssetDatabase.ImportAsset(ReportPath, ImportAssetOptions.ForceUpdate);
    }
}
