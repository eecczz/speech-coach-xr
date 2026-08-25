using System.IO;
using System.Linq;
using SpeakUpXR;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs;

/// <summary>
/// Turns the scene's former name-only XR hierarchy into an inspectable, standard
/// OpenXR/XRI rig. The objects remain authored in the scene and are never spawned
/// by gameplay code.
/// </summary>
[InitializeOnLoad]
public static class XrSceneRigAuthoringUpgrade
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private const string MarkerPath = "Assets/SpeakUpXR/UI/xr-scene-rig-authored-v1.txt";
    private static bool _running;

    static XrSceneRigAuthoringUpgrade() => EditorApplication.delayCall += TryUpgrade;

    [InitializeOnLoadMethod]
    private static void ScheduleAfterReload()
    {
        EditorApplication.delayCall -= TryUpgrade;
        EditorApplication.delayCall += TryUpgrade;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredEditMode)
            EditorApplication.delayCall += TryUpgrade;
    }

    [MenuItem("SpeakUpXR/Author Standard OpenXR Rig In Interview Scene")]
    public static void UpgradeNow() => Upgrade();

    private static void TryUpgrade()
    {
        if (_running || File.Exists(MarkerPath) || EditorApplication.isCompiling ||
            EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (File.Exists(ScenePath)) Upgrade();
    }

    private static void Upgrade()
    {
        _running = true;
        try
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var originObject = GameObject.Find("XR Origin_EDIT_POSITION_WITH_WAYPOINTS");
            var camera = Camera.main ?? Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Include);
            if (!originObject || !camera) throw new MissingReferenceException("Interview XR Origin/Main Camera was not found.");

            var cameraOffset = camera.transform.parent ? camera.transform.parent.gameObject : originObject;
            var xrOrigin = originObject.GetComponent<XROrigin>() ?? originObject.AddComponent<XROrigin>();
            xrOrigin.Origin = originObject;
            xrOrigin.Camera = camera;
            xrOrigin.CameraFloorOffsetObject = cameraOffset;
            xrOrigin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.NotSpecified;
            xrOrigin.CameraYOffset = 0f;
            ConfigurePoseDriver(camera.gameObject, "<XRHMD>", "centerEye");

            var left = FindOrCreate("Left Controller (OpenXR Tracked)", cameraOffset.transform);
            var right = FindOrCreate("Right Controller (OpenXR Tracked)", cameraOffset.transform);
            ConfigurePoseDriver(left, "<XRController>{LeftHand}", "device");
            ConfigurePoseDriver(right, "<XRController>{RightHand}", "device");

            var managerObject = GameObject.Find("XR Interaction Manager_EDIT_XR_HERE")
                ?? new GameObject("XR Interaction Manager_EDIT_XR_HERE");
            var interactionManager = managerObject.GetComponent<XRInteractionManager>()
                ?? managerObject.AddComponent<XRInteractionManager>();
            var modality = managerObject.GetComponent<XRInputModalityManager>()
                ?? managerObject.AddComponent<XRInputModalityManager>();
            modality.leftController = left;
            modality.rightController = right;

            var candidate = Object.FindFirstObjectByType<FirstPersonAvatarController>(FindObjectsInactive.Include);
            var upperBody = candidate ? candidate.GetComponent<XrUpperBodyAvatarDriver>() : null;
            if (upperBody)
            {
                upperBody.LeftHandTarget = left.transform;
                upperBody.RightHandTarget = right.transform;
                EditorUtility.SetDirty(upperBody);
            }

            var panel = Object.FindFirstObjectByType<InterviewerPanel>(FindObjectsInactive.Include);
            if (panel && panel.Members != null)
            {
                foreach (var member in panel.Members.Where(value => value))
                {
                    member.CharacterAnimator = member.AvatarRoot
                        ? member.AvatarRoot.GetComponentInChildren<Animator>(true)
                        : null;
                    member.UseFullBodySpeakingGesture = false;
                    member.SubtleSpeakingNodDegrees = 1.8f;
                    if (member.CharacterAnimator)
                    {
                        member.CharacterAnimator.applyRootMotion = false;
                        member.CharacterAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                        var tracker = member.CharacterAnimator.GetComponent<InterviewerHeadTracker>()
                            ?? member.CharacterAnimator.gameObject.AddComponent<InterviewerHeadTracker>();
                        tracker.Animator = member.CharacterAnimator;
                        tracker.Target = camera.transform;
                        tracker.AvatarFacingRoot = member.AvatarRoot.transform;
                        tracker.Weight = 1f;
                        tracker.LockEveryFrame = true;
                        EditorUtility.SetDirty(tracker);
                    }
                    EditorUtility.SetDirty(member);
                }
            }

            var entrance = Object.FindFirstObjectByType<InterviewEntranceSequence>(FindObjectsInactive.Include);
            if (entrance) { entrance.GreetingPauseSeconds = 0f; EditorUtility.SetDirty(entrance); }
            if (candidate)
            {
                candidate.WalkSpeed = 1.25f;
                candidate.ArrivalDistance = 0.02f;
                candidate.Walk.StartDelaySeconds = 0f;
                EditorUtility.SetDirty(candidate);
            }

            EditorUtility.SetDirty(xrOrigin);
            EditorUtility.SetDirty(interactionManager);
            EditorUtility.SetDirty(modality);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            File.WriteAllText(MarkerPath,
                "Standard XROrigin + bound HMD/left/right TrackedPoseDriver + XRInteractionManager.\n" +
                "Three independent interviewer Animators; persistent LateUpdate head tracking; subtle speaking nods.\n");
            AssetDatabase.ImportAsset(MarkerPath);
            Debug.Log("[SpeakUpXR] Standard OpenXR rig and restrained independent interviewer turns authored in Interview scene.");
        }
        finally { _running = false; }
    }

    private static GameObject FindOrCreate(string name, Transform parent)
    {
        var found = GameObject.Find(name) ?? new GameObject(name);
        found.transform.SetParent(parent, false);
        found.transform.localPosition = Vector3.zero;
        found.transform.localRotation = Quaternion.identity;
        return found;
    }

    private static void ConfigurePoseDriver(GameObject target, string devicePath, string posePrefix)
    {
        var driver = target.GetComponent<TrackedPoseDriver>() ?? target.AddComponent<TrackedPoseDriver>();
        var position = new InputAction("Position", InputActionType.Value,
            $"{devicePath}/{posePrefix}Position", expectedControlType: "Vector3");
        var rotation = new InputAction("Rotation", InputActionType.Value,
            $"{devicePath}/{posePrefix}Rotation", expectedControlType: "Quaternion");
        var tracking = new InputAction("Tracking State", InputActionType.Value,
            $"{devicePath}/trackingState", expectedControlType: "Integer");
        driver.positionInput = new InputActionProperty(position);
        driver.rotationInput = new InputActionProperty(rotation);
        driver.trackingStateInput = new InputActionProperty(tracking);
        driver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
        driver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
        driver.ignoreTrackingState = false;
        EditorUtility.SetDirty(driver);
    }
}
