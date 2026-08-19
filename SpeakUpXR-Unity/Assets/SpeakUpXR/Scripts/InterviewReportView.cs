using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace SpeakUpXR
{
    public class InterviewReportView : MonoBehaviour
    {
        public GameObject Root;
        public GameObject LoadingRoot;
        public GameObject ReportRoot;
        public Text TitleText;
        public Text SummaryText;
        public Text DetailText;

        private void Awake() { if (Root) Root.SetActive(false); }

        public void ShowLoading()
        {
            if (Root) Root.SetActive(true);
            if (LoadingRoot) LoadingRoot.SetActive(true);
            if (ReportRoot) ReportRoot.SetActive(false);
        }

        public void ShowReport(InterviewReportResponse report, QAExchange[] history)
        {
            if (report == null) report = BuildLocalReport(history);
            if (Root) Root.SetActive(true);
            if (LoadingRoot) LoadingRoot.SetActive(false);
            if (ReportRoot) ReportRoot.SetActive(true);
            if (TitleText) TitleText.text = "면접 코칭 리포트";
            if (SummaryText) SummaryText.text = report.overall_summary;

            var detail = new StringBuilder();
            if (report?.strengths != null && report.strengths.Length > 0)
                detail.AppendLine("잘한 점  ·  " + string.Join("  /  ", report.strengths));
            if (report?.improvements != null && report.improvements.Length > 0)
                detail.AppendLine("개선할 점  ·  " + string.Join("  /  ", report.improvements));
            if (history != null && history.Length > 0)
            {
                float gaze = 0f, wpm = 0f;
                int gazeN = 0, wpmN = 0, fillers = 0, switches = 0;
                foreach (var qa in history)
                {
                    if (qa.gaze_ratio >= 0f) { gaze += qa.gaze_ratio; gazeN++; }
                    if (qa.wpm > 0f) { wpm += qa.wpm; wpmN++; }
                    fillers += qa.filler_count;
                    switches += qa.gaze_switches;
                }
                detail.AppendLine();
                detail.Append($"평균 말 속도  {(wpmN > 0 ? wpm / wpmN : 0f):0} WPM   ·   ");
                detail.Append($"질문자 응시  {(gazeN > 0 ? gaze / gazeN * 100f : 0f):0}%   ·   ");
                detail.Append($"필러  {fillers}회   ·   시선 전환  {switches}회");
            }
            if (DetailText) DetailText.text = detail.ToString();
        }

        private static InterviewReportResponse BuildLocalReport(QAExchange[] history)
        {
            int answered = 0;
            if (history != null)
                foreach (var qa in history)
                    if (qa != null && !string.IsNullOrWhiteSpace(qa.answer)) answered++;

            return new InterviewReportResponse
            {
                overall_summary = answered == 0
                    ? "이번 연습에서는 평가할 수 있는 음성 답변이 기록되지 않았습니다. 면접 진행 기록은 정상 보존되었습니다. 다음 연습에서는 질문마다 짧게라도 실제 답변을 남겨 주세요."
                    : "AI 리포트 연결이 지연되어 기록된 답변과 XR 관찰값으로 기본 리포트를 표시합니다.",
                strengths = new string[0],
                improvements = answered == 0
                    ? new[] { "질문마다 결론부터 한두 문장으로 답변 시작", "질문당 최소 30초 이상 구체적인 사례 설명" }
                    : new[] { "결론을 먼저 말하고 구체적인 근거와 본인의 행동을 연결" },
                per_question = new PerQuestionEval[0]
            };
        }
    }
}
