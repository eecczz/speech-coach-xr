import { createAvatarStage } from './avatar/stage';
import {
  applyFaceToVRM,
  applyPoseToVRM,
  applyHandsToVRM,
  applyFaceToFallback,
} from './avatar/retarget';
import { resolveAvatarUrl } from './avatar/registry';
import { createLandmarkers, detect } from './mediapipe/landmarkers';
import { AvatarRecorder } from './recorder/canvas-record';
import { computeVisionFrame, resetSignalState, type VisionFrame } from './signals/compute';
import { SilenceDetector } from './signals/silence';
import { createAggregatorClient, createHudClient, type LiveHudResponse, type ProsodyFrame } from './ws/client';
import { getCurrentProject, getProjectById } from './project-store';
import { savePendingMedia, setPendingAnalysis, type LiveCoachingEvent } from './session-store';
import { getActiveUser, listAgentMessages, listRemoteSessions, saveAgentMessage } from './app-api';
import {
  SilenceTrigger,
  GazeTrigger,
  SmileAbsenceTrigger,
  MotionAbsenceTrigger,
  FillerTrigger,
  SpeechRateTrigger,
  ContentTrigger,
  countFillers,
  type TriggerEvent,
  type TriggerKind,
} from './agent/triggers';
import {
  AgentDispatcher,
  VisionContextBuffer,
  TranscriptBuffer,
  type MultimodalContext,
} from './agent/dispatcher';

type SpeechRecognitionAlternativeLike = {
  transcript: string;
  confidence: number;
};

type SpeechRecognitionResultLike = {
  readonly isFinal: boolean;
  readonly length: number;
  [index: number]: SpeechRecognitionAlternativeLike;
};

type SpeechRecognitionEventLike = Event & {
  resultIndex: number;
  results: {
    readonly length: number;
    [index: number]: SpeechRecognitionResultLike;
  };
};

type SpeechRecognitionErrorEventLike = Event & {
  error: string;
};

type SpeechRecognitionLike = {
  lang: string;
  continuous: boolean;
  interimResults: boolean;
  maxAlternatives: number;
  onstart: (() => void) | null;
  onresult: ((event: SpeechRecognitionEventLike) => void) | null;
  onerror: ((event: SpeechRecognitionErrorEventLike) => void) | null;
  onend: (() => void) | null;
  start: () => void;
  stop: () => void;
  abort: () => void;
};

type SpeechRecognitionConstructor = new () => SpeechRecognitionLike;

const status = document.getElementById('status') as HTMLDivElement;
const video = document.getElementById('cam') as HTMLVideoElement;
const canvas = document.getElementById('avatar') as HTMLCanvasElement;
const debug = document.getElementById('debug') as HTMLPreElement;
const camSelect = document.getElementById('cam-select') as HTMLSelectElement;
const btnStart = document.getElementById('btn-start') as HTMLButtonElement;
const btnPause = document.getElementById('btn-pause') as HTMLButtonElement | null;
const btnStop = document.getElementById('btn-stop') as HTMLButtonElement;
const recorded = document.getElementById('recorded') as HTMLVideoElement | null;
const timerEl = document.querySelector('.timer') as HTMLDivElement | null;
const recordBadge = document.querySelector('.record-badge') as HTMLDivElement | null;
const agentFeed = document.getElementById('agent-chat-feed') as HTMLElement | null;
const agentForm = document.getElementById('agent-chat-form') as HTMLFormElement | null;
const agentInput = document.getElementById('agent-chat-input') as HTMLInputElement | null;
const agentState = document.getElementById('agent-state') as HTMLElement | null;
const agentSessionList = document.getElementById('agent-session-list') as HTMLElement | null;
const agentVoiceToggle = document.getElementById('agent-voice-toggle') as HTMLButtonElement | null;
const liveCoachToast = document.getElementById('live-coach-toast') as HTMLElement | null;
const liveCoachTime = document.getElementById('live-coach-time') as HTMLElement | null;
const liveCoachMessage = document.getElementById('live-coach-message') as HTMLElement | null;
const hudCards = {
  wpm: document.querySelector<HTMLElement>('[data-hud-card="wpm"]'),
  filler: document.querySelector<HTMLElement>('[data-hud-card="filler"]'),
  silence: document.querySelector<HTMLElement>('[data-hud-card="silence"]'),
};
const focusAxes = {
  verbal: document.querySelector<HTMLElement>('[data-live-axis="verbal"]'),
  prosody: document.querySelector<HTMLElement>('[data-live-axis="prosody"]'),
  nonverbal: document.querySelector<HTMLElement>('[data-live-axis="nonverbal"]'),
};

const params = new URLSearchParams(location.search);
const projectId = params.get('projectId') || localStorage.getItem('speakup-current-project-id') || '';
const projectRef = getProjectById(projectId) || getCurrentProject();
const projectName = params.get('project') || projectRef?.name || '오늘의 말하기 연습';
const typeName = params.get('type') || 'free';
const remoteSessionId = params.get('sessionId') || '';
const situationName = params.get('situation') || localStorage.getItem('speakup-practice-situation') || '';
const goals = (params.get('goal') || '말 속도')
  .split(',')
  .map((item) => item.trim())
  .filter(Boolean);
const focusSet = new Set(goals);

if (projectRef) {
  localStorage.setItem('speakup-current-project-id', projectRef.id);
  localStorage.setItem('speakup-project-name', projectRef.name);
}

const SCENARIO_MAP: Record<string, string> = {
  presentation: 'presentation',
  interview: 'interview',
  negotiation: 'presentation',
  persuasion: 'presentation',
  daily: 'casual',
  phone: 'customer_service',
  online: 'presentation',
  free: 'presentation',
};

const FOCUS_LABELS = {
  wpm: ['말 속도', '전달력', '목소리 톤'],
  filler: ['필러 표현', '논리 흐름', '전달력'],
  silence: ['침묵 구간', '말 속도', '목소리 톤'],
  verbal: ['논리 흐름', '필러 표현', '전달력'],
  prosody: ['말 속도', '침묵 구간', '목소리 톤'],
  nonverbal: ['시선 처리', '자세', '표정', '제스처', '자신감'],
  gaze: ['시선 처리', '자신감'],
  posture: ['자세', '자신감'],
  expression: ['표정', '자신감'],
  gesture: ['제스처', '자신감'],
} as const;
const AGENT_STT_CHUNK_MS = 4500;

