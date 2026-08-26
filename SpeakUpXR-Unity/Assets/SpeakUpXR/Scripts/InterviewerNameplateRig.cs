using System;
using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>Keeps all panel nameplates front-facing and equally distant from the seated viewer.</summary>
    [DisallowMultipleComponent]
    public sealed class InterviewerNameplateRig : MonoBehaviour
    {
        public Transform Viewer;
        public InterviewSession Session;
        public RectTransform[] Nameplates = Array.Empty<RectTransform>();

        [Header("Common placement — editable in Inspector")]
        [Min(0.5f)] public float DistanceFromViewer = 2.35f;
        public float VerticalOffsetFromEyes = -0.68f;
        public bool LockDuringEntrance;

        private Vector3[] _planarDirections = Array.Empty<Vector3>();
        private bool _initializedForPlay;

        private void OnEnable() => _initializedForPlay = false;

        private void OnValidate()
        {
            DistanceFromViewer = Mathf.Max(0.5f, DistanceFromViewer);
        }

        private void LateUpdate()
        {
            if (!Viewer || Nameplates == null || Nameplates.Length == 0) return;
            if (!LockDuringEntrance && Session && Session.State == SessionState.Entrance) return;
            if (!_initializedForPlay || _planarDirections.Length != Nameplates.Length)
            {
                CacheDirections();
                _initializedForPlay = true;
            }
            AlignNow();
        }

        public void CacheDirections()
        {
            if (!Viewer || Nameplates == null) return;
            _planarDirections = new Vector3[Nameplates.Length];
            for (int i = 0; i < Nameplates.Length; i++)
            {
                if (!Nameplates[i]) continue;
                Vector3 direction = Nameplates[i].position - Viewer.position;
                direction.y = 0f;
                _planarDirections[i] = direction.sqrMagnitude > 0.0001f
                    ? direction.normalized
                    : Viewer.forward;
            }
        }

        public void AlignNow()
        {
            if (!Viewer || Nameplates == null) return;
            for (int i = 0; i < Nameplates.Length; i++)
            {
                RectTransform plate = Nameplates[i];
                if (!plate) continue;
                Vector3 direction = i < _planarDirections.Length ? _planarDirections[i] : Vector3.zero;
                if (direction.sqrMagnitude < 0.0001f) direction = Viewer.forward;
                direction.y = 0f;
                direction.Normalize();
                plate.position = Viewer.position + direction * DistanceFromViewer +
                                 Vector3.up * VerticalOffsetFromEyes;

                // World-space UGUI is read from its -forward side, so +forward points
                // away from the player. All three plates consequently share a true
                // front-on, upright angle without mirrored text.
                plate.rotation = Quaternion.LookRotation(direction, Vector3.up);
            }
        }
    }
}
