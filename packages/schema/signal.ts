import { z } from 'zod';

// One 5fps vision-signal frame emitted by the browser client.
// All values relative to the recording start (seconds).
export const VisionFrameSchema = z.object({
  t: z.number().min(0),
  gaze_fixation_ratio: z.number().min(0).max(1),
  posture_sway: z.number().min(0),
  shoulder_tilt: z.number(),
  expression_diversity: z.number().min(0),
  hand_gesture_freq: z.number().min(0),
  head_pitch_deg: z.number().default(0),
  head_yaw_deg: z.number().default(0),
  head_roll_deg: z.number().default(0),
  chin_on_hand: z.boolean().default(false),
  mouth_open: z.number().min(0).max(1).default(0),
});
export type VisionFrame = z.infer<typeof VisionFrameSchema>;

// One STT segment from the live audio pipeline.
export const SttSegmentSchema = z.object({
  t_start: z.number().min(0),
  t_end: z.number().min(0),
  text: z.string(),
  words: z
    .array(
      z.object({
        t_start: z.number(),
        t_end: z.number(),
        word: z.string(),
        prob: z.number().optional(),
      }),
    )
    .default([]),
  is_final: z.boolean().default(true),
});
export type SttSegment = z.infer<typeof SttSegmentSchema>;

// Per-window prosody aggregate from the audio pipeline OR a synthetic frame the
// browser pushes for silence-only detection while STT is not yet running.
export const ProsodyFrameSchema = z.object({
  t_start: z.number().min(0),
  t_end: z.number().min(0),
  wpm: z.number().min(0).default(0),
  word_count: z.number().int().min(0).default(0),
  filler_count: z.number().int().min(0).default(0),
  filler_terms: z.array(z.string()).default([]),
  pause_seconds: z.number().min(0).default(0),
  silence_seconds: z.number().min(0).default(0),
  f0_variance: z.number().min(0).optional(),
  rms_mean: z.number().min(0).optional(),
});
export type ProsodyFrame = z.infer<typeof ProsodyFrameSchema>;

// Aggregated 5-second window (output of aggregator before forwarding to coach /live).
export const SignalWindowSchema = z.object({
  session_id: z.string(),
  t_start: z.number().min(0),
  t_end: z.number().min(0),
  vision: VisionFrameSchema.partial().nullable(), // mean of frames in window
  prosody: ProsodyFrameSchema.nullable(),
  transcript: z.string(), // concatenated STT text within the window
});
export type SignalWindow = z.infer<typeof SignalWindowSchema>;
