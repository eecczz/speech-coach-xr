// Real-time agent trigger evaluators.
//
// Each evaluator owns a tiny piece of state (last-fire timestamp, absence
// window start, etc.) and answers a single question every tick: "should we
// fire a feedback event right now?" The dispatcher takes that yes/no plus
// the trigger payload and calls the coach backend.
//
// Why this lives in the browser:
// - We already have all the signals locally (vision frame, silence detector,
//   STT result, filler matches). Forwarding every frame to the server just so
//   the server can apply a 5-second cooldown would be wasteful.
// - The browser also owns the user-facing rate limit. The model only sees the
//   trigger after it actually fires, so it never has to reason about timing.

import type { VisionFrame } from '../signals/compute';

export type TriggerKind =
  | 'silence'
  | 'gaze'
  | 'smile_absence'
  | 'motion_absence'
  | 'filler'
  | 'speech_rate'
  | 'content';

export interface TriggerEvent {
  kind: TriggerKind;
  // Session-time seconds when the trigger fired.
  t: number;
  // Trigger-specific payload that gets forwarded to the backend prompt.
  payload: Record<string, unknown>;
}

// ── Threshold / cooldown constants (sec) ──
// Spec says 5s but in practice that yields "feedback hell" — multiple
// nonverbal triggers stacking on top of content reactions. Push the
// continuous-state ones out to 7s so they only fire when truly sustained,
// and let the user lean on content/reaction events as the main rhythm.
const SUSTAIN_S = 7.0;
const RATE_LIMIT_S = 7.0;

// Gaze: head yaw magnitude > 20° OR gaze_fixation_ratio collapsed to ~0 counts
// as "looking away". 20° is roughly when a viewer reads it as a clear turn —
// micro-saccades stay under that.
const GAZE_YAW_OFF_DEG = 20.0;
const GAZE_FIXATION_OFF = 0.15;

// Smile: blendshape mouthSmile avg above this is "smiling now". The model
// doesn't need a strong grin; even a light smile counts as warmth.
const SMILE_ON_THRESHOLD = 0.18;

// Motion: hand_velocity_max is the cleanest "did the user move" signal —
// it spikes for any wrist movement and the compute layer already smooths it.
// expression_change_rate covers face-only motion; we OR them so a head turn or
// gesture both count as "alive".
const MOTION_HAND_THRESHOLD = 0.04;
const MOTION_EXPR_CHANGE_THRESHOLD = 0.05;

// Speech rate: WPM bands picked from Korean presentation norms.
const WPM_TOO_FAST = 200;
const WPM_TOO_SLOW = 70;

// Filler: fire every 5 filler hits (5, 10, 15, ...).
const FILLER_STEP = 5;

// Content trigger: keep STT segments at least this long; shorter ones aren't
// worth a full LLM round-trip. Interval is tight (1.5s) so the agent can
// react ~live; the global dispatcher cooldown (2s) and model-side
// "stay quiet on plain utterances" are the real throttle.
const CONTENT_MIN_LEN = 8;
const CONTENT_MIN_INTERVAL_S = 1.5;

// ── Continuous-state triggers ─────────────────────────────────────────────

export class SilenceTrigger {
  private lastFireT = -Infinity;

  evaluate(silenceSeconds: number, t: number): TriggerEvent | null {
    if (silenceSeconds < SUSTAIN_S) return null;
    if (t - this.lastFireT < RATE_LIMIT_S) return null;
    this.lastFireT = t;
    return { kind: 'silence', t, payload: { silence_seconds: silenceSeconds } };
  }

  reset() {
    this.lastFireT = -Infinity;
  }
}

export class GazeTrigger {
  // Track when the sustained off-axis run started, so we only fire after 5s.
  private offSince: number | null = null;
  private lastFireT = -Infinity;

  evaluate(frame: VisionFrame, t: number): TriggerEvent | null {
    const yawMag = Math.abs(frame.head_yaw_deg);
    const isOff = yawMag >= GAZE_YAW_OFF_DEG || frame.gaze_fixation_ratio < GAZE_FIXATION_OFF;
    if (!isOff) {
      this.offSince = null;
      return null;
    }
    if (this.offSince === null) this.offSince = t;
    const offDur = t - this.offSince;
    if (offDur < SUSTAIN_S) return null;
    if (t - this.lastFireT < RATE_LIMIT_S) return null;
    this.lastFireT = t;
    return {
      kind: 'gaze',
      t,
      payload: { gaze_off_seconds: offDur, head_yaw_deg: frame.head_yaw_deg },
    };
  }

  reset() {
    this.offSince = null;
    this.lastFireT = -Infinity;
  }
}

// ── Absence-window triggers ───────────────────────────────────────────────
//
// Spec: "5초 동안 한 번도 X가 안 나오면 피드백, 만약 X가 나오면 그 시점부터
// 다시 5초 측정." We track the last positive-event time and fire if (t -
// windowStart) ≥ 5s with no positive event. After firing, reset windowStart
// to now so the next 5s starts over.

export class SmileAbsenceTrigger {
  // Initialize at session start time so the very first window starts cleanly.
  private windowStart = 0;
  private lastFireT = -Infinity;

  start(t: number) {
    this.windowStart = t;
  }

  evaluate(frame: VisionFrame, t: number): TriggerEvent | null {
    if (frame.smile_intensity >= SMILE_ON_THRESHOLD) {
      // Smile present — reset the absence window, no fire.
      this.windowStart = t;
      return null;
    }
    if (t - this.windowStart < SUSTAIN_S) return null;
    if (t - this.lastFireT < RATE_LIMIT_S) return null;
    this.lastFireT = t;
    this.windowStart = t; // new 5s window starts after fire
    return { kind: 'smile_absence', t, payload: { absence_seconds: SUSTAIN_S } };
  }

