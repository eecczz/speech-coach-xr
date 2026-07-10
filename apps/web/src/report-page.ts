import {
  average,
  deriveNextTarget,
  findAxis,
  findPreviousSession,
  formatTimeShort,
  getAxisScore,
  getDisplayAxisScores,
  getTopMoments,
} from './report-utils';
import type { AnnotatedMoment } from './review/types';
import type { CompletedSession, LiveCoachingEvent } from './session-store';
import { getCompletedSession, getCompletedSessions, loadPendingMedia } from './session-store';

declare const Chart: any;

const themeToggle = document.querySelector('[data-theme-toggle]') as HTMLButtonElement | null;
const retryLink = document.getElementById('retry-link') as HTMLAnchorElement | null;
const reportHeading = document.getElementById('report-heading') as HTMLElement | null;
const reportSubtitle = document.getElementById('report-subtitle') as HTMLElement | null;
const focusLine = document.getElementById('report-focus-line') as HTMLElement | null;
const currentScoreEl = document.getElementById('current-score') as HTMLElement | null;
const targetScoreEl = document.getElementById('target-score') as HTMLElement | null;
const deltaScoreEl = document.getElementById('delta-score') as HTMLElement | null;
const summaryEl = document.getElementById('ai-summary') as HTMLElement | null;
const priorityCard = document.getElementById('priority-card') as HTMLElement | null;
const strengthCard = document.getElementById('strength-card') as HTMLElement | null;
const nextActionCard = document.getElementById('next-action-card') as HTMLElement | null;
const focusAnalysisList = document.getElementById('focus-analysis-list') as HTMLElement | null;
const agentHistoryList = document.getElementById('agent-history-list') as HTMLElement | null;
const expertMetricsGrid = document.getElementById('expert-metrics-grid') as HTMLElement | null;
const nextGoalsList = document.getElementById('next-goals-list') as HTMLOListElement | null;
const downloadVideoButton = document.getElementById('download-video-link') as HTMLButtonElement | null;
const downloadMp4Button = document.getElementById('download-mp4-link') as HTMLButtonElement | null;
const modalVideo = document.getElementById('modal-video') as HTMLVideoElement | null;
const modalVideoEmpty = document.getElementById('modal-video-empty') as HTMLElement | null;
const modalMomentMeta = document.getElementById('modal-moment-meta') as HTMLElement | null;
const modalFeedback = document.getElementById('modal-feedback') as HTMLElement | null;
const modalBack = document.getElementById('modal-back') as HTMLButtonElement | null;
const modalPlay = document.getElementById('modal-play') as HTMLButtonElement | null;
const modalForward = document.getElementById('modal-forward') as HTMLButtonElement | null;

let modalVideoUrl: string | null = null;
let reportMediaBlob: Blob | null = null;
let reportMediaFilename = 'speakup-video.webm';
let reportMediaMimeType = 'video/webm';
let activeMomentTime = 0;
// Toggle state for the "아바타로 보기" button. We cache the active session so
// the toggle handler can re-attach without re-running the whole render flow.
let avatarMode = false;
let activeSession: CompletedSession | null = null;
const avatarToggleLink = document.getElementById('avatar-toggle-link') as HTMLButtonElement | null;

function syncThemeToggle() {
  if (!themeToggle) return;
  const theme = document.documentElement.dataset.theme || 'light';
  themeToggle.textContent = theme === 'dark' ? '☀️' : '🌙';
  themeToggle.setAttribute('aria-label', theme === 'dark' ? '라이트 모드로 전환' : '다크 모드로 전환');
}

themeToggle?.addEventListener('click', () => {
  const nextTheme = (document.documentElement.dataset.theme || 'light') === 'dark' ? 'light' : 'dark';
  document.documentElement.dataset.theme = nextTheme;
  localStorage.setItem('speakup-theme', nextTheme);
  syncThemeToggle();
});

