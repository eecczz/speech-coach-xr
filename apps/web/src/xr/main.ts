import { createXRInterview } from './xr-stage';
import { resolveAvatarUrl } from '../avatar/registry';
import type { SessionState } from './interview-session';
import type { QAExchange } from './interview-brain';
import { fetchInterviewReport } from './llm-brain';

// Stage-1/2 entry: boot the WebXR interview room and drive the interview loop.
// Reuses the verified engine's avatar registry (default.vrm).

const container = document.getElementById('xr-root');
const statusEl = document.getElementById('xr-status');
const startBtn = document.getElementById('btn-start') as HTMLButtonElement | null;
const nextBtn = document.getElementById('btn-next') as HTMLButtonElement | null;

function setStatus(msg: string): void {
  if (statusEl) statusEl.textContent = msg;
}

if (!container) {
  throw new Error('#xr-root container missing');
}

const params = new URLSearchParams(location.search);
const vrmUrl = resolveAvatarUrl(params.get('avatar'));
// ?brain=llm → AI interviewer (coach service); default 'script' runs fully offline.
const brainMode: 'script' | 'llm' = params.get('brain') === 'llm' ? 'llm' : 'script';

// Reflect the interview state machine onto the 2D control buttons.
function applySessionState(state: SessionState): void {
  if (!startBtn || !nextBtn) return;
  switch (state) {
    case 'idle':
      startBtn.disabled = false;
      nextBtn.disabled = true;
      break;
    case 'listening':
      startBtn.disabled = true;
      nextBtn.disabled = false; // candidate advances after answering
      break;
    case 'done':
      startBtn.disabled = true;
      nextBtn.disabled = true;
      setStatus('면접이 종료되었습니다. (다음 단계: 통합 리포트 생성)');
      break;
    default: // intro / asking / thinking / closing — interviewer is busy
      startBtn.disabled = true;
      nextBtn.disabled = true;
  }
}

async function onDone(history: QAExchange[]): Promise<void> {
  console.log('[interview] transcript', history);
  if (brainMode !== 'llm') return;
  // Fetch the text-only answer-structure report (Stage 3 adds XR signals to it).
  setStatus('리포트를 생성하는 중…');
  const report = await fetchInterviewReport(history, { difficulty: '보통' });
  if (report) {
    console.log('[interview] report', report);
    setStatus('리포트 생성 완료 — 콘솔에서 확인하세요.');
  } else {
    setStatus('리포트 생성 실패 — coach 서비스/LLM 키를 확인하세요.');
  }
}

createXRInterview(container, vrmUrl, {
  onStatus: setStatus,
  onSessionState: applySessionState,
  onDone,
  brainMode,
})
  .then((stage) => {
    if (!stage.session) {
      if (startBtn) startBtn.disabled = true;
      return;
    }
    const session = stage.session;
    startBtn?.addEventListener('click', () => void session.start());
    nextBtn?.addEventListener('click', () => session.finishAnswer());
    applySessionState('idle');
  })
  .catch((err) => {
    console.error(err);
    setStatus('초기화 실패 — 콘솔을 확인하세요.');
  });