type RealtimeFocusKey = keyof typeof FOCUS_LABELS;

// ── New agent pipeline: triggers + dispatcher + multimodal context buffers ──
// Each trigger owns its own thresholds + per-kind cooldown (see triggers.ts).
// The dispatcher layers a global cooldown on top + the LLM call.
const silenceTrigger = new SilenceTrigger();
const gazeTrigger = new GazeTrigger();
const smileAbsenceTrigger = new SmileAbsenceTrigger();
const motionAbsenceTrigger = new MotionAbsenceTrigger();
const fillerTrigger = new FillerTrigger();
const speechRateTrigger = new SpeechRateTrigger();
const contentTrigger = new ContentTrigger();
const visionContextBuffer = new VisionContextBuffer();
const transcriptBuffer = new TranscriptBuffer();
let fillerTotal = 0;
let agentDispatcher: AgentDispatcher | null = null;

const liveCoachingEvents: LiveCoachingEvent[] = [];
let currentSessionSeconds = 0;
// Pause bookkeeping. While paused we freeze the session clock and skip every
// trigger evaluation so cooldowns don't drain in the background. pausedAtTSec
// holds the wall-clock instant we paused; pausedAccumSec is the total wall-time
// already spent paused before *this* run.
let paused = false;
let pausedAtTSec = 0;
let pausedAccumSec = 0;
let liveCoachToastTimer = 0;
let agentVoiceEnabled = localStorage.getItem('speakup-agent-voice') === 'true';
let speechRecognition: SpeechRecognitionLike | null = null;
let speechAgentListening = false;
let speechChunkRecorder: MediaRecorder | null = null;
let speechChunkTimer = 0;
let speechChunkSource: MediaStream | null = null;
let speechChunkIndex = 0;
let lastSpokenTranscriptKey = '';
// Track the most recent silence reading for the dispatcher's multimodal context.
let lastSilenceSeconds = 0;

function focusEnabled(key: RealtimeFocusKey): boolean {
  if (focusSet.size === 0) return true;
  return FOCUS_LABELS[key].some((label) => focusSet.has(label));
}

function resolveFocusGoals(): string[] {
  const params = new URLSearchParams(location.search);
  const goal = params.get('goal') || '';
  return goal
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean);
}

function applyFocusVisibility(): void {
  const hudMap: Record<keyof typeof hudCards, keyof typeof FOCUS_LABELS> = {
    wpm: 'wpm',
    filler: 'filler',
    silence: 'silence',
  };
  Object.entries(hudMap).forEach(([cardKey, focusKey]) => {
    const card = hudCards[cardKey as keyof typeof hudCards];
    if (card) card.hidden = !focusEnabled(focusKey);
  });
  const axisMap: Record<keyof typeof focusAxes, keyof typeof FOCUS_LABELS> = {
    verbal: 'verbal',
    prosody: 'prosody',
    nonverbal: 'nonverbal',
  };
  Object.entries(axisMap).forEach(([axisKey, focusKey]) => {
    const row = focusAxes[axisKey as keyof typeof focusAxes];
    if (row) row.hidden = !focusEnabled(focusKey);
  });
}

function setAgentState(label: string): void {
  if (agentState) agentState.textContent = label;
}

function syncAgentVoiceToggle(): void {
  if (!agentVoiceToggle) return;
  agentVoiceToggle.setAttribute('aria-pressed', String(agentVoiceEnabled));
  const icon = agentVoiceToggle.querySelector('i');
  const label = agentVoiceToggle.querySelector('span');
  if (icon) icon.className = agentVoiceEnabled ? 'ti ti-volume' : 'ti ti-volume-off';
  if (label) label.textContent = agentVoiceEnabled ? '음성 켜짐' : '음성 꺼짐';
}

function speakAgentReply(content: string): void {
  if (!agentVoiceEnabled || !('speechSynthesis' in window)) return;
  window.speechSynthesis.cancel();
  const utterance = new SpeechSynthesisUtterance(content);
  utterance.lang = 'ko-KR';
  utterance.rate = 1;
  utterance.pitch = 1;
  window.speechSynthesis.speak(utterance);
}

function showLiveCoachToast(content: string, sessionT: number): void {
  if (!liveCoachToast || !liveCoachMessage || !liveCoachTime) return;
  liveCoachTime.textContent = formatClock(sessionT);
  liveCoachMessage.textContent = content;
  liveCoachToast.hidden = false;
  liveCoachToast.classList.add('is-visible');
  window.clearTimeout(liveCoachToastTimer);
  liveCoachToastTimer = window.setTimeout(() => {
    liveCoachToast.classList.remove('is-visible');
    window.setTimeout(() => {
      if (!liveCoachToast.classList.contains('is-visible')) liveCoachToast.hidden = true;
    }, 180);
  }, 2800);
}

function getSpeechRecognitionConstructor(): SpeechRecognitionConstructor | null {
  const speechWindow = window as typeof window & {
    SpeechRecognition?: SpeechRecognitionConstructor;
    webkitSpeechRecognition?: SpeechRecognitionConstructor;
  };
  return speechWindow.SpeechRecognition ?? speechWindow.webkitSpeechRecognition ?? null;
}

function appendAgentMessage(
  role: 'agent' | 'user' | 'system',
  content: string,
  t: number | null = null,
  metadata: Record<string, unknown> = {},
  persist = true,
): void {
  if (!agentFeed) return;
  const bubble = document.createElement('div');
  bubble.className = `agent-bubble is-${role}`;
  if (typeof t === 'number') {
    const time = document.createElement('span');
    time.textContent = formatClock(t);
    bubble.appendChild(time);
  }
  const paragraph = document.createElement('p');
  paragraph.textContent = content;
  bubble.appendChild(paragraph);
  agentFeed.appendChild(bubble);
  agentFeed.scrollTop = agentFeed.scrollHeight;
  if (remoteSessionId && persist) {
    void saveAgentMessage({ session_id: remoteSessionId, role, content, t, metadata }).catch((err) => {
      console.warn('[agent] message save failed', err);
    });
  }
}

