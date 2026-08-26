using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>Routes live XR behavioral signals through the existing web agent endpoint.</summary>
    public class XrRealtimeFeedbackController : MonoBehaviour
    {
        public InterviewSession Session;
        public CandidateSignalTracker Signals;
        public CoachApi Api;
        public InterviewerPanel Panel;
        public InterviewHud Hud;
        public FeedbackCompanionController Companion;

        private readonly Queue<CandidateFeedbackSignal> _pending = new();
        private readonly Queue<string> _recentMessages = new();
        private bool _busy;

        private void OnEnable() { if (Signals) Signals.FeedbackTriggered += QueueFeedback; }
        private void OnDisable() { if (Signals) Signals.FeedbackTriggered -= QueueFeedback; }

        private void QueueFeedback(CandidateFeedbackSignal signal)
        {
            if (!Session || Session.State != SessionState.Listening) return;
            _pending.Enqueue(signal);
            if (!_busy) StartCoroutine(Process());
        }

        private IEnumerator Process()
        {
            _busy = true;
            while (_pending.Count > 0 && Session && Session.State == SessionState.Listening)
            {
                var signal = _pending.Dequeue();
                var request = new AgentFeedbackRequest
                {
                    kind = signal.Kind,
                    scenario = "interview",
                    situation = Session.Config.situation + " / " + Session.Config.job_role + " / " + Session.Config.topic,
                    focus_goals = Session.Config.focus_goals,
                    payload = new AgentFeedbackPayload
                    {
                        silence_seconds = signal.Kind == "silence" ? signal.Value : 0f,
                        gaze_off_seconds = signal.Kind == "gaze" ? signal.Value : 0f,
                        posture_sway = signal.Kind == "posture" ? signal.Value : 0f,
                        hand_velocity = signal.Kind == "gesture" ? signal.Value : 0f,
                        hand_span = Signals.CurrentHandSpan,
                        gesture_idle_seconds = Signals.CurrentGestureIdleSeconds,
                    },
                    context = new AgentMultimodalContext
                    {
                        gaze_fixation_ratio_avg = Signals.CurrentGazeRatio,
                        hand_velocity_max_avg = Signals.CurrentHandMotion,
                        posture_sway_avg = Signals.CurrentPostureSway,
                        current_silence_seconds = Signals.Microphone ? Signals.Microphone.CurrentSilenceSeconds : -1f,
                        session_elapsed_s = Time.unscaledTime,
                        recent_agent_messages = _recentMessages.ToArray(),
                    }
                };
                AgentFeedbackResponse response = null;
                if (Api) yield return Api.AgentFeedback(request, value => response = value, _ => { });
                if (response != null && !string.IsNullOrWhiteSpace(response.message) && Session.State == SessionState.Listening)
                {
                    if (Companion) yield return Companion.SpeakFeedback(response.message, response.tone);
                    _recentMessages.Enqueue(response.message);
                    while (_recentMessages.Count > 4) _recentMessages.Dequeue();
                }
            }
            _busy = false;
        }

        public IEnumerator DeliverAnswerMetrics(AudioAnalysisResponse analysis)
        {
            if (analysis == null) yield break;
            float wpm = analysis.WeightedWpm();
            int fillers = analysis.FillerCount();
            float pitchVariation = analysis.AveragePitchVariation();
            float endEnergy = analysis.AverageEndEnergy();
            CandidateFeedbackSignal signal = wpm > 165f
                ? new CandidateFeedbackSignal { Kind = "speech_rate", Value = wpm, SpeakerId = "challenging" }
                : fillers >= 3
                    ? new CandidateFeedbackSignal { Kind = "filler", Value = fillers, SpeakerId = "analytical" }
                    : pitchVariation >= 0f && pitchVariation < 1.1f
                        ? new CandidateFeedbackSignal { Kind = "vocal_tone", Value = pitchVariation, SpeakerId = "analytical" }
                        : endEnergy > 0f && endEnergy < 0.55f
                            ? new CandidateFeedbackSignal { Kind = "vocal_tone", Value = endEnergy, SpeakerId = "analytical" }
                            : null;
            if (signal == null) yield break;
            var request = new AgentFeedbackRequest
            {
                kind = signal.Kind,
                scenario = "interview",
                situation = Session.Config.situation + " / " + Session.Config.job_role + " / " + Session.Config.topic,
                focus_goals = Session.Config.focus_goals,
                payload = new AgentFeedbackPayload { wpm = wpm, filler_count = fillers, pitch_sd_semitones = pitchVariation, end_energy_drop = endEnergy },
                context = new AgentMultimodalContext
                {
                    wpm = wpm,
                    filler_count_total = fillers,
                    gaze_fixation_ratio_avg = Signals ? Signals.CurrentGazeRatio : -1f,
                    hand_velocity_max_avg = Signals ? Signals.CurrentHandMotion : -1f,
                    posture_sway_avg = Signals ? Signals.CurrentPostureSway : -1f,
                    recent_agent_messages = _recentMessages.ToArray(),
                }
            };
            AgentFeedbackResponse response = null;
            if (Api) yield return Api.AgentFeedback(request, value => response = value, _ => { });
            if (response == null || string.IsNullOrWhiteSpace(response.message)) yield break;
            if (Companion) yield return Companion.SpeakFeedback(response.message, response.tone);
            _recentMessages.Enqueue(response.message);
            while (_recentMessages.Count > 4) _recentMessages.Dequeue();
        }
    }
}
