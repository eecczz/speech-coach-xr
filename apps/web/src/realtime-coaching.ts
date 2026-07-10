import type { VisionFrame } from './signals/compute';
import type { LiveHudResponse, ProsodyFrame } from './ws/client';

export type RealtimeFocusKey =
  | 'wpm'
  | 'filler'
  | 'silence'
  | 'verbal'
  | 'prosody'
  | 'nonverbal'
  | 'gaze'
  | 'posture'
  | 'expression'
  | 'gesture';

export interface RealtimeCoachingCandidate {
  key: string;
  focusKey: RealtimeFocusKey;
  axis: string;
  level: 'info' | 'warn' | 'critical';
  title: string;
  message: string;
  t: number;
  duration_s: number;
  impact: number;
  metric?: string;
  value?: number | null;
  window_seconds: number;
  cooldown_s: number;
  metadata?: Record<string, number | string | boolean | null>;
}

const WINDOW_SECONDS = 5;
const VISION_SAMPLE_LIMIT_SECONDS = 8;
const PROSODY_SAMPLE_LIMIT_SECONDS = 8;

function recentVisionSamples(samples: VisionFrame[], sessionT: number): VisionFrame[] {
  const start = Math.max(0, sessionT - WINDOW_SECONDS);
  return samples.filter((sample) => sample.t >= start && sample.t <= sessionT);
}

function recentProsodySamples(samples: ProsodyFrame[], sessionT: number): ProsodyFrame[] {
  const start = Math.max(0, sessionT - WINDOW_SECONDS);
  return samples.filter((sample) => sample.t_end >= start && sample.t_end <= sessionT);
}

function average(values: number[]): number | null {
  const finite = values.filter(Number.isFinite);
  if (finite.length === 0) return null;
  return finite.reduce((sum, value) => sum + value, 0) / finite.length;
}

function maxAbs(values: number[]): number | null {
  const finite = values.filter(Number.isFinite).map((value) => Math.abs(value));
  if (finite.length === 0) return null;
  return Math.max(...finite);
}

function firstNumber(text: string): number | null {
  const match = text.match(/(\d+(?:\.\d+)?)/);
  return match ? Number(match[1]) : null;
}

export function selectCoachingCandidate(
  candidates: RealtimeCoachingCandidate[],
  focusEnabled: (key: RealtimeFocusKey) => boolean,
): RealtimeCoachingCandidate | null {
  const score = (candidate: RealtimeCoachingCandidate) => {
    const levelScore = candidate.level === 'critical' ? 300 : candidate.level === 'warn' ? 200 : 100;
    return levelScore + Math.abs(candidate.impact);
  };
  return candidates
    .filter((candidate) => focusEnabled(candidate.focusKey))
    .sort((a, b) => score(b) - score(a))[0] ?? null;
}

export class RealtimeCoachingEngine {
  private visionSamples: VisionFrame[] = [];
  private prosodySamples: ProsodyFrame[] = [];

  reset(): void {
    this.visionSamples = [];
    this.prosodySamples = [];
  }

  addVision(frame: VisionFrame): void {
    this.visionSamples.push(frame);
    const minT = frame.t - VISION_SAMPLE_LIMIT_SECONDS;
    this.visionSamples = this.visionSamples.filter((sample) => sample.t >= minT);
  }

  addProsody(frame: ProsodyFrame): void {
    this.prosodySamples.push(frame);
    const minT = frame.t_end - PROSODY_SAMPLE_LIMIT_SECONDS;
    this.prosodySamples = this.prosodySamples.filter((sample) => sample.t_end >= minT);
  }

  fromHud(payload: LiveHudResponse): RealtimeCoachingCandidate[] {
    const candidates: RealtimeCoachingCandidate[] = [];
    const byKind = new Map(payload.signals.map((signal) => [signal.kind, signal]));
    const t = payload.window_t_end;
    const windowSeconds = Math.max(1, payload.window_t_end - payload.window_t_start);
    const wpmSignal = byKind.get('wpm_very_high') ?? byKind.get('wpm_high');

    if (wpmSignal) {
      const wpm = firstNumber(wpmSignal.text);
      const fast = typeof wpm === 'number' && wpm >= 180;
      candidates.push({
        key: fast ? 'wpm_very_high' : 'wpm_high',
        focusKey: 'wpm',
        axis: 'delivery',
        level: wpmSignal.level,
        title: fast ? '말 속도 빠름' : '말 속도 상승',
        message: fast
          ? '말 속도가 빨라지고 있어요. 한 문장 끝에서 반 박자 쉬고 이어가세요.'
          : '말 속도가 조금 빠릅니다. 핵심 단어 앞에서 속도를 낮춰보세요.',
        t,
        duration_s: windowSeconds,
        impact: wpmSignal.level === 'critical' ? -10 : -6,
        metric: 'wpm',
        value: wpm,
        window_seconds: windowSeconds,
        cooldown_s: 10,
        metadata: { wpm },
      });
    }

    const fillerSignal = byKind.get('filler_burst');
    if (fillerSignal) {
      const count = firstNumber(fillerSignal.text);
      candidates.push({
        key: 'filler_burst',
        focusKey: 'filler',
        axis: 'delivery',
        level: fillerSignal.level,
        title: '필러 표현 반복',
        message: '필러 표현이 반복됩니다. 다음 문장은 바로 말하지 말고 짧게 숨을 고른 뒤 시작하세요.',
        t,
        duration_s: windowSeconds,
        impact: fillerSignal.level === 'critical' ? -9 : -5,
        metric: 'filler_count',
        value: count,
        window_seconds: windowSeconds,
        cooldown_s: 12,
        metadata: { filler_count: count },
      });
    }

    return candidates;
  }