// Snapshot helper — builds the multimodal context that we attach to every
// dispatcher call. Kept here (not in dispatcher.ts) because it needs to read
// browser-scoped state: the transcript buffer, current silence, focus goals.
function buildMultimodalContext(event: TriggerEvent): MultimodalContext {
  const vision = visionContextBuffer.snapshot();
  // For content triggers, the "thing to evaluate" is the segment that fired
  // the trigger. For other trigger kinds (silence/gaze/...), there's no single
  // sentence under the microscope — we still pass current_utterance so the
  // LLM has the latest line for situational awareness, but the focus is on
  // the trigger itself.
  const currentFromTrigger =
    event.kind === 'content' && typeof event.payload.segment === 'string'
      ? (event.payload.segment as string)
      : '';
  const current = currentFromTrigger || transcriptBuffer.currentUtterance();
  const previous = transcriptBuffer.previousUtterances();
  return {
    recent_transcript: transcriptBuffer.recent(),
    previous_utterances: previous,
    current_utterance: current,
    wpm: estimateRecentWpm(),
    filler_count_total: fillerTotal,
    recent_fillers: fillerTrigger['recentFillers']?.slice?.(-5) ?? [],
    gaze_fixation_ratio_avg: vision.gaze_fixation_ratio_avg,
    head_yaw_deg_avg: vision.head_yaw_deg_avg,
    smile_intensity_avg: vision.smile_intensity_avg,
    expression_change_rate_avg: vision.expression_change_rate_avg,
    hand_velocity_max_avg: vision.hand_velocity_max_avg,
    posture_sway_avg: vision.posture_sway_avg,
    current_silence_seconds: lastSilenceSeconds > 0.5 ? lastSilenceSeconds : null,
    session_elapsed_s: currentSessionSeconds,
    recent_agent_messages: agentDispatcher?.recentMessages() ?? [],
  };
}

// Reuse SpeechRateTrigger's internal sample buffer to ballpark the WPM we send
// in the context. The trigger itself decides when to *fire*; we only need the
// number here for the LLM's awareness.
function estimateRecentWpm(): number | null {
  const samples = (speechRateTrigger as unknown as { samples?: Array<{ t: number; words: number }> }).samples;
  if (!samples || samples.length === 0) return null;
  const t = currentSessionSeconds;
  const span = Math.max(t - samples[0].t, 5.0);
  const totalWords = samples.reduce((sum, s) => sum + s.words, 0);
  return (totalWords / span) * 60;
}

function onAgentFeedback(message: string, t: number, kind: TriggerKind, tone: string | null) {
  appendAgentMessage('agent', message, t, { kind: 'agent_feedback', trigger: kind, tone });
  speakAgentReply(message);
  showLiveCoachToast(message, t);
  liveCoachingEvents.push({
    id: `agent_${kind}_${Math.floor(t * 1000)}`,
    t,
    duration_s: 0,
    axis: triggerToAxis(kind),
    key: kind,
    level: tone === 'critique' ? 'warn' : tone === 'praise' ? 'info' : 'info',
    title: kind,
    message,
    impact: tone === 'critique' ? -1 : tone === 'praise' ? 1 : 0,
    metadata: { source: 'agent_dispatcher', tone },
  });
}

function triggerToAxis(kind: TriggerKind): string {
  switch (kind) {
    case 'silence':
    case 'speech_rate':
      return 'delivery';
    case 'gaze':
      return 'gaze';
    case 'smile_absence':
      return 'expression';
    case 'motion_absence':
      return 'gesture';
    case 'filler':
      return 'delivery';
    case 'content':
      return 'logic';
    default:
      return 'overall';
  }
}

// Final STT segment arrived → log it to the chat, push into the transcript +
// speech-rate buffers, count fillers, then let the content trigger decide if
// it wants to fire an LLM round.
function handleSpokenText(text: string, confidence: number | null): void {
  // Paused = no recording + no coaching. Drop the segment entirely; the next
  // resume will re-arm STT and the user will see the timer pick back up.
  if (paused) return;
  const transcript = text.replace(/\s+/g, ' ').trim();
  if (transcript.length < 2) return;
  const transcriptKey = transcript.replace(/[\s.,!?！？。]/g, '').toLowerCase();
  if (!transcriptKey || transcriptKey === lastSpokenTranscriptKey) return;
  lastSpokenTranscriptKey = transcriptKey;
  const sessionT = currentSessionSeconds || 0;
  appendAgentMessage('user', transcript, sessionT, {
    kind: 'spoken_transcript',
    confidence,
  });
  transcriptBuffer.add(transcript, sessionT);
  speechRateTrigger.addUtterance(transcript, sessionT);

  // Filler counting — feed each match into the trigger so it can fire on
  // the next 5/10/15... threshold.
  const { count, matched } = countFillers(transcript);
  if (count > 0) {
    fillerTotal += count;
    matched.forEach((term) => fillerTrigger.noteFiller(term));
    const fillerEvent = fillerTrigger.evaluate(fillerTotal, sessionT);
    if (fillerEvent) agentDispatcher?.submit(fillerEvent);
  }

  // Speech rate — evaluate after the utterance is in the buffer.
  const rateEvent = speechRateTrigger.evaluate(sessionT);
  if (rateEvent) agentDispatcher?.submit(rateEvent);

  // Content — LLM decides whether this utterance is worth a remark.
  const contentEvent = contentTrigger.evaluate(transcript, sessionT);
  if (contentEvent) agentDispatcher?.submit(contentEvent);
}

function chooseAgentAudioMimeType(): string {
  const candidates = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4'];
  return candidates.find((type) => MediaRecorder.isTypeSupported(type)) ?? '';
}

async function transcribeAgentAudioChunk(blob: Blob): Promise<void> {
  if (blob.size < 1000) return;
  const form = new FormData();
  form.append('audio', blob, `agent-speech-${speechChunkIndex}.webm`);
  form.append('language', 'ko');
  const response = await fetch('/transcribe', {
    method: 'POST',
    body: form,
  });
  if (!response.ok) {
    console.warn('[agent] speech chunk transcribe failed', response.status);
    return;
  }
  const payload = await response.json().catch(() => ({})) as { full_text?: string; text?: string };
  const transcript = String(payload.full_text || payload.text || '').trim();
  if (transcript) handleSpokenText(transcript, null);
}

