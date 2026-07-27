// Backend client for the coach service — the Unity counterpart of llm-brain.ts.
// Same endpoints, same JSON contracts: POST /interview/next, POST /interview/report.
// The FastAPI backend is stack-agnostic, so the verified engine carries over as-is.

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
    }

    [Serializable]
    public class InterviewConfig
    {
        public string job_role = "신입 공통 인성";
        public string situation = "";
        public string difficulty = "보통"; // 쉬움 | 보통 | 어려움
    }

    [Serializable]
    public class InterviewNextRequest
    {
        public InterviewConfig config = new InterviewConfig();
        public QAExchange[] history = Array.Empty<QAExchange>();
        public int asked_count;
        public int max_questions = 5;
    }

    [Serializable]
    public class InterviewNextResponse
    {
        public string question; // null/empty => done
        public string kind = "base";
        public bool done;
    }

    [Serializable]
    public class InterviewReportRequest
    {
        public InterviewConfig config = new InterviewConfig();
        public QAExchange[] history = Array.Empty<QAExchange>();
    }

    [Serializable]
    public class PerQuestionEval
    {
        public string question;
        public string comment;
        public bool star;
    }

    [Serializable]
    public class InterviewReportResponse
    {
        public string overall_summary;
        public string[] strengths;
        public string[] improvements;
        public PerQuestionEval[] per_question;
    }

    /// <summary>
    /// Thin coroutine-based HTTP client. On-device (Quest) point BaseUrl at the
    /// dev Mac's LAN IP; in the editor localhost works.
    /// </summary>
    public class CoachApi : MonoBehaviour
    {
        [Tooltip("coach service origin, e.g. http://192.168.0.10:8002")]
        public string BaseUrl = "http://127.0.0.1:8002";

        public IEnumerator NextQuestion(InterviewNextRequest req, Action<InterviewNextResponse> onOk, Action<string> onErr)
        {
            yield return PostJson("/interview/next", JsonUtility.ToJson(req),
                json => onOk(JsonUtility.FromJson<InterviewNextResponse>(json)), onErr);
        }

        public IEnumerator Report(InterviewReportRequest req, Action<InterviewReportResponse> onOk, Action<string> onErr)
        {
            yield return PostJson("/interview/report", JsonUtility.ToJson(req),
                json => onOk(JsonUtility.FromJson<InterviewReportResponse>(json)), onErr);
        }

        private IEnumerator PostJson(string path, string body, Action<string> onOk, Action<string> onErr)
        {
            using var www = new UnityWebRequest(BaseUrl + path, "POST");
            www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.timeout = 30;
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
                onErr?.Invoke($"{path} failed: {www.result} {www.responseCode} {www.error}");
            else
                onOk?.Invoke(www.downloadHandler.text);
        }
    }
}
