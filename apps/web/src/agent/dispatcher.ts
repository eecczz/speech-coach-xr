// Agent dispatcher — bundles every trigger fire with a multimodal snapshot,
// debounces the LLM call, and pushes the reply to the chat + TTS.
//
// Why a separate layer:
// - Triggers don't know anything about the user's recent speech or vision
//   averages. Bundling that context here keeps trigger evaluators pure.
// - We need a *global* cooldown on top of per-trigger cooldowns: even if four
//   triggers fire in the same second, the user should only hear one nudge.
// - Concurrent in-flight calls for the same kind get coalesced — no point
//   firing two "침묵 길어" requests when the first is still mid-flight.

import type { VisionFrame } from '../signals/compute';
import type { TriggerEvent, TriggerKind } from './triggers';

export interface MultimodalContext {
  // ── Verbal — structured into "context" and "thing to evaluate" ──
  // Old single-string `recent_transcript` is kept for back-compat (the field is
  // still wired through the request body), but new code should populate the
  // two fields below so the LLM can tell "what to coach" from "context only".
  recent_transcript: string;
  // The handful of utterances *before* current_utterance. Used as context only,
  // never as the coaching target. Order: oldest → newest.
  previous_utterances: string[];
  // The newest final STT segment. When this content trigger fires, THIS is the
  // single sentence the LLM should react to.
  current_utterance: string;
  wpm: number | null;
  filler_count_total: number;
  recent_fillers: string[];
  // Nonverbal — smoothed averages over the recent vision buffer
  gaze_fixation_ratio_avg: number | null;
  head_yaw_deg_avg: number | null;
  smile_intensity_avg: number | null;
  expression_change_rate_avg: number | null;
  hand_velocity_max_avg: number | null;
  posture_sway_avg: number | null;
  // Audio
  current_silence_seconds: number | null;
  // Session position
  session_elapsed_s: number;
  // Self-history (anti-repetition) — what *we* (the agent) just said. Lets the
  // backend prompt the model with "don't say something like these again."
  recent_agent_messages: string[];
}

export interface AgentDispatcherCtx {
  scenario: string;
  situation: string;
  focusGoals: string[];
  getContext: (event: TriggerEvent) => MultimodalContext;
  onFeedback: (message: string, t: number, kind: TriggerKind, tone: string | null) => void;
}

// Backend response shape (mirrors AgentFeedbackResponse in coach/app/agent.py).
interface AgentFeedbackResponse {
  message?: string | null;
  tone?: 'praise' | 'nudge' | 'critique' | null;
}

// Global cooldown between *any* two feedbacks. Keeps the coach from talking
// over itself when several triggers fire in a tight cluster.
const GLOBAL_COOLDOWN_S = 2.0;

// How many of the agent's own recent messages to expose for anti-repetition.
const SELF_HISTORY_LEN = 5;

export class AgentDispatcher {
  // Per-trigger-kind in-flight tracking: while the model is still answering
  // a silence event, don't fire a second silence request.
  private inflight = new Set<TriggerKind>();
  private lastEmitT = -Infinity;
  // What we (the agent) have actually said to the user in this session,
  // oldest → newest. Exposed in the multimodal context so the prompt can
  // tell the model not to repeat itself.
  private selfHistory: string[] = [];
  private ctx: AgentDispatcherCtx;

  constructor(ctx: AgentDispatcherCtx) {
    this.ctx = ctx;
  }

  recentMessages(): string[] {
    return this.selfHistory.slice(-SELF_HISTORY_LEN);
  }

  // Returns true if the event was accepted for dispatch. Callers can use the
  // return value to log, but the actual LLM call runs in the background.
  submit(event: TriggerEvent): boolean {
    if (this.inflight.has(event.kind)) return false;
    if (event.t - this.lastEmitT < GLOBAL_COOLDOWN_S) return false;
    this.inflight.add(event.kind);
    void this.callBackend(event);
    return true;
  }