function scheduleAgentAudioChunk(): void {
  if (!speechAgentListening || !speechChunkSource) return;
  const chunks: Blob[] = [];
  const mimeType = chooseAgentAudioMimeType();
  const recorder = new MediaRecorder(speechChunkSource, mimeType ? { mimeType } : undefined);
  speechChunkRecorder = recorder;
  recorder.ondataavailable = (event) => {
    if (event.data.size > 0) chunks.push(event.data);
  };
  recorder.onstop = () => {
    speechChunkRecorder = null;
    const blob = new Blob(chunks, { type: mimeType || 'audio/webm' });
    speechChunkIndex += 1;
    void transcribeAgentAudioChunk(blob)
      .catch((err) => console.warn('[agent] speech chunk handling failed', err))
      .finally(() => {
        if (speechAgentListening) {
          window.setTimeout(scheduleAgentAudioChunk, 250);
        }
      });
  };
  try {
    recorder.start();
    speechChunkTimer = window.setTimeout(() => {
      if (recorder.state !== 'inactive') recorder.stop();
    }, AGENT_STT_CHUNK_MS);
  } catch (err) {
    console.warn('[agent] speech chunk recorder start failed', err);
    setAgentState('텍스트 질문 대기');
  }
}

function startBackendSpeechAgentListening(stream: MediaStream): void {
  const audioTracks = stream.getAudioTracks().filter((track) => track.readyState === 'live');
  if (audioTracks.length === 0 || typeof MediaRecorder === 'undefined') {
    setAgentState('텍스트 질문 대기');
    return;
  }
  speechAgentListening = true;
  speechChunkIndex = 0;
  speechChunkSource = new MediaStream(audioTracks);
  setAgentState('듣는 중');
  scheduleAgentAudioChunk();
}

function startSpeechAgentListening(stream: MediaStream): void {
  const Recognition = getSpeechRecognitionConstructor();
  if (!Recognition) {
    startBackendSpeechAgentListening(stream);
    return;
  }

  speechAgentListening = true;
  speechRecognition?.abort();
  const recognition = new Recognition();
  speechRecognition = recognition;
  recognition.lang = 'ko-KR';
  recognition.continuous = true;
  recognition.interimResults = true;
  recognition.maxAlternatives = 1;
  recognition.onstart = () => setAgentState('듣는 중');
  recognition.onresult = (event) => {
    for (let i = event.resultIndex; i < event.results.length; i += 1) {
      const result = event.results[i];
      if (!result.isFinal || result.length === 0) continue;
      const best = result[0];
      handleSpokenText(best.transcript, Number.isFinite(best.confidence) ? best.confidence : null);
    }
  };
  recognition.onerror = (event) => {
    if (event.error === 'no-speech') return;
    console.warn('[agent] speech recognition error', event.error);
    setAgentState('텍스트 질문 대기');
  };
  recognition.onend = () => {
    if (!speechAgentListening) return;
    window.setTimeout(() => {
      if (speechAgentListening) startSpeechAgentListening(stream);
    }, 350);
  };

  try {
    recognition.start();
  } catch (err) {
    console.warn('[agent] speech recognition start failed', err);
    setAgentState('텍스트 질문 대기');
  }
}

function stopSpeechAgentListening(): void {
  speechAgentListening = false;
  window.clearTimeout(speechChunkTimer);
  speechChunkTimer = 0;
  if (speechChunkRecorder && speechChunkRecorder.state !== 'inactive') {
    try {
      speechChunkRecorder.stop();
    } catch (err) {
      console.warn('[agent] speech chunk recorder stop failed', err);
    }
  }
  speechChunkRecorder = null;
  speechChunkSource = null;
  if (!speechRecognition) return;
  const recognition = speechRecognition;
  speechRecognition = null;
  recognition.onend = null;
  try {
    recognition.stop();
  } catch (err) {
    console.warn('[agent] speech recognition stop failed', err);
  }
}

// (Old realtime-coaching engine + nudgeAgent removed — replaced by the new
//  trigger evaluators + AgentDispatcher pipeline above.)

async function hydrateAgentPanel(): Promise<void> {
  const focusText = goals.length ? goals.join(', ') : '전체';
  if (remoteSessionId) {
    try {
      const messages = await listAgentMessages(remoteSessionId);
      if (messages.length > 0 && agentFeed) {
        const chatMessages = messages.filter((message) => {
          const kind = (message.metadata as { kind?: string } | undefined)?.kind;
          return (
            !kind ||
            kind === 'manual_question' ||
            kind === 'manual_answer' ||
            kind === 'session_ready' ||
            kind === 'spoken_transcript' ||
            kind === 'spoken_answer'
          );
        });
        if (chatMessages.length > 0) {
          agentFeed.innerHTML = '';
          chatMessages.slice(-30).forEach((message) => {
            appendAgentMessage(message.role, message.content, message.t, message.metadata, false);
          });
          return;
        }
      }
    } catch (err) {
      console.warn('[agent] messages load failed', err);
    }
  }
  appendAgentMessage(
    'agent',
    `세션을 준비했습니다. ${focusText}에 대해 궁금한 점을 물어보면 바로 답할게요.`,
    null,
    { kind: 'session_ready' },
  );
}

