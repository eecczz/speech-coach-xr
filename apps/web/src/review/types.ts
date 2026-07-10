// Mirror of packages/schema/report.py — TypeScript types for the review UI.
// Kept narrow (we only consume fields the UI renders).

export type QualityLevel =
  | 'brilliant'
  | 'excellent'
  | 'good'
  | 'inaccuracy'
  | 'mistake'
  | 'blunder';

export interface AxisAccuracy {
  axis: string;
  score: number;
  available: boolean;
  note?: string | null;
}

export interface QualityBuckets {
  brilliant: number;
  excellent: number;
  good: number;
  inaccuracy: number;
  mistake: number;
  blunder: number;
}

export interface TimelineSample {
  t: number;
  score: number;
}

export interface AnnotatedMoment {
  t: number;
  axis: string;
  quality: QualityLevel;
  title: string;
  impact: number;
  coach_comment?: string | null;
  duration_s?: number | null;
}

export interface Finding {
  text: string;
  evidence_t?: number[];
  suggestion?: string;
}

export interface Rubric {
  logic: number;
  delivery: number;
  gaze: number;
  posture: number;
  expression: number;
}

export interface EvidenceClip {
  t_start: number;
  t_end: number;
  reason: string;
}

export interface TrainingPrescription {
  title: string;
  addresses: string;
  steps: string[];
}

export interface SubtitleWord {
  t_start: number;
  t_end: number;
  word: string;
}

export interface SubtitleSegment {
  t_start: number;
  t_end: number;
  text: string;
  words: SubtitleWord[];
}

export interface TranscriptCheck {
  phrase: string;
  suggestion?: string | null;
  reason: string;
  t_start?: number | null;
  t_end?: number | null;
}

export interface ComprehensiveReport {
  session_id: string;
  rubric: Rubric;
  overall_summary: string;
  top_priorities: Finding[];
  strengths: Finding[];
  improvements: Finding[];
  training_prescriptions: TrainingPrescription[];
  evidence_clips: EvidenceClip[];
  accuracy_overall: number;
  accuracy_per_axis: AxisAccuracy[];
  quality_buckets: QualityBuckets;
  annotated_moments: AnnotatedMoment[];
  score_timeline: TimelineSample[];
  subtitle_segments?: SubtitleSegment[];
  transcript_checks?: TranscriptCheck[];
}
