import { getLandmarkBuffer } from '../signals/compute';
import type { ComprehensiveReport } from './types';
import { saveReviewSession } from './store';

interface CompleteReviewOptions {
  report: ComprehensiveReport;
  videoBlob: Blob;
  videoName?: string;
  videoType?: string;
  source: 'live' | 'upload';
  setStatus: (msg: string) => void;
}

export async function completeReviewNavigation(options: CompleteReviewOptions): Promise<void> {
  const params = new URLSearchParams(location.search);
  const report = options.report;
  const project = params.get('project') || '오늘의 말하기 연습';
  const goal = params.get('goal') || '말 속도';
  const type = params.get('type') || 'free';
  const scenario = params.get('scenario') || params.get('type') || 'presentation';
  const landmarks = Array.from(getLandmarkBuffer());
  const id = await saveReviewSession({
    report,
    videoBlob: options.videoBlob,
    videoName: options.videoName,
    videoType: options.videoType || options.videoBlob.type || 'video/webm',
    project,
    goal,
    scenario,
    source: options.source,
    landmarks,
  });

  const momentCount = report.annotated_moments?.length ?? 0;
  const scoreText = report.accuracy_overall?.toFixed(1) ?? '?';
  options.setStatus(`평가 완료 — 종합 ${scoreText}점, 순간 ${momentCount}개. 리포트로 이동합니다.`);
  showCompletionToast(`평가 완료`, `종합 ${scoreText}점 · 순간 ${momentCount}개`);

  window.setTimeout(() => {
    const next = new URL('report.html', location.href);
    next.searchParams.set('id', id);
    next.searchParams.set('project', project);
    next.searchParams.set('goal', goal);
    next.searchParams.set('type', type);
    next.searchParams.set('scenario', scenario);
    location.assign(next.toString());
  }, 1200);
}

function showCompletionToast(title: string, detail: string): void {
  document.querySelector('.completion-toast')?.remove();
  const toast = document.createElement('div');
  toast.className = 'completion-toast';
  toast.innerHTML = `
    <span class="completion-toast-icon">✓</span>
    <span>
      <strong>${escapeHtml(title)}</strong>
      <em>${escapeHtml(detail)}</em>
    </span>
  `;
  document.body.appendChild(toast);
  requestAnimationFrame(() => toast.classList.add('is-visible'));
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}
