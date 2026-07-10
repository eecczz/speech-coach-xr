import { average, estimateSessionDuration, getAxisScore, getDisplayAxisScores } from './report-utils';
import { getCompletedSessions } from './session-store';

declare const Chart: any;

const TYPE_LABEL: Record<string, string> = {
  presentation: '발표',
  interview: '면접',
  negotiation: '협상',
  persuasion: '설득',
  daily: '일상 대화',
  phone: '전화',
  online: '온라인',
  free: '자유 연습',
};

function setMetric(key: string, value: string, note: string) {
  const valueEl = document.querySelector<HTMLElement>(`[data-metric="${key}"]`);
  const noteEl = document.querySelector<HTMLElement>(`[data-metric-note="${key}"]`);
  if (valueEl) valueEl.textContent = value;
  if (noteEl) noteEl.textContent = note;
}

function computeStreak(days: string[]): number {
  if (days.length === 0) return 0;
  const unique = [...new Set(days)].sort().reverse();
  let streak = 0;
  let cursor = new Date(unique[0]);
  for (const day of unique) {
    const current = new Date(day);
    if (current.toDateString() === cursor.toDateString()) {
      streak += 1;
      cursor.setDate(cursor.getDate() - 1);
    } else {
      break;
    }
  }
  return streak;
}

function renderChart() {
  const sessions = getCompletedSessions().slice().reverse();
  const canvas = document.getElementById('growth-chart') as HTMLCanvasElement | null;
  if (!canvas || sessions.length === 0) return;
  const labels = sessions.map((_, index) => `${index + 1}회`);
  const overall = sessions.map((session) => Number(session.report.accuracy_overall.toFixed(1)));
  const verbal = sessions.map((session) => getDisplayAxisScores(session.report).verbal ?? null);
  const prosody = sessions.map((session) => getDisplayAxisScores(session.report).prosody ?? null);
  const nonverbal = sessions.map((session) => getDisplayAxisScores(session.report).nonverbal ?? null);

  new Chart(canvas, {
    type: 'line',
    data: {
      labels,
      datasets: [
        { label: '전체', data: overall, borderColor: '#AFCBFF', backgroundColor: '#AFCBFF', borderWidth: 2.2, tension: 0.28 },
        { label: '언어', data: verbal, borderColor: '#F3C9D0', borderDash: [4, 4], borderWidth: 1.8, tension: 0.28 },
        { label: '준언어', data: prosody, borderColor: '#F6E6A8', borderDash: [4, 4], borderWidth: 1.8, tension: 0.28 },
        { label: '비언어', data: nonverbal, borderColor: '#CDEEE7', borderDash: [4, 4], borderWidth: 1.8, tension: 0.28 },
      ],
    },
    options: {
      plugins: { legend: { display: false } },
      scales: {
        y: { min: 0, max: 100, grid: { color: 'rgba(151, 165, 188, 0.18)' } },
        x: { grid: { display: false } },
      },
    },
  });
}

function renderHabits() {
  const sessions = getCompletedSessions().slice(0, 3).reverse();
  const configs = [
    {
      key: 'filler',
      values: sessions.map((session) => {
        const fillerMoments = session.report.annotated_moments.filter((moment) => /filler|필러/i.test(moment.title)).length;
        return Math.max(5, 100 - fillerMoments * 20);
      }),
      note: sessions.length ? '최근 세션의 필러 안정도를 기준으로 정리했어요.' : '데이터 준비 중',
    },
    {
      key: 'delivery',
      values: sessions.map((session) => getAxisScore(session.report, 'delivery') ?? 0),
      note: sessions.length ? '말 속도와 쉼의 리듬 흐름을 함께 봤어요.' : '데이터 준비 중',
    },
    {
      key: 'gaze',
      values: sessions.map((session) => getAxisScore(session.report, 'gaze') ?? 0),
      note: sessions.length ? '카메라 시선 유지 흐름을 기준으로 정리했어요.' : '데이터 준비 중',
    },
  ];

  configs.forEach((config) => {
    const card = document.querySelector<HTMLElement>(`[data-habit="${config.key}"]`);
    if (!card) return;
    const bars = card.querySelectorAll<HTMLElement>('.bar-list i');
    bars.forEach((bar, index) => {
      bar.style.height = `${Math.max(0, Math.min(100, config.values[index] ?? 0))}%`;
      bar.style.background = ['#AFCBFF', '#CDEEE7', '#F3C9D0'][index] || '#AFCBFF';
    });
    const note = card.querySelector<HTMLElement>('p');
    if (note) note.textContent = config.note;
  });
}

function renderScenarioBreakdown() {
  const container = document.getElementById('scenario-breakdown');
  if (!container) return;
  const sessions = getCompletedSessions();
  if (sessions.length === 0) return;

  const scores = new Map<string, number[]>();
  sessions.forEach((session) => {
    const key = session.situation || TYPE_LABEL[session.type] || session.type || '기타';
    const row = scores.get(key) ?? [];
    row.push(session.report.accuracy_overall);
    scores.set(key, row);
  });

  container.innerHTML = [...scores.entries()]
    .slice(0, 4)
    .map(([label, values]) => {
      const score = average(values) ?? 0;
      return `
        <div class="axis-row">
          <span class="axis-label">${label}</span>
          <span class="axis-bar"><span class="axis-fill" style="width: ${Math.round(score)}%"></span></span>
          <span class="axis-score">${Math.round(score)}</span>
        </div>
      `;
    })
    .join('');
}

function renderRecentSessions() {
  const container = document.getElementById('recent-sessions');
  if (!container) return;
  const sessions = getCompletedSessions().slice(0, 6);
  if (sessions.length === 0) return;
  container.innerHTML = sessions
    .map(
      (session) => `
        <a href="report.html?session=${encodeURIComponent(session.sessionId)}">
          <i class="ti ti-clock-hour-4"></i>
          <span>${session.project}</span>
          <em>${Math.round(session.report.accuracy_overall)}</em>
        </a>
      `,
    )
    .join('');
}

function renderMetrics() {
  const sessions = getCompletedSessions();
  if (sessions.length === 0) return;
  const avgScore = average(sessions.map((session) => session.report.accuracy_overall)) ?? 0;
  const totalMinutes = sessions.reduce((sum, session) => sum + estimateSessionDuration(session.report), 0) / 60;
  const streak = computeStreak(sessions.map((session) => session.createdAt.slice(0, 10)));

  setMetric('average', `${Math.round(avgScore)}`, '최근 연습 전체 평균이에요.');
  setMetric('sessions', `${sessions.length}`, '지금까지 쌓인 연습 세션 수예요.');
  setMetric('time', `${Math.round(totalMinutes)}m`, '누적 연습 시간 기준이에요.');
  setMetric('streak', `${streak}일`, '연속으로 연습한 흐름이에요.');

  const latest = sessions[0];
  const goalCopy = document.getElementById('goal-banner-copy');
  const goalMeter = document.getElementById('goal-banner-meter');
  if (goalCopy) {
    const firstDrill = latest.report.training_prescriptions[0];
    goalCopy.textContent = firstDrill
      ? `${firstDrill.title}에 집중해보세요. ${firstDrill.steps[0] ?? firstDrill.addresses}`
      : latest.report.improvements[0]?.text || '가장 최근 세션의 개선 포인트를 다음 목표로 이어가보세요.';
  }
  if (goalMeter) {
    goalMeter.style.width = `${Math.round(latest.report.accuracy_overall)}%`;
  }
}

function render() {
  renderMetrics();
  renderChart();
  renderHabits();
  renderScenarioBreakdown();
  renderRecentSessions();
}

void render();
