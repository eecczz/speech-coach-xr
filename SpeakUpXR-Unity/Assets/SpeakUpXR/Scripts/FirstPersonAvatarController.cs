using System.Collections;
using UnityEngine;
using UnityEngine.XR;

namespace SpeakUpXR
{
    /// <summary>
    /// Drives the scene-authored candidate and mounts the XR rig under the humanoid head.
    /// Translation is deterministic from frame one; the walk clip supplies body/head motion
    /// but is not trusted to contain usable root translation.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator))]
    [DefaultExecutionOrder(20000)]
    public class FirstPersonAvatarController : MonoBehaviour
    {
        [Header("Scene references")]
        public Animator Animator;
        public Transform XrOrigin;
        public Transform CameraOffset;
        public Camera HeadCamera;

        [Header("First-person camera")]
        [Tooltip("Offset from the head using character axes: X=right, Y=world up, Z=character forward. " +
                 "Keep Z in front of the face mesh. Tune live while in Play Mode.")]
        public Vector3 CameraAtHeadOffset = new(0f, 0.03f, 0.18f);
        [Tooltip("Cancels the tracked standing eye height while the avatar is seated in XR.")]
        public float XrTrackedHeightCompensation = -1.65f;
        [Range(55f, 90f)] public float FieldOfView = 72f;

        [Header("XR headset → avatar head")]
        [Tooltip("Copies the HMD/camera world rotation to the candidate humanoid Head bone every LateUpdate.")]
        public bool DriveHeadFromHeadset = true;
        [Range(0f, 1f)] public float HeadsetRotationWeight = 1f;
        [Tooltip("Keeps extreme headset poses from twisting the humanoid rig beyond a natural range.")]
        public bool ClampHeadsetRotation = false;
        [Range(30f, 180f)] public float HeadYawLimit = 120f;
        [Range(20f, 90f)] public float HeadPitchLimit = 75f;

        [Header("Entrance locomotion")]
        [Tooltip("World-space walking speed. Movement begins on the same frame as the Walk crossfade.")]
        [Min(0.1f)] public float WalkSpeed = 1.25f;
        [Min(0.005f)] public float ArrivalDistance = 0.02f;
        [Min(1f)] public float MaximumWalkSeconds = 12f;
        [Min(0.1f)] public float TurnSpeed = 9f;

        [Header("Animation cues — editable during Play Mode")]
        public AnimationCueTiming Walk = new() { StateName = "Walk", CrossFadeSeconds = 0.12f };
        public AnimationCueTiming SitDown = new() { StateName = "Sit Down", CrossFadeSeconds = 0.14f };
        public AnimationCueTiming SeatedIdle = new() { StateName = "Seated Idle", CrossFadeSeconds = 0.18f };

        private Transform _headBone;
        private Quaternion _avatarForwardToHeadOffset;

        private void Awake()
        {
            if (!Animator) Animator = GetComponent<Animator>();
            if (!HeadCamera) HeadCamera = Camera.main;
            if (!XrOrigin && HeadCamera) XrOrigin = HeadCamera.transform.root;
            if (!CameraOffset && HeadCamera && HeadCamera.transform.parent)
                CameraOffset = HeadCamera.transform.parent;

            _headBone = Animator ? Animator.GetBoneTransform(HumanBodyBones.Head) : null;
            if (_headBone)
            {
                Quaternion avatarForward = Quaternion.LookRotation(transform.forward, transform.up);
                _avatarForwardToHeadOffset = Quaternion.Inverse(avatarForward) * _headBone.rotation;
            }
            AttachCameraRigToHead();
        }

        private void LateUpdate()
        {
            // Read the tracked view before touching the Head bone. XR Origin is a
            // child of that bone, so it is restored in world space below to avoid
            // applying the HMD rotation to the camera a second time.
            Quaternion avatarFacing = Quaternion.LookRotation(transform.forward, transform.up);
            Quaternion trackedLocalRotation = HeadCamera && XrOrigin
                ? Quaternion.Inverse(XrOrigin.rotation) * HeadCamera.transform.rotation
                : HeadCamera ? Quaternion.Inverse(avatarFacing) * HeadCamera.transform.rotation : Quaternion.identity;
            Quaternion trackedViewRotation = avatarFacing * trackedLocalRotation;
            ApplyTrackedHeadRotation(trackedViewRotation);

            // Inspector values intentionally remain live while tuning in Play Mode.
            if (HeadCamera) HeadCamera.fieldOfView = FieldOfView;
            if (XrOrigin && _headBone)
            {
                Vector3 worldOffset = transform.right * CameraAtHeadOffset.x
                    + Vector3.up * CameraAtHeadOffset.y
                    + transform.forward * CameraAtHeadOffset.z;
                XrOrigin.SetPositionAndRotation(
                    _headBone.position + worldOffset,
                    Quaternion.LookRotation(transform.forward, Vector3.up));
            }
            if (CameraOffset)
                CameraOffset.localPosition = XRSettings.isDeviceActive
                    ? new Vector3(0f, XrTrackedHeightCompensation, 0f)
                    : Vector3.zero;
        }

