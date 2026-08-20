"""AI interviewer question routing and the text portion of the final report."""

from __future__ import annotations

import os
from typing import List, Literal, Optional

from pydantic import BaseModel, Field


QuestionKind = Literal["base", "followup", "pressure", "closing"]


class QAExchange(BaseModel):
    question: str
    answer: str = ""
    kind: str = "base"
    wpm: Optional[float] = None
    filler_count: int = 0
    gaze_ratio: Optional[float] = None
    gaze_switches: int = 0
    posture_sway: Optional[float] = None
    head_motion: Optional[float] = None
    hand_motion: Optional[float] = None
    hand_span: Optional[float] = None
    gesture_idle_seconds: Optional[float] = None


class InterviewConfig(BaseModel):
    project_name: str = "면접 준비"
    job_role: str = "신입 공통 인성"
    situation: str = ""
    topic: str = "지원 동기와 직무 경험"
    difficulty: Literal["쉬움", "보통", "어려움"] = "보통"
    focus_goals: List[str] = Field(default_factory=list)


class InterviewNextRequest(BaseModel):
    config: InterviewConfig = Field(default_factory=InterviewConfig)
    history: List[QAExchange] = Field(default_factory=list)
    asked_count: int = 0
    max_questions: int = 6


class InterviewNextResponse(BaseModel):
    question: Optional[str] = None
    kind: QuestionKind = "base"
    done: bool = False
    reaction: Optional[str] = None
    reaction_tone: Optional[Literal["warm", "neutral", "challenging"]] = None
    reaction_speaker: Optional[Literal["warm", "analytical", "challenging"]] = None
    question_speaker: Literal["warm", "analytical", "challenging"] = "analytical"


class InterviewReportRequest(BaseModel):
    config: InterviewConfig = Field(default_factory=InterviewConfig)
    history: List[QAExchange] = Field(default_factory=list)


class PerQuestionEval(BaseModel):
    question: str
    comment: str
    star: bool = False


class InterviewReportResponse(BaseModel):
    overall_summary: str = ""
    strengths: List[str] = Field(default_factory=list)
    improvements: List[str] = Field(default_factory=list)
    per_question: List[PerQuestionEval] = Field(default_factory=list)


def _next_system_prompt(cfg: InterviewConfig) -> str:
    situation = f" 상황/기업 유형: '{cfg.situation}'." if cfg.situation.strip() else ""
    topic = f" 면접에서 다룰 핵심 주제: '{cfg.topic}'." if cfg.topic.strip() else ""
    goals = f" 사용자가 연습하려는 항목: {', '.join(cfg.focus_goals)}." if cfg.focus_goals else ""
    pressure = {
        "쉬움": "편안한 기본 질문 위주로 진행하고 압박 표현은 쓰지 마세요.",
        "보통": "기본 질문과 답변에 근거한 꼬리 질문을 균형 있게 사용하세요.",
        "어려움": "답변의 빈틈을 확인하는 꼬리·압박 질문을 사용하되 무례하게 말하지 마세요.",
    }[cfg.difficulty]
    return f"""당신은 실제 채용 면접의 세 명 면접관 패널입니다.
프로젝트: '{cfg.project_name}'. 지원 분야: '{cfg.job_role}'.{situation}{topic}{goals}
난이도: {cfg.difficulty}. {pressure}

직전 답변을 반영하여 다음 질문 하나를 한국어 두 문장 이내로 만드세요.
- 첫 질문이라면 자기소개를 기계적으로 묻지 말고, 지원 분야·상황·핵심 주제에 가장 자연스러운 실제 면접 질문부터 시작하세요. 첫 질문의 reaction은 null입니다.
- 추상적인 답변에는 구체적 사례를 묻고, 사례에는 STAR(상황·과제·행동·결과) 중 빠진 부분을 물으세요.
- 같은 질문이나 이미 답한 주제를 반복하지 마세요.
- 직전 답변에서 실제로 언급한 내용 중 의미 있는 연결이나 근거가 있다면 reaction으로 짧게 확인하세요. 예: '지원 동기와 경험의 연결이 자연스럽군요.' 단, 근거 없이 칭찬하지 마세요.
- reaction은 친구의 맞장구가 아니라 면접관의 절제된 존댓말 한 문장이어야 하며, 곧바로 이어질 질문과 내용이 중복되지 않아야 합니다.
- 칭찬·안심 반응은 warm, 사실 확인과 꼬리 질문은 analytical, 빠른 말·시선 불안·답변의 빈틈 지적은 challenging에게 배정하세요.
- 지원자가 말하지 않은 사실을 만들지 말고, 합격/불합격을 판정하지 마세요.
- 무응답에는 칭찬이나 '잘 들었습니다' 같은 반응을 절대 쓰지 마세요.

JSON 형식:
{{"question":"다음 질문", "kind":"base|followup|pressure", "reaction":"짧은 반응 또는 null", "reaction_tone":"warm|neutral|challenging 또는 null", "reaction_speaker":"warm|analytical|challenging 또는 null", "question_speaker":"warm|analytical|challenging"}}"""