avatarToggleLink?.addEventListener('click', async () => {
  if (!activeSession) return;
  avatarMode = !avatarMode;
  avatarToggleLink.setAttribute('aria-pressed', String(avatarMode));
  avatarToggleLink.textContent = avatarMode ? '원본 영상 보기' : '아바타로 보기';
  await attachModalVideo(activeSession, avatarMode);
  // Keep the timestamp the user was watching when they flipped the toggle.
  // The two recordings start together so the timeline maps 1:1.
  seekModalVideo(activeMomentTime);
});

document.getElementById('print-link')?.addEventListener('click', () => {
  window.print();
});

downloadVideoButton?.addEventListener('click', () => {
  downloadReportVideo();
});

downloadMp4Button?.addEventListener('click', () => {
  void downloadReportVideoAsMp4();
});

document.querySelector('[data-close-modal]')?.addEventListener('click', () => {
  (document.getElementById('video-modal') as HTMLDialogElement | null)?.close();
});

modalBack?.addEventListener('click', () => seekModalVideo(activeMomentTime - 10));
modalForward?.addEventListener('click', () => seekModalVideo(activeMomentTime + 10));
modalPlay?.addEventListener('click', () => {
  seekModalVideo(activeMomentTime);
  void modalVideo?.play().catch(() => {});
});

function seekModalVideo(time: number) {
  if (!modalVideo || !modalVideo.src) return;
  const duration = Number.isFinite(modalVideo.duration) ? modalVideo.duration : Number.POSITIVE_INFINITY;
  modalVideo.currentTime = Math.max(0, Math.min(time, duration));
}

