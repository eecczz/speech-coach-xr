using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        public string CurrentQuestion => _current?.question ?? "";

        private readonly List<QAExchange> _history = new();
        private QAExchange _current;
        private bool _answerBusy;
        private string _sessionId;

        private const string IntroLine = "안녕하세요. 지금부터 면접을 시작하겠습니다. 긴장 푸시고 편하게 답변해 주세요.";
        private const string ClosingWarm = "네, 답변 잘 들었습니다. 오늘 면접에 참여해 주셔서 감사합니다.";
        private const string ClosingExecutive = "수고하셨습니다. 면접은 여기까지입니다. 조심히 나가시면 됩니다.";

        private void Awake()
        {
            _sessionId = Guid.NewGuid().ToString("N");
            if (!Api) Api = GetComponent<CoachApi>();
            if (!Microphone) Microphone = GetComponent<MicrophoneRecorder>();
            if (!InterviewLaunchSettings.TryApplyTo(this))
            {
                AutoStart = false;
                enabled = false;
                Debug.Log("[SpeakUpXR] Interview 씬 직접 실행을 감지해 MainMenu로 이동합니다.");
                SceneManager.LoadScene("MainMenu");
            }
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

        /// <summary>XR controller/UI answer-complete action. Desktop tests may pass text.</summary>
        public void FinishAnswer(string answerOverride = null)
        {
            if (State != SessionState.Listening || _answerBusy) return;
            _answerBusy = true;
            StartCoroutine(CaptureAndAnalyzeAnswer(answerOverride));
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
            yield return GenerateFirstQuestion();
        }

        private IEnumerator GenerateFirstQuestion()
        {
            SetState(SessionState.Thinking);
            Hud?.SetStatus("지원 분야에 맞는 첫 질문을 준비하는 중", HudTone.Think);
            InterviewNextResponse next = null;
            string error = null;
            if (Api)
                yield return Api.NextQuestion(new InterviewNextRequest
                {
                    config = Config,
                    history = Array.Empty<QAExchange>(),
                    asked_count = 0,
                    max_questions = MaxQuestions,
                }, value => next = value, value => error = value);
            if (next == null || next.done || string.IsNullOrWhiteSpace(next.question))
            {
                if (!string.IsNullOrWhiteSpace(error)) Debug.LogWarning("[interview] first question failed: " + error);
                next = new InterviewNextResponse
                {
                    question = $"{Config.job_role} 직무에 지원하신 동기와 {Config.topic}에 관해 말씀해 주시겠습니까?",
                    kind = "base",
                    question_speaker = "warm",
                };
            }
            yield return Ask(next);
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

        private IEnumerator CaptureAndAnalyzeAnswer(string answerOverride)
        {
            SetState(SessionState.Analyzing);
            Hud?.SetStatus("답변을 정리하는 중", HudTone.Think);
            Signals?.EndAnswer();
            byte[] wav = Microphone?.End();
            AudioAnalysisResponse analysis = null;
            string error = null;

            if (string.IsNullOrWhiteSpace(answerOverride) && wav != null && Api)
                yield return Api.AnalyzeAudio(wav, _sessionId, value => analysis = value, value => error = value);

            _current.answer = !string.IsNullOrWhiteSpace(answerOverride)
                ? answerOverride.Trim()
                : analysis?.full_transcript?.Trim() ?? "";
            _current.wpm = analysis != null ? analysis.WeightedWpm() : -1f;
            _current.filler_count = analysis?.FillerCount() ?? 0;
            _current.gaze_ratio = Signals ? Signals.CurrentGazeRatio : -1f;
            _current.gaze_switches = Signals ? Signals.CurrentSwitches : 0;
            _current.posture_sway = Signals ? Signals.CurrentPostureSway : -1f;
            _current.head_motion = Signals ? Signals.CurrentHeadMotion : -1f;
            _current.hand_motion = Signals ? Signals.CurrentHandMotion : -1f;
            _current.hand_span = Signals ? Signals.CurrentHandSpan : -1f;
            _current.gesture_idle_seconds = Signals ? Signals.CurrentGestureIdleSeconds : -1f;
            if (string.IsNullOrWhiteSpace(_current.answer))
            {
                Hud?.SetInterim("음성을 인식하지 못했습니다");
                if (!string.IsNullOrEmpty(error)) Debug.LogWarning("[interview] " + error);
                yield return RetryUnheardAnswer();
                yield break;
            }
            _history.Add(_current);
            Hud?.SetInterim(_current.answer);
            if (!string.IsNullOrEmpty(error)) Debug.LogWarning("[interview] " + error);
            _answerBusy = false;
            var liveFeedback = GetComponent<XrRealtimeFeedbackController>();
            if (liveFeedback) yield return liveFeedback.DeliverAnswerMetrics(analysis);
            yield return ThinkAndContinue();
        }

        private IEnumerator RetryUnheardAnswer()
        {
            const string line = "답변이 들리지 않았습니다. 짧게라도 말씀해 주시겠습니까?";
            SetState(SessionState.Asking);
            var speaker = Panel ? Panel.Find("analytical", "followup") : null;
            Hud?.SetSpeaker(speaker ? speaker.DisplayName : "기술 면접관");
            Hud?.SetQuestion(line);
            Hud?.SetStatus("답변을 다시 기다립니다", HudTone.Ask);
            yield return Speak(line, "analytical", "followup", "neutral");
            SetState(SessionState.Listening);
            Hud?.SetQuestion(_current.question);
            Hud?.SetStatus("답변 중 · 끝나면 트리거를 눌러 주세요", HudTone.Listen);
            Hud?.SetInterim("음성을 기록하고 있습니다");
            Signals?.BeginAnswer();
            Microphone?.Begin();
            _answerBusy = false;
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
