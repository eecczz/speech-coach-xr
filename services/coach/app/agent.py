"""Real-time agent feedback — short, scenario-aware nudges per trigger.

This is the L2-style live coach: the browser fires off small trigger events
(silence ≥5s, gaze drift ≥5s, filler-burst, etc.) and we synthesize a single
short Korean sentence per fire. The browser handles rate-limiting and dedup;
this endpoint only does the prompt + LLM call.

Why a separate module from llm.py: the comprehensive evaluator deals with a
huge structured SessionBundle and a strict response_schema. Here we want the
opposite — tiny payload, ~50-token freeform text, sub-2s latency.
"""

from __future__ import annotations

import os
from typing import Any, Dict, List, Optional, Literal

from pydantic import BaseModel, Field


TriggerKind = Literal[
    "silence",
    "gaze",
    "smile_absence",
    "motion_absence",
    "filler",
    "speech_rate",
    "content",
    "posture",
    "gesture",
    "vocal_tone",
]


class MultimodalContext(BaseModel):
    """Snapshot of the user's recent behavior across modalities.

    Always sent alongside every trigger so the LLM judges a moment as a whole
    person, not a single isolated signal. The agent's value proposition is
    *exactly* this multimodal fusion — language + gaze + expression + gesture
    + prosody as a joint judgment, not separate red flags.
    """

    # ── Verbal (STT) — structured: previous utterances vs current utterance ──
    # Why two fields instead of one big string: when a "content" trigger fires
    # we want the LLM to *coach the latest sentence only*, not whatever it
    # finds interesting in the back-context. Keeping them separate also lets
    # us label "이 발화 평가 / 이전은 맥락" explicitly in the prompt.
    #
    # `recent_transcript` is kept for back-compat (older clients send only
    # this). New clients populate the structured pair below.
    recent_transcript: str = ""
    previous_utterances: List[str] = Field(default_factory=list)
    current_utterance: str = ""
    # Words-per-minute over the last ~30s window, computed in the browser.
    wpm: Optional[float] = None
    # Total filler hits this session and the most recent few.
    filler_count_total: int = 0
    recent_fillers: List[str] = Field(default_factory=list)

    # ── Nonverbal (vision, smoothed averages of last ~3s) ──
    # All optional so the browser can omit a signal it doesn't have yet.
    gaze_fixation_ratio_avg: Optional[float] = None  # 0..1; high = locked on
    head_yaw_deg_avg: Optional[float] = None         # +left / -right (degrees)
    smile_intensity_avg: Optional[float] = None      # 0..1; 0 = neutral
    expression_change_rate_avg: Optional[float] = None  # 0..high; 0 = frozen face
    hand_velocity_max_avg: Optional[float] = None    # 0..high; 0 = hands still
    posture_sway_avg: Optional[float] = None         # 0..high; high = restless

    # ── Audio / prosody ──
    current_silence_seconds: Optional[float] = None  # ongoing silence run

    # ── Session position ──
    session_elapsed_s: Optional[float] = None        # how far into the practice we are

    # ── Self-history (anti-repetition) ──
    # The last few messages the agent has actually sent to the user, oldest →
    # newest. The model is told to avoid echoing these so we don't get the
    # same reaction twice in a row.
    recent_agent_messages: List[str] = Field(default_factory=list)


class AgentTriggerRequest(BaseModel):
    kind: TriggerKind
    scenario: str = "presentation"
    # 세션명 — 대화 상황 그 자체 (예: "졸업논문 발표", "구글 면접 1차"). 빈 문자열이면
    # scenario rubric 톤만 사용.
    situation: str = ""
    focus_goals: List[str] = Field(default_factory=list)
    # 트리거별 측정값 (silence_seconds, gaze_off_seconds, filler_count 등).
    payload: Dict[str, Any] = Field(default_factory=dict)
    # 멀티모달 스냅샷 — 언어 + 비언어를 통합 판단하라고 LLM에 같이 던진다.
    context: MultimodalContext = Field(default_factory=MultimodalContext)
    # 직전 발화 — context.recent_transcript와 같음 (호환용). 새 코드에서는 context 사용.
    recent_transcript: str = ""


class AgentFeedbackResponse(BaseModel):
    # LLM이 굳이 응답 안 줄 수도 있음 (content 트리거에서 평이한 발화면 null).
    message: Optional[str] = None
    tone: Optional[Literal["praise", "nudge", "critique"]] = None


_FEEDBACK_SCHEMA_HINT = (
    '응답 형식(JSON): {"message": "한 줄 피드백 또는 null", '
    '"tone": "praise|nudge|critique 또는 null"}'
)


