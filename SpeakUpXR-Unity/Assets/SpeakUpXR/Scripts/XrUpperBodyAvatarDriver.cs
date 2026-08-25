using UnityEngine;
using UnityEngine.XR;

namespace SpeakUpXR
{
    /// <summary>
    /// Retargets HMD + controller/hand poses to a Humanoid candidate. The headset
    /// drives Head in FirstPersonAvatarController; this component drives hands,
    /// elbows and an inferred chest/spine pose through Animator IK.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator))]
    [DefaultExecutionOrder(15000)]
    public sealed class XrUpperBodyAvatarDriver : MonoBehaviour
    {
        public Animator Animator;
        public Camera HeadCamera;
        [Tooltip("Optional scene targets. XR device poses are used when these are empty.")]
        public Transform LeftHandTarget;
        public Transform RightHandTarget;

        [Header("Hand IK")]
        public bool DriveHands = true;
        [Range(0f, 1f)] public float HandPositionWeight = 1f;
        [Range(0f, 1f)] public float HandRotationWeight = 1f;
        [Range(0f, 1f)] public float ElbowHintWeight = 0.65f;
        public Vector3 LeftHandRotationOffset = new(0f, 0f, 90f);
        public Vector3 RightHandRotationOffset = new(0f, 0f, -90f);
        [Min(0.01f)] public float ElbowOutwardMeters = 0.18f;
        [Min(0.01f)] public float ElbowDownMeters = 0.16f;

        [Header("Inferred upper body")]
        public bool DriveUpperBody = true;
        [Range(0f, 1f)] public float ChestYawWeight = 0.32f;
        [Range(0f, 1f)] public float ChestPitchWeight = 0.18f;
        [Range(0f, 1f)] public float ChestRollWeight = 0.28f;
        [Range(0f, 40f)] public float LeanDegreesPerMeter = 22f;
        [Range(5f, 45f)] public float MaximumChestAngle = 24f;

        public bool LeftHandTracked { get; private set; }
        public bool RightHandTracked { get; private set; }

        private Transform _chest;
        private Vector3 _headTrackingBaseline;
        private bool _calibrated;

        private void Awake()
        {
            if (!Animator) Animator = GetComponent<Animator>();
            if (!HeadCamera) HeadCamera = Camera.main;
            if (Animator && Animator.isHuman)
                _chest = Animator.GetBoneTransform(HumanBodyBones.Chest)
                    ?? Animator.GetBoneTransform(HumanBodyBones.UpperChest)
                    ?? Animator.GetBoneTransform(HumanBodyBones.Spine);
            Recalibrate();
        }

        public void Recalibrate()
        {
            _calibrated = TryGetRawPose(XRNode.Head, out _headTrackingBaseline, out _);
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (!Animator || !Animator.isHuman) return;
            bool headTracked = TryGetRawPose(XRNode.Head, out Vector3 rawHeadPosition, out Quaternion rawHeadRotation);
            if (!_calibrated && headTracked)
            {
                _headTrackingBaseline = rawHeadPosition;
                _calibrated = true;
            }
            if (DriveUpperBody && _chest && _calibrated && headTracked)
                ApplyUpperBody(rawHeadPosition, rawHeadRotation);

            LeftHandTracked = ResolveHandPose(XRNode.LeftHand, LeftHandTarget, out Vector3 leftPosition, out Quaternion leftRotation);
            RightHandTracked = ResolveHandPose(XRNode.RightHand, RightHandTarget, out Vector3 rightPosition, out Quaternion rightRotation);
            ApplyHand(AvatarIKGoal.LeftHand, AvatarIKHint.LeftElbow, LeftHandTracked, leftPosition,
                leftRotation * Quaternion.Euler(LeftHandRotationOffset), -1f);
            ApplyHand(AvatarIKGoal.RightHand, AvatarIKHint.RightElbow, RightHandTracked, rightPosition,
                rightRotation * Quaternion.Euler(RightHandRotationOffset), 1f);
        }

        private void ApplyUpperBody(Vector3 rawHeadPosition, Quaternion rawHeadRotation)
        {
            Vector3 delta = rawHeadPosition - _headTrackingBaseline;
            Quaternion relative = Quaternion.Inverse(transform.rotation) * rawHeadRotation;
            Vector3 euler = SignedEuler(relative.eulerAngles);
            float yaw = Mathf.Clamp(euler.y * ChestYawWeight, -MaximumChestAngle, MaximumChestAngle);
            float pitch = Mathf.Clamp(euler.x * ChestPitchWeight - delta.z * LeanDegreesPerMeter,
                -MaximumChestAngle, MaximumChestAngle);
            float roll = Mathf.Clamp(euler.z * ChestRollWeight - delta.x * LeanDegreesPerMeter,
                -MaximumChestAngle, MaximumChestAngle);
            _chest.localRotation *= Quaternion.Euler(pitch, yaw, roll);
        }

        private void ApplyHand(AvatarIKGoal goal, AvatarIKHint hint, bool tracked, Vector3 position,
            Quaternion rotation, float side)
        {
            float enabled = DriveHands && tracked ? 1f : 0f;
            Animator.SetIKPositionWeight(goal, HandPositionWeight * enabled);
            Animator.SetIKRotationWeight(goal, HandRotationWeight * enabled);
            Animator.SetIKHintPositionWeight(hint, ElbowHintWeight * enabled);
            if (enabled <= 0f) return;
            Animator.SetIKPosition(goal, position);
            Animator.SetIKRotation(goal, rotation);
            Vector3 elbow = Vector3.Lerp(_chest ? _chest.position : transform.position, position, 0.46f)
                + transform.right * (side * ElbowOutwardMeters) - transform.up * ElbowDownMeters;
            Animator.SetIKHintPosition(hint, elbow);
        }

        private bool ResolveHandPose(XRNode node, Transform sceneTarget, out Vector3 position, out Quaternion rotation)
        {
            if (sceneTarget)
            {
                position = sceneTarget.position;
                rotation = sceneTarget.rotation;
                return sceneTarget.gameObject.activeInHierarchy;
            }
            return TryGetWorldPose(node, out position, out rotation);
        }

        private bool TryGetWorldPose(XRNode node, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            if (!HeadCamera || !TryGetRawPose(XRNode.Head, out Vector3 headPosition, out _) ||
                !TryGetRawPose(node, out Vector3 localPosition, out Quaternion localRotation)) return false;
            // OpenXR device poses share tracking-origin coordinates. Map that space
            // with the avatar root, not the already head-driven camera rotation.
            Quaternion trackingToWorldRotation = transform.rotation;
            position = HeadCamera.transform.position + trackingToWorldRotation * (localPosition - headPosition);
            rotation = trackingToWorldRotation * localRotation;
            return true;
        }

        private static bool TryGetRawPose(XRNode node, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            var device = InputDevices.GetDeviceAtXRNode(node);
            bool positionValid = device.isValid && device.TryGetFeatureValue(CommonUsages.devicePosition, out position);
            bool rotationValid = device.isValid && device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
            if (!positionValid) position = default;
            if (!rotationValid) rotation = Quaternion.identity;
            return positionValid && rotationValid;
        }

        private static Vector3 SignedEuler(Vector3 euler) => new(
            Mathf.DeltaAngle(0f, euler.x), Mathf.DeltaAngle(0f, euler.y), Mathf.DeltaAngle(0f, euler.z));
    }
}
