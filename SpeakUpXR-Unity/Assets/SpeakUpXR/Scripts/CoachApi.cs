using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace SpeakUpXR
{
    [Serializable]
    public class QAExchange
    {
        public string question;
        public string answer = "";
        public string kind = "base";
        public float wpm = -1f;
        public int filler_count;
        public float gaze_ratio = -1f;
        public int gaze_switches;
        public float posture_sway = -1f;
        public float head_motion = -1f;
        public float hand_motion = -1f;
        public float hand_span = -1f;
        public float gesture_idle_seconds = -1f;
    }

    [Serializable]
    public class InterviewConfig
    {
        public string project_name = "면접 준비";
        public string job_role = "신입 공통 인성";
        public string situation = "실무 및 인성 종합 면접";
        public string topic = "지원 동기와 직무 경험";
        public string difficulty = "보통";
        public string[] focus_goals = Array.Empty<string>();
    }

    [Serializable]
    public class InterviewNextRequest
    {
        public InterviewConfig config = new();
        public QAExchange[] history = Array.Empty<QAExchange>();
        public int asked_count;
        public int max_questions = 5;
    }

    [Serializable]
    public class InterviewNextResponse
    {
        public string question;
        public string kind = "base";
        public bool done;
        public string reaction;
        public string reaction_tone;
        public string reaction_speaker;
        public string question_speaker = "analytical";
    }

    [Serializable]
    public class InterviewReportRequest
    {
        public InterviewConfig config = new();
        public QAExchange[] history = Array.Empty<QAExchange>();
    }

    [Serializable] public class PerQuestionEval { public string question; public string comment; public bool star; }
    [Serializable]
    public class InterviewReportResponse
    {
        public string overall_summary;
        public string[] strengths = Array.Empty<string>();
        public string[] improvements = Array.Empty<string>();
        public PerQuestionEval[] per_question = Array.Empty<PerQuestionEval>();
    }

    [Serializable] public class SttSegment { public float t_start; public float t_end; public string text; }
    [Serializable]
    public class ProsodyFrame
    {
        public float t_start;
        public float t_end;
        public float wpm;
        public int word_count;
        public int filler_count;
        public string[] filler_terms;
        public float rms_mean;
        public float pitch_sd_semitones;
        public float pitch_range_semitones;
        public float intensity_cv;
        public float end_energy_drop;
    }
    [Serializable]
    public class AudioAnalysisResponse
    {
        public string full_transcript;
        public SttSegment[] stt_segments = Array.Empty<SttSegment>();
        public ProsodyFrame[] prosody_frames = Array.Empty<ProsodyFrame>();

        public float WeightedWpm()
        {
            float words = 0f, seconds = 0f;
            foreach (var frame in prosody_frames ?? Array.Empty<ProsodyFrame>())
            {
                words += frame.word_count;
                seconds += Mathf.Max(0f, frame.t_end - frame.t_start);
            }
            return seconds > 0.5f ? words / seconds * 60f : 0f;
        }

        public int FillerCount()
        {
            int count = 0;
            foreach (var frame in prosody_frames ?? Array.Empty<ProsodyFrame>()) count += frame.filler_count;
            return count;
        }

        public float AveragePitchVariation()
        {
            float sum = 0f; int count = 0;
            foreach (var frame in prosody_frames ?? Array.Empty<ProsodyFrame>())
                if (frame.pitch_sd_semitones > 0f) { sum += frame.pitch_sd_semitones; count++; }
            return count > 0 ? sum / count : -1f;
        }

        public float AverageEndEnergy()
        {
            float sum = 0f; int count = 0;
            foreach (var frame in prosody_frames ?? Array.Empty<ProsodyFrame>())
                if (frame.end_energy_drop > 0f) { sum += frame.end_energy_drop; count++; }
            return count > 0 ? sum / count : -1f;
        }
    }

    [Serializable]
    internal class TtsRequest
    {
        public string text;
        public string voice;
        public int rate_percent;
        public int pitch_percent;
        public string tone;
    }

    [Serializable] public class AgentFeedbackPayload { public float silence_seconds; public float gaze_off_seconds; public float posture_sway; public float hand_velocity; public float hand_span; public float gesture_idle_seconds; public float wpm; public int filler_count; public float pitch_sd_semitones; public float end_energy_drop; }
    [Serializable]
    public class AgentMultimodalContext
    {
        public string recent_transcript = "";
        public float gaze_fixation_ratio_avg = -1f;
        public float hand_velocity_max_avg = -1f;
        public float posture_sway_avg = -1f;
        public float current_silence_seconds = -1f;
        public float wpm = -1f;
        public int filler_count_total;
        public float session_elapsed_s;
        public string[] recent_agent_messages = Array.Empty<string>();
    }
    [Serializable]
    public class AgentFeedbackRequest
    {
        public string kind;
        public string scenario = "interview";
        public string situation = "";
        public string[] focus_goals = Array.Empty<string>();
        public AgentFeedbackPayload payload = new();
        public AgentMultimodalContext context = new();
        public string recent_transcript = "";
    }
    [Serializable] public class AgentFeedbackResponse { public string message; public string tone; }
    [Serializable] public class CoachHealthResponse { public bool ok; public string provider; public string model; public string base_url; }

    public class CoachApi : MonoBehaviour
    {
        [Tooltip("coach service, e.g. http://192.168.0.10:8002")]
        public string BaseUrl = "http://127.0.0.1:8002";
        [Tooltip("audio-pipeline service, e.g. http://192.168.0.10:8000")]
        public string AudioBaseUrl = "http://127.0.0.1:8000";
        [Min(5)] public int TimeoutSeconds = 180;

        public IEnumerator NextQuestion(InterviewNextRequest req, Action<InterviewNextResponse> ok, Action<string> error) =>
            PostJson("/interview/next", JsonUtility.ToJson(req), json => ok?.Invoke(JsonUtility.FromJson<InterviewNextResponse>(json)), error);

        public IEnumerator Report(InterviewReportRequest req, Action<InterviewReportResponse> ok, Action<string> error) =>
            PostJson("/interview/report", JsonUtility.ToJson(req), json => ok?.Invoke(JsonUtility.FromJson<InterviewReportResponse>(json)), error);

        public IEnumerator AgentFeedback(AgentFeedbackRequest req, Action<AgentFeedbackResponse> ok, Action<string> error) =>
            PostJson("/agent-feedback", JsonUtility.ToJson(req), json => ok?.Invoke(JsonUtility.FromJson<AgentFeedbackResponse>(json)), error);

        public IEnumerator Health(Action<CoachHealthResponse> ok, Action<string> error)
        {
            using var request = UnityWebRequest.Get(BaseUrl.TrimEnd('/') + "/healthz");
            request.timeout = 5;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success) error?.Invoke(request.error);
            else ok?.Invoke(JsonUtility.FromJson<CoachHealthResponse>(request.downloadHandler.text));
        }

        public IEnumerator Synthesize(string text, InterviewerVoice voice, string tone, Action<AudioClip> ok, Action<string> error)
        {
            var body = JsonUtility.ToJson(new TtsRequest
            {
                text = text,
                voice = voice.VoiceName,
                rate_percent = voice.RatePercent,
                pitch_percent = voice.PitchPercent,
                tone = string.IsNullOrWhiteSpace(tone) ? "neutral" : tone,
            });
            var url = BaseUrl.TrimEnd('/') + "/interview/tts";
            using var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerAudioClip(url, AudioType.MPEG);
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = TimeoutSeconds;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
                error?.Invoke($"TTS failed: {request.responseCode} {request.error}");
            else
                ok?.Invoke(DownloadHandlerAudioClip.GetContent(request));
        }

        public IEnumerator AnalyzeAudio(byte[] wav, string sessionId, Action<AudioAnalysisResponse> ok, Action<string> error)
        {
            var form = new WWWForm();
            form.AddField("session_id", sessionId);
            form.AddField("language", "ko");
            form.AddBinaryData("audio", wav, "answer.wav", "audio/wav");
            using var request = UnityWebRequest.Post(AudioBaseUrl.TrimEnd('/') + "/analyze", form);
            request.timeout = Mathf.Max(TimeoutSeconds, 90);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
                error?.Invoke($"STT failed: {request.responseCode} {request.error}");
            else
                ok?.Invoke(JsonUtility.FromJson<AudioAnalysisResponse>(request.downloadHandler.text));
        }

        private IEnumerator PostJson(string path, string body, Action<string> ok, Action<string> error)
        {
            using var request = new UnityWebRequest(BaseUrl.TrimEnd('/') + path, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = TimeoutSeconds;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
                error?.Invoke($"{path} failed: {request.responseCode} {request.error}");
            else
                ok?.Invoke(request.downloadHandler.text);
        }
    }
}
