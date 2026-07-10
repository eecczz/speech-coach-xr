import { uploadForAnalysis } from './audio-upload';
import { createLandmarkers } from './mediapipe/landmarkers';
import { touchProject } from './project-store';
import type { AnnotatedMoment, ComprehensiveReport, QualityLevel } from './review/types';
import {
  clearPendingAnalysis,
  getPendingAnalysis,
  loadPendingMedia,
  saveCompletedSession,
  type LiveCoachingEvent,
} from './session-store';
import { analyzeUploadedVideo } from './upload-analyze';
import { getActiveUser, saveRemoteSession } from './app-api';
import { createAggregatorClient, finalizeSession } from './ws/client';

type StepState = 'is-pending' | 'is-current' | 'is-complete';
type Phase = 'video' | 'audio' | 'content' | 'done';

const themeToggle = document.querySelector('[data-theme-toggle]') as HTMLButtonElement | null;
const noteEl = document.querySelector('.loading-note') as HTMLParagraphElement | null;
const stepsEl = document.getElementById('steps') as HTMLDivElement | null;
const headingText = document.querySelector('.page-heading p') as HTMLParagraphElement | null;

const stepDefs = [
  { title: '영상 분석 중', detail: '시선 · 표정 · 움직임 확인' },
  { title: '음성 분석 대기 중', detail: '말 속도 · 침묵 구간 분석' },
  { title: '내용 분석 대기 중', detail: '논리 흐름 · 반복 표현 확인' },
];

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

const items = (stepDefs.map((step) => {
  const row = document.createElement('div');
  row.className = 'loading-step';
  row.innerHTML = `
    <div class="step-state" aria-hidden="true">○</div>
    <div class="loading-copy">
      <strong>${step.title}</strong>
      <p>${step.detail}</p>
    </div>
  `;
  stepsEl?.appendChild(row);
  return row;
}));

function setStepState(index: number, state: StepState) {
  const row = items[index];
  if (!row) return;
  row.classList.remove('is-pending', 'is-current', 'is-complete');
  row.classList.add(state);
  const indicator = row.querySelector('.step-state');
  if (!indicator) return;
  if (state === 'is-complete') indicator.textContent = '✓';
  else if (state === 'is-current') indicator.textContent = '●';
  else indicator.textContent = '○';
}

function showPhase(phase: Phase, source: 'live' | 'upload') {
  if (source === 'live') {
    setStepState(0, 'is-complete');
    if (phase === 'audio') {
      setStepState(1, 'is-current');
      setStepState(2, 'is-pending');
      return;
    }
    if (phase === 'content' || phase === 'done') {
      setStepState(1, 'is-complete');
      setStepState(2, phase === 'done' ? 'is-complete' : 'is-current');
      return;
    }
  }

  if (phase === 'video') {
    setStepState(0, 'is-current');
    setStepState(1, 'is-pending');
    setStepState(2, 'is-pending');
  } else if (phase === 'audio') {
    setStepState(0, 'is-complete');
    setStepState(1, 'is-current');
    setStepState(2, 'is-pending');
  } else if (phase === 'content') {
    setStepState(0, 'is-complete');
    setStepState(1, 'is-complete');
    setStepState(2, 'is-current');
  } else {
    setStepState(0, 'is-complete');
    setStepState(1, 'is-complete');
    setStepState(2, 'is-complete');
  }
}

function setStatus(message: string) {
  if (headingText) headingText.textContent = message;
}

function redirectToReport(sessionId: string) {
  const next = new URL('report.html', location.href);
  next.searchParams.set('session', sessionId);
  location.href = next.toString();
}

function qualityFromLiveEvent(level: LiveCoachingEvent['level']): QualityLevel {
  if (level === 'critical') return 'mistake';
  if (level === 'warn') return 'inaccuracy';
  return 'good';
}

function liveEventToMoment(event: LiveCoachingEvent): AnnotatedMoment {
  return {
    t: event.t,
    axis: event.axis,
    quality: qualityFromLiveEvent(event.level),
    title: event.title,
    impact: event.impact,
    coach_comment: event.message,
    duration_s: event.duration_s,
  };
}

