using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        [Serializable]
        private sealed class SavedInterviewReport
        {
            public string session_id;
            public string generated_at_utc;
            public InterviewConfig config;
            public QAExchange[] history;
            public InterviewReportResponse report;
        }

        [Header("Scene references")]
        public CoachApi Api;
        public InterviewerPanel Panel;
        public InterviewHud Hud;
        public MicrophoneRecorder Microphone;
        public CandidateSignalTracker Signals;
        public InterviewEntranceSequence Entrance;
        public InterviewReportView ReportView;
        public FeedbackCompanionController FeedbackCompanion;

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
        private bool _launchReady;
        private bool _heardAnswerSpeech;
        private float _listeningStartedAt;
        private string _sessionId;

        private void Awake()
        {
            Time.timeScale = 1f;
            StopAllCoroutines();
            _history.Clear();
            _current = null;
            _answerBusy = false;
            State = SessionState.Idle;
            _sessionId = Guid.NewGuid().ToString("N");
            if (!Api) Api = GetComponent<CoachApi>();
            if (Api) Api.TimeoutSeconds = Mathf.Max(Api.TimeoutSeconds, 180);
            if (!Microphone) Microphone = GetComponent<MicrophoneRecorder>();
            if (!InterviewLaunchSettings.TryApplyTo(this))
            {
                Debug.Log("[SpeakUpXR] 메뉴 입력 없이 Interview 씬을 실행해 MainMenu로 이동합니다.");
                enabled = false;
                SceneManager.LoadScene("MainMenu");
                return;
            }
            _launchReady = true;
            if (!FeedbackCompanion) FeedbackCompanion = FindFirstObjectByType<FeedbackCompanionController>();
            Debug.Log($"[SpeakUpXR Interview] 새 세션 {_sessionId[..8]} · 질문 {MaxQuestions}개 · {Config.job_role} / {Config.topic}");
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
            if (!_launchReady) return;
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

        private void Update()
        {
            if (!_launchReady || State != SessionState.Listening || _answerBusy) return;
            if (Microphone && Microphone.IsRecording && Microphone.CurrentRms >= Microphone.SilenceThreshold)
                _heardAnswerSpeech = true;
            if (_heardAnswerSpeech || Time.unscaledTime - _listeningStartedAt < 10f) return;
            _answerBusy = true;
            StartCoroutine(SkipUnansweredAfterTimeout());
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
            Debug.Log($"[SpeakUpXR Interview] {value} · 답변 {_history.Count}/{MaxQuestions}");
            OnStateChanged?.Invoke(value);
        }

        private IEnumerator RunIntro()
        {
            SetState(SessionState.Intro);
            Hud?.SetQuestion("면접을 시작합니다.");
            Hud?.SetStatus("면접 시작", HudTone.Ask);
            yield return GenerateFirstQuestion();
        }

        private IEnumerator GenerateFirstQuestion()
        {
            SetState(SessionState.Thinking);
            Hud?.SetStatus("지원 분야에 맞는 첫 질문을 준비하는 중", HudTone.Think);
            InterviewNextResponse next = null;
            while (next == null || next.done || string.IsNullOrWhiteSpace(next.question))
            {
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
                    Hud?.SetStatus("로컬 AI 연결을 기다리는 중", HudTone.Think);
                    yield return new WaitForSecondsRealtime(1.5f);
                }
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
            _heardAnswerSpeech = false;
            _listeningStartedAt = Time.unscaledTime;
        }

        private IEnumerator SkipUnansweredAfterTimeout()
        {
            Signals?.EndAnswer();
            Microphone?.End();
            if (FeedbackCompanion)
                yield return FeedbackCompanion.SpeakFeedback("아, 네. 다음 질문으로 넘어갈까요?", "neutral");

            _current.answer = "(10초 이상 무응답)";
            _current.wpm = -1f;
            _current.gaze_ratio = Signals ? Signals.CurrentGazeRatio : -1f;
            _current.gaze_switches = Signals ? Signals.CurrentSwitches : 0;
            _current.posture_sway = Signals ? Signals.CurrentPostureSway : -1f;
            _current.head_motion = Signals ? Signals.CurrentHeadMotion : -1f;
            _current.hand_motion = Signals ? Signals.CurrentHandMotion : -1f;
            _current.hand_span = Signals ? Signals.CurrentHandSpan : -1f;
            _current.gesture_idle_seconds = Signals ? Signals.CurrentGestureIdleSeconds : -1f;
            _history.Add(_current);
            _answerBusy = false;
            yield return ThinkAndContinue();
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
            SetState(SessionState.Listening);
            Hud?.SetQuestion(_current.question);
            Hud?.SetStatus("음성이 감지되지 않았습니다 · 다시 답변해 주세요", HudTone.Listen);
            Hud?.SetInterim("음성을 기록하고 있습니다");
            Signals?.BeginAnswer();
            Microphone?.Begin();
            _heardAnswerSpeech = false;
            _listeningStartedAt = Time.unscaledTime;
            _answerBusy = false;
            yield break;
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
            while (next == null || (!next.done && string.IsNullOrWhiteSpace(next.question)))
            {
                string error = null;
                if (Api) yield return Api.NextQuestion(request, value => next = value, value => error = value);
                if (next == null || (!next.done && string.IsNullOrWhiteSpace(next.question)))
                {
                    if (!string.IsNullOrEmpty(error)) Debug.LogWarning("[interview] local LLM retry: " + error);
                    Hud?.SetStatus("로컬 AI 응답을 다시 기다리는 중", HudTone.Think);
                    yield return new WaitForSecondsRealtime(1.5f);
                }
            }

            if (next.done)
            {
                yield return CloseInterview(next);
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

        private IEnumerator CloseInterview(InterviewNextResponse closing)
        {
            SetState(SessionState.Closing);
            Hud?.SetInterim("");
            Hud?.SetQuestion("면접이 종료되었습니다.");
            Hud?.SetStatus("마무리 인사", HudTone.Done);
            if (!string.IsNullOrWhiteSpace(closing?.reaction))
                yield return Speak(
                    closing.reaction,
                    closing.reaction_speaker ?? "warm",
                    "closing",
                    closing.reaction_tone ?? "warm");

            // Freeze the interview world while networking/UI continue in real time.
            SetState(SessionState.ReportLoading);
            Time.timeScale = 0f;
            ReportView?.ShowLoading();
            Hud?.SetStatus("면접 리포트를 불러오는 중", HudTone.Think);

            InterviewReportResponse report = null;
            while (report == null)
            {
                string error = null;
                if (Api)
                    yield return Api.Report(
                    new InterviewReportRequest { config = Config, history = _history.ToArray() },
                    value =>
                    {
                        if (IsUsefulReport(value)) report = value;
                        else error = "리포트 응답에 요약·평가 본문이 없습니다.";
                    },
                    value => error = value);
                if (report == null)
                {
                    if (!string.IsNullOrEmpty(error)) Debug.LogWarning("[interview] report retry: " + error);
                    Hud?.SetStatus("로컬 AI 리포트를 다시 생성하는 중", HudTone.Think);
                    yield return new WaitForSecondsRealtime(1.5f);
                }
            }

            SetState(SessionState.Done);
            SaveReport(report);
            Hud?.SetStatus("리포트가 준비되었습니다", HudTone.Done);
            ReportView?.ShowReport(report, _history.ToArray());
            OnFinished?.Invoke(_history, report);
        }

        private static bool IsUsefulReport(InterviewReportResponse report) =>
            report != null &&
            (!string.IsNullOrWhiteSpace(report.overall_summary) ||
             report.strengths?.Length > 0 || report.improvements?.Length > 0 ||
             report.per_question?.Length > 0);

        private void SaveReport(InterviewReportResponse report)
        {
            try
            {
                string directory = Path.Combine(Application.persistentDataPath, "InterviewReports");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"interview-report-{_sessionId}.json");
                var saved = new SavedInterviewReport
                {
                    session_id = _sessionId,
                    generated_at_utc = DateTime.UtcNow.ToString("O"),
                    config = Config,
                    history = _history.ToArray(),
                    report = report,
                };
                File.WriteAllText(path, JsonUtility.ToJson(saved, true));
                Debug.Log($"[SpeakUpXR Report] 저장 완료: {path}");
            }
            catch (Exception exception)
            {
                Debug.LogError("[SpeakUpXR Report] 리포트 저장 실패: " + exception.Message);
            }
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
