// Chess-style review dashboard for a completed session.
//
// Self-contained: builds its own markup inside the passed container (defaults to
// #review) on every call. The host page only needs an empty container to exist —
// makes the dashboard robust to host-page rewrites and lets us add the new mistake-
// marker overlay over the video without changing practice.html.

import type {
  ComprehensiveReport,
  AnnotatedMoment,
  TimelineSample,
  AxisAccuracy,
  SubtitleSegment,
} from './types';
import { getLandmarksAtTime, type LandmarkSnapshot, type Point2 } from '../signals/compute';

// Korean filler dictionary — mirrors services/audio-pipeline/app/prosody.py so
// the subtitle can highlight the same words that triggered FILLER_BURST events.
const KOREAN_FILLERS = new Set([
  '음', '어', '아', '에', '저', '저기', '그', '그게', '그니까', '그러니까', '이제',
  '뭐', '약간', '막', '근데',
  '음...', '어...', '에...',
]);

function normalizeWord(w: string): string {
  return w.trim().replace(/^[.,!?…"'()[\]{}]+|[.,!?…"'()[\]{}]+$/g, '').trim();
}

// Pulls filler terms out of a moment's title — events.py formats filler bursts
// as e.g. "filler 폭주: 5개 (음, 어, 그)". Returns an empty set for non-filler
// verbal moments, which still get the segment-level "subtitle-active" outline.
function extractFillerTerms(m: AnnotatedMoment): Set<string> {
  if (!/filler|필러|폭주/i.test(m.title)) return new Set();
  const match = m.title.match(/\(([^)]+)\)/);
  if (!match) return new Set();
  return new Set(match[1].split(',').map((s) => normalizeWord(s)));
}

const QUALITY_META: Record<string, { icon: string; color: string; label: string }> = {
  brilliant:  { icon: '★',  color: '#5dd5a2', label: '탁월' },
  excellent:  { icon: '!!', color: '#5dd5a2', label: '우수' },
  good:       { icon: '★',  color: '#9bb',    label: '무난' },
  inaccuracy: { icon: '!',  color: '#4aa3ff', label: '주의' },
  mistake:    { icon: '?',  color: '#f0a040', label: '실수' },
  blunder:    { icon: '✕',  color: '#e04848', label: '심각' },
};

const AXIS_LABEL: Record<string, string> = {
  gaze: '시선', posture: '자세', expression: '표정',
  gesture: '제스처', delivery: '전달력', logic: '논리', overall: '종합',
};

// ── Visual mistake marker mapping ──
//
// Verbal-axis moments get a compact caption-strip marker near the subtitles.
const AXIS_REGION: Record<string, [number, number]> = {
  gaze:       [50, 22],   // head/eye area
  expression: [50, 27],   // face
  gesture:    [50, 52],   // body center (no per-side hint in current signals)
  posture:    [50, 62],   // torso
  delivery:   [50, 78],   // caption strip (above standard <video controls>)
  logic:      [50, 78],
  overall:    [50, 50],
};

// Two moments are "concurrent" if their time spans overlap (with a small fuzz pad
// so near-adjacent point events still cluster). A long-pause silence and a sudden
// raised arm during that silence must show up together — the user explicitly does
// not want one to mask the other.
const CONCURRENT_FUZZ_S = 0.8;

function findConcurrent(moments: AnnotatedMoment[], i: number): number[] {
  const m = moments[i];
  const aStart = m.t - CONCURRENT_FUZZ_S;
  const aEnd = m.t + (m.duration_s ?? 0) + CONCURRENT_FUZZ_S;
  const out: number[] = [i];
  for (let j = 0; j < moments.length; j++) {
    if (j === i) continue;
    const o = moments[j];
    const bStart = o.t - CONCURRENT_FUZZ_S;
    const bEnd = o.t + (o.duration_s ?? 0) + CONCURRENT_FUZZ_S;
    if (aStart < bEnd && bStart < aEnd) out.push(j);
  }
  return out.sort((x, y) => moments[x].t - moments[y].t);
}

// Renders the coach bubble for the active (primary) moment plus a clickable list
// of any concurrent moments below. Clicking a concurrent item promotes it to
// primary via the supplied callback (re-runs selectIndex on that moment's index).
function renderCoachBubble(
  bubble: HTMLDivElement,
  primary: AnnotatedMoment,
  others: AnnotatedMoment[],
  otherIndices: number[],
  onClickOther: (idx: number) => void,
): void {
  const meta = QUALITY_META[primary.quality];
  let html = `
    <div class="bubble-head">
      <span class="bubble-icon" style="color:${meta.color}">${meta.icon}</span>
      <span class="bubble-time">${formatMomentTime(primary)}</span>
      <span class="bubble-quality">${meta.label}</span>
      <span class="bubble-axis">${AXIS_LABEL[primary.axis] ?? primary.axis}</span>
    </div>
    <div class="bubble-title">${escapeHtml(humanizeCoachText(primary.title))}</div>
    <div class="bubble-comment">${escapeHtml(humanizeCoachText(primary.coach_comment ?? '(코멘트 없음)'))}</div>
  `;
  if (others.length > 0) {
    html += `
      <div class="bubble-concurrent">
        <div class="bubble-concurrent-label">동시 발생 (${others.length})</div>
        <ul class="bubble-concurrent-list" data-el="concurrent-list">
          ${others
            .map((c, k) => {
              const cMeta = QUALITY_META[c.quality];
              return `
                <li class="bubble-concurrent-item moment-${c.quality}" data-other-index="${otherIndices[k]}">
                  <span class="bubble-concurrent-icon" style="color:${cMeta.color}">${cMeta.icon}</span>
                  <span class="bubble-concurrent-axis">${AXIS_LABEL[c.axis] ?? c.axis}</span>
                  <span class="bubble-concurrent-title">${escapeHtml(humanizeCoachText(c.title))}</span>
                  ${c.coach_comment ? `<div class="bubble-concurrent-comment">${escapeHtml(humanizeCoachText(c.coach_comment))}</div>` : ''}
                </li>
              `;
            })
            .join('')}
        </ul>
      </div>
    `;
  }
  bubble.innerHTML = html;
  bubble
    .querySelectorAll<HTMLLIElement>('[data-other-index]')
    .forEach((li) => {
      const idx = Number(li.dataset.otherIndex);
      li.addEventListener('click', () => onClickOther(idx));
    });
}

const REVIEW_TEMPLATE = `
  <header class="review-header">
    <div class="overall-card">
      <div class="overall-label">종합 정확성</div>
      <div data-el="overall" class="overall-value">—</div>
    </div>
    <div data-el="coach-bubble" class="coach-bubble">선택된 순간이 없습니다.</div>
    <div data-el="coach-impact" class="impact-pill">±0</div>
  </header>

  <div class="review-controls">
    <button data-el="prev">← 이전</button>
    <button data-el="next">다음 →</button>
    <button data-el="overlay-toggle" class="overlay-toggle" type="button" aria-pressed="true">오버레이 켜짐</button>
    <button data-el="subtitle-toggle" class="subtitle-toggle" type="button" aria-pressed="true">자막 켜짐</button>
  </div>

  <div class="review-grid">
    <div class="review-left">
      <div class="pdf-video-note">PDF에는 영상이 포함되지 않습니다. 주요 순간, 총평, 전사 확인 표현, 측정 지표가 저장됩니다.</div>
      <div class="video-overlay-wrap">
        <video data-el="video" controls playsinline></video>
        <div data-el="overlay" class="video-mistake-overlay"></div>
        <div data-el="subtitle" class="video-subtitle"></div>
      </div>
      <svg data-el="timeline" viewBox="0 0 800 120" preserveAspectRatio="none"></svg>
    </div>
    <div class="review-right">
      <h3 class="panel-h">축별 정확성</h3>
      <div data-el="axes" class="axes"></div>
      <h3 class="panel-h">품질 분포</h3>
      <div data-el="buckets" class="buckets"></div>
    </div>
  </div>

  <h3 class="panel-h">순간 (Moments)</h3>
  <ol data-el="moments-list" class="moments"></ol>
  <h3 class="panel-h">총평</h3>
  <p data-el="summary" class="summary"></p>
  <section data-el="transcript-check-panel" class="transcript-check-panel" hidden>
    <h3 class="panel-h">전사 확인 필요 표현</h3>
    <ul data-el="transcript-check-list" class="transcript-check-list"></ul>
  </section>
  <section data-el="metrics-panel" class="metrics-panel">
    <h3 class="panel-h">측정 지표</h3>
    <div data-el="metrics" class="metrics-grid"></div>
  </section>
  <section data-el="transcript-panel" class="transcript-panel" hidden>
    <h3 class="panel-h">전사 텍스트</h3>
    <p data-el="transcript-text" class="transcript-text"></p>
  </section>
`;

export interface ReviewController {
  selectIndex(i: number): void;
  next(): void;
  prev(): void;
}

export function renderReview(
  report: ComprehensiveReport,
  videoEl: HTMLVideoElement,
  container?: HTMLElement,
): ReviewController {
  const root = container ?? (document.getElementById('review') as HTMLElement | null);
  if (!root) {
    throw new Error('renderReview: no container — pass one or ensure #review exists');
  }
  root.innerHTML = REVIEW_TEMPLATE;

  const $ = <T extends Element>(name: string): T =>
    root.querySelector(`[data-el="${name}"]`) as T;

  const $overall  = $<HTMLDivElement>('overall');
  const $bubble   = $<HTMLDivElement>('coach-bubble');
  const $impact   = $<HTMLDivElement>('coach-impact');
  const $prev     = $<HTMLButtonElement>('prev');
  const $next     = $<HTMLButtonElement>('next');
  const $overlayToggle = $<HTMLButtonElement>('overlay-toggle');
  const $subtitleToggle = $<HTMLButtonElement>('subtitle-toggle');
  const $video    = $<HTMLVideoElement>('video');
  const $overlay  = $<HTMLDivElement>('overlay');
  const $subtitle = $<HTMLDivElement>('subtitle');
  const $svg      = $<SVGSVGElement>('timeline');
  const $axes     = $<HTMLDivElement>('axes');
  const $buckets  = $<HTMLDivElement>('buckets');
  const $list     = $<HTMLOListElement>('moments-list');
  const $summary  = $<HTMLParagraphElement>('summary');
  const $transcriptCheckPanel = $<HTMLElement>('transcript-check-panel');
  const $transcriptCheckList = $<HTMLUListElement>('transcript-check-list');
  const $metrics  = $<HTMLDivElement>('metrics');
  const $transcriptPanel = $<HTMLElement>('transcript-panel');
  const $transcriptText = $<HTMLParagraphElement>('transcript-text');

  // STT segments shipped with the report (from /analyze). Empty when STT didn't
  // run for this session → subtitle stays hidden. The rAF overlay loop reads
  // these continuously and decides per-frame which segment / words to highlight.
  const subtitleSegs: SubtitleSegment[] = report.subtitle_segments ?? [];

  // Mirror the recorded blob into our self-built video element. Blob URLs can be
  // shared across multiple <video> elements safely.
  if (videoEl.src) $video.src = videoEl.src;

  const moments = report.annotated_moments ?? [];
  const timeline = report.score_timeline ?? [];
  let current = 0;
  let overlayEnabled = localStorage.getItem('speakup-review-overlay') !== 'false';
  let subtitleEnabled = localStorage.getItem('speakup-review-subtitle') !== 'false';

  $overall.textContent = `${report.accuracy_overall.toFixed(1)}%`;
  $summary.textContent = humanizeCoachText(report.overall_summary ?? '');

  renderAxes($axes, report.accuracy_per_axis ?? []);
  renderBuckets($buckets, report.quality_buckets);
  renderMetrics($metrics, report.accuracy_per_axis ?? []);
  renderTranscriptChecks(
    $transcriptCheckPanel,
    $transcriptCheckList,
    report.transcript_checks ?? [],
    (t) => {
      $video.currentTime = t;
      void $video.play().catch(() => {});
    },
  );
  syncOverlayToggle();
  syncSubtitleToggle();

  const transcriptText = buildTranscriptText(subtitleSegs);
  if (transcriptText) {
    $transcriptPanel.hidden = false;
    $transcriptText.textContent = transcriptText;
  }

  // ── moments list
  moments.forEach((m, i) => {
    const li = document.createElement('li');
    li.className = `moment moment-${m.quality}`;
    li.dataset.index = String(i);
    const meta = QUALITY_META[m.quality];
    li.innerHTML = `
      <span class="moment-time">${formatMomentTime(m)}</span>
      <span class="moment-icon" style="color:${meta.color}">${meta.icon}</span>
      <span class="moment-axis">${AXIS_LABEL[m.axis] ?? m.axis}</span>
      <span class="moment-title">${escapeHtml(humanizeCoachText(m.title))}</span>
      <span class="moment-impact">${m.impact >= 0 ? '+' : ''}${m.impact}</span>
    `;
    li.addEventListener('click', () => selectIndex(i));
    $list.appendChild(li);
  });

  renderTimeline($svg, timeline, moments, (i) => selectIndex(i));

  // ── Time-driven overlay loop ──
  // Markers stay pinned to whatever the user clicked (selectIndex sets pinnedMoments),
  // so they remain visible while the video plays. The CIRCLES inside the markers
  // reposition every frame to the body part's current location — so e.g. when
  // the user clicked a "raised arm" moment and presses play, the circle stays on
  // the moving wrist instead of disappearing the instant playback steps past the
  // moment's time range. (The previous "active-moments-at-currentT" approach
  // hid markers anywhere outside the moment span — markers seemed to only appear
  // while paused.)
  //
  // Subtitle text follows currentT separately so the words read correctly with
  // playback; the active-verbal *highlight* keys off pinnedMoments so it agrees
  // with the markers above.
  let pinnedMoments: AnnotatedMoment[] = [];
  function pinnedVerbalMoment(): AnnotatedMoment | undefined {
    return pinnedMoments.find((m) => m.axis === 'delivery' || m.axis === 'logic');
  }

  function updateSubtitle(): void {
    if (!subtitleEnabled || subtitleSegs.length === 0) {
      $subtitle.classList.remove('subtitle-visible');
      $subtitle.innerHTML = '';
      return;
    }
    const t = $video.currentTime;
    const seg = subtitleSegs.find((s) => s.t_start <= t && t <= s.t_end + 0.4);
    if (!seg) {
      $subtitle.classList.remove('subtitle-visible');
      $subtitle.innerHTML = '';
      return;
    }
    // Subtitle highlight follows the user's clicked context (pinned), not the
    // current playback time. So the filler / segment outline stays consistent
    // with the markers above the video instead of randomly jumping when
    // playback drifts into another verbal mistake the user didn't click.
    const verbal = pinnedVerbalMoment();
    const explicitFillers = verbal ? extractFillerTerms(verbal) : new Set<string>();
    const html = seg.words.length > 0
      ? seg.words.map((w) => {
          const norm = normalizeWord(w.word);
          const isFiller = !!verbal && (explicitFillers.has(norm) || KOREAN_FILLERS.has(norm));
          const filler = isFiller ? ' subtitle-filler' : '';
          const current = t >= w.t_start && t <= w.t_end + 0.1 ? ' subtitle-current' : '';
          return `<span class="subtitle-word${filler}${current}">${escapeHtml(w.word)}</span>`;
        }).join(' ')
      : `<span>${escapeHtml(seg.text)}</span>`;
    const activeOnThis = !!verbal;
    $subtitle.className = `video-subtitle subtitle-visible${activeOnThis ? ' subtitle-active' : ''}`;
    $subtitle.innerHTML = html;
  }

  function renderOverlayAtCurrentTime(): void {
    const t = $video.currentTime;
    if (overlayEnabled) {
      // Pinned moments stay on-screen; circles re-pick body coords for THIS instant.
      renderMistakeMarkers($overlay, pinnedMoments, t);
    } else {
      $overlay.innerHTML = '';
    }
    updateSubtitle();
  }

  // rAF loop runs only while playing — paused video uses one-shot updates so
  // we don't burn cycles forever.
  let rafId = 0;
  function rafLoop(): void {
    renderOverlayAtCurrentTime();
    if (!$video.paused && !$video.ended) {
      rafId = requestAnimationFrame(rafLoop);
    }
  }
  $video.addEventListener('play', () => {
    cancelAnimationFrame(rafId);
    rafId = requestAnimationFrame(rafLoop);
  });
  $video.addEventListener('pause', () => {
    cancelAnimationFrame(rafId);
    renderOverlayAtCurrentTime();
  });
  $video.addEventListener('ended', () => cancelAnimationFrame(rafId));
  $video.addEventListener('seeked', renderOverlayAtCurrentTime);
  $video.addEventListener('loadedmetadata', () => {
    const selected = moments[current];
    if (selected) {
      try { $video.currentTime = selected.t; } catch { /* video not seekable yet */ }
    }
    renderOverlayAtCurrentTime();
  });

  $overlayToggle.addEventListener('click', () => {
    overlayEnabled = !overlayEnabled;
    localStorage.setItem('speakup-review-overlay', String(overlayEnabled));
    syncOverlayToggle();
    renderOverlayAtCurrentTime();
  });
  $subtitleToggle.addEventListener('click', () => {
    subtitleEnabled = !subtitleEnabled;
    localStorage.setItem('speakup-review-subtitle', String(subtitleEnabled));
    syncSubtitleToggle();
    renderOverlayAtCurrentTime();
  });

  $prev.addEventListener('click', () => selectIndex(current - 1));
  $next.addEventListener('click', () => selectIndex(current + 1));
  document.addEventListener('keydown', (e) => {
    if (document.activeElement && (document.activeElement as HTMLElement).tagName === 'INPUT') return;
    if (e.key === 'ArrowLeft') selectIndex(current - 1);
    else if (e.key === 'ArrowRight') selectIndex(current + 1);
  });

  function selectIndex(i: number) {
    if (moments.length === 0) return;
    const clamped = Math.max(0, Math.min(moments.length - 1, i));
    current = clamped;
    const m = moments[clamped];
    const concurrentIdx = findConcurrent(moments, clamped);
    const otherIdx = concurrentIdx.filter((j) => j !== clamped);
    const others = otherIdx.map((j) => moments[j]);

    // Coach bubble — primary moment plus a clickable list of all concurrent ones.
    // Clicking a concurrent item re-runs selectIndex on it (promotes to primary).
    renderCoachBubble($bubble, m, others, otherIdx, (idx) => selectIndex(idx));
    $impact.textContent = `${m.impact >= 0 ? '+' : ''}${m.impact}`;

    // Moments list — active for the clicked one, "concurrent" for its siblings.
    $list.querySelectorAll('li').forEach((el) => el.classList.remove('active', 'concurrent'));
    const activeLi = $list.querySelector(`li[data-index="${clamped}"]`);
    if (activeLi) {
      activeLi.classList.add('active');
      (activeLi as HTMLElement).scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }
    for (const j of otherIdx) {
      const li = $list.querySelector(`li[data-index="${j}"]`);
      if (li) li.classList.add('concurrent');
    }

    // Timeline dots — same active / concurrent dual highlight.
    $svg.querySelectorAll('.timeline-dot').forEach((el) => el.classList.remove('active', 'concurrent'));
    const activeDot = $svg.querySelector(`.timeline-dot[data-index="${clamped}"]`);
    if (activeDot) activeDot.classList.add('active');
    for (const j of otherIdx) {
      const dot = $svg.querySelector(`.timeline-dot[data-index="${j}"]`);
      if (dot) dot.classList.add('concurrent');
    }

    // Pin this moment + its concurrents so the overlay keeps them on-screen
    // while the user plays through the section. The rAF loop will continually
    // refresh circle positions to wherever the body parts are at the current
    // frame, so the marker tracks the speaker instead of staying static.
    pinnedMoments = concurrentIdx.map((j) => moments[j]);

    // Seek video and PAUSE — user explicitly asked for this; auto-play scrubs past
    // the mistake before they can inspect it. The rAF overlay loop picks up the
    // new currentTime via the 'seeked' event and re-renders markers/subtitle.
    try {
      $video.pause();
      $video.currentTime = m.t;
    } catch { /* video not ready */ }
    if (videoEl !== $video) {
      try { videoEl.pause(); videoEl.currentTime = m.t; } catch { /* ignore */ }
    }

    $prev.disabled = clamped === 0;
    $next.disabled = clamped === moments.length - 1;
  }

  function syncOverlayToggle(): void {
    $overlayToggle.classList.toggle('is-active', overlayEnabled);
    $overlayToggle.setAttribute('aria-pressed', String(overlayEnabled));
    $overlayToggle.textContent = overlayEnabled ? '오버레이 켜짐' : '오버레이 꺼짐';
  }

  function syncSubtitleToggle(): void {
    $subtitleToggle.classList.toggle('is-active', subtitleEnabled);
    $subtitleToggle.setAttribute('aria-pressed', String(subtitleEnabled));
    $subtitleToggle.textContent = subtitleEnabled ? '자막 켜짐' : '자막 꺼짐';
  }

  if (moments.length > 0) selectIndex(0);

  return {
    selectIndex,
    next: () => selectIndex(current + 1),
    prev: () => selectIndex(current - 1),
  };
}

// ── Bone-based mistake / positive markers over the video ──
//
// Draw a compact body-tracking layer along the body part causing the moment:
// forearm for gesture, eye/face frame for gaze/expression, upper-body frame for
// posture. The marker is intentionally label-based, not a huge punctuation glyph,
// so the video still reads like a coaching product instead of a debug overlay.
//
// Verbal axes (delivery / logic) still use the caption-strip fallback below the
// video — subtitle word-highlight handles those.

interface Bone {
  a: Point2;
  b: Point2;
}

function pickBones(m: AnnotatedMoment, snap: LandmarkSnapshot): Bone[] {
  const titleHas = (s: string) => m.title.includes(s);

  // Helpers to bundle related bones — drawing multiple connected segments
  // makes the highlight read as "this whole limb / face region" instead of a
  // floating fragment the user has to mentally connect to anything.
  const fullArm = (
    side: 'left' | 'right',
    p: NonNullable<LandmarkSnapshot['pose']>,
  ): Bone[] =>
    side === 'left'
      ? [
          { a: p.leftShoulder, b: p.leftElbow },
          { a: p.leftElbow, b: p.leftWrist },
        ]
      : [
          { a: p.rightShoulder, b: p.rightElbow },
          { a: p.rightElbow, b: p.rightWrist },
        ];

  const upperBodyFrame = (p: NonNullable<LandmarkSnapshot['pose']>): Bone[] => {
    const midShoulder: Point2 = {
      x: (p.leftShoulder.x + p.rightShoulder.x) / 2,
      y: (p.leftShoulder.y + p.rightShoulder.y) / 2,
    };
    const midHip: Point2 = {
      x: (p.leftHip.x + p.rightHip.x) / 2,
      y: (p.leftHip.y + p.rightHip.y) / 2,
    };
    return [
      { a: p.head, b: midShoulder },               // neck
      { a: midShoulder, b: midHip },               // spine
      { a: p.leftShoulder, b: p.rightShoulder },   // shoulder line
      { a: p.leftHip, b: p.rightHip },             // hip line
      { a: p.leftShoulder, b: p.leftHip },         // torso side
      { a: p.rightShoulder, b: p.rightHip },       // torso side
    ];
  };

  const faceFrame = (f: NonNullable<LandmarkSnapshot['face']>): Bone[] => {
    // Eye line + a short nose-to-mouth vertical so the face region reads as a
    // small "skeleton" instead of a single horizontal stroke.
    const eyeMid: Point2 = {
      x: (f.leftEye.x + f.rightEye.x) / 2,
      y: (f.leftEye.y + f.rightEye.y) / 2,
    };
    return [
      { a: f.leftEye, b: f.rightEye },
      { a: eyeMid, b: f.mouth },
    ];
  };

  switch (m.axis) {
    case 'gaze':
    case 'expression':
      if (snap.face) return faceFrame(snap.face);
      break;

    case 'gesture':
      if (snap.pose) {
        // Both full arms — issue could be either, drawing both gives context.
        return [...fullArm('left', snap.pose), ...fullArm('right', snap.pose)];
      }
      break;

    case 'posture': {
      const wristToFace =
        (titleHas('턱 괴기') || titleHas('얼굴') || titleHas('만지')) && snap.face && snap.pose;
      if (wristToFace && snap.face && snap.pose) {
        const fcx = (snap.face.bbox.minX + snap.face.bbox.maxX) / 2;
        const fcy = (snap.face.bbox.minY + snap.face.bbox.maxY) / 2;
        const lwd = Math.hypot(snap.pose.leftWrist.x - fcx, snap.pose.leftWrist.y - fcy);
        const rwd = Math.hypot(snap.pose.rightWrist.x - fcx, snap.pose.rightWrist.y - fcy);
        // Full arm of the touching side — shoulder→elbow→wrist so the user
        // sees which whole arm is up against the face, not just the forearm.
        return fullArm(lwd < rwd ? 'left' : 'right', snap.pose);
      }
      if (titleHas('만지작') && snap.pose) {
        return [...fullArm('left', snap.pose), ...fullArm('right', snap.pose)];
      }
      // Generic posture: upper-body skeleton frame (spine + shoulder line).
      if (snap.pose) return upperBodyFrame(snap.pose);
      break;
    }

    case 'overall':
      // BRILLIANT — green upper-body skeleton as the positive marker.
      if (snap.pose) return upperBodyFrame(snap.pose);
      break;

    // Verbal axes leave the body alone; subtitle row handles them.
    default:
      break;
  }
  return [];
}

// When two markers' centroids land within this distance (in % of video) we
// stagger the second one horizontally so the glyphs don't overlap and
// become unreadable.
const GLYPH_COLLISION_PCT = 5;
const GLYPH_STAGGER_PCT = 7;

function renderMistakeMarkers(
  overlay: HTMLDivElement,
  moments: AnnotatedMoment[],
  currentT: number,
): void {
  overlay.innerHTML = '';
  if (moments.length === 0) return;

  // One SVG layer for all bone lines. viewBox 0..100 with preserveAspectRatio=
  // "none" lets us specify coords as % of the video; vector-effect=
  // non-scaling-stroke keeps line thickness consistent across video aspects.
  const svgNS = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(svgNS, 'svg');
  svg.setAttribute('class', 'mistake-bone-layer');
  svg.setAttribute('viewBox', '0 0 100 100');
  svg.setAttribute('preserveAspectRatio', 'none');

  const snap = getLandmarksAtTime(currentT, 0.4);
  const placedGlyphs: Array<{ x: number; y: number }> = [];

  for (const [idx, m] of moments.entries()) {
    const meta = QUALITY_META[m.quality];
    const isCaption = m.axis === 'delivery' || m.axis === 'logic';
    const isPrimary = idx === 0;

    // Caption-strip moments: skip bone, drop a compact badge in the verbal strip
    // (subtitle word-highlight does the precise "what word" pinpointing).
    if (isCaption) {
      const [px, py] = AXIS_REGION[m.axis] ?? AXIS_REGION.gesture;
      const positioned = staggerIfNeeded(px, py, placedGlyphs);
      overlay.appendChild(renderMarkerBadge(m, meta, positioned.x, positioned.y, true, isPrimary));
      continue;
    }

    const bones = snap ? pickBones(m, snap) : [];

    // No landmark coverage at this instant → skip the on-video marker. The
    // moment is still in the bubble + list + timeline; we just don't dump a
    // misleading "?" at a default coordinate (was producing stray icons above
    // the speaker's head when MediaPipe missed a frame, or when the video
    // had B-roll / title cards without a visible person).
    if (bones.length === 0) continue;

    // Draw every bone in the group with a soft track underlay, a crisp core
    // line, and joint dots. This reads as a body skeleton instead of a random
    // decorative stroke on the video.
    for (const bone of bones) {
      svg.appendChild(createBoneLine(svgNS, bone, meta.color, 'mistake-bone-track'));
      svg.appendChild(createBoneLine(svgNS, bone, meta.color, 'mistake-bone-core'));
      svg.appendChild(createJoint(svgNS, bone.a, meta.color));
      svg.appendChild(createJoint(svgNS, bone.b, meta.color));
    }

    // Badge sits just above the centroid of all bone midpoints — stable even
    // when the group has many segments.
    let cx = 0, cy = 0;
    for (const b of bones) {
      cx += (b.a.x + b.b.x) / 2;
      cy += (b.a.y + b.b.y) / 2;
    }
    cx = (cx / bones.length) * 100;
    cy = (cy / bones.length) * 100;
    const positioned = staggerIfNeeded(cx, cy, placedGlyphs);
    overlay.appendChild(renderMarkerBadge(m, meta, positioned.x, Math.max(8, positioned.y - 8), false, isPrimary));
  }

  // Append SVG once after collecting all bones (avoids z-order surprises —
  // marker divs naturally render on top of the earlier SVG sibling).
  overlay.insertBefore(svg, overlay.firstChild);
}

function createBoneLine(
  svgNS: string,
  bone: Bone,
  color: string,
  className: string,
): SVGLineElement {
  const line = document.createElementNS(svgNS, 'line') as SVGLineElement;
  line.setAttribute('x1', String(bone.a.x * 100));
  line.setAttribute('y1', String(bone.a.y * 100));
  line.setAttribute('x2', String(bone.b.x * 100));
  line.setAttribute('y2', String(bone.b.y * 100));
  line.setAttribute('stroke', color);
  line.setAttribute('stroke-linecap', 'round');
  line.setAttribute('vector-effect', 'non-scaling-stroke');
  line.setAttribute('class', className);
  return line;
}

function createJoint(svgNS: string, point: Point2, color: string): SVGCircleElement {
  const circle = document.createElementNS(svgNS, 'circle') as SVGCircleElement;
  circle.setAttribute('cx', String(point.x * 100));
  circle.setAttribute('cy', String(point.y * 100));
  circle.setAttribute('r', '1.15');
  circle.setAttribute('fill', color);
  circle.setAttribute('class', 'mistake-joint');
  return circle;
}

function renderMarkerBadge(
  m: AnnotatedMoment,
  meta: { color: string; label: string },
  x: number,
  y: number,
  caption: boolean,
  primary = true,
): HTMLDivElement {
  const el = document.createElement('div');
  el.className = `analysis-marker quality-${m.quality}${caption ? ' analysis-marker-caption' : ''}${primary ? '' : ' analysis-marker-secondary'}`;
  el.style.setProperty('--marker-color', meta.color);
  el.style.left = `${clampPct(x)}%`;
  el.style.top = `${clampPct(y)}%`;
  el.innerHTML = primary
    ? `
      <span class="analysis-marker-dot"></span>
      <span class="analysis-marker-copy">
        <strong>${escapeHtml(AXIS_LABEL[m.axis] ?? m.axis)}</strong>
        <em>${escapeHtml(meta.label)}</em>
      </span>
    `
    : `<span class="analysis-marker-dot"></span>`;
  el.title = humanizeCoachText(m.title);
  return el;
}

function clampPct(v: number): number {
  return Math.max(4, Math.min(96, v));
}

/** Returns a non-overlapping position for a new glyph; shifts right in
 *  GLYPH_STAGGER_PCT increments until clear of already-placed positions
 *  (up to 6 collisions — beyond that we accept the overlap). */
function staggerIfNeeded(
  x: number,
  y: number,
  placed: Array<{ x: number; y: number }>,
): { x: number; y: number } {
  let adjusted = x;
  for (let i = 0; i < 6; i++) {
    const collides = placed.some(
      (p) => Math.abs(p.x - adjusted) < GLYPH_COLLISION_PCT &&
             Math.abs(p.y - y) < GLYPH_COLLISION_PCT,
    );
    if (!collides) break;
    adjusted += GLYPH_STAGGER_PCT;
  }
  placed.push({ x: adjusted, y });
  return { x: adjusted, y };
}

function humanizeCoachText(text: string): string {
  return text
    .replace(/전달력\(Delivery\)/g, '전달력')
    .replace(/시선 처리\(Gaze\)/g, '시선 처리')
    .replace(/\bDelivery\b/g, '전달력')
    .replace(/\bGaze\b/g, '시선')
    .replace(/세션 평균\s*WPM\s*([0-9.]+)/g, '세션 평균 말 속도는 분당 $1어절')
    .replace(/말 속도 급상승:\s*WPM\s*([0-9.]+)/g, '말 속도 급상승: 분당 $1어절')
    .replace(/말 속도 느림:\s*WPM\s*([0-9.]+)/g, '말 속도 느림: 분당 $1어절')
    .replace(/평균\s*WPM\s*([0-9.]+)\s*은/g, '평균 말 속도는 분당 $1어절로')
    .replace(/평균\s*WPM\s*([0-9.]+)/g, '평균 말 속도는 분당 $1어절')
    .replace(/\bWPM\s*([0-9.]+)/g, '분당 $1어절')
    .replace(/\bWPM\b/g, '분당 말한 어절 수')
    .replace(/시선 중앙 유지율/g, '정면을 바라본 비율')
    .replace(
      /전사 텍스트\s*(?:상에서|상으로|기준으로|기준)?[^.]*?(?:의미 전달이 불분명|논리적 명료성|명료성 개선)[^.]*\./g,
      '전사 텍스트에 어색한 표현 후보가 있어 STT 오인식 가능성을 확인해야 하며, 언어와 논리 평가는 참고용으로 보는 것이 안전합니다.',
    )
    .replace(/더딘 및\s*/g, '')
    .replace(/\bfiller\b/gi, '필러 표현')
    .replace(/\btranscript\b/gi, '전사 텍스트');
}

function buildTranscriptText(segments: SubtitleSegment[]): string {
  return segments
    .map((seg) => seg.text.trim())
    .filter(Boolean)
    .join(' ')
    .replace(/\s+/g, ' ')
    .trim();
}

function renderAxes(el: HTMLElement, axes: AxisAccuracy[]): void {
  el.innerHTML = '';
  for (const a of axes) {
    const row = document.createElement('div');
    row.className = 'axis-row';
    const w = a.available ? Math.max(0, Math.min(100, a.score)) : 0;
    const note = a.note ? humanizeCoachText(a.note) : (!a.available ? 'N/A' : '');
    row.innerHTML = `
      <span class="axis-label">${AXIS_LABEL[a.axis] ?? a.axis}</span>
      <span class="axis-bar"><span class="axis-fill" style="width:${w}%"></span></span>
      <span class="axis-score">${a.available ? a.score.toFixed(0) : '—'}</span>
      <span class="axis-note">${escapeHtml(note)}</span>
    `;
    el.appendChild(row);
  }
}

function renderMetrics(el: HTMLElement, axes: AxisAccuracy[]): void {
  el.innerHTML = '';
  for (const a of axes) {
    const cell = document.createElement('div');
    cell.className = `metric-cell metric-${a.axis}`;
    const label = AXIS_LABEL[a.axis] ?? a.axis;
    const score = a.available ? `${a.score.toFixed(0)}점` : '측정 불가';
    const note = a.note ? humanizeCoachText(a.note) : (a.available ? '측정 근거 수집됨' : '데이터 부족');
    cell.innerHTML = `
      <span class="metric-label">${escapeHtml(label)}</span>
      <strong class="metric-score">${escapeHtml(score)}</strong>
      <span class="metric-note">${escapeHtml(note)}</span>
    `;
    el.appendChild(cell);
  }
}

function renderTranscriptChecks(
  panel: HTMLElement,
  list: HTMLUListElement,
  checks: NonNullable<ComprehensiveReport['transcript_checks']>,
  onSeek: (t: number) => void,
): void {
  list.innerHTML = '';
  panel.hidden = checks.length === 0;
  if (checks.length === 0) return;

  for (const check of checks.slice(0, 3)) {
    const li = document.createElement('li');
    li.className = 'transcript-check-item';
    const hasTime = typeof check.t_start === 'number';
    const time = hasTime ? formatTime(check.t_start as number) : null;
    li.innerHTML = `
      <div class="transcript-check-main">
        <span class="transcript-check-phrase">"${escapeHtml(check.phrase)}"</span>
        ${check.suggestion ? `<span class="transcript-check-arrow">→</span><span class="transcript-check-suggestion">"${escapeHtml(check.suggestion)}"</span>` : ''}
      </div>
      <p>${escapeHtml(check.reason)}</p>
      ${time ? `<button type="button" class="transcript-check-time">${time} 확인</button>` : ''}
    `;
    if (hasTime) {
      li.querySelector<HTMLButtonElement>('.transcript-check-time')?.addEventListener('click', () => {
        onSeek(Math.max(0, (check.t_start ?? 0) - 0.4));
      });
    }
    list.appendChild(li);
  }
}

function renderBuckets(el: HTMLElement, b: ComprehensiveReport['quality_buckets']): void {
  el.innerHTML = '';
  const items: Array<[string, number]> = [
    ['brilliant', b?.brilliant ?? 0],
    ['excellent', b?.excellent ?? 0],
    ['good', b?.good ?? 0],
    ['inaccuracy', b?.inaccuracy ?? 0],
    ['mistake', b?.mistake ?? 0],
    ['blunder', b?.blunder ?? 0],
  ];
  for (const [k, n] of items) {
    const meta = QUALITY_META[k];
    const cell = document.createElement('div');
    cell.className = 'bucket-cell';
    cell.innerHTML = `
      <span class="bucket-icon" style="color:${meta.color}">${meta.icon}</span>
      <span class="bucket-label">${meta.label}</span>
      <span class="bucket-count">${n}</span>
    `;
    el.appendChild(cell);
  }
}

function renderTimeline(
  svg: SVGSVGElement,
  samples: TimelineSample[],
  moments: AnnotatedMoment[],
  onDot: (i: number) => void,
): void {
  while (svg.firstChild) svg.removeChild(svg.firstChild);
  if (samples.length === 0 && moments.length === 0) return;

  const W = svg.viewBox.baseVal?.width || 800;
  const H = svg.viewBox.baseVal?.height || 120;
  const PAD = 8;

  const tMax = Math.max(
    samples.length ? samples[samples.length - 1].t : 0,
    moments.length ? moments[moments.length - 1].t : 0,
    1,
  );

  const xOf = (t: number) => PAD + (t / tMax) * (W - 2 * PAD);
  const yOf = (s: number) => H - PAD - (s / 100) * (H - 2 * PAD);

  if (samples.length > 1) {
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    let d = `M ${xOf(samples[0].t).toFixed(1)} ${yOf(samples[0].score).toFixed(1)}`;
    for (let i = 1; i < samples.length; i++) {
      d += ` L ${xOf(samples[i].t).toFixed(1)} ${yOf(samples[i].score).toFixed(1)}`;
    }
    path.setAttribute('d', d);
    path.setAttribute('class', 'timeline-line');
    svg.appendChild(path);
  }

  for (const score of [25, 50, 75]) {
    const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
    line.setAttribute('x1', String(PAD));
    line.setAttribute('x2', String(W - PAD));
    line.setAttribute('y1', String(yOf(score)));
    line.setAttribute('y2', String(yOf(score)));
    line.setAttribute('class', 'timeline-grid');
    svg.appendChild(line);
  }

  moments.forEach((m, i) => {
    const c = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
    c.setAttribute('cx', String(xOf(m.t)));
    const nearby = samples.reduce(
      (acc, s) => (Math.abs(s.t - m.t) < Math.abs(acc.t - m.t) ? s : acc),
      samples[0] ?? { t: m.t, score: 75 },
    );
    c.setAttribute('cy', String(yOf(nearby?.score ?? 75)));
    c.setAttribute('r', '5');
    c.setAttribute('class', `timeline-dot quality-${m.quality}`);
    c.setAttribute('data-index', String(i));
    c.setAttribute('fill', QUALITY_META[m.quality].color);
    c.addEventListener('click', () => onDot(i));
    const title = document.createElementNS('http://www.w3.org/2000/svg', 'title');
    title.textContent = `${formatMomentTime(m)} ${m.title}`;
    c.appendChild(title);
    svg.appendChild(c);
  });
}

function formatMomentTime(moment: AnnotatedMoment): string {
  const duration = moment.duration_s ?? 0;
  if (duration >= 1.5) {
    return `${formatTime(moment.t)}-${formatTime(moment.t + duration)}`;
  }
  return formatTime(moment.t);
}

function formatTime(s: number): string {
  const m = Math.floor(s / 60);
  const sec = Math.floor(s % 60);
  return `${m.toString().padStart(2, '0')}:${sec.toString().padStart(2, '0')}`;
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}