def _next_user_prompt(req: InterviewNextRequest) -> str:
    if not req.history:
        transcript = "(아직 대화 없음 — 첫 질문)"
    else:
        lines: list[str] = []
        for index, qa in enumerate(req.history, 1):
            lines.extend((f"Q{index}({qa.kind}): {qa.question}", f"A{index}: {qa.answer.strip() or '(무응답)'}"))
            metrics: list[str] = []
            if qa.wpm is not None:
                metrics.append(f"말속도 {qa.wpm:.0f}WPM")
            if qa.filler_count:
                metrics.append(f"필러 {qa.filler_count}회")
            if qa.gaze_ratio is not None:
                metrics.append(f"질문자 응시 {qa.gaze_ratio * 100:.0f}%")
            if qa.gaze_switches:
                metrics.append(f"시선 전환 {qa.gaze_switches}회")
            if qa.posture_sway is not None and qa.posture_sway >= 0:
                metrics.append(f"상체 이동 {qa.posture_sway:.2f}m")
            if qa.hand_motion is not None and qa.hand_motion >= 0:
                metrics.append(f"손 최대속도 {qa.hand_motion:.2f}m/s")
            if qa.hand_span is not None and qa.hand_span >= 0:
                metrics.append(f"양손 간격 {qa.hand_span:.2f}m")
            if qa.gesture_idle_seconds is not None and qa.gesture_idle_seconds >= 0:
                metrics.append(f"손동작 정지 {qa.gesture_idle_seconds:.1f}초")
            if metrics:
                lines.append("관찰: " + ", ".join(metrics))
        transcript = "\n".join(lines)
    return f"현재까지 면접:\n{transcript}\n\n질문 수: {req.asked_count}/{req.max_questions}. 다음 질문을 JSON으로 작성하세요."


def _report_system_prompt(cfg: InterviewConfig) -> str:
    return f"""당신은 면접 코치입니다. 지원 분야는 '{cfg.job_role}'입니다.
답변 내용과 구조, 구체적 근거, STAR 구성, 결론의 명확성을 평가하세요.
제공된 말속도·필러·질문자 응시 관찰도 함께 반영하되 관찰하지 않은 사실은 만들지 마세요.
합격/불합격을 판정하지 말고 연습을 위한 구체적인 피드백을 한국어로 작성하세요.
JSON 형식: {{"overall_summary":string,"strengths":string[],"improvements":string[],"per_question":[{{"question":string,"comment":string,"star":boolean}}]}}"""


def _report_user_prompt(req: InterviewReportRequest) -> str:
    lines: list[str] = []
    for index, qa in enumerate(req.history, 1):
        lines.extend((f"Q{index}: {qa.question}", f"A{index}: {qa.answer.strip() or '(무응답)'}"))
        metrics: list[str] = []
        if qa.wpm is not None:
            metrics.append(f"말속도 {qa.wpm:.0f}WPM")
        if qa.filler_count:
            metrics.append(f"필러 {qa.filler_count}회")
        if qa.gaze_ratio is not None:
            metrics.append(f"질문자 응시 {qa.gaze_ratio * 100:.0f}%")
        if qa.gaze_switches:
            metrics.append(f"시선 전환 {qa.gaze_switches}회")
        if qa.posture_sway is not None and qa.posture_sway >= 0:
            metrics.append(f"상체 이동 {qa.posture_sway:.2f}m")
        if qa.hand_motion is not None and qa.hand_motion >= 0:
            metrics.append(f"손 최대속도 {qa.hand_motion:.2f}m/s")
        if qa.hand_span is not None and qa.hand_span >= 0:
            metrics.append(f"양손 간격 {qa.hand_span:.2f}m")
        if qa.gesture_idle_seconds is not None and qa.gesture_idle_seconds >= 0:
            metrics.append(f"손동작 정지 {qa.gesture_idle_seconds:.1f}초")
        if metrics:
            lines.append("관찰: " + ", ".join(metrics))
    return "면접 기록:\n" + "\n".join(lines) + "\n\n전체 및 질문별 피드백을 JSON으로 작성하세요."


