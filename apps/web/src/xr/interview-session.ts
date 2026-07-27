// The XR interview loop as an explicit state machine.
//
//   idle → intro → asking → listening → thinking → (asking …) → closing → done
//
// It orchestrates four collaborators and owns none of them:
//   - brain        : decides the next question (script now, LLM later)
//   - interviewer  : the VRM (speaking mouth, nod, gaze)
//   - ui           : the in-world question / status panel
//   - speech       : browser TTS + STT
//
// The candidate advances a turn by clicking "다음" (or a VR controller later);
// STT fills the answer text in the background. This keeps pacing in the user's
// hands and makes the loop demoable even where STT is unavailable.

import type { Interviewer } from './interviewer';
import type { InterviewUI } from './interview-ui';
import type { InterviewBrain, InterviewTurn, QAExchange } from './interview-brain';
import { speak, listen, sttAvailable, type ListenHandle } from './speech';

export type SessionState =
  | 'idle'
  | 'intro'
  | 'asking'
  | 'listening'
  | 'thinking'
  | 'closing'
  | 'done';

export interface InterviewSessionDeps {
  brain: InterviewBrain;
  interviewer: Interviewer;
  ui: InterviewUI;
  onStateChange?: (state: SessionState) => void;
  onDone?: (history: QAExchange[]) => void;
}

export interface InterviewSession {
  start: () => Promise<void>;
  /** Candidate finished the current answer → move on. No-op unless listening. */
  finishAnswer: () => void;
  getState: () => SessionState;
  getHistory: () => QAExchange[];
}

export function createInterviewSession(deps: InterviewSessionDeps): InterviewSession {
  const { brain, interviewer, ui, onStateChange, onDone } = deps;

  let state: SessionState = 'idle';
  let currentTurn: InterviewTurn | null = null;
  let currentAnswer = '';
  let listenHandle: ListenHandle | null = null;
  const history: QAExchange[] = [];

  function setState(s: SessionState) {
    state = s;
    onStateChange?.(s);
  }

  async function speakLine(text: string) {
    interviewer.setSpeaking(true);
    try {
      await speak(text, { onEnd: () => interviewer.setSpeaking(false) }).done;
    } finally {
      interviewer.setSpeaking(false);
    }
  }

  async function askTurn(turn: InterviewTurn) {
    currentTurn = turn;
    setState('asking');
    ui.setQuestion(turn.question);
    ui.setStatus('면접관이 질문하는 중', 'ask');
    await speakLine(turn.question);
    startListening();
  }

  function startListening() {
    setState('listening');
    currentAnswer = '';
    ui.setStatus('🎤 답변을 듣는 중 — 끝나면 "다음"', 'listen');
    listenHandle = listen({
      onInterim: (t) => ui.setInterim(t),
      onFinal: (t) => {
        currentAnswer = t;
        ui.setInterim(t);
      },
      onError: () => {
        // STT blocked/unsupported — the candidate can still advance manually.
        if (!sttAvailable()) {
          ui.setStatus('🎤 음성 인식 미지원 — 답변 후 "다음"', 'listen');
        }
      },
    });
  }

  async function finishAnswer() {
    if (state !== 'listening') return;
    listenHandle?.stop();
    listenHandle = null;
    if (currentTurn) {
      history.push({
        question: currentTurn.question,
        answer: currentAnswer,
        kind: currentTurn.kind,
      });
    }
    setState('thinking');
    ui.setInterim('');
    ui.setStatus('생각하는 중…', 'think');
    interviewer.nod();

    const next = await brain.next(history);
    if (next) {
      await askTurn(next);
    } else {
      await closing();
    }
  }

  async function closing() {
    setState('closing');
    ui.setQuestion('면접이 종료되었습니다. 리포트를 준비합니다.');
    ui.setStatus('면접 종료', 'done');
    await speakLine(brain.closing());
    setState('done');
    onDone?.(history);
  }

  async function start() {
    if (state !== 'idle') return;
    setState('intro');
    ui.setQuestion('면접을 시작합니다.');
    ui.setStatus('면접 시작', 'ask');
    await speakLine(brain.intro());
    await askTurn(brain.first());
  }

  return {
    start,
    finishAnswer,
    getState: () => state,
    getHistory: () => history.slice(),
  };
}
