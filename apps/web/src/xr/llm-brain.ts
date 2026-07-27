// LLM-backed interviewer brain. Same InterviewBrain contract as the script brain,
// but `next()` asks the coach service to generate an answer-aware follow-up /
// pressure question. Drop-in: swap createScriptBrain → createLlmBrain.
//
// The coach endpoints are reached through the vite proxy: /api/coach/* → coach:8002
// (see vite.config.ts). Intro/first/closing stay local for a natural, instant start;
// the LLM decides everything from the 2nd question on.

import type { InterviewBrain, InterviewTurn, QAExchange, QuestionKind } from './interview-brain';

export interface LlmBrainOptions {
  jobRole?: string;
  situation?: string;
  difficulty?: '쉬움' | '보통' | '어려움';
  maxQuestions?: number;
  /** Base path for the coach service (proxied). */
  apiBase?: string;
}

interface NextResponse {
  question: string | null;
  kind: QuestionKind;
  done: boolean;
}

export interface InterviewReport {
  overall_summary: string;
  strengths: string[];
  improvements: string[];
  per_question: { question: string; comment: string; star: boolean }[];
}

export function createLlmBrain(opts: LlmBrainOptions = {}): InterviewBrain {
  const config = {
    job_role: opts.jobRole ?? '신입 공통 인성',
    situation: opts.situation ?? '',
    difficulty: opts.difficulty ?? '보통',
  };
  const maxQuestions = opts.maxQuestions ?? 5;
  const apiBase = opts.apiBase ?? '/api/coach';

  return {
    intro() {
      return '안녕하세요. 지금부터 면접을 시작하겠습니다. 긴장 푸시고, 편하게 답변해 주세요.';
    },
    first(): InterviewTurn {
      // A fixed, friendly opener; the LLM takes over from the follow-up on.
      return { kind: 'base', question: '먼저 간단히 자기소개 부탁드립니다.' };
    },
    async next(history: QAExchange[]): Promise<InterviewTurn | null> {
      try {
        const res = await fetch(`${apiBase}/interview/next`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            config,
            history,
            asked_count: history.length,
            max_questions: maxQuestions,
          }),
        });
        if (!res.ok) throw new Error(`interview/next ${res.status}`);
        const data = (await res.json()) as NextResponse;
        if (data.done || !data.question) return null;
        return { question: data.question, kind: data.kind ?? 'followup' };
      } catch (err) {
        console.error('[llm-brain] next() failed, ending interview:', err);
        return null; // graceful close if the backend is unreachable
      }
    },
    closing() {
      return '수고하셨습니다. 이상으로 면접을 마치겠습니다. 곧 결과 리포트를 확인하실 수 있습니다.';
    },
  };
}

/** Fetch the text-only answer-structure report for a finished interview. */
export async function fetchInterviewReport(
  history: QAExchange[],
  opts: LlmBrainOptions = {},
): Promise<InterviewReport | null> {
  const apiBase = opts.apiBase ?? '/api/coach';
  try {
    const res = await fetch(`${apiBase}/interview/report`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        config: {
          job_role: opts.jobRole ?? '신입 공통 인성',
          situation: opts.situation ?? '',
          difficulty: opts.difficulty ?? '보통',
        },
        history,
      }),
    });
    if (!res.ok) throw new Error(`interview/report ${res.status}`);
    return (await res.json()) as InterviewReport;
  } catch (err) {
    console.error('[llm-brain] report failed:', err);
    return null;
  }
}
