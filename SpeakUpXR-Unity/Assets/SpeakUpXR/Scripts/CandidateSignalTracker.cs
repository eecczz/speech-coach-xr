using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>
    /// Quest-friendly nonverbal signal: estimates whether the headset faces the
    /// speaking interviewer and how often attention jumps across the panel.
    /// </summary>
    public class CandidateSignalTracker : MonoBehaviour
    {
        public Transform Head;
        public InterviewerPanel Panel;
        [Range(8f, 45f)] public float GazeConeDegrees = 23f;
        [Range(0.1f, 1f)] public float SampleInterval = 0.2f;

        public float CurrentGazeRatio => _samples > 0 ? (float)_onTarget / _samples : 0f;
        public int CurrentSwitches => _switches;

        private bool _tracking;
        private float _nextSample;
        private int _samples;
        private int _onTarget;
        private int _switches;
        private int _lastTarget = -1;

        public void BeginAnswer()
        {
            _tracking = true;
            _samples = _onTarget = _switches = 0;
            _lastTarget = -1;
            _nextSample = Time.unscaledTime;
        }

        public void EndAnswer() => _tracking = false;

        private void Update()
        {
            if (!_tracking || !Head || !Panel || Time.unscaledTime < _nextSample) return;
            _nextSample = Time.unscaledTime + SampleInterval;
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
            if (bestAngle <= GazeConeDegrees) _onTarget++;
            if (_lastTarget >= 0 && nearest >= 0 && nearest != _lastTarget) _switches++;
            if (nearest >= 0) _lastTarget = nearest;
        }
    }
}