async function hydrateSessionList(): Promise<void> {
  if (!agentSessionList) return;
  const user = getActiveUser();
  if (!user) {
    agentSessionList.innerHTML = '<p>로그인 후 세션 목록을 볼 수 있습니다.</p>';
    return;
  }
  try {
    const sessions = await listRemoteSessions(user.id);
    agentSessionList.textContent = '';
    if (sessions.length === 0) {
      const empty = document.createElement('p');
      empty.textContent = '저장된 세션이 없습니다.';
      agentSessionList.appendChild(empty);
      return;
    }
    sessions.forEach((session) => {
      const url = new URL('practice.html', location.href);
      url.searchParams.set('sessionId', session.id);
      url.searchParams.set('project', session.title);
      url.searchParams.set('type', session.scenario);
      url.searchParams.set('scenario', SCENARIO_MAP[session.scenario] || 'presentation');
      url.searchParams.set('goal', session.focus_goals.join(', '));
      const item = document.createElement('a');
      item.className = `agent-session-item${session.id === remoteSessionId ? ' is-current' : ''}`;
      item.href = url.toString();
      const title = document.createElement('strong');
      title.textContent = session.title;
      const focus = document.createElement('span');
      focus.textContent = session.focus_goals.join(', ') || '포커스 없음';
      item.append(title, focus);
      agentSessionList.appendChild(item);
    });
  } catch (err) {
    console.warn('[agent] sessions load failed', err);
    agentSessionList.innerHTML = '<p>세션 목록을 불러오지 못했습니다.</p>';
  }
}

agentForm?.addEventListener('submit', (event) => {
  event.preventDefault();
  const text = agentInput?.value.trim();
  if (!text) return;
  appendAgentMessage('user', text, null, { kind: 'manual_question' });
  if (agentInput) agentInput.value = '';
  // Route manual questions through the same dispatcher as the realtime triggers
  // — kind 'content' makes the LLM treat it as a one-off utterance to react to.
  // Dispatcher will inject the multimodal context so the model still sees what
  // the user has been doing nonverbally.
  agentDispatcher?.submit({
    kind: 'content',
    t: currentSessionSeconds,
    payload: { segment: text, manual: true },
  });
});

agentVoiceToggle?.addEventListener('click', () => {
  agentVoiceEnabled = !agentVoiceEnabled;
  localStorage.setItem('speakup-agent-voice', String(agentVoiceEnabled));
  if (!agentVoiceEnabled && 'speechSynthesis' in window) window.speechSynthesis.cancel();
  syncAgentVoiceToggle();
});

const VIRTUAL_HINTS = ['virtual', 'mirametrix', 'obs', 'snap', 'nvidia broadcast', 'xsplit', 'manycam'];

function isLikelyVirtual(label: string): boolean {
  const l = label.toLowerCase();
  return VIRTUAL_HINTS.some((h) => l.includes(h));
}

async function acquireStream(preferredDeviceId?: string): Promise<MediaStream> {
  if (!preferredDeviceId) {
    let scratch: MediaStream | null = null;
    try {
      scratch = await navigator.mediaDevices.getUserMedia({ video: true, audio: true });
    } catch (e) {
      throw e;
    }
    const devices = await navigator.mediaDevices.enumerateDevices();
    const cams = devices.filter((d) => d.kind === 'videoinput');
    console.log('[practice] cameras:', cams.map((c) => `${c.label}${isLikelyVirtual(c.label) ? ' [VIRTUAL]' : ''}`));
    populateCamSelect(cams);

    const currentLabel = scratch.getVideoTracks()[0]?.label ?? '';
    if (isLikelyVirtual(currentLabel)) {
      const real = cams.find((c) => c.label && !isLikelyVirtual(c.label));
      if (real) {
        console.warn(`[practice] swapping virtual cam "${currentLabel}" → "${real.label}"`);
        scratch.getTracks().forEach((t) => t.stop());
        camSelect.value = real.deviceId;
        return acquireStream(real.deviceId);
      }
      console.warn('[practice] only virtual cameras found — proceeding with virtual; preview will likely be blank');
    } else {
      camSelect.value = cams.find((c) => c.label === currentLabel)?.deviceId ?? '';
    }
    return scratch;
  }

  return navigator.mediaDevices.getUserMedia({
    video: { deviceId: { exact: preferredDeviceId }, width: 640, height: 480 },
    audio: { echoCancellation: true, noiseSuppression: true },
  });
}

function populateCamSelect(cams: MediaDeviceInfo[]): void {
  camSelect.innerHTML = '';
  for (const c of cams) {
    const opt = document.createElement('option');
    opt.value = c.deviceId;
    opt.textContent = `${c.label || c.deviceId.slice(0, 8)}${isLikelyVirtual(c.label) ? ' (가상)' : ''}`;
    camSelect.appendChild(opt);
  }
}

function setStatus(msg: string) {
  status.textContent = msg;
  console.log('[practice]', msg);
}

