"""Interview brain — the AI interviewer's next-question generator + a light,
text-only end-of-interview report.

This is the XR interview counterpart to agent.py. Where agent.py reacts to a
practicing speaker with tiny nudges, here the model *plays the interviewer*:
given the running Q&A history it produces the next question — a probing
follow-up, a pressure question, or the next base question — scaled to the
requested difficulty. The end-of-interview report evaluates answer structure
(직접성·근거·사례/STAR·결론) from the transcript alone; multimodal (gaze/posture)
scoring is layered on later once the XR signal pipeline (Stage 3) feeds in.

Provider routing mirrors agent.py: jeonbuk (OpenAI-compatible) | gemini | claude.
Safety: education-only feedback, never a 합격/불합격 verdict, and no invented facts.
"""

from __future__ import annotations

import os
from typing import List, Literal, Optional

from pydantic import BaseModel, Field


# ── Shared models ─────────────────────────────────────────────────

QuestionKind = Literal["base", "followup", "pressure", "closing"]


class QAExchange(BaseModel):
    question: str
    answer: str = ""
    kind: str = "base"


class InterviewConfig(BaseModel):
    # 직무/분야 — 질문 톤을 잡는 데 사용 (1차 시연은 "신입 공통 인성").
    job_role: str = "신입 공통 인성"
    # 기업 유형/세션명 등 상황 (선택).
    situation: str = ""
    # 쉬움 | 보통 | 어려움 — 압박 강도와 꼬리질문 깊이를 조절.
    difficulty: Literal["쉬움", "보통", "어려움"] = "보통"


class InterviewNextRequest(BaseModel):
    config: InterviewConfig = Field(default_factory=InterviewConfig)
    history: List[QAExchange] = Field(default_factory=list)
    # 지금까지 물어본 질문 수(인트로 제외). 서버가 이 값으로 종료 시점을 강제한다.
    asked_count: int = 0
    max_questions: int = 6


class InterviewNextResponse(BaseModel):
    # 다음 질문. done=true면 null.
    question: Optional[str] = None
    kind: QuestionKind = "base"
    done: bool = False


class InterviewReportRequest(BaseModel):
    config: InterviewConfig = Field(default_factory=InterviewConfig)
    history: List[QAExchange] = Field(default_factory=list)


class PerQuestionEval(BaseModel):
    question: str
    comment: str
    star: bool = False  # 답변이 STAR(상황-과제-행동-결과) 구조를 갖췄는지


class InterviewReportResponse(BaseModel):
    overall_summary: str = ""
    strengths: List[str] = Field(default_factory=list)
    improvements: List[str] = Field(default_factory=list)
    per_question: List[PerQuestionEval] = Field(default_factory=list)


# ── Prompt builders ───────────────────────────────────────────────

_SAFETY = (
    "안전장치: 당신의 출력은 채용 합격/불합격 판정이 아니라 교육용 연습 피드백입니다. "
    "지원자가 말하지 않은 사실을 지어내지 말고, 전사(STT) 텍스트에 근거해서만 판단하세요."
)


def _next_system_prompt(cfg: InterviewConfig) -> str:
    situation = f" 상황/기업유형: '{cfg.situation}'." if cfg.situation.strip() else ""
    pressure = {
        "쉬움": "압박 질문은 피하고, 지원자가 편하게 풀어낼 수 있는 기본/꼬리 질문 위주로.",
        "보통": "대체로 기본·꼬리 질문. 답변이 두루뭉술하면 한 번씩 가볍게 압박.",
        "어려움": "답변의 허점을 파고드는 꼬리·압박 질문을 적극적으로. 단, 무례하지 않게.",
    }[cfg.difficulty]
    return (
        "당신은 채용 면접의 면접관입니다. 지원자와 1:1 인성 면접을 진행합니다. "
        f"직무/분야: '{cfg.job_role}'.{situation}\n"
        f"난이도: {cfg.difficulty}. {pressure}\n"
        "질문 원칙:\n"
        "- 직전 답변을 실제로 반영해서 다음 질문을 만드세요. 답변이 추상적이면 구체적 사례를, "
        "사례가 나오면 STAR(상황·과제·행동·결과) 중 빠진 부분을 파고드세요.\n"
        "- 같은 질문을 반복하지 마세요. 이미 물어본 주제는 피하세요.\n"
        "- 질문은 한국어 존댓말, 한 번에 하나, 두 문장 이내로 간결하게.\n"
        f"{_SAFETY}\n"
        '응답 형식(JSON): {"question": "다음 질문 텍스트", "kind": "base|followup|pressure"}'
    )