  private async callBackend(event: TriggerEvent): Promise<void> {
    try {
      const context = this.ctx.getContext(event);
      const body = {
        kind: event.kind,
        scenario: this.ctx.scenario,
        situation: this.ctx.situation,
        focus_goals: this.ctx.focusGoals,
        payload: event.payload,
        context,
        recent_transcript: context.recent_transcript, // back-compat field
      };
      const resp = await fetch('/api/coach/agent-feedback', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      if (!resp.ok) {
        console.warn(`[agent] dispatcher: ${event.kind} -> HTTP ${resp.status}`);
        return;
      }
      const json = (await resp.json()) as AgentFeedbackResponse;
      const msg = json.message?.trim();
      if (!msg) return; // model chose to stay silent — that's fine
      // Only update the global cooldown when we actually emit something.
      this.lastEmitT = event.t;
      this.selfHistory.push(msg);
      if (this.selfHistory.length > 20) this.selfHistory.shift();
      this.ctx.onFeedback(msg, event.t, event.kind, json.tone ?? null);
    } catch (err) {
      console.warn('[agent] dispatcher: feedback call failed', err);
    } finally {
      this.inflight.delete(event.kind);
    }
  }

  reset() {
    this.inflight.clear();
    this.lastEmitT = -Infinity;
    this.selfHistory = [];
  }
}

// ── Vision rolling buffer for context averages ──
//
// We keep a small ring of the most recent frames so getContext() can hand the
// model smoothed numbers instead of the single-frame value at trigger time.
// 3s of history at 5fps = 15 samples — enough to dampen noise without lagging.

const CONTEXT_WINDOW_S = 3.0;

export class VisionContextBuffer {
  private samples: VisionFrame[] = [];

  add(frame: VisionFrame) {
    this.samples.push(frame);
    const cutoff = frame.t - CONTEXT_WINDOW_S;
    while (this.samples.length > 0 && this.samples[0].t < cutoff) this.samples.shift();
  }

  // Returns averaged signal values, or null when we don't have any samples yet.
  snapshot(): {
    gaze_fixation_ratio_avg: number | null;
    head_yaw_deg_avg: number | null;
    smile_intensity_avg: number | null;
    expression_change_rate_avg: number | null;
    hand_velocity_max_avg: number | null;
    posture_sway_avg: number | null;
  } {
    if (this.samples.length === 0) {
      return {
        gaze_fixation_ratio_avg: null,
        head_yaw_deg_avg: null,
        smile_intensity_avg: null,
        expression_change_rate_avg: null,
        hand_velocity_max_avg: null,
        posture_sway_avg: null,
      };
    }
    const n = this.samples.length;
    let gaze = 0, yaw = 0, smile = 0, exprChg = 0, handV = 0, sway = 0;
    for (const f of this.samples) {
      gaze += f.gaze_fixation_ratio;
      yaw += f.head_yaw_deg;
      smile += f.smile_intensity;
      exprChg += f.expression_change_rate;
      handV += f.hand_velocity_max;
      sway += f.posture_sway;
    }
    return {
      gaze_fixation_ratio_avg: gaze / n,
      head_yaw_deg_avg: yaw / n,
      smile_intensity_avg: smile / n,
      expression_change_rate_avg: exprChg / n,
      hand_velocity_max_avg: handV / n,
      posture_sway_avg: sway / n,
    };
  }

  reset() {
    this.samples = [];
  }
}

// ── Recent transcript buffer ──
//
// Keep the last ~30s of final STT segments so every dispatch can show the
// model what the user has actually been saying — not just the segment that
// happened to land at trigger time.

const TRANSCRIPT_WINDOW_S = 30.0;

// How many of the most recent utterances to expose as "previous context" to
// the LLM. Few enough that token cost stays tiny, enough to cover a topic.
const PREVIOUS_UTTERANCE_COUNT = 5;

export class TranscriptBuffer {
  private entries: Array<{ t: number; text: string }> = [];

  add(text: string, t: number) {
    const clean = text.trim();
    if (!clean) return;
    this.entries.push({ t, text: clean });
    const cutoff = t - TRANSCRIPT_WINDOW_S;
    while (this.entries.length > 0 && this.entries[0].t < cutoff) this.entries.shift();
  }

  // Returns the joined transcript or '' if empty. Used for back-compat and for
  // non-content triggers (silence/gaze/etc.) where the LLM only needs a flat
  // "what has the user been saying" peek.
  recent(): string {
    return this.entries.map((e) => e.text).join(' ').trim();
  }

  // Returns the last `n` utterances *excluding* the most recent one. The most
  // recent goes into `current_utterance` separately so the LLM knows that's
  // the coaching target.
  previousUtterances(n = PREVIOUS_UTTERANCE_COUNT): string[] {
    if (this.entries.length <= 1) return [];
    const allButLast = this.entries.slice(0, -1);
    return allButLast.slice(-n).map((e) => e.text);
  }

  currentUtterance(): string {
    if (this.entries.length === 0) return '';
    return this.entries[this.entries.length - 1].text;
  }

  reset() {
    this.entries = [];
  }
}
