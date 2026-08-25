using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// One-shot author-time migration from the generated placeholder room to the office-building
/// instance already positioned by the user. It preserves the user's building transform and
/// only refines the interview anchors around the currently placed characters.
/// </summary>
[InitializeOnLoad]
public static class NewOfficeInterviewRoomUpgrade
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private const string EnvironmentAssetRoot = "Assets/Enviroment/";
    private const string OldRoomName = "InterviewRoom_EDIT_ME";
    private const string SetupRootName = "InterviewSetup_NEW_OFFICE_EDIT_HERE";
    private const string BuildingName = "OfficeBuilding_NEW_ENVIRONMENT_EDIT_HERE";
    private const string MarkerPath = "Assets/SpeakUpXR/UI/new-office-interview-layout-v1.txt";
    private static readonly string[] LegacyPanelProps =
        { "DeskTop", "DeskFront", "Chair_1", "Chair_2", "Chair_3" };
    private static bool _running;

    static NewOfficeInterviewRoomUpgrade() => EditorApplication.delayCall += TryUpgradeActiveScene;

    [MenuItem("SpeakUpXR/Apply Interview Systems To New Office Building")]
    public static void UpgradeNow()
    {
        if (SceneManager.GetActiveScene().path != ScenePath)
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Upgrade(force: true);
    }

    private static void TryUpgradeActiveScene()
    {
        if (_running || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (SceneManager.GetActiveScene().path != ScenePath) return;
        if (File.Exists(MarkerPath) && !GameObject.Find(OldRoomName) && !HasLegacyPanelProps()) return;
        Upgrade(force: false);
    }

    private static void Upgrade(bool force)
    {
        if (_running) return;
        _running = true;
        try
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject building = FindEnvironmentRoot(scene);
            if (!building)
            {
                if (force) Debug.LogError("[SpeakUpXR] No scene object backed by Assets/Enviroment was found. Place the office-building prefab first.");
                return;
            }

            var panel = UnityEngine.Object.FindFirstObjectByType<InterviewerPanel>(FindObjectsInactive.Include);
            var entrance = UnityEngine.Object.FindFirstObjectByType<InterviewEntranceSequence>(FindObjectsInactive.Include);
            var candidate = UnityEngine.Object.FindFirstObjectByType<FirstPersonAvatarController>(FindObjectsInactive.Include);
            Camera headCamera = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Include);
            if (!panel || !entrance || !entrance.EntrancePoint || !entrance.SeatPoint || !candidate || !headCamera)
            {
                Debug.LogError("[SpeakUpXR] New-office migration stopped: panel, entrance points, candidate, or main camera is missing.");
                return;
            }

            GameObject oldRoom = GameObject.Find(OldRoomName);
            if (oldRoom)
            {
                // This is the exact generated placeholder-room root. The imported office
                // building was found first, so an absent/misidentified replacement can never
                // cause the old environment to be removed.
                UnityEngine.Object.DestroyImmediate(oldRoom);
            }

            building.name = BuildingName;
            building.SetActive(true);

            GameObject setupRoot = GameObject.Find(SetupRootName) ?? new GameObject(SetupRootName);
            MoveUnderPreservingWorld(panel.transform, setupRoot.transform);
            MoveUnderPreservingWorld(entrance.transform, setupRoot.transform);
            RemoveLegacyPanelProps(panel.transform);

            RefineSeatAndPanel(panel, entrance, candidate, headCamera.transform);
            WireRuntimeSystems(panel, entrance, candidate, headCamera.transform);

            var layout = setupRoot.GetComponent<NewOfficeInterviewLayout>() ??
                         setupRoot.AddComponent<NewOfficeInterviewLayout>();
            layout.OfficeBuilding = building;
            layout.EntranceSequence = entrance;
            layout.EntrancePoint = entrance.EntrancePoint;
            layout.SeatPoint = entrance.SeatPoint;
            layout.Candidate = candidate;
            layout.Panel = panel;
            layout.InterviewerSpacing = CalculateCurrentSpacing(panel);

            EditorUtility.SetDirty(building);
            EditorUtility.SetDirty(setupRoot);
            EditorUtility.SetDirty(layout);
            EditorUtility.SetDirty(panel);
            EditorUtility.SetDirty(entrance);
            EditorUtility.SetDirty(candidate);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath) ?? "Assets/SpeakUpXR/UI");
            File.WriteAllText(MarkerPath,
                "New office building retained; generated InterviewRoom_EDIT_ME removed.\n" +
                "Generated desk and chair primitives removed; the office-building furniture remains.\n" +
                "Interview systems are grouped under InterviewSetup_NEW_OFFICE_EDIT_HERE.\n" +
                "Entrance door is intentionally unassigned until a specific building door pivot is selected.\n");
            AssetDatabase.ImportAsset(MarkerPath);
            Debug.Log("[SpeakUpXR] New office interview layout applied: placeholder room removed, three interviewers aligned, gaze/XR/TTS references refreshed, and scene saved.");
        }
        finally
        {
            _running = false;
        }
    }

    private static GameObject FindEnvironmentRoot(Scene scene)
    {
        GameObject best = null;
        int bestScore = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            int score = EnvironmentScore(root);
            if (score <= bestScore) continue;
            best = root;
            bestScore = score;
        }
        return best;
    }

    private static int EnvironmentScore(GameObject root)
    {
        int score = IsEnvironmentPath(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root)) ? 10000 : 0;
        foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            if (filter.sharedMesh && IsEnvironmentPath(AssetDatabase.GetAssetPath(filter.sharedMesh))) score++;
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (renderer.sharedMesh && IsEnvironmentPath(AssetDatabase.GetAssetPath(renderer.sharedMesh))) score++;
        return score;
    }

    private static bool IsEnvironmentPath(string path) =>
        !string.IsNullOrEmpty(path) && path.Replace('\\', '/').StartsWith(EnvironmentAssetRoot, StringComparison.OrdinalIgnoreCase);

    private static bool HasLegacyPanelProps()
    {
        var panel = UnityEngine.Object.FindFirstObjectByType<InterviewerPanel>(FindObjectsInactive.Include);
        return panel && LegacyPanelProps.Any(name => panel.transform.Find(name));
    }

    private static void RemoveLegacyPanelProps(Transform panel)
    {
        foreach (string name in LegacyPanelProps)
        {
            Transform legacy = panel.Find(name);
            if (legacy) UnityEngine.Object.DestroyImmediate(legacy.gameObject);
        }
    }

    private static void RefineSeatAndPanel(InterviewerPanel panel, InterviewEntranceSequence entrance,
        FirstPersonAvatarController candidate, Transform lookTarget)
    {
        var members = panel.Members?.Where(member => member).ToArray() ?? Array.Empty<InterviewerController>();
        if (members.Length == 0) return;

        Vector3 center = Vector3.zero;
        foreach (var member in members) center += Visual(member).position;
        center /= members.Length;

        Vector3 centerToSeat = Flat(entrance.SeatPoint.position - center);
        float currentDistance = centerToSeat.magnitude;
        if (currentDistance < 0.01f)
            centerToSeat = Flat(-Visual(members[0]).forward);
        if (centerToSeat.sqrMagnitude < 0.01f) centerToSeat = Vector3.back;
        Vector3 towardCandidate = centerToSeat.normalized;

        // If the approximate seat was left implausibly near/far from the panel, keep its
        // chosen side and floor height but bring it into a close, pressure-inducing range.
        if (currentDistance < 1.35f || currentDistance > 3.5f)
        {
            Vector3 seat = center + towardCandidate * 2.25f;
            seat.y = entrance.SeatPoint.position.y;
            entrance.SeatPoint.position = seat;
        }

        Vector3 seatToPanel = Flat(center - entrance.SeatPoint.position);
        if (seatToPanel.sqrMagnitude > 0.001f)
            entrance.SeatPoint.rotation = Quaternion.LookRotation(seatToPanel.normalized, Vector3.up);

        Vector3 entranceToSeat = Flat(entrance.SeatPoint.position - entrance.EntrancePoint.position);
        if (entranceToSeat.sqrMagnitude > 0.001f)
            entrance.EntrancePoint.rotation = Quaternion.LookRotation(entranceToSeat.normalized, Vector3.up);

        Vector3 right = Vector3.Cross(Vector3.up, towardCandidate).normalized;
        var ordered = members.OrderBy(member => Vector3.Dot(Visual(member).position - center, right)).ToArray();
        float spacing = CalculateCurrentSpacing(panel);
        for (int i = 0; i < ordered.Length; i++)
        {
            var member = ordered[i];
            Transform visual = Visual(member);
            float slot = i - (ordered.Length - 1) * 0.5f;
            Vector3 desired = center + right * (slot * spacing);
            if (ordered.Length == 3 && i != 1) desired += towardCandidate * 0.08f;
            desired.y = visual.position.y;

            Vector3 delta = desired - visual.position;
            delta.y = 0f;
            delta = Vector3.ClampMagnitude(delta, 0.35f);
            member.transform.position += delta;

            Vector3 faceDirection = Flat(entrance.SeatPoint.position - visual.position);
            if (faceDirection.sqrMagnitude > 0.001f)
                visual.rotation = Quaternion.LookRotation(faceDirection.normalized, Vector3.up);

            Transform chair = panel.transform.Find("Chair_" + ((int)member.Personality + 1));
            if (chair) chair.position += delta;
            PositionNameplate(panel.transform, member, visual, entrance.SeatPoint.position);
        }

        candidate.transform.SetPositionAndRotation(entrance.EntrancePoint.position, entrance.EntrancePoint.rotation);
        candidate.HeadCamera = lookTarget.GetComponent<Camera>() ?? Camera.main;
    }

    private static void PositionNameplate(Transform panel, InterviewerController member, Transform visual, Vector3 seat)
    {
        Transform plate = panel.Find("NamePlate_" + member.DisplayName);
        if (!plate)
        {
            plate = panel.Cast<Transform>()
                .FirstOrDefault(child => child.name.StartsWith("NamePlate_", StringComparison.Ordinal) &&
                                         child.name.Contains(RoleKeyword(member.Personality), StringComparison.Ordinal));
        }
        if (!plate) return;

        Vector3 towardSeat = Flat(seat - visual.position);
        if (towardSeat.sqrMagnitude < 0.001f) towardSeat = -visual.forward;
        towardSeat.Normalize();
        plate.position = visual.position + towardSeat * 0.72f + Vector3.up * 0.92f;

        // Unity world-space Canvas is read from its -forward side. Point +forward away
        // from the candidate so the text remains readable rather than mirrored.
        Vector3 awayFromSeat = Flat(plate.position - seat);
        if (awayFromSeat.sqrMagnitude > 0.001f)
            plate.rotation = Quaternion.LookRotation(awayFromSeat.normalized, Vector3.up);
        EditorUtility.SetDirty(plate);
    }

    private static void WireRuntimeSystems(InterviewerPanel panel, InterviewEntranceSequence entrance,
        FirstPersonAvatarController candidate, Transform lookTarget)
    {
        entrance.PlayerAvatar = candidate;
        entrance.XrOrigin = candidate.XrOrigin;
        // The imported building contains many unrelated doors. Keep this explicit in the
        // Inspector instead of rotating whichever mesh happens to be nearest.
        entrance.Door = null;

        var usedAnimators = new HashSet<Animator>();
        foreach (var member in panel.Members)
        {
            if (!member) continue;
            member.LookTarget = lookTarget;
            member.CharacterAnimator = member.AvatarRoot
                ? member.AvatarRoot.GetComponentInChildren<Animator>(true)
                : member.GetComponentInChildren<Animator>(true);
            if (member.CharacterAnimator)
            {
                member.CharacterAnimator.applyRootMotion = false;
                member.CharacterAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                if (!usedAnimators.Add(member.CharacterAnimator))
                    Debug.LogError("[SpeakUpXR] Interviewers still share an Animator; assign a separate avatar instance to each seat.", member);

                var tracker = member.CharacterAnimator.GetComponent<InterviewerHeadTracker>() ??
                              member.CharacterAnimator.gameObject.AddComponent<InterviewerHeadTracker>();
                tracker.Animator = member.CharacterAnimator;
                tracker.Target = lookTarget;
                tracker.AvatarFacingRoot = member.AvatarRoot ? member.AvatarRoot.transform : member.transform;
                tracker.LockEveryFrame = true;
                EditorUtility.SetDirty(tracker);
            }

            member.UseFullBodySpeakingGesture = false;
            if (!member.VoiceSource) member.VoiceSource = member.GetComponent<AudioSource>() ?? member.gameObject.AddComponent<AudioSource>();
            member.VoiceSource.spatialBlend = 1f;
            member.VoiceSource.minDistance = 0.6f;
            member.VoiceSource.maxDistance = 8f;
            EditorUtility.SetDirty(member);
        }

        var casting = UnityEngine.Object.FindFirstObjectByType<VoiceCastingController>(FindObjectsInactive.Include);
        if (casting)
        {
            casting.Panel = panel;
            casting.ApplySelection();
            EditorUtility.SetDirty(casting);
        }

        var feedback = UnityEngine.Object.FindFirstObjectByType<XrRealtimeFeedbackController>(FindObjectsInactive.Include);
        if (feedback) EditorUtility.SetDirty(feedback);
    }

    private static float CalculateCurrentSpacing(InterviewerPanel panel)
    {
        var positions = panel.Members?.Where(member => member).Select(member => Visual(member).position).ToArray()
                        ?? Array.Empty<Vector3>();
        if (positions.Length < 2) return 1.05f;
        float max = 0f;
        for (int i = 0; i < positions.Length; i++)
            for (int j = i + 1; j < positions.Length; j++)
                max = Mathf.Max(max, Flat(positions[i] - positions[j]).magnitude);
        return Mathf.Clamp(max / (positions.Length - 1), 0.9f, 1.2f);
    }

    private static Transform Visual(InterviewerController member) =>
        member.AvatarRoot ? member.AvatarRoot.transform : member.transform;

    private static Vector3 Flat(Vector3 value)
    {
        value.y = 0f;
        return value;
    }

    private static string RoleKeyword(InterviewerPersonality personality) =>
        personality == InterviewerPersonality.Analytical ? "기술" :
        personality == InterviewerPersonality.Challenging ? "임원" : "인사";

    private static void MoveUnderPreservingWorld(Transform child, Transform parent)
    {
        if (child.parent == parent) return;
        child.SetParent(parent, true);
        EditorUtility.SetDirty(child);
    }
}
