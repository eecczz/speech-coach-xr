using System;
using System.Collections;
using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>
    /// Lightweight author-time cutscene. Rig, door and waypoints are scene objects,
    /// so timing and positions can be adjusted without rebuilding anything.
    /// </summary>
    public class InterviewEntranceSequence : MonoBehaviour
    {
        public Transform XrOrigin;
        [Tooltip("Scene-authored player avatar. Its walk animation and actual movement begin together.")]
        public FirstPersonAvatarController PlayerAvatar;
        public Transform Door;
        public Transform EntrancePoint;
        public Transform SeatPoint;
        [Range(0.2f, 3f)] public float DoorOpenSeconds = 0.8f;
        [Range(1f, 10f)] public float WalkSeconds = 4f;
        [Range(0f, 2f)] public float GreetingPauseSeconds;
        public Vector3 DoorOpenEuler = new(0f, -95f, 0f);
        public bool PlayOnStart = true;

        public event Action Finished;
        private Quaternion _doorClosed;

        private void Awake()
        {
            if (Door) _doorClosed = Door.localRotation;
            PlayerAvatar?.PrepareEntrancePose(EntrancePoint, SeatPoint);
        }

        private void Start()
        {
            if (PlayOnStart) Play();
        }

        public void Play() => StartCoroutine(Run());

        private IEnumerator Run()
        {
            if ((!XrOrigin && !PlayerAvatar) || !EntrancePoint || !SeatPoint) { Finished?.Invoke(); yield break; }
            if (!PlayerAvatar) XrOrigin.position = EntrancePoint.position;
            // Door and candidate start together. Waiting for the entire door animation
            // used to leave the candidate standing still before the walk began.
            if (Door) StartCoroutine(RotateDoor(_doorClosed, _doorClosed * Quaternion.Euler(DoorOpenEuler), DoorOpenSeconds));
            if (PlayerAvatar)
            {
                yield return PlayerAvatar.EnterAndSit(EntrancePoint, SeatPoint);
                Finished?.Invoke();
                yield break;
            }

            Vector3 from = EntrancePoint.position;
            float elapsed = 0f;
            while (elapsed < WalkSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / WalkSeconds));
                XrOrigin.position = Vector3.Lerp(from, SeatPoint.position, t);
                yield return null;
            }
            XrOrigin.position = SeatPoint.position;
            float end = Time.realtimeSinceStartup + GreetingPauseSeconds;
            while (Time.realtimeSinceStartup < end) yield return null;
            Finished?.Invoke();
        }

        private IEnumerator RotateDoor(Quaternion from, Quaternion to, float duration)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                Door.localRotation = Quaternion.Slerp(from, to, Mathf.SmoothStep(0f, 1f, elapsed / duration));
                yield return null;
            }
            Door.localRotation = to;
        }
    }
}