function formatClock(totalSeconds: number): string {
  const safe = Math.max(0, Math.floor(totalSeconds));
  const minutes = Math.floor(safe / 60);
  const seconds = safe % 60;
  return `${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
}

function setTimer(seconds: number): void {
  if (timerEl) timerEl.textContent = formatClock(seconds);
}

function setRecordBadge(label: string, state: 'idle' | 'recording' | 'done' = 'idle'): void {
  if (!recordBadge) return;
  recordBadge.dataset.state = state;
  const dot = recordBadge.querySelector('span');
  recordBadge.textContent = '';
  if (dot) recordBadge.appendChild(dot);
  recordBadge.append(label);
}

function setHudCard(
  key: keyof typeof hudCards,
  value: string,
  meterPct: number,
  tone: 'idle' | 'ok' | 'warn' | 'critical' = 'idle',
): void {
  const card = hudCards[key];
  if (!card) return;
  const valueEl = card.querySelector<HTMLElement>('[data-hud-value]');
  const meterEl = card.querySelector<HTMLElement>('.meter i');
  card.classList.remove('is-muted', 'is-ok', 'is-warn', 'is-critical');
  card.classList.add(
    tone === 'ok' ? 'is-ok' : tone === 'warn' ? 'is-warn' : tone === 'critical' ? 'is-critical' : 'is-muted',
  );
  if (valueEl) valueEl.textContent = value;
  if (meterEl) meterEl.style.width = `${Math.max(0, Math.min(100, meterPct))}%`;
}

function resetHudCards(): void {
  setHudCard('wpm', '—', 0, 'idle');
  setHudCard('filler', '—', 0, 'idle');
  setHudCard('silence', '—', 0, 'idle');
}

function setFocusAxis(key: keyof typeof focusAxes, score: number | null): void {
  const row = focusAxes[key];
  if (!row) return;
  const fill = row.querySelector<HTMLElement>('.axis-fill');
  const scoreEl = row.querySelector<HTMLElement>('.axis-score');
  if (score == null) {
    if (fill) fill.style.width = '0%';
    if (scoreEl) scoreEl.textContent = '—';
    return;
  }
  const clamped = Math.round(Math.max(0, Math.min(100, score)));
  if (fill) fill.style.width = `${clamped}%`;
  if (scoreEl) scoreEl.textContent = `${clamped}`;
}

function resetFocusAxes(): void {
  setFocusAxis('verbal', null);
  setFocusAxis('prosody', null);
  setFocusAxis('nonverbal', null);
}

function parseHudNumber(text: string): number | null {
  const match = text.match(/(\d+(?:\.\d+)?)/);
  return match ? Number(match[1]) : null;
}

function syncHudFromResponse(payload: LiveHudResponse, recording: boolean): void {
  if (!recording) return;
  const byKind = new Map(payload.signals.map((signal) => [signal.kind, signal]));
  const wpmSignal = byKind.get('wpm_very_high') ?? byKind.get('wpm_high');
  let verbalScore = 86;
  if (wpmSignal) {
    const wpm = parseHudNumber(wpmSignal.text) ?? 0;
    const tone = wpmSignal.level === 'critical' ? 'critical' : 'warn';
    if (focusEnabled('wpm')) {
      setHudCard('wpm', `${Math.round(wpm)} WPM`, Math.min(100, (wpm / 240) * 100), tone);
    }
    verbalScore -= wpmSignal.level === 'critical' ? 34 : 18;
  } else {
    if (focusEnabled('wpm')) setHudCard('wpm', '안정적', 42, 'ok');
  }

  const fillerSignal = byKind.get('filler_burst');
  if (fillerSignal) {
    const count = parseHudNumber(fillerSignal.text) ?? 0;
    const tone = fillerSignal.level === 'critical' ? 'critical' : 'warn';
    if (focusEnabled('filler')) {
      setHudCard('filler', `${Math.round(count)}회`, Math.min(100, count * 20), tone);
    }
    verbalScore -= fillerSignal.level === 'critical' ? 28 : 14;
  } else {
    if (focusEnabled('filler')) setHudCard('filler', '낮음', 18, 'ok');
  }
  if (focusEnabled('verbal')) setFocusAxis('verbal', verbalScore);
}

function scoreNonverbalFrame(frame: VisionFrame): number {
  const gaze = Math.max(0, Math.min(1, frame.gaze_fixation_ratio)) * 100;
  const posture = Math.max(0, 100 - frame.posture_sway * 1200 - Math.abs(frame.shoulder_tilt) * 80);
  const gesture = Math.max(0, 100 - frame.hand_gesture_freq * 80 - frame.hand_velocity_max * 24);
  return gaze * 0.45 + posture * 0.35 + gesture * 0.2;
}

async function bootstrap() {
  setStatus('카메라/마이크 권한 요청 중…');
  let stream = await acquireStream();
  const vTracks = stream.getVideoTracks();
  const aTracks = stream.getAudioTracks();
  const summarize = (t: MediaStreamTrack) =>
    `label="${t.label}" enabled=${t.enabled} muted=${t.muted} state=${t.readyState}`;
  console.log('[practice] video track:', vTracks.map(summarize).join(' | '));
  console.log('[practice] audio track:', aTracks.map(summarize).join(' | '));
  vTracks[0]?.addEventListener('mute', () => console.warn('[practice] video track went MUTED — camera stopped delivering frames'));
  vTracks[0]?.addEventListener('unmute', () => console.log('[practice] video track UNMUTED — frames flowing again'));
  if (vTracks.length === 0) {
    throw new Error('카메라 트랙이 0개 — 권한은 통과했지만 비디오 장치가 활성화되지 않았습니다.');
  }

  video.srcObject = stream;
  await new Promise<void>((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error('비디오 메타데이터 타임아웃(10s)')), 10000);
    video.onloadedmetadata = async () => {
      clearTimeout(timeout);
      try {
        await video.play();
        console.log('[practice] video playing', video.videoWidth, 'x', video.videoHeight);
        resolve();
      } catch (e) {
        reject(e);
      }
    };
    video.onerror = () => {
      clearTimeout(timeout);
      reject(new Error('video element error'));
    };
  });

  setStatus('실시간 코칭 화면 준비 중…');
  const style = new URLSearchParams(location.search).get('style');
  const vrmUrl = resolveAvatarUrl(style);
  const stage = await createAvatarStage(canvas, vrmUrl);

  setStatus('MediaPipe 모델 로딩 중… (CDN 첫 다운로드 시 ~5-10s)');
  const landmarkers = await createLandmarkers();

  setStatus('준비됨 — 녹화를 시작하면 실시간 코칭과 세션 영상 저장이 함께 진행됩니다.');
  setAgentState('질문 대기');
  btnStart.disabled = false;
  btnStart.textContent = '녹화 시작';
  setTimer(0);
  setRecordBadge('대기 중');
  resetHudCards();
  resetFocusAxes();

  const recorder = new AvatarRecorder();
  // Second recorder: same retarget pipeline already feeds the avatar canvas
  // every frame. We tap its captureStream and combine it with the user's mic
  // track so the saved blob plays back with audio in sync.
  const avatarRecorder = new AvatarRecorder();
  const aggregator = createAggregatorClient();
  const hudClient = createHudClient();
  let recording = false;
  let recordingStartTSec = 0;
  let lastSignalSendT = 0;
  let lastProsodySendT = 0;
  let silenceDetector: SilenceDetector | null = null;
  let sessionId = remoteSessionId;
  const scenario = params.get('scenario') || SCENARIO_MAP[typeName] || 'presentation';

  await hudClient.connect((payload) => {
    syncHudFromResponse(payload, recording);
  });

  btnStart.addEventListener('click', async () => {
    btnStart.disabled = true;
    btnStart.textContent = '녹화 중';
    setAgentState('코칭 중');
    setTimer(0);
    setRecordBadge('녹화 중', 'recording');
    setStatus('세션 시작 중…');
    sessionId = remoteSessionId || `sess_${Date.now()}`;
    const focusGoals = resolveFocusGoals();
    resetSignalState();
    liveCoachingEvents.length = 0;
    paused = false;
    pausedAccumSec = 0;
    pausedAtTSec = 0;
    if (btnPause) {
      btnPause.disabled = false;
      btnPause.textContent = '일시정지';
    }

    // Reset every agent-pipeline piece so a re-record starts clean.
    silenceTrigger.reset();
    gazeTrigger.reset();
    smileAbsenceTrigger.reset();
    smileAbsenceTrigger.start(0);
    motionAbsenceTrigger.reset();
    motionAbsenceTrigger.start(0);
    fillerTrigger.reset();
    speechRateTrigger.reset();
    contentTrigger.reset();
    visionContextBuffer.reset();
    transcriptBuffer.reset();
    fillerTotal = 0;
    lastSilenceSeconds = 0;
    agentDispatcher = new AgentDispatcher({
      scenario,
      situation: situationName || projectName,
      focusGoals,
      getContext: buildMultimodalContext,
      onFeedback: onAgentFeedback,
    });

    await aggregator.start(sessionId, scenario, focusGoals);
    recorder.start(stream);
    // Avatar recording uses the canvas's captureStream as video + the user's
    // mic track for audio. canvas.captureStream(30) gives us the VRM render
    // at 30fps; we merge audio so the two saved blobs are independently
    // playable.
    const avatarStream = canvas.captureStream(30);
    stream.getAudioTracks().forEach((track) => avatarStream.addTrack(track));
    avatarRecorder.start(avatarStream);
    silenceDetector = new SilenceDetector(stream);
    silenceDetector.start();
    recording = true;
    lastSpokenTranscriptKey = '';
    recordingStartTSec = performance.now() / 1000;
    lastSignalSendT = 0;
    lastProsodySendT = 0;
    btnStop.disabled = false;
    resetHudCards();
    resetFocusAxes();
    startSpeechAgentListening(stream);
    setStatus(`녹화 중… (세션 ${sessionId})`);
  });

  btnPause?.addEventListener('click', () => {
    if (!recording) return;
    if (!paused) {
      // Pause: freeze the recorder + start the paused clock. Trigger evaluators
      // see currentSessionSeconds stop advancing, so cooldowns don't drain
      // during the break.
      recorder.pause();
      avatarRecorder.pause();
      pausedAtTSec = performance.now() / 1000;
      paused = true;
      btnPause.textContent = '재개';
      setRecordBadge('일시정지', 'idle');
      setStatus('일시정지됨 — "재개"를 누르면 이어서 녹화합니다.');
    } else {
      // Resume: subtract the just-elapsed pause span from session time so the
      // clock continues from where it stopped.
      pausedAccumSec += performance.now() / 1000 - pausedAtTSec;
      paused = false;
      recorder.resume();
      avatarRecorder.resume();
      btnPause.textContent = '일시정지';
      setRecordBadge('녹화 중', 'recording');
      setStatus(`녹화 중… (세션 ${sessionId})`);
    }
  });

  btnStop.addEventListener('click', async () => {
    btnStop.disabled = true;
    if (btnPause) btnPause.disabled = true;
    paused = false;
    recording = false;
    stopSpeechAgentListening();
    if (silenceDetector) {
      silenceDetector.stop();
      silenceDetector = null;
    }
    // Stop both recorders in parallel; pause + stop are synchronous events
    // on MediaRecorder so awaiting them together is safe.
    const [rec, avatarRec] = await Promise.all([recorder.stop(), avatarRecorder.stop()]);
    if (recorded) recorded.src = rec.url;
    setTimer(rec.durationMs / 1000);
    setRecordBadge('녹화 완료', 'done');
    setAgentState('리포트 생성');

    try {
      setStatus('코칭 화면으로 이동 중…');
      const mediaId = await savePendingMedia(rec.blob, `${sessionId}.webm`, rec.blob.type || 'video/webm');
      // Avatar version is optional — it's only there if captureStream + retarget
      // produced any frames. If the user has no VRM (fallback face only) the
      // blob is still present and just shows the fallback canvas.
      const avatarFilename = `${sessionId}.avatar.webm`;
      const avatarMimeType = avatarRec.blob.type || 'video/webm';
      const avatarMediaId = avatarRec.blob.size > 0
        ? await savePendingMedia(avatarRec.blob, avatarFilename, avatarMimeType)
        : undefined;
      setPendingAnalysis({
        sessionId,
        projectId: projectRef?.id,
        project: projectName,
        goal: goals,
        type: typeName,
        situation: situationName,
        source: 'live',
        createdAt: new Date().toISOString(),
        mediaId,
        filename: `${sessionId}.webm`,
        mimeType: rec.blob.type || 'video/webm',
        avatarMediaId,
        avatarFilename: avatarMediaId ? avatarFilename : undefined,
        avatarMimeType: avatarMediaId ? avatarMimeType : undefined,
        scenario,
        liveEvents: liveCoachingEvents,
      });
      const next = new URL('loading.html', location.href);
      next.searchParams.set('session', sessionId);
      next.searchParams.set('source', 'live');
      if (projectRef?.id) next.searchParams.set('projectId', projectRef.id);
      next.searchParams.set('project', projectName);
      next.searchParams.set('goal', goals.join(', '));
      next.searchParams.set('type', typeName);
      if (situationName) next.searchParams.set('situation', situationName);
      location.href = next.toString();
    } catch (e) {
      console.error('[practice] failed to hand off live analysis', e);
      btnStart.disabled = false;
      btnStart.textContent = '다시 녹화';
      btnStop.disabled = false;
      setStatus('분석 준비에 실패했어요. 다시 시도해주세요.');
    }
  });

  let lastT = performance.now();
  let frames = 0;
  let lastFpsT = performance.now();
  let fps = 0;
  let faceCount = 0;
  let poseCount = 0;
  let handCount = 0;
  let detectError: string | null = null;
  let lastTs = -1;
  let pixelMean = -1;

  const probe = document.createElement('canvas');
  probe.width = 64;
  probe.height = 48;
  const probeCtx = probe.getContext('2d', { willReadFrequently: true })!;
  let probeFrame = 0;

  const tick = () => {
    const now = performance.now();
    const delta = (now - lastT) / 1000;
    lastT = now;
    frames++;
    if (now - lastFpsT >= 1000) {
      fps = (frames * 1000) / (now - lastFpsT);
      frames = 0;
      lastFpsT = now;
    }

    const ready = video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA;
    const vw = video.videoWidth;
    const vh = video.videoHeight;

    if (ready && vw > 0 && vh > 0) {
      if (probeFrame++ % 10 === 0) {
        probeCtx.drawImage(video, 0, 0, probe.width, probe.height);
        const data = probeCtx.getImageData(0, 0, probe.width, probe.height).data;
        let sum = 0;
        for (let i = 0; i < data.length; i += 4) {
          sum += data[i] + data[i + 1] + data[i + 2];
        }
        pixelMean = sum / (data.length / 4) / 3;
      }

      const ts = Math.max(Math.floor(now), lastTs + 1);
      lastTs = ts;
      try {
        const { face, pose, hand } = detect(landmarkers, video, ts);
        faceCount = face.faceLandmarks?.length ?? 0;
        poseCount = pose.landmarks?.length ?? 0;
        handCount = hand.landmarks?.length ?? 0;
        if (stage.vrm) {
          applyFaceToVRM(stage.vrm, face);
          applyPoseToVRM(stage.vrm, pose);
          applyHandsToVRM(stage.vrm, hand);
        } else if (stage.fallback) {
          applyFaceToFallback(stage.fallback, face);
        }

        if (recording) {
          // Paused sessions freeze the clock + skip every signal forward + every
          // trigger evaluation. The recorder itself is paused at the MediaRecorder
          // layer, and STT keeps running so the next resumption picks up cleanly.
          if (paused) {
            stage.render(delta);
            requestAnimationFrame(tick);
            return;
          }
          const sessionT = now / 1000 - recordingStartTSec - pausedAccumSec;
          currentSessionSeconds = sessionT;
          setTimer(sessionT);
          if (sessionT - lastSignalSendT >= 0.2) {
            const frame = computeVisionFrame(sessionT, face, pose, hand);
            aggregator.sendVision(frame);
            // Feed the context buffer + run the vision-side trigger evaluators.
            visionContextBuffer.add(frame);
            const nonverbalScore = scoreNonverbalFrame(frame);
            if (focusEnabled('nonverbal')) setFocusAxis('nonverbal', nonverbalScore);
            if (agentDispatcher) {
              const gazeEvt = gazeTrigger.evaluate(frame, sessionT);
              if (gazeEvt) agentDispatcher.submit(gazeEvt);
              const smileEvt = smileAbsenceTrigger.evaluate(frame, sessionT);
              if (smileEvt) agentDispatcher.submit(smileEvt);
              const motionEvt = motionAbsenceTrigger.evaluate(frame, sessionT);
              if (motionEvt) agentDispatcher.submit(motionEvt);
            }
            lastSignalSendT = sessionT;
          }
          if (silenceDetector && sessionT - lastProsodySendT >= 1.0) {
            const { silenceSeconds, rmsMean } = silenceDetector.snapshot();
            const prosodyFrame: ProsodyFrame = {
              t_start: lastProsodySendT,
              t_end: sessionT,
              silence_seconds: silenceSeconds,
              rms_mean: rmsMean,
            };
            aggregator.sendProsody(prosodyFrame);
            lastSilenceSeconds = silenceSeconds;
            const tone = silenceSeconds >= 4 ? 'critical' : silenceSeconds >= 2 ? 'warn' : 'idle';
            if (focusEnabled('silence')) {
              setHudCard(
                'silence',
                silenceSeconds > 0.1 ? `${silenceSeconds.toFixed(1)}초` : '짧음',
                Math.min(100, (silenceSeconds / 4) * 100),
                tone,
              );
            }
            if (focusEnabled('prosody')) setFocusAxis('prosody', silenceSeconds >= 4 ? 48 : silenceSeconds >= 2 ? 72 : 86);
            if (agentDispatcher) {
              const silEvt = silenceTrigger.evaluate(silenceSeconds, sessionT);
              if (silEvt) agentDispatcher.submit(silEvt);
            }
            silenceDetector.resetWindow();
            lastProsodySendT = sessionT;
          }
        }
      } catch (e) {
        detectError = e instanceof Error ? e.message : String(e);
      }
    }

    const debugLine =
      `fps ${fps.toFixed(0)}  video ${vw}x${vh} ready=${video.readyState} pix=${pixelMean.toFixed(0)}\n` +
      `face=${faceCount} pose=${poseCount} hands=${handCount} vrm=${stage.vrm ? 'yes' : 'no(fallback)'}` +
      (detectError ? `\nERR ${detectError}` : '');
    debug.textContent = debugLine;
    if (now - lastFpsT < 50) {
      console.log('[debug]', debugLine.replace(/\n/g, ' | '));
    }

    stage.render(delta);
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);

  window.addEventListener('beforeunload', (e) => {
    if (recording) {
      e.preventDefault();
      e.returnValue = '';
    }
  });

  window.addEventListener('pagehide', () => {
    hudClient.close();
    aggregator.close();
  });
}

applyFocusVisibility();
syncAgentVoiceToggle();
void hydrateAgentPanel();
void hydrateSessionList();

bootstrap().catch((err) => {
  console.error(err);
  const name = err instanceof Error ? err.name : '';
  if (name === 'NotAllowedError' || name === 'SecurityError') {
    setStatus(
      '카메라/마이크 권한이 거부됐어요. 주소창 자물쇠 → 사이트 설정에서 허용 후 새로고침하세요.',
    );
  } else if (name === 'NotFoundError' || name === 'OverconstrainedError') {
    setStatus('카메라 또는 마이크 장치를 찾을 수 없어요. 연결 상태를 확인해주세요.');
  } else if (name === 'NotReadableError') {
    setStatus('다른 앱이 카메라를 사용 중인 것 같아요. 해당 앱을 닫고 새로고침하세요.');
  } else {
    setStatus(`초기화 실패: ${err instanceof Error ? err.message : String(err)}`);
  }
});
