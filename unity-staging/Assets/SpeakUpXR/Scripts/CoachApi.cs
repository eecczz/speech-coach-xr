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
    }

    [Serializable]
    public class InterviewConfig
    {
        public string job_role = "신입 공통 인성";
        public string situation = "실무 및 인성 종합 면접";
        public string difficulty = "보통";
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

    public class CoachApi : MonoBehaviour
    {
        [Tooltip("coach service, e.g. http://192.168.0.10:8002")]
        public string BaseUrl = "http://127.0.0.1:8002";
        [Tooltip("audio-pipeline service, e.g. http://192.168.0.10:8000")]
        public string AudioBaseUrl = "http://127.0.0.1:8000";
        [Min(5)] public int TimeoutSeconds = 35;

        public IEnumerator NextQuestion(InterviewNextRequest req, Action<InterviewNextResponse> ok, Action<string> error) =>
            PostJson("/interview/next", JsonUtility.ToJson(req), json => ok?.Invoke(JsonUtility.FromJson<InterviewNextResponse>(json)), error);

        public IEnumerator Report(InterviewReportRequest req, Action<InterviewReportResponse> ok, Action<string> error) =>
            PostJson("/interview/report", JsonUtility.ToJson(req), json => ok?.Invoke(JsonUtility.FromJson<InterviewReportResponse>(json)), error);

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
            request.downloadHandler = new DownloadHandlerAudioClip(url, AudioType.WAV);
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
