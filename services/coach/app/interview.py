"""AI interviewer question routing and the text portion of the final report."""

from __future__ import annotations

import json
import os
import re
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
    question: Optional[str] = Field(
        default=None,
        description="실제로 말할 한국어 면접 질문. 첫 턴과 진행 중에는 반드시 구체적 문장이며 종료 턴에만 null",
    )
    kind: QuestionKind = Field(default="base", description="이번 질문의 유형")
    done: bool = Field(default=False, description="질문 수가 최대치에 도달한 종료 턴에만 true")
    reaction: Optional[str] = Field(default=None, description="직전 답변에 근거한 짧은 반응. 첫 턴에는 null")
    reaction_tone: Optional[Literal["warm", "neutral", "challenging"]] = None
    reaction_speaker: Optional[Literal["warm", "analytical", "challenging"]] = None
    question_speaker: Literal["warm", "analytical", "challenging"] = "analytical"


class LocalQuestionOutput(BaseModel):
    question: str = Field(description="지원 상황과 대화 기록에 맞는 실제 한국어 면접 질문 한 문장")
    reaction: Optional[str] = Field(default=None, description="직전 답변에 근거한 짧은 면접관 반응. 첫 질문이면 null")
    speaker: Literal["warm", "analytical", "challenging"] = Field(
        default="analytical", description="이 질문에 가장 어울리는 면접관"
    )


class LocalClosingOutput(BaseModel):
    closing: str = Field(description="지원자에게 말할 실제 한국어 면접 종료 인사 한 문장")
    speaker: Literal["warm", "analytical", "challenging"] = "warm"


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
    return f"""실제 채용 면접의 세 면접관 패널로 행동하세요.
지원: '{cfg.job_role}'.{situation}{topic}{goals} 난이도: {cfg.difficulty}. {pressure}
답변에 근거한 질문 한 개만 한국어 존댓말로 만드세요. 추상적이면 사례를, 사례면 빠진 STAR 요소를 묻고 반복·허구·합격판정은 금지합니다.
질문 수가 0이면 지원 분야·상황·주제에 맞는 실제 첫 질문을 question에 반드시 작성하고 reaction=null로 하세요. 이후 reaction은 근거 있는 절제된 한 문장만 쓰며 무응답을 칭찬하지 마세요.
칭찬은 warm, 사실·꼬리질문은 analytical, 답변 빈틈·빠른 말·불안한 시선 지적은 challenging에게 배정하세요. 짧은 필러는 자연스러울 때만 사용하세요.
질문 수가 최대면 done=true, question=null, kind="closing", reaction=짧은 마무리 인사입니다. 아니면 done=false입니다.
응답 스키마에 맞는 JSON 객체 하나만 출력하세요."""


def _next_user_prompt(req: InterviewNextRequest) -> str:
    if not req.history:
        return (
            f"질문 수: {req.asked_count}/{req.max_questions}. "
            "첫 질문입니다. 지원자가 이 주제에서 직접 수행한 행동, 판단 근거, 측정 가능한 결과 중 "
            "하나를 구체적으로 설명하게 만드는 면접 질문을 작성하세요."
        )
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


def _local_turn_system_prompt(cfg: InterviewConfig, closing: bool) -> str:
    context = " / ".join(value for value in (cfg.job_role, cfg.situation, cfg.topic) if value.strip())
    if closing:
        return f"당신은 '{context}' 채용 면접관입니다. 면접을 끝내는 절제된 한국어 존댓말 한 문장만 출력하세요."
    # This prompt runs on a CPU-only local model. Keep it deliberately compact:
    # prompt evaluation was the dominant latency on low-end interview PCs.
    return (
        f"'{context}' 한국 채용 면접관입니다. 존댓말 질문 한 문장만 출력하세요. "
        "첫 질문은 실제 행동·성과를, 이후에는 직전 답변에서 빠진 근거를 묻습니다. "
        "설명·JSON·근거 없는 칭찬은 금지합니다."
    )


def _local_dialogue_user_prompt(req: InterviewNextRequest) -> str:
    if not req.history:
        return (
            f"지원 분야: {req.config.job_role}\n"
            f"면접 상황: {req.config.situation}\n"
            f"핵심 주제: {req.config.topic}\n"
            "위 입력을 직접 반영해 지원자의 실제 행동이나 측정 가능한 성과를 확인하는 "
            "구체적인 첫 질문 한 문장만 하세요."
        )
    latest = req.history[-1]
    return (
        f"직전 질문: {latest.question}\n"
        f"지원자 답변: {latest.answer.strip() or '(무응답)'}\n"
        "이 답변에서 빠진 구체적 근거 한 가지를 골라 확인하는 꼬리 질문 한 문장만 하세요."
    )


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
    return os.environ.get("LLM_PROVIDER", "ollama").lower().strip()


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


def _ollama_json(system: str, user: str, schema: type[BaseModel], max_tokens: int = 180):
    from .llm import _ollama_chat

    response = _ollama_chat(
        [
            {"role": "system", "content": system},
            {"role": "user", "content": user + "\n반드시 JSON 객체 하나만 반환하세요. Markdown 금지."},
        ],
        temperature=0.35,
        max_tokens=max_tokens,
        response_schema=schema.model_json_schema(),
    )
    raw = (response.choices[0].message.content or "").strip()
    if raw.startswith("```"):
        raw = raw.split("```", 2)[1].removeprefix("json").strip()
    start, end = raw.find("{"), raw.rfind("}")
    return schema.model_validate_json(raw[start : end + 1] if start >= 0 and end > start else raw)


