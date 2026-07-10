import { replaceLandmarkBuffer } from './signals/compute';
import { renderReview } from './review/render';
import { loadReviewSession } from './review/store';

const title = document.getElementById('report-title') as HTMLElement;
const subtitle = document.getElementById('report-subtitle') as HTMLElement;
const heading = document.getElementById('report-heading') as HTMLElement;
const focusLine = document.getElementById('report-focus-line') as HTMLElement;
const statusBox = document.getElementById('report-status') as HTMLElement;
const review = document.getElementById('review') as HTMLElement;
const retryLink = document.getElementById('retry-link') as HTMLAnchorElement;
const printButton = document.getElementById('print-link') as HTMLButtonElement;
const themeToggle = document.querySelector('[data-theme-toggle]') as HTMLButtonElement;

function syncThemeToggle(): void {
  const theme = document.documentElement.dataset.theme || 'light';
  themeToggle.textContent = theme === 'dark' ? '☀️' : '🌙';
  themeToggle.setAttribute('aria-label', theme === 'dark' ? '라이트 모드로 전환' : '다크 모드로 전환');
}

themeToggle.addEventListener('click', () => {
  const nextTheme = (document.documentElement.dataset.theme || 'light') === 'dark' ? 'light' : 'dark';
  document.documentElement.dataset.theme = nextTheme;
  localStorage.setItem('speakup-theme', nextTheme);
  syncThemeToggle();
});

printButton.addEventListener('click', () => {
  window.print();
});

async function main(): Promise<void> {
  syncThemeToggle();

  const params = new URLSearchParams(location.search);
  const id = params.get('id');
  const project = params.get('project') || '오늘의 말하기 연습';
  const goal = params.get('goal') || '말 속도';
  retryLink.href = buildRetryHref(params, project, goal);

  title.textContent = project;
  heading.textContent = project;
  focusLine.textContent = `${goal} 중심으로 분석한 코칭 결과입니다.`;

  if (!id) {
    showStatus('연결된 리포트가 없습니다. 연습을 완료하면 이 화면에 결과가 표시됩니다.', true);
    return;
  }

  const session = await loadReviewSession(id);
  if (!session) {
    showStatus('저장된 리포트를 찾지 못했습니다. 브라우저 저장소가 비워졌을 수 있습니다.', true);
    return;
  }

  const report = session.report;
  const created = new Date(session.createdAt);
  subtitle.textContent = `${created.toLocaleString('ko-KR', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })} 코칭`;
  focusLine.textContent =
    `${session.goal || goal} 중심 · 종합 ${report.accuracy_overall.toFixed(1)}점 · 순간 ${report.annotated_moments?.length ?? 0}개`;
  replaceLandmarkBuffer(session.landmarks ?? []);

  const sourceVideo = document.createElement('video');
  sourceVideo.src = URL.createObjectURL(session.videoBlob);
  sourceVideo.preload = 'metadata';

  review.hidden = false;
  statusBox.hidden = true;
  renderReview(report, sourceVideo, review);
}

function buildRetryHref(params: URLSearchParams, project: string, goal: string): string {
  const next = new URL('practice.html', location.href);
  next.searchParams.set('project', project);
  next.searchParams.set('goal', goal);
  for (const key of ['type', 'scenario']) {
    const value = params.get(key);
    if (value) next.searchParams.set(key, value);
  }
  return next.toString();
}

function showStatus(message: string, isError = false): void {
  statusBox.hidden = false;
  statusBox.textContent = message;
  statusBox.classList.toggle('is-error', isError);
}

main().catch((err) => {
  console.error('[report] failed', err);
  showStatus(`리포트 로딩 실패: ${err instanceof Error ? err.message : String(err)}`, true);
});