def _trigger_brief(req: AgentTriggerRequest) -> str:
    """One-liner describing what just happened, in Korean, for the user prompt."""
    p = req.payload
    if req.kind == "silence":
        sec = float(p.get("silence_seconds", 0))
        return f"사용자가 약 {sec:.1f}초간 침묵하고 있습니다."
    if req.kind == "gaze":
        sec = float(p.get("gaze_off_seconds", 0))
        return f"사용자가 약 {sec:.1f}초간 정면(청자 쪽)을 보지 않고 있습니다."
    if req.kind == "smile_absence":
        return "최근 5초간 사용자의 얼굴에서 미소가 한 번도 감지되지 않았습니다 (무표정 지속)."
    if req.kind == "motion_absence":
        return "최근 5초간 사용자의 몸이 거의 움직이지 않았습니다 (제스처/자세 변화 없음)."
    if req.kind == "filler":
        n = int(p.get("filler_count", 0))
        recent = p.get("recent_fillers") or []
        recent_str = ", ".join(recent[-5:]) if recent else "(예시 없음)"
        return f"세션 누적 필러 사용이 {n}회에 도달했습니다 (최근: {recent_str})."
    if req.kind == "speech_rate":
        wpm = float(p.get("wpm", 0))
        return f"최근 1분 기준 말 속도가 약 {wpm:.0f} WPM입니다."
    if req.kind == "content":
        return f'사용자가 방금 다음과 같이 말했습니다: "{req.recent_transcript.strip()}"'
    if req.kind == "posture":
        return "헤드셋 움직임 기준으로 고개와 상체가 과도하게 흔들리고 있습니다."
    if req.kind == "gesture":
        return "XR 컨트롤러 기준으로 손동작이 과도하게 빠릅니다."
    if req.kind == "vocal_tone":
        variation = float(p.get("pitch_sd_semitones", -1))
        ending = float(p.get("end_energy_drop", -1))
        return f"음높이 변화 {variation:.2f}, 문장 끝 에너지 비율 {ending:.2f}로 목소리 톤이 단조롭거나 말끝이 흐립니다."
    return f"트리거: {req.kind}"


def _trigger_directive(kind: TriggerKind) -> str:
    """What the LLM should *do* for this kind. Keeps the model focused."""
    if kind == "silence":
        return "꽤 길게 멈춰있어. 흐름 다시 잡으라고 부드럽게 짧게 한 마디."
    if kind == "gaze":
        return "시선이 오래 떠나 있어. 청자와 다시 눈 맞추라고 짧게 한 마디."
    if kind == "smile_absence":
        return "표정이 굳어있어. 부담 없이 환기. 상황에 안 맞는 미소 권유는 금지."
    if kind == "motion_absence":
        return "몸이 정지해 있어. 자연스러운 손짓·자세 변화를 가볍게."
    if kind == "filler":
        return "필러가 누적돼. 부담 없이 알리고 짧은 호흡 권유."
    if kind == "speech_rate":
        return "말 속도가 비정상이야. 짧게 조정 권유."
    if kind == "content":
        return (
            "**중요: 이건 코칭이 아니라 함께 듣고 있는 친구의 자연스러운 리액션이야.**\n"
            "- 방금 발화 내용에 맞장구치거나 ('오~ 그 부분 진짜 그래', '아, 그런 시각도 있구나'), "
            "공감하거나, 잘한 표현 발견하면 짧게 칭찬해줘 ('완벽한 인사야', '딱 맞는 표현이야').\n"
            "- 비판/지적은 정말로 명백히 부적절할 때만 (예: 면접인데 무례한 표현). "
            "그것도 아주 가끔, 부드럽게.\n"
            "- 평이한 발화면 굳이 끼어들지 말고 message를 null로. 매번 반응하면 짜증나.\n"
            "- 사용자가 직접 입력한 질문(manual:true)이면 그 질문에 답해줘."
        )
    if kind == "posture":
        return "면접 자세가 불안정해 보여. 고개와 상체를 안정시키도록 짧게 말해줘."
    if kind == "gesture":
        return "손동작이 산만해 보여. 제스처를 차분하게 하도록 짧게 말해줘."
    if kind == "vocal_tone":
        return "목소리가 단조롭거나 말끝이 흐려. 핵심어에 억양을 주고 문장 끝을 분명히 하도록 짧게 말해줘."
    return "상황에 맞게 한 줄로 반응."


