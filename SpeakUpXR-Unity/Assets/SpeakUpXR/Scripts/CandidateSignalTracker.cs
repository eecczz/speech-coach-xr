using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace SpeakUpXR
{
    [Serializable]
    public class CandidateFeedbackSignal
    {
        public string Kind;
        public float Value;
        public string SpeakerId;
    }

    /// <summary>Measures headset gaze/posture and XR controller motion while answering.</summary>
    public class CandidateSignalTracker : MonoBehaviour
    {
        public Transform Head;
        public InterviewerPanel Panel;
        public MicrophoneRecorder Microphone;
        [Range(8f, 45f)] public float GazeConeDegrees = 23f;
        [Range(0.05f, 1f)] public float SampleInterval = 0.2f;
        public float GazeFeedbackSeconds = 4f;
        public float SilenceFeedbackSeconds = 4f;
        public float FeedbackCooldownSeconds = 12f;
        public float ExcessiveHeadSpeed = 45f;
        public float ExcessiveHandSpeed = 1.4f;
        public float GestureAbsenceFeedbackSeconds = 12f;
        public float ActiveHandSpeed = 0.12f;

        public float CurrentGazeRatio => _samples > 0 ? (float)_onTarget / _samples : 0f;
        public int CurrentSwitches => _switches;
        public float CurrentHeadMotion { get; private set; }
        public float CurrentPostureSway { get; private set; }
        public float CurrentHandMotion { get; private set; }
        public float CurrentHandSpan { get; private set; }
        public float CurrentGestureIdleSeconds { get; private set; }
        public event Action<CandidateFeedbackSignal> FeedbackTriggered;

        private readonly Dictionary<string, float> _lastFeedback = new();
        private bool _tracking;
        private float _nextSample, _gazeOffSeconds;
        private int _samples, _onTarget, _switches, _lastTarget = -1;
        private Vector3 _headBaseline;
        private Quaternion _lastHeadRotation;
        private Vector3 _lastLeftHand, _lastRightHand;
        private bool _hadLeftHand, _hadRightHand;

        public void BeginAnswer()
        {
            _tracking = true;
            _samples = _onTarget = _switches = 0;
            _lastTarget = -1;
            _gazeOffSeconds = CurrentHeadMotion = CurrentPostureSway = CurrentHandMotion = 0f;
            CurrentHandSpan = CurrentGestureIdleSeconds = 0f;
            _hadLeftHand = _hadRightHand = false;
            _nextSample = Time.unscaledTime;
            if (Head) { _headBaseline = Head.position; _lastHeadRotation = Head.rotation; }
        }

        public void EndAnswer() => _tracking = false;

        private void Update()
        {
            if (!_tracking || !Head || !Panel || Time.unscaledTime < _nextSample) return;
            float dt = Mathf.Max(0.01f, SampleInterval);
            _nextSample = Time.unscaledTime + dt;
            SampleGaze(dt);
            SampleMovement(dt);
            if (_gazeOffSeconds >= GazeFeedbackSeconds) Fire("gaze", _gazeOffSeconds, "challenging");
            if (Microphone && Microphone.CurrentSilenceSeconds >= SilenceFeedbackSeconds)
                Fire("silence", Microphone.CurrentSilenceSeconds, "warm");
            if (CurrentHeadMotion >= ExcessiveHeadSpeed || CurrentPostureSway >= 0.22f)
                Fire("posture", Mathf.Max(CurrentHeadMotion, CurrentPostureSway), "analytical");
            if (CurrentHandMotion >= ExcessiveHandSpeed) Fire("gesture", CurrentHandMotion, "analytical");
            if (CurrentGestureIdleSeconds >= GestureAbsenceFeedbackSeconds)
                Fire("motion_absence", CurrentGestureIdleSeconds, "warm");
        }

        private void SampleGaze(float dt)
        {
            var targets = Panel.GazeTargets();
            int nearest = -1;
            float bestAngle = 180f;
            for (int i = 0; i < targets.Length; i++)
            {
                if (!targets[i]) continue;
                float angle = Vector3.Angle(Head.forward, (targets[i].position - Head.position).normalized);
                if (angle < bestAngle) { bestAngle = angle; nearest = i; }
            }
            _samples++;
            if (bestAngle <= GazeConeDegrees) { _onTarget++; _gazeOffSeconds = 0f; }
            else _gazeOffSeconds += dt;
            if (_lastTarget >= 0 && nearest >= 0 && nearest != _lastTarget) _switches++;
            if (nearest >= 0) _lastTarget = nearest;
        }

        private void SampleMovement(float dt)
        {
            CurrentHeadMotion = Quaternion.Angle(_lastHeadRotation, Head.rotation) / dt;
            CurrentPostureSway = Mathf.Max(CurrentPostureSway, Vector3.Distance(_headBaseline, Head.position));
            _lastHeadRotation = Head.rotation;
            float hand = 0f;
            bool leftTracked = false, rightTracked = false;
            Vector3 leftPosition = default, rightPosition = default;
            foreach (XRNode node in new[] { XRNode.LeftHand, XRNode.RightHand })
            {
                var device = InputDevices.GetDeviceAtXRNode(node);
                if (device.TryGetFeatureValue(CommonUsages.deviceVelocity, out Vector3 velocity))
                    hand = Mathf.Max(hand, velocity.magnitude);
                if (device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position))
                {
                    if (node == XRNode.LeftHand)
                    {
                        leftTracked = true; leftPosition = position;
                        if (_hadLeftHand) hand = Mathf.Max(hand, Vector3.Distance(position, _lastLeftHand) / dt);
                        _lastLeftHand = position; _hadLeftHand = true;
                    }
                    else
                    {
                        rightTracked = true; rightPosition = position;
                        if (_hadRightHand) hand = Mathf.Max(hand, Vector3.Distance(position, _lastRightHand) / dt);
                        _lastRightHand = position; _hadRightHand = true;
                    }
                }
            }
            if (!leftTracked) _hadLeftHand = false;
            if (!rightTracked) _hadRightHand = false;
            CurrentHandMotion = Mathf.Max(CurrentHandMotion * 0.75f, hand);
            if (leftTracked && rightTracked) CurrentHandSpan = Vector3.Distance(leftPosition, rightPosition);
            if (leftTracked || rightTracked)
                CurrentGestureIdleSeconds = hand >= ActiveHandSpeed ? 0f : CurrentGestureIdleSeconds + dt;
            else CurrentGestureIdleSeconds = 0f;
        }

        private void Fire(string kind, float value, string speaker)
        {
            if (_lastFeedback.TryGetValue(kind, out float last) && Time.unscaledTime - last < FeedbackCooldownSeconds) return;
            _lastFeedback[kind] = Time.unscaledTime;
            FeedbackTriggered?.Invoke(new CandidateFeedbackSignal { Kind = kind, Value = value, SpeakerId = speaker });
        }
    }
}