function sanitizeDownloadFilename(filename: string): string {
  return filename
    .trim()
    .replace(/[\\/:*?"<>|]+/g, '_')
    .replace(/\s+/g, ' ')
    || 'speakup-video.webm';
}

function extensionFromMime(mimeType: string): string {
  if (mimeType.includes('mp4')) return '.mp4';
  if (mimeType.includes('quicktime')) return '.mov';
  if (mimeType.includes('webm')) return '.webm';
  return '.webm';
}

function getMediaExtension(filename: string, mimeType: string): string {
  return filename.match(/\.[A-Za-z0-9]{2,5}$/)?.[0] ?? extensionFromMime(mimeType);
}

function formatDownloadTimestamp(createdAt: string): string {
  const created = new Date(createdAt);
  const date = Number.isNaN(created.getTime()) ? new Date() : created;
  return date.toISOString().replace(/[:.]/g, '-').slice(0, 19);
}

function getReportVideoFilename(session: CompletedSession, mediaFilename: string, mimeType: string): string {
  if (session.source === 'upload' && mediaFilename) {
    return sanitizeDownloadFilename(mediaFilename);
  }
  const ext = getMediaExtension(mediaFilename, mimeType);
  const timestamp = formatDownloadTimestamp(session.createdAt);
  return sanitizeDownloadFilename(`SpeakUp_${session.project}_${timestamp}${ext}`);
}

function getMp4Filename(filename: string): string {
  const withoutExtension = filename.replace(/\.[A-Za-z0-9]{2,5}$/, '');
  return sanitizeDownloadFilename(`${withoutExtension || 'speakup-video'}.mp4`);
}

function setDownloadVideoAvailable(available: boolean) {
  if (downloadVideoButton) {
    downloadVideoButton.disabled = !available;
    downloadVideoButton.textContent = available ? '영상 저장' : '저장할 영상 없음';
    downloadVideoButton.title = available
      ? '분석에 사용한 원본 영상을 별도로 저장합니다. PDF에는 영상이 포함되지 않습니다.'
      : '저장된 영상이 없어 다운로드할 수 없습니다.';
  }
  if (downloadMp4Button) {
    downloadMp4Button.disabled = !available;
    downloadMp4Button.textContent = available ? 'MP4로 저장' : 'MP4 저장 불가';
    downloadMp4Button.title = available
      ? '저장된 영상을 MP4로 변환해서 내려받습니다.'
      : '저장된 영상이 없어 MP4로 변환할 수 없습니다.';
  }
}

function clearReportMedia() {
  reportMediaBlob = null;
  reportMediaFilename = 'speakup-video.webm';
  reportMediaMimeType = 'video/webm';
  setDownloadVideoAvailable(false);
}

function triggerBlobDownload(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 0);
}

function downloadReportVideo() {
  if (!reportMediaBlob) return;
  triggerBlobDownload(reportMediaBlob, reportMediaFilename);
}

function isMp4Source(filename: string, mimeType: string): boolean {
  return mimeType.includes('mp4') || filename.toLowerCase().endsWith('.mp4');
}

async function downloadReportVideoAsMp4() {
  if (!reportMediaBlob || !downloadMp4Button) return;
  const previousText = downloadMp4Button.textContent || 'MP4로 저장';
  downloadMp4Button.disabled = true;
  downloadMp4Button.textContent = 'MP4 변환 중...';
  try {
    if (isMp4Source(reportMediaFilename, reportMediaMimeType)) {
      triggerBlobDownload(reportMediaBlob, getMp4Filename(reportMediaFilename));
      return;
    }
    const form = new FormData();
    const file = new File([reportMediaBlob], reportMediaFilename, {
      type: reportMediaMimeType || reportMediaBlob.type || 'video/webm',
    });
    form.append('video', file, file.name);
    const response = await fetch('/convert/mp4', {
      method: 'POST',
      body: form,
    });
    if (!response.ok) {
      const detail = await response.json().catch(() => null);
      throw new Error(detail?.error || `MP4 변환 실패 (${response.status})`);
    }
    const mp4Blob = await response.blob();
    triggerBlobDownload(mp4Blob, getMp4Filename(reportMediaFilename));
  } catch (error) {
    console.error('[report] mp4 download failed', error);
    alert('MP4 변환에 실패했습니다. 원본 영상 저장을 먼저 사용해 주세요.');
  } finally {
    downloadMp4Button.disabled = false;
    downloadMp4Button.textContent = previousText;
  }
}

function formatMomentRange(moment: Pick<AnnotatedMoment, 't' | 'duration_s'>): string {
  const start = formatTimeShort(moment.t);
  const duration = typeof moment.duration_s === 'number' && Number.isFinite(moment.duration_s)
    ? Math.max(0, moment.duration_s)
    : 0;
  if (duration <= 0) return start;
  return `${start}-${formatTimeShort(moment.t + duration)}`;
}

function openMoment(moment: AnnotatedMoment): void {
  activeMomentTime = moment.t;
  seekModalVideo(activeMomentTime);
  if (modalMomentMeta) {
    modalMomentMeta.textContent = `${formatMomentRange(moment)} · ${moment.title}`;
  }
  if (modalFeedback) modalFeedback.textContent = moment.coach_comment || '이 구간의 코칭 코멘트가 여기에 표시됩니다.';
  (document.getElementById('video-modal') as HTMLDialogElement | null)?.showModal();
}

function setAxisCard(key: 'verbal' | 'prosody' | 'nonverbal', score: number | null, note: string) {
  const card = document.querySelector<HTMLElement>(`[data-axis-card="${key}"]`);
  if (!card) return;
  const valueEl = card.querySelector<HTMLElement>('[data-axis-value]');
  const noteEl = card.querySelector<HTMLElement>('[data-axis-note]');
  const meter = card.querySelector<HTMLElement>('.meter i');
  if (valueEl) valueEl.textContent = typeof score === 'number' ? `${Math.round(score)}` : '—';
  if (noteEl) noteEl.textContent = note;
  if (meter) meter.style.width = `${Math.max(0, Math.min(100, score ?? 0))}%`;
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}

function inferPracticeContext(session: CompletedSession): {
  label: string;
  cue: string;
  focusTone: string;
} {
  const raw = `${session.project} ${session.situation ?? ''} ${session.type}`.toLowerCase();
  if (raw.includes('면접') || raw.includes('interview')) {
    return {
      label: '면접',
      cue: '답변 신뢰감과 자연스러운 눈맞춤이 중요합니다.',
      focusTone: '면접 상황에서는 안정감과 과하지 않은 반응이 핵심입니다.',
    };
  }
  if (raw.includes('협상') || raw.includes('negotiation')) {
    return {
      label: '협상',
      cue: '상대가 납득할 수 있는 근거 제시와 차분한 톤이 중요합니다.',
      focusTone: '협상 상황에서는 강한 전달보다 균형 잡힌 설득력이 중요합니다.',
    };
  }
  if (raw.includes('전화') || raw.includes('온라인') || raw.includes('online') || raw.includes('phone')) {
    return {
      label: '온라인/전화',
      cue: '화면 밖에서도 명료하게 들리는 속도와 쉼이 중요합니다.',
      focusTone: '비대면 상황에서는 말의 리듬과 음성 명료도가 더 크게 작용합니다.',
    };
  }
  if (raw.includes('일상') || raw.includes('daily')) {
    return {
      label: '일상 대화',
      cue: '편안한 흐름과 상대가 끼어들 수 있는 여백이 중요합니다.',
      focusTone: '일상 대화에서는 자연스러운 반응과 부담 없는 속도가 중요합니다.',
    };
  }
  if (raw.includes('논문')) {
    return {
      label: '논문 발표',
      cue: '전문 용어를 또렷하게 전달하고 핵심 근거를 분명히 연결해야 합니다.',
      focusTone: '논문 발표에서는 정확한 용어와 논리 연결이 평가 포인트입니다.',
    };
  }
  return {
    label: '발표',
    cue: '청중이 따라오기 쉬운 속도, 시선, 핵심 메시지 전달이 중요합니다.',
    focusTone: '발표 상황에서는 청중과의 연결감과 메시지 전달력이 중요합니다.',
  };
}

const focusAxisMap: Record<string, string> = {
  '말 속도': 'delivery',
  '필러 표현': 'delivery',
  '침묵 구간': 'delivery',
  '목소리 톤': 'delivery',
  '시선 처리': 'gaze',
  자세: 'posture',
  표정: 'expression',
  제스처: 'gesture',
  '논리 흐름': 'logic',
  자신감: 'confidence',
};

const axisLabelMap: Record<string, string> = {
  delivery: '말 전달',
  gaze: '시선 처리',
  posture: '자세',
  expression: '표정',
  gesture: '제스처',
  logic: '논리 흐름',
  confidence: '자신감',
};

function getSelectedFocus(session: CompletedSession): string[] {
  const fallback = ['말 속도', '시선 처리', '필러 표현', '침묵 구간', '자세'];
  const goals = session.goal.length ? session.goal : fallback;
  return [...new Set(goals.map((goal) => goal.trim()).filter(Boolean))];
}

function getFocusAxis(focus: string): string {
  return focusAxisMap[focus] ?? 'delivery';
}

function getFocusScore(session: CompletedSession, focus: string): number | null {
  const axis = getFocusAxis(focus);
  if (axis === 'confidence') {
    return average([
      getAxisScore(session.report, 'gaze'),
      getAxisScore(session.report, 'posture'),
      getAxisScore(session.report, 'expression'),
    ]);
  }
  return getAxisScore(session.report, axis);
}

function matchesFocus(moment: AnnotatedMoment, focus: string): boolean {
  const axis = getFocusAxis(focus);
  const haystack = `${moment.axis} ${moment.title} ${moment.coach_comment ?? ''}`;
  if (axis === moment.axis) return true;
  if (focus.includes('필러')) return /필러|반복|음|어|그러니까/.test(haystack);
  if (focus.includes('침묵')) return /침묵|무음|쉼/.test(haystack);
  if (focus.includes('말 속도')) return /속도|wpm|느림|빠름/.test(haystack);
  return haystack.includes(focus);
}

function pickFocusMoment(session: CompletedSession, focus: string): AnnotatedMoment | null {
  return [...session.report.annotated_moments]
    .filter((moment) => matchesFocus(moment, focus))
    .sort((a, b) => a.impact - b.impact)[0] ?? null;
}

function focusContextComment(session: CompletedSession, focus: string, score: number | null): string {
  const context = inferPracticeContext(session);
  const level = typeof score === 'number' && score >= 80 ? '강점으로 유지할 수 있습니다' : '다음 연습에서 우선 확인할 필요가 있습니다';
  if (focus === '시선 처리' && context.label === '면접') {
    return `면접에서는 시선을 오래 고정하는 것보다 질문자를 자연스럽게 바라보는 균형이 중요합니다. 현재 ${focus}는 ${level}.`;
  }
  if (focus === '시선 처리' && context.label.includes('발표')) {
    return `발표에서는 청중과 연결되는 느낌을 주는 정면 응시가 중요합니다. 현재 ${focus}는 ${level}.`;
  }
  if (focus === '말 속도') {
    return `${context.label} 상황에서는 듣는 사람이 따라올 수 있는 리듬이 중요합니다. 현재 ${focus}는 ${level}.`;
  }
  if (focus === '논리 흐름') {
    return `${context.label} 상황에서는 앞 문장과 다음 문장의 연결이 설득력을 만듭니다. 현재 ${focus}는 ${level}.`;
  }
  return `${context.focusTone} 현재 ${focus}는 ${level}.`;
}

function setCoachingCard(
  card: HTMLElement | null,
  label: string,
  title: string,
  body: string,
) {
  if (!card) return;
  card.innerHTML = `
    <span>${escapeHtml(label)}</span>
    <strong>${escapeHtml(title)}</strong>
    <p>${escapeHtml(body)}</p>
  `;
}

function renderCoachingSummary(session: CompletedSession) {
  const context = inferPracticeContext(session);
  const priority = session.report.top_priorities[0];
  const strength = session.report.strengths[0];
  const nextDrill = session.report.training_prescriptions[0];
  const nextText = nextDrill?.steps[0] ?? session.report.improvements[0]?.suggestion ?? '다음 연습에서는 가장 낮은 포커스부터 30초 단위로 다시 확인하세요.';

  setCoachingCard(
    priorityCard,
    '먼저 고칠 점',
    priority?.text ?? '우선순위 분석 대기',
    priority?.suggestion ?? `${context.cue} 주의 구간 그래프에서 가장 크게 점수가 떨어진 지점을 먼저 확인하세요.`,
  );
  setCoachingCard(
    strengthCard,
    '잘한 점',
    strength?.text ?? '유지할 강점 분석 대기',
    strength?.suggestion ?? `${context.label} 상황에서 이미 안정적인 요소는 다음 세션에서도 유지하는 것이 좋습니다.`,
  );
  setCoachingCard(
    nextActionCard,
    '다음 연습 목표',
    nextDrill?.title ?? '다음 행동',
    nextText,
  );
}

function renderFocusAnalysis(session: CompletedSession) {
  if (!focusAnalysisList) return;
  const rows = getSelectedFocus(session).map((focus) => {
    const axis = getFocusAxis(focus);
    const axisInfo = findAxis(session.report, axis === 'confidence' ? 'posture' : axis);
    const score = getFocusScore(session, focus);
    const moment = pickFocusMoment(session, focus);
    const timeText = moment ? formatMomentRange(moment) : '전체';
    const contextNote = focusContextComment(session, focus, score);
    const metricNote = axisInfo?.note || moment?.coach_comment || '';
    const body = metricNote ? `${contextNote} ${metricNote}` : contextNote;
    return `
      <article class="focus-analysis-item">
        <span>${escapeHtml(axisLabelMap[axis] ?? focus)}</span>
        <strong>${escapeHtml(focus)}</strong>
        <div class="focus-analysis-score">${score === null ? '—' : `${Math.round(score)}점`}</div>
        <p>${escapeHtml(body)}</p>
        <em>${escapeHtml(timeText)}</em>
      </article>
    `;
  });
  focusAnalysisList.innerHTML = rows.length
    ? rows.join('')
    : '<div class="empty-copy">선택한 집중 포커스가 없어 기본 평가만 표시됩니다.</div>';
}

function renderAgentHistory(session: CompletedSession) {
  if (!agentHistoryList) return;
  const events = [...(session.liveEvents ?? [])]
    .sort((a, b) => a.t - b.t)
    .slice(0, 8);
  if (!events.length) {
    agentHistoryList.innerHTML = '<div class="empty-copy">이번 세션에 저장된 실시간 agent 코칭 기록이 없습니다.</div>';
    return;
  }
  agentHistoryList.innerHTML = events.map((event: LiveCoachingEvent) => `
    <article class="agent-history-item">
      <span>${escapeHtml(event.axis || 'agent')}</span>
      <strong>${escapeHtml(event.title)}</strong>
      <div class="agent-history-time">${escapeHtml(formatTimeShort(event.t))}</div>
      <p>${escapeHtml(event.message)}</p>
    </article>
  `).join('');
}

function renderExpertMetrics(session: CompletedSession) {
  if (!expertMetricsGrid) return;
  const report = session.report;
  const metrics = [
    { label: '말 속도/WPM', axis: 'delivery', fallback: '평균 말 속도와 필러, 긴 침묵을 함께 계산한 전달 지표입니다.' },
    { label: '정면 응시 비율', axis: 'gaze', fallback: '카메라/정면을 바라본 비율을 기반으로 계산합니다.' },
    { label: '필러 수', axis: 'delivery', fallback: '음, 어, 그러니까 등 반복 표현을 STT 구간에서 확인합니다.' },
    { label: '침묵 시간', axis: 'delivery', fallback: '무음 또는 긴 쉼 구간을 누적해 확인합니다.' },
    { label: '자세 흔들림', axis: 'posture', fallback: '어깨, 머리 기울임, 신체 안정성을 기반으로 계산합니다.' },
    { label: '표정 변화량', axis: 'expression', fallback: '표정 다양성과 변화 흐름을 기반으로 계산합니다.' },
    { label: '제스처 비율', axis: 'gesture', fallback: '손동작과 움직임 구간을 기반으로 계산합니다.' },
    { label: '논리 흐름', axis: 'logic', fallback: '전사 텍스트 기반 구조 평가입니다.' },
  ];
  expertMetricsGrid.innerHTML = metrics.map((metric) => {
    const axis = findAxis(report, metric.axis);
    const score = axis?.available ? `${Math.round(axis.score)}점` : '—';
    const note = axis?.note || metric.fallback;
    return `
      <article class="expert-metric-item">
        <span>${escapeHtml(metric.label)}</span>
        <strong>${escapeHtml(score)}</strong>
        <p>${escapeHtml(note)}</p>
      </article>
    `;
  }).join('');
}

async function attachModalVideo(
  session: ReturnType<typeof getCompletedSession>,
  useAvatar = false,
) {
  if (!session) {
    clearReportMedia();
    setModalVideoAvailable(false);
    return;
  }
  // Pick which recording to load. Toggle defaults to the raw user video; the
  // avatar version is only attempted when explicitly requested AND the
  // session actually has one stored.
  const mediaId = useAvatar ? session.avatarMediaId : session.mediaId;
  const fallbackFilename = useAvatar ? session.avatarFilename : session.filename;
  const fallbackMimeType = useAvatar ? session.avatarMimeType : session.mimeType;
  if (!modalVideo || !modalVideoEmpty || !mediaId) {
    clearReportMedia();
    setModalVideoAvailable(false);
    return;
  }
  const media = await loadPendingMedia(mediaId);
  if (!media) {
    clearReportMedia();
    setModalVideoAvailable(false);
    return;
  }
  reportMediaBlob = media.blob;
  reportMediaFilename = getReportVideoFilename(
    session,
    media.filename || fallbackFilename || '',
    media.mimeType || fallbackMimeType || media.blob.type || 'video/webm',
  );
  reportMediaMimeType = media.mimeType || fallbackMimeType || media.blob.type || 'video/webm';
  setDownloadVideoAvailable(true);
  if (modalVideoUrl) URL.revokeObjectURL(modalVideoUrl);
  modalVideoUrl = URL.createObjectURL(media.blob);
  modalVideo.src = modalVideoUrl;
  modalVideo.load();
  setModalVideoAvailable(true);
}

function setModalVideoAvailable(available: boolean) {
  if (modalVideo) modalVideo.hidden = !available;
  if (modalVideoEmpty) modalVideoEmpty.hidden = available;
  if (modalBack) modalBack.disabled = !available;
  if (modalPlay) modalPlay.disabled = !available;
  if (modalForward) modalForward.disabled = !available;
}

function renderNextGoals(session: ReturnType<typeof getCompletedSession>) {
  if (!nextGoalsList || !session) return;
  const drills = session.report.training_prescriptions.slice(0, 3);
  const improvements = session.report.improvements.slice(0, 3);
  const rows = drills.length > 0
    ? drills.map((drill) => `${drill.title}: ${drill.steps[0] ?? drill.addresses}`)
    : improvements.map((item) => item.suggestion || item.text);
  nextGoalsList.innerHTML = rows.length
    ? rows.map((row) => `<li>${row}</li>`).join('')
    : '<li>다음 연습에서 다시 확인할 포인트를 곧 보여드릴게요.</li>';
}

function renderChart(session: ReturnType<typeof getCompletedSession>) {
  const canvas = document.getElementById('score-chart') as HTMLCanvasElement | null;
  if (!canvas || !session) return;
  const labels = session.report.score_timeline.map((sample) => formatTimeShort(sample.t));
  const data = session.report.score_timeline.map((sample) => Number(sample.score.toFixed(1)));
  const topMoments = getTopMoments(session.report, 8);
  const momentsByIndex = new Map<number, AnnotatedMoment>();
  const momentData = labels.map(() => null as number | null);
  topMoments.forEach((moment) => {
    const nearestIndex = session.report.score_timeline.reduce((bestIndex, sample, index, samples) => {
      const bestDelta = Math.abs(samples[bestIndex].t - moment.t);
      const nextDelta = Math.abs(sample.t - moment.t);
      return nextDelta < bestDelta ? index : bestIndex;
    }, 0);
    momentsByIndex.set(nearestIndex, moment);
    momentData[nearestIndex] = data[nearestIndex] ?? Math.max(40, 100 + Math.min(0, moment.impact));
  });
  new Chart(canvas, {
    type: 'line',
    data: {
      labels,
      datasets: [
        {
          label: '발화 흐름',
          data,
          borderColor: '#AFCBFF',
          backgroundColor: '#AFCBFF',
          pointBackgroundColor: '#AFCBFF',
          pointRadius: 3,
          borderWidth: 2,
          tension: 0.28,
        },
        {
          label: '주의 구간',
          data: momentData,
          borderColor: 'transparent',
          backgroundColor: '#2e2c3a',
          pointBackgroundColor: '#2e2c3a',
          pointBorderColor: '#CDEEE7',
          pointBorderWidth: 3,
          pointRadius: 7,
          pointHoverRadius: 9,
          showLine: false,
        },
      ],
    },
    options: {
      responsive: true,
      onClick: (_event: unknown, elements: Array<{ datasetIndex: number; index: number }>) => {
        const hit = elements.find((element) => element.datasetIndex === 1);
        if (!hit) return;
        const moment = momentsByIndex.get(hit.index);
        if (moment) openMoment(moment);
      },
      plugins: {
        legend: { display: false },
        tooltip: {
          callbacks: {
            title: (items: Array<{ dataIndex: number }>) => {
              const moment = momentsByIndex.get(items[0]?.dataIndex ?? -1);
              return moment ? formatMomentRange(moment) : '';
            },
            label: (item: { datasetIndex: number; dataIndex: number; formattedValue: string }) => {
              const moment = momentsByIndex.get(item.dataIndex);
              if (item.datasetIndex === 1 && moment) return `${moment.title} · 클릭해서 복기`;
              return `점수: ${item.formattedValue}`;
            },
          },
        },
      },
      scales: {
        y: { min: 0, max: 100, grid: { color: 'rgba(151, 165, 188, 0.18)' } },
        x: { grid: { display: false } },
      },
    },
  });
}

async function render() {
  syncThemeToggle();
  setDownloadVideoAvailable(false);
  const params = new URLSearchParams(location.search);
  const sessions = getCompletedSessions();
  const session = params.get('session')
    ? getCompletedSession(params.get('session')!)
    : sessions[0] ?? null;

  if (!session) {
    clearReportMedia();
    if (summaryEl) summaryEl.textContent = '표시할 분석 결과가 아직 없습니다.';
    return;
  }

  const previous = findPreviousSession(sessions, session);
  const previousScore = previous?.report.accuracy_overall ?? null;
  const targetScore = deriveNextTarget(session.report.accuracy_overall, previousScore);
  const axisScores = getDisplayAxisScores(session.report);
  const goalText = session.goal.length ? session.goal.join(', ') : '말하기 흐름';

  if (reportHeading) reportHeading.textContent = session.project;
  if (reportSubtitle) reportSubtitle.textContent = `${session.project} 리포트`;
  if (focusLine) focusLine.textContent = `이번 연습에서는 ${goalText}를 중심으로 돌아봤어요.`;
  if (currentScoreEl) currentScoreEl.textContent = `${Math.round(session.report.accuracy_overall)}`;
  if (targetScoreEl) targetScoreEl.textContent = `${Math.round(targetScore)}`;
  if (deltaScoreEl) {
    const delta = typeof previousScore === 'number' ? session.report.accuracy_overall - previousScore : session.report.accuracy_overall - targetScore + 5;
    deltaScoreEl.textContent = `${delta >= 0 ? '+' : ''}${Math.round(delta)}`;
  }
  if (summaryEl) {
    const context = inferPracticeContext(session);
    summaryEl.textContent = session.report.overall_summary
      ? `${session.report.overall_summary} ${context.cue}`
      : `이번 ${context.label} 연습은 ${goalText}를 중심으로 정리했습니다. ${context.cue}`;
  }
  if (retryLink) {
    const retryUrl = new URL('practice.html', location.href);
    retryUrl.searchParams.set('sessionId', session.sessionId);
    if (session.projectId) retryUrl.searchParams.set('projectId', session.projectId);
    retryUrl.searchParams.set('project', session.project);
    retryUrl.searchParams.set('goal', goalText);
    retryUrl.searchParams.set('type', session.type);
    if (session.situation) retryUrl.searchParams.set('situation', session.situation);
    retryLink.href = retryUrl.toString();
  }

  setAxisCard(
    'verbal',
    axisScores.verbal,
    session.report.strengths[0]?.text || session.report.top_priorities[0]?.text || '핵심 문장 흐름을 기준으로 살펴봤어요.',
  );
  setAxisCard(
    'prosody',
    axisScores.prosody,
    session.report.improvements[0]?.text || '속도와 쉼의 리듬을 함께 정리했어요.',
  );
  setAxisCard(
    'nonverbal',
    axisScores.nonverbal,
    session.report.top_priorities[0]?.suggestion || '시선, 자세, 표정 흐름을 함께 돌아봤어요.',
  );

  renderCoachingSummary(session);
  renderFocusAnalysis(session);
  renderAgentHistory(session);
  renderExpertMetrics(session);
  renderNextGoals(session);
  renderChart(session);
  // Cache + show toggle only if this session actually has an avatar recording.
  // Re-entry into render() (e.g. switching sessions) resets toggle state so
  // the user always starts on the raw video.
  activeSession = session;
  avatarMode = false;
  if (avatarToggleLink) {
    avatarToggleLink.hidden = !session.avatarMediaId;
    avatarToggleLink.setAttribute('aria-pressed', 'false');
    avatarToggleLink.textContent = '아바타로 보기';
  }
  await attachModalVideo(session, false);

  const modalLines = [
    session.report.top_priorities[0]?.text,
    session.report.top_priorities[0]?.suggestion,
  ].filter(Boolean);
  if (modalFeedback) modalFeedback.textContent = modalLines.join(' ') || '세부 코칭이 준비되면 이곳에 표시됩니다.';
}

void render();