        private void ApplyTrackedHeadRotation(Quaternion trackedViewRotation)
        {
            if (!DriveHeadFromHeadset || !_headBone || HeadsetRotationWeight <= 0f) return;
            Quaternion view = trackedViewRotation;
            if (ClampHeadsetRotation)
            {
                Vector3 localForward = transform.InverseTransformDirection(view * Vector3.forward);
                float yaw = Mathf.Clamp(Mathf.Atan2(localForward.x, localForward.z) * Mathf.Rad2Deg,
                    -HeadYawLimit, HeadYawLimit);
                float pitch = Mathf.Clamp(-Mathf.Atan2(localForward.y,
                    new Vector2(localForward.x, localForward.z).magnitude) * Mathf.Rad2Deg,
                    -HeadPitchLimit, HeadPitchLimit);
                float roll = Mathf.DeltaAngle(0f, (Quaternion.Inverse(transform.rotation) * view).eulerAngles.z);
                view = transform.rotation * Quaternion.Euler(pitch, yaw, roll);
            }
            Quaternion desired = view * _avatarForwardToHeadOffset;
            _headBone.rotation = Quaternion.Slerp(_headBone.rotation, desired, HeadsetRotationWeight);
        }

        public IEnumerator EnterAndSit(Transform entrancePoint, Transform seatPoint)
        {
            if (!Animator || !entrancePoint || !seatPoint) yield break;

            transform.SetPositionAndRotation(entrancePoint.position, entrancePoint.rotation);
            Animator.applyRootMotion = false;

            // Never allow an authored cue delay to turn into a visible in-place walk.
            // The clip still starts at its inspector-selectable source frame.
            PlayCueImmediately(Animator, Walk);

            float end = Time.realtimeSinceStartup + MaximumWalkSeconds;
            while (PlanarDistance(transform.position, seatPoint.position) > ArrivalDistance &&
                   Time.realtimeSinceStartup < end)
            {
                Vector3 direction = seatPoint.position - transform.position;
                direction.y = 0f;
                if (direction.sqrMagnitude > 0.001f)
                {
                    transform.rotation = Quaternion.Slerp(transform.rotation,
                        Quaternion.LookRotation(direction.normalized, Vector3.up),
                        TurnSpeed * Time.unscaledDeltaTime);
                    transform.position = Vector3.MoveTowards(
                        transform.position,
                        new Vector3(seatPoint.position.x, transform.position.y, seatPoint.position.z),
                        WalkSpeed * Time.unscaledDeltaTime);
                }
                yield return null;
            }

            // MoveTowards leaves at most ArrivalDistance (2 cm by default), so this is
            // an imperceptible alignment rather than the old long seat-correction lerp.
            transform.SetPositionAndRotation(seatPoint.position, seatPoint.rotation);
            yield return PlayCue(Animator, SitDown, true);
            yield return PlayCue(Animator, SeatedIdle, false);
            Animator.applyRootMotion = false;
        }

        private static void PlayCueImmediately(Animator animator, AnimationCueTiming cue)
        {
            if (!animator || cue == null || string.IsNullOrWhiteSpace(cue.StateName)) return;
            animator.speed = Mathf.Max(0.1f, cue.Speed);
            animator.CrossFadeInFixedTime(cue.StateName, cue.CrossFadeSeconds, 0, cue.NormalizedStart);
        }

        private static IEnumerator PlayCue(Animator animator, AnimationCueTiming cue, bool waitForClip)
        {
            if (cue == null || string.IsNullOrWhiteSpace(cue.StateName)) yield break;
            float delayEnd = Time.realtimeSinceStartup + cue.StartDelaySeconds;
            while (Time.realtimeSinceStartup < delayEnd) yield return null;

            animator.speed = Mathf.Max(0.1f, cue.Speed);
            animator.CrossFadeInFixedTime(cue.StateName, cue.CrossFadeSeconds, 0, cue.NormalizedStart);
            if (!waitForClip) yield break;

            float duration = cue.RemainingDuration;
            if (duration <= 0f) duration = 0.7f;
            float end = Time.realtimeSinceStartup + duration;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        private void AttachCameraRigToHead()
        {
            if (!_headBone || !XrOrigin || !HeadCamera) return;
            XrOrigin.SetParent(_headBone, false);
            Vector3 worldOffset = transform.right * CameraAtHeadOffset.x
                + Vector3.up * CameraAtHeadOffset.y
                + transform.forward * CameraAtHeadOffset.z;
            XrOrigin.SetPositionAndRotation(
                _headBone.position + worldOffset,
                Quaternion.LookRotation(transform.forward, Vector3.up));
            if (CameraOffset)
            {
                CameraOffset.localPosition = Vector3.zero;
                CameraOffset.localRotation = Quaternion.identity;
            }
            HeadCamera.transform.localPosition = Vector3.zero;
            HeadCamera.transform.localRotation = Quaternion.identity;
            HeadCamera.nearClipPlane = 0.05f;
            HeadCamera.fieldOfView = FieldOfView;
        }

        private static float PlanarDistance(Vector3 a, Vector3 b)
        {
            a.y = b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