  reset() {
    this.windowStart = 0;
    this.lastFireT = -Infinity;
  }
}

export class MotionAbsenceTrigger {
  private windowStart = 0;
  private lastFireT = -Infinity;

  start(t: number) {
    this.windowStart = t;
  }

  evaluate(frame: VisionFrame, t: number): TriggerEvent | null {
    // "제스처" = body or expression movement — exclude mouth/eyes per spec. We
    // OR hand_velocity_max (wrist motion) with expression_change_rate (face
    // motion excluding mouth-open). If either is alive, the user isn't frozen.
    const moving =
      frame.hand_velocity_max >= MOTION_HAND_THRESHOLD ||
      frame.expression_change_rate >= MOTION_EXPR_CHANGE_THRESHOLD ||
      frame.hand_gesture_freq > 0.1;
    if (moving) {
      this.windowStart = t;
      return null;
    }
    if (t - this.windowStart < SUSTAIN_S) return null;
    if (t - this.lastFireT < RATE_LIMIT_S) return null;
    this.lastFireT = t;
    this.windowStart = t;
    return { kind: 'motion_absence', t, payload: { absence_seconds: SUSTAIN_S } };
  }

  reset() {
    this.windowStart = 0;
    this.lastFireT = -Infinity;
  }
}

// ── Count-based trigger ───────────────────────────────────────────────────

export class FillerTrigger {
  private nextThreshold = FILLER_STEP;
  private recentFillers: string[] = [];

  noteFiller(term: string) {
    this.recentFillers.push(term);
    if (this.recentFillers.length > 10) this.recentFillers.shift();
  }

  evaluate(totalCount: number, t: number): TriggerEvent | null {
    if (totalCount < this.nextThreshold) return null;
    const fired = this.nextThreshold;
    this.nextThreshold += FILLER_STEP;
    return {
      kind: 'filler',
      t,
      payload: {
        filler_count: fired,
        recent_fillers: [...this.recentFillers],
      },
    };
  }

  reset() {
    this.nextThreshold = FILLER_STEP;
    this.recentFillers = [];
  }
}

// ── Speech rate trigger ───────────────────────────────────────────────────
//
// SpeechRecognition's final result doesn't carry per-word timestamps in any
// portable way, so we track our own: each final segment adds its word count
// at the moment we received it. WPM = (words in last 30s) * 60 / 30. 30s
// smooths out the "pause to think" dip while still catching real speed-ups.

export class SpeechRateTrigger {
  private samples: Array<{ t: number; words: number }> = [];
  private lastFireT = -Infinity;
  private readonly windowS = 30.0;

  addUtterance(text: string, t: number) {
    const words = text.trim().split(/\s+/).filter(Boolean).length;
    if (words === 0) return;
    this.samples.push({ t, words });
    // Drop samples older than windowS.
    const cutoff = t - this.windowS;
    while (this.samples.length > 0 && this.samples[0].t < cutoff) this.samples.shift();
  }

  evaluate(t: number): TriggerEvent | null {
    if (this.samples.length === 0) return null;
    const cutoff = t - this.windowS;
    const recent = this.samples.filter((s) => s.t >= cutoff);
    if (recent.length === 0) return null;
    const earliest = recent[0].t;
    const span = Math.max(t - earliest, 5.0); // floor at 5s so single utterance doesn't blow up WPM
    const totalWords = recent.reduce((sum, s) => sum + s.words, 0);
    const wpm = (totalWords / span) * 60;
    if (wpm > WPM_TOO_FAST || (wpm < WPM_TOO_SLOW && span >= 15)) {
      if (t - this.lastFireT < RATE_LIMIT_S) return null;
      this.lastFireT = t;
      return { kind: 'speech_rate', t, payload: { wpm: Math.round(wpm) } };
    }
    return null;
  }

  reset() {
    this.samples = [];
    this.lastFireT = -Infinity;
  }
}

// ── Content trigger ──────────────────────────────────────────────────────
//
// Fires whenever a final STT segment lands, subject to a min length and a
// short cooldown so we don't spam the LLM on rapid-fire utterances. The
// backend decides whether the content is worth a comment (and can return
// message=null for plain/neutral utterances).

export class ContentTrigger {
  private lastFireT = -Infinity;

  evaluate(segment: string, t: number): TriggerEvent | null {
    const text = segment.trim();
    if (text.length < CONTENT_MIN_LEN) return null;
    if (t - this.lastFireT < CONTENT_MIN_INTERVAL_S) return null;
    this.lastFireT = t;
    return { kind: 'content', t, payload: { segment: text } };
  }

  reset() {
    this.lastFireT = -Infinity;
  }
}

// ── Korean filler dictionary ──
// Same list the audio-pipeline prosody analyzer uses. Match whole tokens,
// case-insensitive, against each final STT word.
const FILLER_TERMS = new Set([
  '음', '어', '그', '그니까', '그러니까', '이제', '뭐', '약간', '막',
  '근데', '아니', '아', '에', '음...', '으...',
]);

export function countFillers(text: string): { count: number; matched: string[] } {
  const tokens = text.split(/\s+/).filter(Boolean);
  const matched: string[] = [];
  for (const tok of tokens) {
    const clean = tok.replace(/[.,!?…]+$/g, '').toLowerCase();
    if (FILLER_TERMS.has(clean)) matched.push(clean);
  }
  return { count: matched.length, matched };
}