def _system_prompt(req: AgentTriggerRequest) -> str:
    situation_line = (
        f"이번 세션의 대화 상황: '{req.situation}'"
        if req.situation.strip()
        else f"이번 세션의 시나리오 카테고리: {req.scenario}"
    )
    focus_line = (
        f"사용자가 특히 신경 쓰는 항목: {', '.join(req.focus_goals)}"
        if req.focus_goals
        else "특별히 명시된 포커스는 없음."
    )
    if req.scenario == "interview":
        return (
            "당신은 실제 채용 면접 중 지원자의 답변을 듣는 세 명의 면접관 중 한 명입니다. "
            "측정된 시선·침묵·자세·제스처·말속도 신호를 종합해 필요한 순간에만 짧게 개입하세요.\n"
            f"{situation_line}\n{focus_line}\n"
            "한국어 존댓말 한 문장, 35자 이내. 관찰 근거 없이 칭찬하지 말고, 무응답에 '잘 들었습니다'라고 하지 마세요. "
            "평이하거나 확신이 낮으면 message를 null로 반환하세요. recent_agent_messages와 같은 표현을 반복하지 마세요.\n"
            f"{_FEEDBACK_SCHEMA_HINT}"
        )
    return (
        "당신은 사용자가 발표 연습할 때 **옆에서 같이 듣고 있는 친구**입니다. "
        "선생님이 아니에요. 평가하거나 채점하는 사람이 아닙니다. "
        "그냥 자연스럽게 듣다가 마음 가는 순간에 반응합니다.\n"
        f"{situation_line}\n{focus_line}\n"
        "받는 정보:\n"
        "- 사용자가 방금 말한 문장 (current_utterance)\n"
        "- 그 직전 흐름 (previous_utterances — 맥락용)\n"
        "- 시선/표정/제스처/말속도/침묵 같은 비언어 신호\n"
        "- 트리거 종류 — '지금 왜 호출됐는지' 단서일 뿐, 무조건 그것만 보지 마.\n"
        "리액션 톤 (가장 중요):\n"
        "- **친구 톤**: '오~', '아 진짜', '와 그러네', '맞아 맞아', '음 그렇구나' 같은 추임새 OK.\n"
        "- **공감/맞장구**가 기본. 잘 들은 것처럼 반응해줘 — '컴공 코딩 필수 맞지', '그 시각 흥미롭다'.\n"
        "- 가끔 **칭찬** — 인사 자연스러우면 '인사 깔끔해', 비유 좋으면 '비유 좋네' 정도.\n"
        "- **코치 톤 금지**: '~해보자', '~좀 어때?', '~을 권장해요' 같은 말 X. 평가하지 마.\n"
        "- 침묵/시선/필러 같은 비언어 트리거에서는 짧게 환기 정도 — 한 줄, 부담 없이.\n"
        "응답 규칙:\n"
        "- 한국어 반말, 한 문장, 30자 이내.\n"
        "- 자명한 사실 나열 금지. '말씀하셨네요', '말씀이 빠르네요' 같은 거 X.\n"
        "- 평이한 발화엔 굳이 반응 안 해도 돼. message를 null로.\n"
        "- recent_agent_messages에 있는 표현 다시 쓰지 마. 같은 말 두 번 안 함.\n"
        "- 매번 반응하면 짜증. 의미 있을 때만.\n"
        f"{_FEEDBACK_SCHEMA_HINT}"
    )