def _ollama_text(system: str, user: str, max_tokens: int = 100) -> str:
    """Use plain generation for live dialogue.

    Small local models are both faster and materially less likely to echo schema
    descriptions when they only have to author the sentence the actor will say.
    """
    from .llm import _ollama_chat

    response = _ollama_chat(
        [{"role": "system", "content": system}, {"role": "user", "content": user}],
        temperature=0.35,
        max_tokens=max_tokens,
        json_mode=False,
    )
    text = (response.choices[0].message.content or "").strip()
    if text.startswith("```"):
        text = text.split("```", 2)[1].strip()
    text = text.strip()
    if text.startswith("{") and text.endswith("}"):
        try:
            payload = json.loads(text)
            for key in ("question", "질문", "closing", "마무리"):
                value = payload.get(key)
                if isinstance(value, str) and value.strip():
                    text = value
                    break
        except (json.JSONDecodeError, AttributeError):
            pass
    if text.startswith("{"):
        match = re.search(r'["\']?(?:질문|question|마무리|closing)["\']?\s*:\s*["\']([^"\']+)', text, re.IGNORECASE)
        if match:
            text = match.group(1)
    text = text.strip().strip('"').strip("'").strip()
    for prefix in ("질문:", "반응:", "마무리:", "closing:", "question:"):
        if text.lower().startswith(prefix.lower()):
            text = text[len(prefix):].strip()
    return " ".join(text.split())


def _route_local_interviewer(req: InterviewNextRequest, question: str) -> str:
    """Route generated wording to the matching scene role without authoring dialogue.

    warm=HR, analytical=technical, challenging=executive/pressure. The question
    itself always comes from the LLM; this only selects which authored actor says it.
    """
    configured_context = f"{req.config.job_role} {req.config.topic}".lower()
    generated_question = question.lower()
    if req.config.difficulty == "어려움" and req.history:
        return "challenging"
    technical = (
        "기술", "개발", "구현", "설계", "성능", "장애", "데이터", "서버", "백엔드",
        "프론트", "알고리즘", "프로젝트", "코드", "api", "db", "ai", "모델",
    )
    hr = ("지원 동기", "협업", "갈등", "조직", "성격", "가치관", "강점", "약점", "입사")
    # The user's configured interview purpose outranks incidental vocabulary in
    # the generated sentence (for example an HR question mentioning a project).
    if any(keyword in configured_context for keyword in hr):
        return "warm"
    if any(keyword in configured_context for keyword in technical):
        return "analytical"
    if any(keyword in generated_question for keyword in hr):
        return "warm"
    if any(keyword in generated_question for keyword in technical):
        return "analytical"
    return "analytical" if req.history else "warm"


def _route_json(system: str, user: str, schema: type[BaseModel]):
    provider = _provider()
    if provider == "jeonbuk":
        return _jeonbuk_json(system, user, schema)
    if provider in ("nvidia", "qwen"):
        return _nvidia_json(system, user, schema)
    if provider in ("ollama", "local"):
        return _ollama_json(system, user, schema)
    return _gemini_json(system, user, schema)


def generate_next_question(req: InterviewNextRequest) -> InterviewNextResponse:
    if _provider() in ("ollama", "local"):
        if req.asked_count >= req.max_questions:
            closing = _ollama_text(
                _local_turn_system_prompt(req.config, True),
                _next_user_prompt(req).replace("다음 질문을 JSON으로 작성하세요.", "면접 종료 인사를 한 문장으로 작성하세요."),
                max_tokens=80,
            )
            if not closing:
                raise ValueError("local LLM returned an empty closing line")
            return InterviewNextResponse(
                question=None,
                kind="closing",
                done=True,
                reaction=closing,
                reaction_tone="warm",
                reaction_speaker="warm",
                question_speaker="warm",
            )
        first = not req.history
        dialogue_prompt = _local_turn_system_prompt(req.config, False)
        question = _ollama_text(
            dialogue_prompt,
            _local_dialogue_user_prompt(req),
            max_tokens=64,
        )
        if not question or question.startswith("{"):
            raise ValueError("local LLM returned an empty interview question")
        speaker = _route_local_interviewer(req, question)
        result = InterviewNextResponse(
            question=question,
            kind="base" if first else ("pressure" if speaker == "challenging" else "followup"),
            done=False,
            reaction=None,
            reaction_tone=None,
            reaction_speaker=None,
            question_speaker=speaker,
        )
    else:
        result = _route_json(_next_system_prompt(req.config), _next_user_prompt(req), InterviewNextResponse)
    if result.reaction and result.reaction.strip().lower() == "null":
        result.reaction = None
    if not req.history:
        result.reaction = None
        result.reaction_tone = None
        result.reaction_speaker = None
    should_close = req.asked_count >= req.max_questions
    if should_close:
        result.done = True
        result.kind = "closing"
        result.question = None
        if not result.reaction or not result.reaction.strip():
            raise ValueError("local LLM returned an empty closing line")
        return result
    result.done = False
    if not result.question or not result.question.strip() or result.question.strip() == "다음 질문 또는 null":
        raise ValueError("local LLM returned an empty interview question")
    result.question = result.question.strip()
    if result.kind not in ("base", "followup", "pressure"):
        result.kind = "followup"
    if result.kind == "pressure":
        result.question_speaker = "challenging"
    return result


def generate_interview_report(req: InterviewReportRequest) -> InterviewReportResponse:
    if _provider() in ("ollama", "local"):
        return _ollama_json(
            _report_system_prompt(req.config),
            _report_user_prompt(req),
            InterviewReportResponse,
            max_tokens=600,
        )
    return _route_json(_report_system_prompt(req.config), _report_user_prompt(req), InterviewReportResponse)
