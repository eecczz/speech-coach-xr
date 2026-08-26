using System.Collections.Generic;
using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>
    /// Keeps authored office walls from covering the interview panel when the tracked
    /// head or entrance animation briefly crosses a wall collider. Only renderers that
    /// physically block the camera-to-panel line are hidden, and they are restored as
    /// soon as the line of sight clears.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    [DisallowMultipleComponent]
    public sealed class InterviewCameraOcclusionGuard : MonoBehaviour
    {
        public Camera HeadCamera;
        public InterviewerPanel Panel;
        [Range(0.01f, 0.2f)] public float ProbeRadius = 0.07f;
        [Range(0f, 0.3f)] public float TargetPadding = 0.12f;
        public LayerMask ObstructionLayers = ~0;

        private readonly RaycastHit[] _hits = new RaycastHit[32];
        private readonly HashSet<Renderer> _hidden = new();
        private readonly HashSet<Renderer> _blockedThisFrame = new();

        private void Awake()
        {
            if (!HeadCamera) HeadCamera = GetComponent<Camera>();
            if (!Panel) Panel = FindFirstObjectByType<InterviewerPanel>();
            if (HeadCamera) HeadCamera.nearClipPlane = Mathf.Min(HeadCamera.nearClipPlane, 0.02f);
        }

        private void LateUpdate()
        {
            if (!HeadCamera || !TryGetPanelFocus(out Vector3 focus))
            {
                RestoreAll();
                return;
            }

            Vector3 origin = HeadCamera.transform.position;
            Vector3 delta = focus - origin;
            float distance = delta.magnitude - TargetPadding;
            if (distance <= 0.05f)
            {
                RestoreAll();
                return;
            }

            _blockedThisFrame.Clear();
            int count = Physics.SphereCastNonAlloc(origin, ProbeRadius, delta.normalized, _hits,
                distance, ObstructionLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Renderer renderer = _hits[i].collider
                    ? _hits[i].collider.GetComponentInParent<Renderer>()
                    : null;
                if (!renderer || IsPanelRenderer(renderer) || renderer.transform.IsChildOf(transform.root)) continue;
                _blockedThisFrame.Add(renderer);
                if (_hidden.Add(renderer)) renderer.enabled = false;
            }

            _hidden.RemoveWhere(renderer =>
            {
                if (!renderer) return true;
                if (_blockedThisFrame.Contains(renderer)) return false;
                renderer.enabled = true;
                return true;
            });
        }

        private bool TryGetPanelFocus(out Vector3 focus)
        {
            focus = default;
            if (!Panel || Panel.Members == null) return false;
            int count = 0;
            foreach (InterviewerController member in Panel.Members)
            {
                if (!member || !member.gameObject.activeInHierarchy) continue;
                Animator animator = member.AvatarRoot
                    ? member.AvatarRoot.GetComponentInChildren<Animator>(true)
                    : member.GetComponentInChildren<Animator>(true);
                Transform head = animator ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
                focus += head ? head.position : member.transform.position + Vector3.up * 1.45f;
                count++;
            }
            if (count == 0) return false;
            focus /= count;
            return true;
        }

        private bool IsPanelRenderer(Renderer renderer)
        {
            if (!Panel || Panel.Members == null) return false;
            foreach (InterviewerController member in Panel.Members)
                if (member && renderer.transform.IsChildOf(member.transform)) return true;
            return false;
        }

        private void OnDisable() => RestoreAll();
        private void OnDestroy() => RestoreAll();

        private void RestoreAll()
        {
            foreach (Renderer renderer in _hidden)
                if (renderer) renderer.enabled = true;
            _hidden.Clear();
            _blockedThisFrame.Clear();
        }
    }
}
