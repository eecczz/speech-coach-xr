import { z } from 'zod';

// Live HUD signals (output of L1 rule engine, sent back to the client per window).
export const LiveHudSignalSchema = z.object({
  level: z.enum(['info', 'warn', 'critical']),
  text: z.string(), // "말이 빠릅니다 (WPM 240)"
  kind: z.string(), // rule id like "wpm_high"
});
export type LiveHudSignal = z.infer<typeof LiveHudSignalSchema>;

export const LiveHudResponseSchema = z.object({
  window_t_start: z.number(),
  window_t_end: z.number(),
  signals: z.array(LiveHudSignalSchema),
});
export type LiveHudResponse = z.infer<typeof LiveHudResponseSchema>;

// Final comprehensive report (output of L3 — Claude Sonnet 4.6 structured response).
export const RubricSchema = z.object({
  logic: z.number().min(0).max(5),       // 논리/구조
  delivery: z.number().min(0).max(5),    // 전달력 (말 속도, filler, pause)
  gaze: z.number().min(0).max(5),
  posture: z.number().min(0).max(5),
  expression: z.number().min(0).max(5),
});
export type Rubric = z.infer<typeof RubricSchema>;

export const FindingSchema = z.object({
  text: z.string(),
  evidence_t: z.tuple([z.number(), z.number()]).optional(), // [t_start, t_end] in seconds
  suggestion: z.string().optional(),
});
export type Finding = z.infer<typeof FindingSchema>;

export const EvidenceClipSchema = z.object({
  t_start: z.number().min(0),
  t_end: z.number().min(0),
  reason: z.string(),
});
export type EvidenceClip = z.infer<typeof EvidenceClipSchema>;

export const TrainingPrescriptionSchema = z.object({
  title: z.string(),
  addresses: z.string(),
  steps: z.array(z.string()),
});
export type TrainingPrescription = z.infer<typeof TrainingPrescriptionSchema>;

export const TranscriptCheckSchema = z.object({
  phrase: z.string(),
  suggestion: z.string().nullable().optional(),
  reason: z.string(),
  t_start: z.number().nullable().optional(),
  t_end: z.number().nullable().optional(),
});
export type TranscriptCheck = z.infer<typeof TranscriptCheckSchema>;

export const ComprehensiveReportSchema = z.object({
  session_id: z.string(),
  rubric: RubricSchema,
  overall_summary: z.string(),
  top_priorities: z.array(FindingSchema).default([]),
  strengths: z.array(FindingSchema),
  improvements: z.array(FindingSchema),
  training_prescriptions: z.array(TrainingPrescriptionSchema).default([]),
  evidence_clips: z.array(EvidenceClipSchema),
  transcript_checks: z.array(TranscriptCheckSchema).default([]),
});
export type ComprehensiveReport = z.infer<typeof ComprehensiveReportSchema>;