function mergeLiveEventsIntoReport(report: ComprehensiveReport, liveEvents: LiveCoachingEvent[] = []): ComprehensiveReport {
  if (liveEvents.length === 0) return report;
  const existingKeys = new Set(
    (report.annotated_moments ?? []).map((moment) => `${Math.round(moment.t)}:${moment.axis}:${moment.title}`),
  );
  const liveMoments = liveEvents
    .map(liveEventToMoment)
    .filter((moment) => {
      const key = `${Math.round(moment.t)}:${moment.axis}:${moment.title}`;
      if (existingKeys.has(key)) return false;
      existingKeys.add(key);
      return true;
    });
  return {
    ...report,
    annotated_moments: [...(report.annotated_moments ?? []), ...liveMoments].sort((a, b) => a.t - b.t),
  };
}

async function run() {
  syncThemeToggle();
  const pending = getPendingAnalysis();
  if (!pending) {
    setStatus('준비된 분석이 없어요. 다시 연습 화면에서 시작해주세요.');
    if (noteEl) noteEl.textContent = '연습 화면으로 돌아가서 다시 분석을 시작할 수 있어요.';
    return;
  }

  const media = await loadPendingMedia(pending.mediaId);
  if (!media) {
    clearPendingAnalysis();
    setStatus('분석할 영상을 찾지 못했어요. 다시 업로드하거나 녹화해주세요.');
    return;
  }

  const file = new File([media.blob], media.filename, { type: media.mimeType || media.blob.type || 'video/webm' });
  let report: ComprehensiveReport | null = null;

  try {
    if (pending.source === 'upload') {
      showPhase('video', 'upload');
      const landmarkers = await createLandmarkers();
      const aggregator = createAggregatorClient();
      report = await analyzeUploadedVideo(file, {
        scenario: pending.scenario,
        focusGoals: pending.goal ?? [],
        landmarkers,
        aggregator,
        setStatus,
        onPhaseChange: (phase) => showPhase(phase, 'upload'),
      });
    } else {
      showPhase('audio', 'live');
      setStatus('음성 분석을 정리하고 있어요.');
      const audioResult = await uploadForAnalysis(file, pending.sessionId, pending.filename);
      showPhase('content', 'live');
      setStatus('종합 코칭을 정리하고 있어요.');
      const result = await finalizeSession(pending.sessionId, audioResult);
      if (result && (result as { report?: ComprehensiveReport }).report) {
        report = (result as { report: ComprehensiveReport }).report;
      }
      showPhase('done', 'live');
    }

    if (!report) {
      throw new Error('코칭 결과를 받지 못했습니다.');
    }
    report = mergeLiveEventsIntoReport(report, pending.liveEvents);

    saveCompletedSession({
      sessionId: pending.sessionId,
      projectId: pending.projectId,
      project: pending.project,
      goal: pending.goal,
      type: pending.type,
      situation: pending.situation,
      source: pending.source,
      createdAt: pending.createdAt,
      report,
      mediaId: pending.mediaId,
      filename: pending.filename,
      mimeType: pending.mimeType,
      avatarMediaId: pending.avatarMediaId,
      avatarFilename: pending.avatarFilename,
      avatarMimeType: pending.avatarMimeType,
      liveEvents: pending.liveEvents,
    });
    const activeUser = getActiveUser();
    if (activeUser) {
      await saveRemoteSession({
        id: pending.sessionId,
        user_id: activeUser.id,
        title: pending.project,
        scenario: pending.type,
        situation: pending.situation || pending.project,
        focus_goals: pending.goal,
        source: pending.source,
        status: 'completed',
        last_report: report,
      }).catch((error) => {
        console.warn('[loading] remote session save failed', error);
      });
    }
    touchProject(pending.projectId);
    clearPendingAnalysis();
    showPhase('done', pending.source);
    setStatus('코칭이 준비됐어요. 결과 화면으로 이동합니다.');
    redirectToReport(pending.sessionId);
  } catch (error) {
    console.error('[loading] analysis failed', error);
    const detail = error instanceof Error ? error.message : '';
    setStatus('분석을 마무리하지 못했어요.');
    if (noteEl) {
      noteEl.textContent = detail
        ? `원인: ${detail}`
        : '창은 그대로 두고, 문제가 반복되면 다시 연습을 시작해주세요.';
    }
  }
}

void run();
