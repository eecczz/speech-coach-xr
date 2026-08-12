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
        public Transform Door;
        public Transform EntrancePoint;
        public Transform SeatPoint;
        [Range(0.2f, 3f)] public float DoorOpenSeconds = 0.8f;
        [Range(1f, 10f)] public float WalkSeconds = 4f;
        [Range(0f, 2f)] public float GreetingPauseSeconds = 0.8f;
        public Vector3 DoorOpenEuler = new(0f, -95f, 0f);
        public bool PlayOnStart = true;

        public event Action Finished;
        private Quaternion _doorClosed;

        private void Awake()
        {
            if (Door) _doorClosed = Door.localRotation;
        }

        private void Start()
        {
            if (PlayOnStart) Play();
        }

        public void Play() => StartCoroutine(Run());

        private IEnumerator Run()
        {
            if (!XrOrigin || !EntrancePoint || !SeatPoint) { Finished?.Invoke(); yield break; }
            XrOrigin.SetPositionAndRotation(EntrancePoint.position, EntrancePoint.rotation);
            if (Door)
            {
                var open = _doorClosed * Quaternion.Euler(DoorOpenEuler);
                yield return RotateDoor(_doorClosed, open, DoorOpenSeconds);
            }
            Vector3 from = EntrancePoint.position;
            Quaternion fromRot = EntrancePoint.rotation;
            float elapsed = 0f;
            while (elapsed < WalkSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / WalkSeconds));
                XrOrigin.SetPositionAndRotation(Vector3.Lerp(from, SeatPoint.position, t), Quaternion.Slerp(fromRot, SeatPoint.rotation, t));
                yield return null;
            }
            XrOrigin.SetPositionAndRotation(SeatPoint.position, SeatPoint.rotation);
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
