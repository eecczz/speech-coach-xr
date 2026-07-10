import { z } from 'zod';

// Semantic events (L2) — derived from raw signals by the aggregator before sending to
// the comprehensive coach. Each event names a thing that happened with timestamps so
// the LLM can cite specific evidence (impossible to hallucinate around).

export const EventKindSchema = z.enum([
  'wpm_spike',
  'wpm_slow',
  'long_pause',
  'filler_burst',
  'gaze_lapse',
  'gaze_downward',
  'posture_sway',
  'head_tilt_sustained',
  'head_nodding',
  'chin_on_hand',
  'expression_flat',
  'hand_freeze',
  'gesture_excessive',
  'monotone',
  'sentence_trailing',
  'slurred_articulation',
  'face_touch',
  'hand_fidget',
  'motion_hesitation',
  'low_smile',
  'gaze_wander',
  'silence_long',
  'voice_flat',
  'aggregate',
]);
export type EventKind = z.infer<typeof EventKindSchema>;

export const SemanticEventSchema = z.object({
  kind: EventKindSchema,
  t_start: z.number().min(0),
  t_end: z.number().min(0),
  // Human-readable description in Korean (LLM-ready).
  text: z.string(),
  // Optional transcript snippet from this moment for grounding.
  transcript_snippet: z.string().optional(),
  // Numeric payload for the LLM to reference exact values.
  metrics: z.record(z.string(), z.number()).optional(),
});
export type SemanticEvent = z.infer<typeof SemanticEventSchema>;

// Bundle sent to coach /comprehensive at session end.
export const SessionBundleSchema = z.object({
  session_id: z.string(),
  scenario: z.string().default('presentation'),
  focus_goals: z.array(z.string()).default([]),
  duration_s: z.number().min(0),
  full_transcript: z.string(),
  // Word-level transcript for evidence_clip precision.
  words: z.array(
    z.object({
      t_start: z.number(),
      t_end: z.number(),
      word: z.string(),
    }),
  ),
  // L2 semantic events derived by the aggregator.
  events: z.array(SemanticEventSchema),
  // Session-wide aggregates the LLM can use as priors.
  aggregates: z.object({
    avg_wpm: z.number(),
    filler_per_minute: z.number(),
    gaze_central_fraction: z.number(), // fraction of session in central cone
    posture_sway_mean: z.number(),
    expression_diversity_mean: z.number(),
  }),
});
export type SessionBundle = z.infer<typeof SessionBundleSchema>;
