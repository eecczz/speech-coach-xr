using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SpeakUpXR
{
    public enum SessionState { Idle, Entrance, Intro, Asking, Listening, Analyzing, Thinking, Closing, ReportLoading, Done }

    /// <summary>
    /// Turn-based VR interview: entrance → panel question → recorded answer →
    /// short persona-routed reaction → adaptive follow-up → paused report.
    /// </summary>
    public class InterviewSession : MonoBehaviour
    {
        [Header("Scene references")]
        public CoachApi Api;
        public InterviewerPanel Panel;
        public InterviewHud Hud;
        public MicrophoneRecorder Microphone;
        public CandidateSignalTracker Signals;
        public InterviewEntranceSequence Entrance;
        public InterviewReportView ReportView;

        [Header("Interview setup")]
        public InterviewConfig Config = new();
        [Range(1, 12)] public int MaxQuestions = 5;
        public bool AutoStart = true;

        public SessionState State { get; private set; } = SessionState.Idle;
        public event Action<SessionState> OnStateChanged;
        public event Action<List<QAExchange>, InterviewReportResponse> OnFinished;

        private readonly List<QAExchange> _history = new();
        private QAExchange _current;
        private bool _answerBusy;
        private string _sessionId;

        private const string IntroLine = "안녕하세요. 지금부터 면접을 시작하겠습니다. 긴장 푸시고 편하게 답변해 주세요.";
        private const string FirstQuestion = "먼저 간단히 자기소개 부탁드립니다.";
        private const string ClosingWarm = "네, 답변 잘 들었습니다. 오늘 면접에 참여해 주셔서 감사합니다.";
        private const string ClosingExecutive = "수고하셨습니다. 면접은 여기까지입니다. 조심히 나가시면 됩니다.";

        private void Awake()
        {
            _sessionId = Guid.NewGuid().ToString("N");
            if (!Api) Api = GetComponent<CoachApi>();
            if (!Microphone) Microphone = GetComponent<MicrophoneRecorder>();
        }

        private void OnEnable()
        {
            if (Entrance) Entrance.Finished += HandleEntranceFinished;
        }

        private void OnDisable()
        {
            if (Entrance) Entrance.Finished -= HandleEntranceFinished;
        }

        private void Start()
        {
            Time.timeScale = 1f;
            if (!AutoStart) return;
            if (Entrance)
            {
                SetState(SessionState.Entrance);
                Hud?.SetStatus("면접장에 입장하는 중", HudTone.Think);
                if (!Entrance.PlayOnStart) Entrance.Play();
            }
            else StartInterview();
        }

        private void HandleEntranceFinished()
        {
            if (State == SessionState.Entrance || State == SessionState.Idle) StartInterview();
        }

        public void StartInterview()
        {
            if (State != SessionState.Idle && State != SessionState.Entrance) return;
            StartCoroutine(RunIntro());
        }

        /// <summary>XR controller/UI action that finishes the current recorded answer.</summary>
        public void FinishAnswer()
        {
            if (State != SessionState.Listening || _answerBusy) return;
            _answerBusy = true;
            StartCoroutine(CaptureAndAnalyzeAnswer());
        }

        private void SetState(SessionState value)
        {
            State = value;
            OnStateChanged?.Invoke(value);
        }

        private IEnumerator RunIntro()
        {
            SetState(SessionState.Intro);
            Hud?.SetQuestion("면접을 시작합니다.");
            Hud?.SetStatus("면접 시작", HudTone.Ask);
            yield return Speak(IntroLine, "warm", "base", "warm");
            yield return Ask(new InterviewNextResponse
            {
                question = FirstQuestion,
                kind = "base",
                question_speaker = "warm",
            });
        }

        private IEnumerator Ask(InterviewNextResponse next)
        {
            _current = new QAExchange { question = next.question, kind = next.kind };
            SetState(SessionState.Asking);
            var speaker = Panel ? Panel.Find(next.question_speaker, next.kind) : null;
            Hud?.SetSpeaker(speaker ? speaker.DisplayName : "면접관");
            Hud?.SetQuestion(next.question);
            Hud?.SetStatus("면접관이 질문하는 중", HudTone.Ask);
            yield return Speak(next.question, next.question_speaker, next.kind, "neutral");

            SetState(SessionState.Listening);
            Hud?.SetStatus("답변 중 · 끝나면 트리거를 눌러 주세요", HudTone.Listen);
            Hud?.SetInterim("음성을 기록하고 있습니다");
            Signals?.BeginAnswer();
            Microphone?.Begin();
        }

        private IEnumerator CaptureAndAnalyzeAnswer()
        {
            SetState(SessionState.Analyzing);
            Hud?.SetStatus("답변을 정리하는 중", HudTone.Think);
            Signals?.EndAnswer();
            byte[] wav = Microphone?.End();
            AudioAnalysisResponse analysis = null;
            string error = null;

            if (wav != null && Api)
                yield return Api.AnalyzeAudio(wav, _sessionId, value => analysis = value, value => error = value);

            _current.answer = analysis?.full_transcript?.Trim() ?? "";
            _current.wpm = analysis != null ? analysis.WeightedWpm() : -1f;
            _current.filler_count = analysis?.FillerCount() ?? 0;
            _current.gaze_ratio = Signals ? Signals.CurrentGazeRatio : -1f;
            _current.gaze_switches = Signals ? Signals.CurrentSwitches : 0;
            _history.Add(_current);
            Hud?.SetInterim(string.IsNullOrWhiteSpace(_current.answer) ? "음성을 인식하지 못했습니다" : _current.answer);
            if (!string.IsNullOrEmpty(error)) Debug.LogWarning("[interview] " + error);
            _answerBusy = false;
            yield return ThinkAndContinue();
        }

        private IEnumerator ThinkAndContinue()
        {
            SetState(SessionState.Thinking);
            Hud?.SetStatus("다음 질문을 준비하는 중", HudTone.Think);
            Panel?.NodRandom();
            var request = new InterviewNextRequest
            {
                config = Config,
                history = _history.ToArray(),
                asked_count = _history.Count,
                max_questions = MaxQuestions,
            };
            InterviewNextResponse next = null;
            string error = null;
            if (Api) yield return Api.NextQuestion(request, value => next = value, value => error = value);

            if (next == null || next.done || string.IsNullOrWhiteSpace(next.question))
            {
                if (!string.IsNullOrEmpty(error)) Debug.LogWarning("[interview] next failed: " + error);
                yield return CloseInterview();
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(next.reaction))
            {
                var reactionSpeaker = Panel ? Panel.Find(next.reaction_speaker, next.kind) : null;
                Hud?.SetSpeaker(reactionSpeaker ? reactionSpeaker.DisplayName : "면접관");
                Hud?.SetQuestion(next.reaction);
                Hud?.SetStatus("면접관 반응", next.reaction_tone == "challenging" ? HudTone.Think : HudTone.Ask);
                yield return Speak(next.reaction, next.reaction_speaker, next.kind, next.reaction_tone);
            }
            yield return Ask(next);
        }

        private IEnumerator CloseInterview()
        {
            SetState(SessionState.Closing);
            Hud?.SetInterim("");
            Hud?.SetQuestion("면접이 종료되었습니다.");
            Hud?.SetStatus("마무리 인사", HudTone.Done);
            yield return Speak(ClosingWarm, "warm", "closing", "warm");
            yield return Speak(ClosingExecutive, "challenging", "closing", "neutral");

            // Freeze the interview world while networking/UI continue in real time.
            SetState(SessionState.ReportLoading);
            Time.timeScale = 0f;
            ReportView?.ShowLoading();
            Hud?.SetStatus("면접 리포트를 불러오는 중", HudTone.Think);

            InterviewReportResponse report = null;
            string error = null;
            if (Api)
                yield return Api.Report(
                    new InterviewReportRequest { config = Config, history = _history.ToArray() },
                    value => report = value,
                    value => error = value);
            if (!string.IsNullOrEmpty(error)) Debug.LogWarning("[interview] report failed: " + error);

            SetState(SessionState.Done);
            Hud?.SetStatus("리포트가 준비되었습니다", HudTone.Done);
            ReportView?.ShowReport(report, _history.ToArray());
            OnFinished?.Invoke(_history, report);
        }

        private IEnumerator Speak(string line, string speakerId, string kind, string tone)
        {
            if (Panel) yield return Panel.SpeakLine(Api, line, speakerId, kind, tone);
            else
            {
                float end = Time.realtimeSinceStartup + Mathf.Clamp(1.3f + line.Length * 0.055f, 1.8f, 12f);
                while (Time.realtimeSinceStartup < end) yield return null;
            }
        }
    }
}
