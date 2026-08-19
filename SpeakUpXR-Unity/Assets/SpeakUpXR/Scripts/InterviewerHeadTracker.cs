using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>
    /// Applies interviewer eye-line after every Animator update. It intentionally
    /// lives on each avatar Animator rather than on a dialogue state controller.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    [DisallowMultipleComponent]
    public sealed class InterviewerHeadTracker : MonoBehaviour
    {
        public Animator Animator;
        public Transform Target;
        public Transform AvatarFacingRoot;
        [Range(0f, 1f)] public float Weight = 1f;
        [Range(10f, 80f)] public float YawLimit = 52f;
        [Range(5f, 45f)] public float PitchLimit = 28f;
        [Min(0.1f)] public float TurnSpeed = 10f;
        [Tooltip("Apply the final LookRotation every LateUpdate after Animator evaluation.")]
        public bool LockEveryFrame = true;

        private Transform _head;
        private Quaternion _forwardToHeadOffset;

        private void OnEnable() => Rebind();

        public void Rebind()
        {
            if (!Animator) Animator = GetComponent<Animator>();
            if (!AvatarFacingRoot) AvatarFacingRoot = Animator ? Animator.transform : transform;
            if (!Animator || !Animator.isHuman) return;
            Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _head = Animator.GetBoneTransform(HumanBodyBones.Head);
            if (_head)
            {
                Quaternion forward = Quaternion.LookRotation(AvatarFacingRoot.forward, AvatarFacingRoot.up);
                _forwardToHeadOffset = Quaternion.Inverse(forward) * _head.rotation;
            }
        }

        private void LateUpdate()
        {
            if (!_head) Rebind();
            if (!Target && Camera.main) Target = Camera.main.transform;
            if (!_head || !Target || !AvatarFacingRoot || Weight <= 0f) return;

            Vector3 local = AvatarFacingRoot.InverseTransformDirection(Target.position - _head.position);
            if (local.sqrMagnitude < 0.0001f) return;
            float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -YawLimit, YawLimit);
            float pitch = Mathf.Clamp(-Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg, -PitchLimit, PitchLimit);
            Quaternion look = AvatarFacingRoot.rotation * Quaternion.Euler(pitch, yaw, 0f) * _forwardToHeadOffset;
            if (LockEveryFrame && Weight >= 0.999f)
                _head.rotation = look;
            else
            {
                float blend = (1f - Mathf.Exp(-TurnSpeed * Time.unscaledDeltaTime)) * Weight;
                _head.rotation = Quaternion.Slerp(_head.rotation, look, blend);
            }
        }
    }
}