def _format_context(ctx: "MultimodalContext") -> str:
    """Render the multimodal snapshot for the user prompt. Skip empty fields so
    the model doesn't fixate on zero values that just mean 'we didn't measure
    that this tick'."""
    lines = []
    # Verbal — structured pair takes priority. Fall back to recent_transcript
    # only when the client didn't fill the structured fields (old clients).
    if ctx.previous_utterances or ctx.current_utterance:
        if ctx.previous_utterances:
            prev_lines = "\n".join(f'    · "{u.strip()[:200]}"' for u in ctx.previous_utterances)
            lines.append(f"- 직전 발화 (맥락 참고용, 평가 대상 X):\n{prev_lines}")
        if ctx.current_utterance:
            lines.append(f'- 가장 최근 발화 (있다면 이 한 줄을 평가 대상으로): "{ctx.current_utterance.strip()[:240]}"')
    elif ctx.recent_transcript.strip():
        lines.append(f'- 최근 발화: "{ctx.recent_transcript.strip()[:240]}"')
    if ctx.wpm is not None:
        lines.append(f"- 최근 말 속도: {ctx.wpm:.0f} WPM")
    if ctx.filler_count_total > 0:
        recent = ", ".join(ctx.recent_fillers[-5:]) if ctx.recent_fillers else "-"
        lines.append(f"- 누적 필러: {ctx.filler_count_total}회 (최근: {recent})")
    # Nonverbal — gaze
    if ctx.gaze_fixation_ratio_avg is not None or ctx.head_yaw_deg_avg is not None:
        gaze_bits = []
        if ctx.gaze_fixation_ratio_avg is not None:
            gaze_bits.append(f"정면응시비율 {ctx.gaze_fixation_ratio_avg:.2f}")
        if ctx.head_yaw_deg_avg is not None:
            gaze_bits.append(f"고개 yaw {ctx.head_yaw_deg_avg:+.0f}°")
        lines.append("- 시선: " + ", ".join(gaze_bits))
    # Nonverbal — face
    if ctx.smile_intensity_avg is not None or ctx.expression_change_rate_avg is not None:
        face_bits = []
        if ctx.smile_intensity_avg is not None:
            face_bits.append(f"미소 강도 {ctx.smile_intensity_avg:.2f}")
        if ctx.expression_change_rate_avg is not None:
            face_bits.append(f"표정 변화 {ctx.expression_change_rate_avg:.2f}")
        lines.append("- 표정: " + ", ".join(face_bits))
    # Nonverbal — body
    if ctx.hand_velocity_max_avg is not None or ctx.posture_sway_avg is not None:
        body_bits = []
        if ctx.hand_velocity_max_avg is not None:
            body_bits.append(f"손 움직임 {ctx.hand_velocity_max_avg:.2f}")
        if ctx.posture_sway_avg is not None:
            body_bits.append(f"자세 흔들림 {ctx.posture_sway_avg:.2f}")
        lines.append("- 제스처/자세: " + ", ".join(body_bits))
    # Audio
    if ctx.current_silence_seconds is not None and ctx.current_silence_seconds > 0.5:
        lines.append(f"- 현재 침묵: {ctx.current_silence_seconds:.1f}초 진행 중")
    # Session position
    if ctx.session_elapsed_s is not None:
        lines.append(f"- 세션 시작 후 경과: {ctx.session_elapsed_s:.0f}초")
    # Self-history — prevents the same reaction twice in a row.
    if ctx.recent_agent_messages:
        recent_lines = "\n".join(f'    · "{m}"' for m in ctx.recent_agent_messages[-5:])
        lines.append(f"- 내가 방금 했던 말 (이거랑 비슷한 거 다시 하지 마):\n{recent_lines}")
    return "\n".join(lines) if lines else "(추가 컨텍스트 없음)"


def _user_prompt(req: AgentTriggerRequest) -> str:
    ctx = req.context
    # Back-compat: older clients only sent recent_transcript at the top level.
    if not ctx.recent_transcript and req.recent_transcript:
        ctx = ctx.model_copy(update={"recent_transcript": req.recent_transcript})

    return (
        f"[트리거] {_trigger_brief(req)}\n"
        f"[지시] {_trigger_directive(req.kind)}\n"
        f"[멀티모달 컨텍스트]\n{_format_context(ctx)}\n"
        "위 컨텍스트를 함께 보고 한 줄 코칭을 결정해줘. 트리거 하나만 보지 말 것."
    )


def _generate_with_gemini(req: AgentTriggerRequest) -> AgentFeedbackResponse:
    from google.genai import types  # type: ignore

    from .llm import _get_gemini, GEMINI_MODEL  # reuse the existing singleton

    if not os.environ.get("GOOGLE_API_KEY"):
        raise RuntimeError("GOOGLE_API_KEY not set")

    client = _get_gemini()
    response = client.models.generate_content(
        model=GEMINI_MODEL,
        contents=_user_prompt(req),
        config=types.GenerateContentConfig(
            system_instruction=_system_prompt(req),
            response_mime_type="application/json",
            response_schema=AgentFeedbackResponse,
            temperature=0.7,
            max_output_tokens=120,
        ),
    )
    parsed: Optional[AgentFeedbackResponse] = response.parsed  # type: ignore[attr-defined]
    if parsed is None:
        parsed = AgentFeedbackResponse.model_validate_json(response.text)
    return parsed