def _next_user_prompt(req: InterviewNextRequest) -> str:
    if req.history:
        lines = []
        for i, qa in enumerate(req.history, 1):
            lines.append(f"Q{i}({qa.kind}): {qa.question}")
            lines.append(f"A{i}: {qa.answer.strip() or '(무응답)'}")
        transcript = "\n".join(lines)
    else:
        transcript = "(아직 대화 없음 — 첫 질문)"
    return (
        f"지금까지의 면접 대화:\n{transcript}\n\n"
        f"물어본 질문 수: {req.asked_count}/{req.max_questions}.\n"
        "위 흐름을 반영해 다음 질문 하나를 JSON으로 생성하세요."
    )


def _report_system_prompt(cfg: InterviewConfig) -> str:
    return (
        "당신은 면접 코치입니다. 아래 1:1 인성 면접 전사를 바탕으로 지원자의 "
        "'답변 내용·구조'를 평가하는 교육용 리포트를 작성합니다. "
        f"직무/분야: '{cfg.job_role}'.\n"
        "평가 기준: 질문에 직접 답했는지, 근거를 들었는지, 구체적 사례가 있는지, "
        "STAR(상황·과제·행동·결과) 구조인지, 결론이 명료한지.\n"
        f"{_SAFETY}\n"
        "이 리포트는 언어(답변) 측면만 다룹니다. 시선·자세 등 비언어 평가는 별도 단계에서 결합됩니다.\n"
        '응답 형식(JSON): {"overall_summary": string, "strengths": string[], '
        '"improvements": string[], "per_question": [{"question": string, '
        '"comment": string, "star": boolean}]}'
    )


def _report_user_prompt(req: InterviewReportRequest) -> str:
    lines = []
    for i, qa in enumerate(req.history, 1):
        lines.append(f"Q{i}: {qa.question}")
        lines.append(f"A{i}: {qa.answer.strip() or '(무응답)'}")
    return (
        "면접 전사:\n" + "\n".join(lines) + "\n\n"
        "각 답변을 평가 기준으로 검토하고, 전체 총평·강점·개선점과 "
        "질문별 코멘트를 JSON으로 작성하세요."
    )


# ── Provider routing (mirrors agent.py) ───────────────────────────

def _provider() -> str:
    return os.environ.get("LLM_PROVIDER", "gemini").lower().strip()


def _gemini_json(system: str, user: str, schema: type[BaseModel]):
    from google.genai import types  # type: ignore

    from .llm import _get_gemini, GEMINI_MODEL

    if not os.environ.get("GOOGLE_API_KEY"):
        raise RuntimeError("GOOGLE_API_KEY not set")
    client = _get_gemini()
    response = client.models.generate_content(
        model=GEMINI_MODEL,
        contents=user,
        config=types.GenerateContentConfig(
            system_instruction=system,
            response_mime_type="application/json",
            response_schema=schema,
            temperature=0.6,
            max_output_tokens=1200,
        ),
    )
    parsed = response.parsed  # type: ignore[attr-defined]
    if parsed is None:
        return schema.model_validate_json(response.text)
    return parsed