def _provider() -> str:
    return os.environ.get("LLM_PROVIDER", "gemini").lower().strip()


def _gemini_json(system: str, user: str, schema: type[BaseModel]):
    from google.genai import types  # type: ignore
    from .llm import GEMINI_MODEL, _get_gemini

    if not os.environ.get("GOOGLE_API_KEY"):
        raise RuntimeError("GOOGLE_API_KEY not set")
    response = _get_gemini().models.generate_content(
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
    return response.parsed or schema.model_validate_json(response.text)


def _jeonbuk_json(system: str, user: str, schema: type[BaseModel]):
    from .llm import _jeonbuk_chat

    if not os.environ.get("JEONBUK_API_KEY"):
        raise RuntimeError("JEONBUK_API_KEY not set")
    response = _jeonbuk_chat(
        [{"role": "system", "content": system}, {"role": "user", "content": user + "\nJSON만 반환하세요."}],
        temperature=0.6,
    )
    raw = (response.choices[0].message.content or "").strip()
    if raw.startswith("```"):
        raw = raw.split("```", 2)[1].removeprefix("json").strip()
    start, end = raw.find("{"), raw.rfind("}")
    return schema.model_validate_json(raw[start : end + 1] if start >= 0 and end > start else raw)


def _nvidia_json(system: str, user: str, schema: type[BaseModel]):
    from .llm import _nvidia_chat

    if not os.environ.get("NVIDIA_API_KEY"):
        raise RuntimeError("NVIDIA_API_KEY not set")
    response = _nvidia_chat(
        [
            {"role": "system", "content": system},
            {"role": "user", "content": user + "\n반드시 JSON 객체 하나만 반환하세요. Markdown 금지."},
        ],
        temperature=0.6,
        max_tokens=1600,
    )
    raw = (response.choices[0].message.content or "").strip()
    if raw.startswith("```"):
        raw = raw.split("```", 2)[1].removeprefix("json").strip()
    start, end = raw.find("{"), raw.rfind("}")
    return schema.model_validate_json(raw[start : end + 1] if start >= 0 and end > start else raw)


def _route_json(system: str, user: str, schema: type[BaseModel]):
    provider = _provider()
    if provider == "jeonbuk":
        return _jeonbuk_json(system, user, schema)
    if provider in ("nvidia", "qwen"):
        return _nvidia_json(system, user, schema)
    return _gemini_json(system, user, schema)


_MOCK_FOLLOWUPS = (
    ('방금 "{snippet}"라고 하셨는데, 그 내용을 보여주는 구체적인 사례를 말씀해 주시겠습니까?', "followup"),
    ("그 상황에서 본인이 맡은 역할과 실제로 취한 행동은 무엇이었습니까?", "followup"),
    ("같은 결정을 다시 내려야 한다면 무엇을 다르게 하시겠습니까?", "pressure"),
    ("본인의 강점이 이 직무에서 성과로 이어진 사례를 설명해 주십시오.", "base"),
    ("입사 후 이루고 싶은 가장 구체적인 목표는 무엇입니까?", "base"),
)


def _mock_next(req: InterviewNextRequest) -> InterviewNextResponse:
    if not req.history:
        return InterviewNextResponse(
            question=f"{req.config.job_role} 직무에 지원하신 이유와 {req.config.topic}에 대해 말씀해 주시겠습니까?",
            kind="base",
            reaction=None,
            reaction_tone=None,
            reaction_speaker=None,
            question_speaker="warm",
        )
    index = max(0, min(req.asked_count - 1, len(_MOCK_FOLLOWUPS) - 1))
    template, kind = _MOCK_FOLLOWUPS[index]
    last_qa = req.history[-1] if req.history else QAExchange(question="", answer="")
    last = last_qa.answer.strip()
    snippet = (last[:16] + "…") if len(last) > 16 else (last or "말씀하신 내용")
    reaction, tone, speaker = "네, 답변 감사합니다.", "warm", "warm"
    if not last:
        reaction, tone, speaker = "답변이 들리지 않았습니다. 짧게라도 말씀해 주세요.", "neutral", "analytical"
    elif (last_qa.wpm or 0) > 170 or last_qa.filler_count >= 4:
        reaction, tone, speaker = "조금 더 천천히 핵심부터 말씀해 주세요.", "challenging", "challenging"
    elif (last_qa.gaze_ratio is not None and last_qa.gaze_ratio < 0.55) or last_qa.gaze_switches >= 7:
        reaction, tone, speaker = "질문자를 보면서 답변을 이어가 주세요.", "challenging", "challenging"
    elif last and len(last) < 20:
        reaction, tone, speaker = "조금 더 구체적으로 설명해 주시겠습니까?", "neutral", "analytical"
    question_speaker = "challenging" if kind == "pressure" else ("analytical" if kind == "followup" else "warm")
    return InterviewNextResponse(
        question=template.format(snippet=snippet),
        kind=kind,
        reaction=reaction,
        reaction_tone=tone,
        reaction_speaker=speaker,
        question_speaker=question_speaker,
    )


def fallback_interview_report(req: InterviewReportRequest) -> InterviewReportResponse:
    answered = [qa for qa in req.history if qa.answer.strip()]
    if not answered:
        return InterviewReportResponse(
            overall_summary=(
                "이번 연습에서는 평가할 수 있는 음성 답변이 기록되지 않았습니다. "
                "면접 진행 기록은 정상 보존되었으며, 다음 연습에서는 질문마다 짧게라도 실제 답변을 남겨 주세요."
            ),
            strengths=[],
            improvements=[
                "질문마다 결론부터 한두 문장으로 답변 시작",
                "질문당 최소 30초 이상 구체적인 사례와 본인의 행동 설명",
            ],
            per_question=[
                PerQuestionEval(
                    question=qa.question,
                    comment="답변이 기록되지 않아 내용 및 STAR 평가는 보류합니다.",
                    star=False,
                )
                for qa in req.history
            ],
        )

    per_question = [
        PerQuestionEval(
            question=qa.question,
            comment=(
                "답변의 결론과 근거를 더 분명히 연결하면 설득력이 높아집니다."
                if qa.answer.strip()
                else "답변이 기록되지 않아 내용 및 STAR 평가는 보류합니다."
            ),
            star=len(qa.answer.strip()) > 40,
        )
        for qa in req.history
    ]
    return InterviewReportResponse(
        overall_summary="질문에 성실하게 답했습니다. 구체적인 사례와 STAR 구조를 보강하면 답변이 더 선명해집니다.",
        strengths=["질문의 의도를 따라가려는 태도", "차분한 면접 진행"],
        improvements=["수치와 역할 등 구체적인 근거 추가", "결론을 먼저 말한 뒤 사례로 뒷받침"],
        per_question=per_question,
    )


def generate_next_question(req: InterviewNextRequest) -> InterviewNextResponse:
    if req.asked_count >= req.max_questions:
        return InterviewNextResponse(question=None, kind="closing", done=True)
    if _provider() == "mock":
        return _mock_next(req)
    result: InterviewNextResponse = _route_json(_next_system_prompt(req.config), _next_user_prompt(req), InterviewNextResponse)
    result.done = False
    if not result.question or not result.question.strip():
        return InterviewNextResponse(question=None, kind="closing", done=True)
    result.question = result.question.strip()
    if result.kind not in ("base", "followup", "pressure"):
        result.kind = "followup"
    if result.kind == "pressure":
        result.question_speaker = "challenging"
    elif result.kind == "followup" and result.question_speaker == "warm":
        result.question_speaker = "analytical"
    return result


def generate_interview_report(req: InterviewReportRequest) -> InterviewReportResponse:
    # Do not spend an LLM request (or fail on provider quota) when there is no
    # answer content to assess. This is still a valid, displayable report.
    if not any(qa.answer.strip() for qa in req.history):
        return fallback_interview_report(req)
    if _provider() == "mock":
        return fallback_interview_report(req)
    return _route_json(_report_system_prompt(req.config), _report_user_prompt(req), InterviewReportResponse)