  evaluateVision(sessionT: number): RealtimeCoachingCandidate[] {
    const samples = recentVisionSamples(this.visionSamples, sessionT);
    if (samples.length < 4) return [];

    const candidates: RealtimeCoachingCandidate[] = [];
    const gazeRatio = average(samples.map((sample) => sample.gaze_fixation_ratio));
    const postureSway = average(samples.map((sample) => sample.posture_sway));
    const headRoll = maxAbs(samples.map((sample) => sample.head_roll_deg));
    const expressionDiversity = average(samples.map((sample) => sample.expression_diversity));
    const handGestureFreq = average(samples.map((sample) => sample.hand_gesture_freq));
    const windowStart = samples[0]?.t ?? Math.max(0, sessionT - WINDOW_SECONDS);
    const duration = Math.max(1, sessionT - windowStart);

    if (sessionT > 3 && gazeRatio != null && gazeRatio < 0.42) {
      candidates.push({
        key: 'gaze_away',
        focusKey: 'gaze',
        axis: 'gaze',
        level: gazeRatio < 0.28 ? 'critical' : 'warn',
        title: '시선 이탈',
        message: '시선이 정면에서 벗어나고 있어요. 다음 문장은 카메라 렌즈나 청중 중앙을 보고 말해보세요.',
        t: sessionT,
        duration_s: duration,
        impact: gazeRatio < 0.28 ? -10 : -6,
        metric: 'gaze_fixation_ratio',
        value: Number(gazeRatio.toFixed(3)),
        window_seconds: WINDOW_SECONDS,
        cooldown_s: 10,
        metadata: { gaze_fixation_ratio: Number(gazeRatio.toFixed(3)) },
      });
    }

    if (sessionT > 3 && ((postureSway != null && postureSway > 0.08) || (headRoll != null && headRoll > 10))) {
      const postureValue = Math.max(postureSway ?? 0, (headRoll ?? 0) / 100);
      candidates.push({
        key: 'posture_unstable',
        focusKey: 'posture',
        axis: 'posture',
        level: postureValue > 0.16 ? 'critical' : 'warn',
        title: '자세 흔들림',
        message: '자세가 기울어졌습니다. 목을 살짝 세우고 어깨 선을 수평으로 맞춰보세요.',
        t: sessionT,
        duration_s: duration,
        impact: postureValue > 0.16 ? -9 : -5,
        metric: 'posture_sway',
        value: Number((postureSway ?? 0).toFixed(3)),
        window_seconds: WINDOW_SECONDS,
        cooldown_s: 10,
        metadata: {
          posture_sway: Number((postureSway ?? 0).toFixed(3)),
          head_roll_deg: Number((headRoll ?? 0).toFixed(1)),
        },
      });
    }

    if (sessionT > 6 && expressionDiversity != null && expressionDiversity < 0.08) {
      candidates.push({
        key: 'expression_low',
        focusKey: 'expression',
        axis: 'expression',
        level: 'warn',
        title: '표정 변화 부족',
        message: '표정 변화가 적습니다. 핵심 문장을 말할 때 눈썹과 입꼬리를 조금 더 살려보세요.',
        t: sessionT,
        duration_s: duration,
        impact: -4,
        metric: 'expression_diversity',
        value: Number(expressionDiversity.toFixed(3)),
        window_seconds: WINDOW_SECONDS,
        cooldown_s: 14,
        metadata: { expression_diversity: Number(expressionDiversity.toFixed(3)) },
      });
    }

    if (sessionT > 8 && handGestureFreq != null && handGestureFreq < 0.02) {
      candidates.push({
        key: 'gesture_low',
        focusKey: 'gesture',
        axis: 'gesture',
        level: 'warn',
        title: '손동작 부족',
        message: '손동작이 거의 없습니다. 중요한 단어 하나에만 작게 손짓을 붙여보세요.',
        t: sessionT,
        duration_s: duration,
        impact: -4,
        metric: 'hand_gesture_freq',
        value: Number(handGestureFreq.toFixed(3)),
        window_seconds: WINDOW_SECONDS,
        cooldown_s: 14,
        metadata: { hand_gesture_freq: Number(handGestureFreq.toFixed(3)) },
      });
    }

    return candidates;
  }

  evaluateProsody(sessionT: number): RealtimeCoachingCandidate[] {
    const samples = recentProsodySamples(this.prosodySamples, sessionT);
    const maxSilence = samples.length > 0 ? Math.max(...samples.map((sample) => sample.silence_seconds ?? 0)) : 0;
    if (maxSilence < 2) return [];

    const level = maxSilence >= 4 ? 'critical' : 'warn';
    return [
      {
        key: 'silence_long',
        focusKey: 'silence',
        axis: 'delivery',
        level,
        title: '침묵 구간 길어짐',
        message: maxSilence >= 4
          ? '침묵이 길어졌어요. 다음 문장은 짧은 연결어로 다시 시작해보세요.'
          : '잠깐 멈춤이 길어지고 있어요. 결론부터 짧게 이어가세요.',
        t: sessionT,
        duration_s: maxSilence,
        impact: maxSilence >= 4 ? -9 : -4,
        metric: 'silence_seconds',
        value: Number(maxSilence.toFixed(1)),
        window_seconds: WINDOW_SECONDS,
        cooldown_s: 10,
        metadata: { silence_seconds: Number(maxSilence.toFixed(1)) },
      },
    ];
  }
}
