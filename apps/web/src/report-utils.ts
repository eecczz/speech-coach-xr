import type { AxisAccuracy, ComprehensiveReport } from './review/types';
import type { CompletedSession } from './session-store';

export function formatTimeShort(seconds: number): string {
  const mins = Math.floor(seconds / 60);
  const secs = Math.round(seconds % 60);
  return mins > 0 ? `${mins}:${String(secs).padStart(2, '0')}` : `${secs}s`;
}

export function getAxisScore(report: ComprehensiveReport, axis: string): number | null {
  const item = report.accuracy_per_axis.find((entry) => entry.axis === axis && entry.available);
  return item ? item.score : null;
}

export function average(values: Array<number | null | undefined>): number | null {
  const valid = values.filter((value): value is number => typeof value === 'number' && Number.isFinite(value));
  if (valid.length === 0) return null;
  return valid.reduce((sum, value) => sum + value, 0) / valid.length;
}

export function getDisplayAxisScores(report: ComprehensiveReport): {
  verbal: number | null;
  prosody: number | null;
  nonverbal: number | null;
} {
  return {
    verbal: average([getAxisScore(report, 'logic')]),
    prosody: average([getAxisScore(report, 'delivery')]),
    nonverbal: average([
      getAxisScore(report, 'gaze'),
      getAxisScore(report, 'posture'),
      getAxisScore(report, 'expression'),
      getAxisScore(report, 'gesture'),
    ]),
  };
}

export function estimateSessionDuration(report: ComprehensiveReport): number {
  const timelineMax = Math.max(0, ...report.score_timeline.map((sample) => sample.t));
  const momentMax = Math.max(
    0,
    ...report.annotated_moments.map((moment) => moment.t + (moment.duration_s ?? 0)),
  );
  const clipMax = Math.max(0, ...report.evidence_clips.map((clip) => clip.t_end));
  return Math.max(timelineMax, momentMax, clipMax);
}

export function deriveNextTarget(currentScore: number, previousScore?: number | null): number {
  if (typeof previousScore === 'number' && Number.isFinite(previousScore)) {
    return Math.min(100, Math.max(currentScore, Math.round(previousScore + 5)));
  }
  return Math.min(100, Math.max(80, Math.round(currentScore + 5)));
}

export function findPreviousSession(
  sessions: CompletedSession[],
  current: CompletedSession,
): CompletedSession | null {
  const siblings = sessions
    .filter((session) => session.sessionId !== current.sessionId && session.project === current.project)
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  return siblings[0] ?? null;
}

export function findAxis(report: ComprehensiveReport, axis: string): AxisAccuracy | null {
  return report.accuracy_per_axis.find((entry) => entry.axis === axis) ?? null;
}

export function getTopMoments(report: ComprehensiveReport, limit = 3) {
  return [...report.annotated_moments]
    .sort((a, b) => a.impact - b.impact)
    .slice(0, limit);
}