def _generate_with_jeonbuk(req: AgentTriggerRequest) -> AgentFeedbackResponse:
    """Jeonbuk student-API path (OpenAI-compatible). Uses the same _jeonbuk_chat
    helper as the comprehensive evaluator so we share the singleton + the
    json_object fallback for gateways that don't support response_format."""
    from .llm import _jeonbuk_chat

    if not os.environ.get("JEONBUK_API_KEY"):
        raise RuntimeError("JEONBUK_API_KEY not set")

    # We can't pass a Pydantic response_schema through the OpenAI-compat layer,
    # so we lean on a strict JSON contract in the prompt + Pydantic validation
    # on the way out. The contract here mirrors AgentFeedbackResponse.
    json_contract = (
        '\n\n반드시 다음 JSON 형식으로만 응답하세요. Markdown 금지:\n'
        '{"message": "한 줄 코칭 한국어 텍스트 또는 null", '
        '"tone": "praise" | "nudge" | "critique" | null}'
    )
    response = _jeonbuk_chat(
        [
            {"role": "system", "content": _system_prompt(req)},
            {"role": "user", "content": _user_prompt(req) + json_contract},
        ],
        temperature=0.7,
    )
    raw = response.choices[0].message.content or ""
    return AgentFeedbackResponse.model_validate_json(raw)


def _generate_with_nvidia(req: AgentTriggerRequest) -> AgentFeedbackResponse:
    from .llm import _nvidia_chat

    if not os.environ.get("NVIDIA_API_KEY"):
        raise RuntimeError("NVIDIA_API_KEY not set")
    json_contract = (
        '\n\n반드시 다음 JSON 형식으로만 응답하세요. Markdown 금지:\n'
        '{"message": "한 줄 코칭 한국어 텍스트 또는 null", '
        '"tone": "praise" | "nudge" | "critique" | null}'
    )
    response = _nvidia_chat(
        [
            {"role": "system", "content": _system_prompt(req)},
            {"role": "user", "content": _user_prompt(req) + json_contract},
        ],
        temperature=0.7,
        max_tokens=240,
    )
    raw = (response.choices[0].message.content or "").strip()
    if raw.startswith("```"):
        raw = raw.split("```", 2)[1].removeprefix("json").strip()
    start, end = raw.find("{"), raw.rfind("}")
    return AgentFeedbackResponse.model_validate_json(raw[start : end + 1] if start >= 0 and end > start else raw)


def _generate_with_ollama(req: AgentTriggerRequest) -> AgentFeedbackResponse:
    from .llm import _ollama_chat

    json_contract = (
        '\n\n반드시 다음 JSON 형식으로만 응답하세요. Markdown 금지:\n'
        '{"message": "한 줄 코칭 한국어 텍스트 또는 null", '
        '"tone": "praise" | "nudge" | "critique" | null}'
    )
    response = _ollama_chat(
        [
            {"role": "system", "content": _system_prompt(req)},
            {"role": "user", "content": _user_prompt(req) + json_contract},
        ],
        temperature=0.5,
        max_tokens=220,
    )
    raw = (response.choices[0].message.content or "").strip()
    start, end = raw.find("{"), raw.rfind("}")
    return AgentFeedbackResponse.model_validate_json(raw[start : end + 1] if start >= 0 and end > start else raw)


def generate_agent_feedback(req: AgentTriggerRequest) -> AgentFeedbackResponse:
    """Provider-routed agent feedback. Caller wraps in try/except."""
    provider = os.environ.get("LLM_PROVIDER", "ollama").lower().strip()
    if provider == "mock":
        messages = {
            "silence": ("잠시 생각을 정리한 뒤 이어서 말씀해 주세요.", "nudge"),
            "gaze": ("질문자를 보면서 답변해 주세요.", "critique"),
            "posture": ("고개와 상체를 조금 더 안정시켜 주세요.", "nudge"),
            "gesture": ("손동작을 조금만 차분하게 해 주세요.", "nudge"),
            "motion_absence": ("필요한 부분에서는 자연스러운 손짓을 곁들여 주세요.", "nudge"),
            "speech_rate": ("조금 더 천천히 핵심부터 말씀해 주세요.", "critique"),
            "filler": ("짧게 호흡하고 문장을 이어가 주세요.", "nudge"),
            "vocal_tone": ("핵심어에 억양을 주고 말끝을 분명히 해 주세요.", "nudge"),
        }
        message, tone = messages.get(req.kind, (None, None))
        return AgentFeedbackResponse(message=message, tone=tone)
    if provider == "jeonbuk":
        result = _generate_with_jeonbuk(req)
    elif provider in ("nvidia", "qwen"):
        result = _generate_with_nvidia(req)
    elif provider in ("ollama", "local"):
        result = _generate_with_ollama(req)
    else:
        result = _generate_with_gemini(req)
    # Trim — models occasionally return "  하세요. " with trailing whitespace.
    if result.message is not None:
        result.message = result.message.strip() or None
    return result
