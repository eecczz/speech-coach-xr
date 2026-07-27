// The interviewer's "brain": decides what to ask next.
//
// Stage 2 ships a deterministic script brain (신입/공통 인성 면접 1종) so the whole
// XR interview loop runs today with no backend. The InterviewBrain interface is
// async and history-aware, so a later stage can drop in an LLM-backed brain
// (calling the coach service to generate follow-up / pressure questions) without
// touching the session state machine.

export type QuestionKind = 'intro' | 'base' | 'followup' | 'pressure' | 'closing';

export interface InterviewTurn {
  question: string;
  kind: QuestionKind;
}

export interface QAExchange {
  question: string;
  answer: string;
  kind: QuestionKind;
}

export interface InterviewBrain {
  /** Opening line the interviewer says before the first real question. */
  intro(): string;
  /** First question. */
  first(): InterviewTurn;
  /** Next question given the history so far, or null to move to closing. */
  next(history: QAExchange[]): Promise<InterviewTurn | null>;
  /** Closing line spoken when the interview ends. */
  closing(): string;
}

// ── 신입/공통 인성 면접 시드 (1차 시연 범위) ──
const SEED_QUESTIONS: InterviewTurn[] = [
  { kind: 'base', question: '먼저 간단히 자기소개 부탁드립니다.' },
  { kind: 'base', question: '우리 회사에 지원하신 동기가 무엇인가요?' },
  { kind: 'base', question: '본인의 가장 큰 강점과, 보완하고 싶은 약점은 무엇인가요?' },
  { kind: 'base', question: '팀으로 일하며 갈등을 겪었던 경험과, 그때 어떻게 대처했는지 말씀해 주세요.' },
  { kind: 'base', question: '실패했거나 크게 아쉬웠던 경험에서 무엇을 배웠나요?' },
  { kind: 'base', question: '입사 후 이루고 싶은 목표나 포부가 있다면 말씀해 주세요.' },
];

// Light-touch follow-ups injected after some base answers to mimic a real panel's
// probing. Kept generic here; the LLM brain will make these answer-specific.
const GENERIC_FOLLOWUPS = [
  '방금 말씀하신 부분을 조금 더 구체적인 사례로 설명해 주시겠어요?',
  '그 상황에서 본인이 맡은 역할과 실제로 한 행동은 무엇이었나요?',
  '그 결정을 다시 한다면, 무엇을 다르게 하시겠어요?',
];

export interface ScriptBrainOptions {
  /** How many base questions to ask before closing (1차 시연은 짧게). */
  maxBaseQuestions?: number;
  /** Ask a generic follow-up after this many base questions (0 = never). */
  followupEvery?: number;
}

export function createScriptBrain(opts: ScriptBrainOptions = {}): InterviewBrain {
  const maxBase = Math.min(opts.maxBaseQuestions ?? 4, SEED_QUESTIONS.length);
  const followupEvery = opts.followupEvery ?? 2;

  let baseAsked = 0;
  let followupPtr = 0;

  return {
    intro() {
      return '안녕하세요. 지금부터 면접을 시작하겠습니다. 긴장 푸시고, 편하게 답변해 주세요.';
    },
    first() {
      baseAsked = 1;
      return SEED_QUESTIONS[0];
    },
    async next(history) {
      const last = history[history.length - 1];
      // After a base answer, occasionally probe with one follow-up.
      if (
        last?.kind === 'base' &&
        followupEvery > 0 &&
        baseAsked % followupEvery === 0 &&
        followupPtr < GENERIC_FOLLOWUPS.length &&
        baseAsked < maxBase
      ) {
        const q = GENERIC_FOLLOWUPS[followupPtr++];
        return { kind: 'followup', question: q };
      }
      if (baseAsked >= maxBase) return null; // → closing
      const nextTurn = SEED_QUESTIONS[baseAsked];
      baseAsked += 1;
      return nextTurn ?? null;
    },
    closing() {
      return '수고하셨습니다. 이상으로 면접을 마치겠습니다. 곧 결과 리포트를 확인하실 수 있습니다.';
    },
  };
}
