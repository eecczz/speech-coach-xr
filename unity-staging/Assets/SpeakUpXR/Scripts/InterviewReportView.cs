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
            if (Root) Root.SetActive(true);
            if (LoadingRoot) LoadingRoot.SetActive(false);
            if (ReportRoot) ReportRoot.SetActive(true);
            if (TitleText) TitleText.text = "면접 코칭 리포트";
            if (SummaryText) SummaryText.text = report?.overall_summary ?? "리포트를 불러오지 못했습니다. 답변 기록은 보존되었습니다.";

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
    }
}
