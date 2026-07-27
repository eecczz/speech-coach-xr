// The interview loop state machine — C# port of interview-session.ts + interview-brain.ts.
//
//   Idle → Intro → Asking → Listening → Thinking → (Asking …) → Closing → Done
//
// First question is a fixed opener (natural start); every later question comes from
// the coach backend (answer-aware follow-up / pressure). Voice is behind ISpeech so
// the subtitle-only MVP works before TTS/STT are wired.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SpeakUpXR
{
    public enum SessionState { Idle, Intro, Asking, Listening, Thinking, Closing, Done }

    /// <summary>Seam for TTS/STT. MVP: SubtitleSpeech (no audio, timed subtitles).</summary>
    public interface ISpeech
    {
        IEnumerator Speak(string text);          // resolve when "done speaking"
        void StartListening(Action<string> onTranscript);
        string StopListening();                  // returns final transcript so far
    }

    /// <summary>No-audio placeholder: pace by text length; answers arrive via UI/next button.</summary>
    public class SubtitleSpeech : ISpeech
    {
        public IEnumerator Speak(string text)
        {
            yield return new WaitForSeconds(Mathf.Clamp(1.5f + text.Length * 0.055f, 2f, 12f));
        }
        public void StartListening(Action<string> onTranscript) { }
        public string StopListening() => "";
    }

    public class InterviewSession : MonoBehaviour
    {
        public CoachApi Api;
        public InterviewerController Interviewer;
        public InterviewHud Hud;
        public int MaxQuestions = 5;

        public SessionState State { get; private set; } = SessionState.Idle;
        public event Action<SessionState> OnStateChanged;
        public event Action<List<QAExchange>, InterviewReportResponse> OnFinished;

        private readonly List<QAExchange> _history = new();
        private QAExchange _current;
        private ISpeech _speech = new SubtitleSpeech();

        private const string IntroLine = "안녕하세요. 지금부터 면접을 시작하겠습니다. 긴장 푸시고, 편하게 답변해 주세요.";
        private const string FirstQuestion = "먼저 간단히 자기소개 부탁드립니다.";
        private const string ClosingLine = "수고하셨습니다. 이상으로 면접을 마치겠습니다. 곧 결과 리포트를 확인하실 수 있습니다.";

        public void SetSpeech(ISpeech speech) => _speech = speech;

        public void StartInterview()
        {
            if (State != SessionState.Idle) return;
            StartCoroutine(RunIntro());
        }

        /// <summary>Candidate finished answering (UI button / controller trigger).</summary>
        public void FinishAnswer(string answerOverride = null)
        {
            if (State != SessionState.Listening) return;
            var stt = _speech.StopListening();
            _current.answer = !string.IsNullOrEmpty(answerOverride) ? answerOverride : stt;
            _history.Add(_current);
            StartCoroutine(RunThinking());
        }

        private void SetState(SessionState s)
        {
            State = s;
            OnStateChanged?.Invoke(s);
        }

        private IEnumerator RunIntro()
        {
            SetState(SessionState.Intro);
            Hud.SetQuestion("면접을 시작합니다.");
            Hud.SetStatus("면접 시작", HudTone.Ask);
            yield return SpeakAs(IntroLine);
            yield return RunAsk(new InterviewNextResponse { question = FirstQuestion, kind = "base" });
        }

        private IEnumerator RunAsk(InterviewNextResponse next)
        {
            _current = new QAExchange { question = next.question, kind = next.kind };
            SetState(SessionState.Asking);
            Hud.SetQuestion(next.question);
            Hud.SetStatus("면접관이 질문하는 중", HudTone.Ask);
            yield return SpeakAs(next.question);

            SetState(SessionState.Listening);
            Hud.SetStatus("답변해 주세요 — 끝나면 트리거/다음", HudTone.Listen);
            _speech.StartListening(t => Hud.SetInterim(t));
        }

        private IEnumerator RunThinking()
        {
            SetState(SessionState.Thinking);
            Hud.SetInterim("");
            Hud.SetStatus("생각하는 중…", HudTone.Think);
            if (Interviewer != null) Interviewer.Nod();

            var req = new InterviewNextRequest
            {
                history = _history.ToArray(),
                asked_count = _history.Count,
                max_questions = MaxQuestions,
            };
            InterviewNextResponse next = null;
            string err = null;
            yield return Api.NextQuestion(req, r => next = r, e => err = e);

            if (err != null || next == null || next.done || string.IsNullOrEmpty(next.question))
            {
                if (err != null) Debug.LogWarning($"[interview] next failed → closing: {err}");
                yield return RunClosing();
            }
            else
            {
                yield return RunAsk(next);
            }
        }

        private IEnumerator RunClosing()
        {
            SetState(SessionState.Closing);
            Hud.SetQuestion("면접이 종료되었습니다. 리포트를 준비합니다.");
            Hud.SetStatus("면접 종료", HudTone.Done);
            yield return SpeakAs(ClosingLine);

            InterviewReportResponse report = null;
            yield return Api.Report(
                new InterviewReportRequest { history = _history.ToArray() },
                r => report = r,
                e => Debug.LogWarning($"[interview] report failed: {e}"));

            SetState(SessionState.Done);
            OnFinished?.Invoke(_history, report);
        }

        private IEnumerator SpeakAs(string line)
        {
            if (Interviewer != null) Interviewer.SetSpeaking(true);
            yield return _speech.Speak(line);
            if (Interviewer != null) Interviewer.SetSpeaking(false);
        }
    }
}