def _jeonbuk_json(system: str, user: str, schema: type[BaseModel]):
    from .llm import _jeonbuk_chat

    if not os.environ.get("JEONBUK_API_KEY"):
        raise RuntimeError("JEONBUK_API_KEY not set")
    response = _jeonbuk_chat(
        [
            {"role": "system", "content": system},
            {"role": "user", "content": user + "\n\n반드시 위 JSON 형식 하나로만 응답하세요. Markdown 금지."},
        ],
        temperature=0.6,
    )
    raw = response.choices[0].message.content or ""
    raw = raw.strip()
    if raw.startswith("```"):
        raw = raw.split("```", 2)[1] if "```" in raw[3:] else raw
        raw = raw.lstrip("json").strip().strip("`").strip()
    start, end = raw.find("{"), raw.rfind("}")
    if start >= 0 and end > start:
        raw = raw[start : end + 1]
    return schema.model_validate_json(raw)


def _route_json(system: str, user: str, schema: type[BaseModel]):
    if _provider() == "jeonbuk":
        return _jeonbuk_json(system, user, schema)
    return _gemini_json(system, user, schema)


# ── Mock provider (no API key needed — dev/demo only) ─────────────
# Lets the whole XR interview loop run end-to-end without a live LLM key. It is
# NOT the real model: it echoes the candidate's last answer into a templated
# follow-up so the loop is demoable. Set LLM_PROVIDER=mock to use it.

_MOCK_FOLLOWUPS = (
    ('방금 "{snippet}"라고 하셨는데, 그 부분을 조금 더 구체적인 사례로 설명해 주시겠어요?', "followup"),
    ("그 상황에서 본인이 맡은 역할과 실제로 한 행동은 무엇이었나요?", "followup"),
    ("만약 그 결정을 다시 한다면, 무엇을 다르게 하시겠어요?", "pressure"),
    ("본인의 강점이 이 직무에서 구체적으로 어떻게 발휘될 수 있을까요?", "base"),
    ("마지막으로, 입사 후 이루고 싶은 목표가 있다면 말씀해 주세요.", "base"),
)


def _mock_next(req: InterviewNextRequest) -> InterviewNextResponse:
    idx = min(req.asked_count - 1, len(_MOCK_FOLLOWUPS) - 1)
    if idx < 0:
        idx = 0
    template, kind = _MOCK_FOLLOWUPS[idx]
    last = req.history[-1].answer.strip() if req.history else ""
    snippet = (last[:16] + "…") if len(last) > 16 else (last or "말씀하신 내용")
    return InterviewNextResponse(question=template.format(snippet=snippet), kind=kind, done=False)


def _mock_report(req: InterviewReportRequest) -> InterviewReportResponse:
    per_q = [
        PerQuestionEval(
            question=qa.question,
            comment="답변에 결론과 근거가 드러나면 더 설득력이 높아집니다. (mock)",
            star=len(qa.answer) > 40,
        )
        for qa in req.history
    ]
    return InterviewReportResponse(
        overall_summary="전반적으로 성실하게 답변했습니다. 구체적 사례와 STAR 구조를 보강하면 좋겠습니다. (mock 리포트 — 실제 LLM 키 연결 시 대체됨)",
        strengths=["질문에 끝까지 답하려는 태도", "차분한 진행"],
        improvements=["답변에 수치·사례 등 구체 근거 추가", "결론을 먼저 말하는 두괄식 연습"],
        per_question=per_q,
    )


# ── Public API ────────────────────────────────────────────────────

def generate_next_question(req: InterviewNextRequest) -> InterviewNextResponse:
    # Server enforces the hard stop so a chatty model can't run forever.
    if req.asked_count >= req.max_questions:
        return InterviewNextResponse(question=None, kind="closing", done=True)
    if _provider() == "mock":
        return _mock_next(req)
    result: InterviewNextResponse = _route_json(
        _next_system_prompt(req.config),
        _next_user_prompt(req),
        InterviewNextResponse,
    )
    result.done = False
    if not result.question or not result.question.strip():
        return InterviewNextResponse(question=None, kind="closing", done=True)
    result.question = result.question.strip()
    if result.kind not in ("base", "followup", "pressure"):
        result.kind = "followup"
    return result


def generate_interview_report(req: InterviewReportRequest) -> InterviewReportResponse:
    if _provider() == "mock":
        return _mock_report(req)
    return _route_json(
        _report_system_prompt(req.config),
        _report_user_prompt(req),
        InterviewReportResponse,
    )
