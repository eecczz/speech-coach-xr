"""L3 LLM dispatcher — swap providers via LLM_PROVIDER env var.

Both providers receive the same SessionBundle and return a ComprehensiveReport.
Keeps Claude, Gemini, and OpenAI-compatible providers behind a single contract
so the API surface in main.py doesn't care which one is active.
"""

from __future__ import annotations

import json
import os
import re
from typing import Any, Optional

from pydantic import ValidationError

from packages.schema import SessionBundle, ComprehensiveReport
from packages.schema.report import AnnotatedMoment, TranscriptCheck
from .prompts import SYSTEM_PROMPT_BASE, OUTPUT_INSTRUCTION
from .rubric import compose_scenario_block


def _compose_system_prompt(scenario: str) -> str:
    """Stitches the universal SYSTEM_PROMPT_BASE, the scenario-specific rubric block
    (loaded from YAML), and the universal OUTPUT_INSTRUCTION into one system text.
    Result is byte-stable per scenario → safe for prompt caching."""
    return f"{SYSTEM_PROMPT_BASE}\n\n{compose_scenario_block(scenario)}\n{OUTPUT_INSTRUCTION}"


def _moment_key(moment: AnnotatedMoment) -> tuple[float, str, str, str]:
    return (
        round(moment.t, 2),
        moment.axis,
        str(moment.quality),
        moment.title,
    )


def _same_rule_moment(a: AnnotatedMoment, b: AnnotatedMoment) -> bool:
    return (
        round(a.t, 2) == round(b.t, 2)
        and a.axis == b.axis
        and a.quality == b.quality
        and a.title == b.title
    )


def _fallback_moment_comment(moment: AnnotatedMoment) -> str:
    title = moment.title
    if "말 속도 느림" in title:
        return "이 구간은 말 속도가 느려 핵심 메시지의 긴장감이 떨어질 수 있습니다. 문장을 짧게 끊고 다음 핵심어로 바로 넘어가는 연습이 필요합니다."
    if "말 속도 급상승" in title:
        return "이 구간은 말이 갑자기 빨라져 청중이 내용을 따라가기 어려울 수 있습니다. 핵심 단어 앞에서 한 박자 늦추면 전달력이 안정됩니다."
    if "침묵" in title:
        return "말이 멈춘 시간이 길어져 흐름이 끊겨 보일 수 있습니다. 다음 문장 첫 단어를 미리 정해 두면 공백을 줄일 수 있습니다."
    if "필러" in title:
        return "필러 표현이 모여 나오면 준비가 덜 된 인상을 줄 수 있습니다. 불필요한 연결어 대신 짧은 침묵으로 호흡을 정리하세요."
    if "시선" in title:
        return "시선이 정면에서 벗어나 청중과의 연결감이 약해질 수 있습니다. 문장마다 한 번씩 카메라나 청중 중심을 확인하세요."
    if "자세" in title or moment.axis == "posture":
        return "자세가 안정되어 신뢰감을 주는 기반은 좋습니다. 이 안정감을 유지한 채 전달 속도와 시선 처리를 함께 끌어올리면 좋습니다."
    if "표정" in title or moment.axis == "expression":
        return "표정 변화가 살아 있어 메시지가 덜 딱딱하게 전달됩니다. 핵심 문장에서도 이 자연스러운 표정을 유지하세요."
    if moment.impact >= 0:
        return "이 구간은 강점으로 볼 수 있습니다. 같은 말하기 리듬을 다음 핵심 구간에도 유지하세요."
    return "이 구간은 선택한 평가 축에서 개선이 필요한 순간입니다. 같은 구간을 다시 보며 원인을 확인하세요."


TRANSCRIPT_CHECK_HINTS: tuple[tuple[str, str, str], ...] = (
    (
        r"공무전",
        "공모전",
        "대회/지원 맥락에서는 '공모전'일 가능성이 큽니다. 실제 발화와 STT 결과를 함께 확인하세요.",
    ),
    (
        r"발악[이가의]?\s*스펀[드을]?",
        "말하기 습관",
        "문맥상 '말하기 습관'처럼 말하기 행동을 설명하는 표현이었을 가능성이 있습니다.",
    ),
    (
        r"확인하게\s*하?기로\s*했다고",
        "확인하기 어렵다고",
        "문장 흐름상 '객관적으로 확인하기 어렵다'는 취지였는지 확인하면 좋습니다.",
    ),
    (
        r"대격기",
        "느꼈기",
        "문장 끝 연결상 '느꼈기 때문입니다'가 STT에서 다르게 기록됐을 가능성이 있습니다.",
    ),
)


def _normalize_phrase(text: str) -> str:
    return re.sub(r"[\s.,!?\"'“”‘’()\[\]{}]+", "", text).lower()


def _phrase_time_range(bundle: SessionBundle, phrase: str) -> tuple[float | None, float | None]:
    target = _normalize_phrase(phrase)
    if not target:
        return None, None
    words = bundle.words
    for i in range(len(words)):
        acc = ""
        for j in range(i, min(i + 8, len(words))):
            acc += _normalize_phrase(words[j].word)
            if acc == target or target in acc:
                return words[i].t_start, words[j].t_end
            if len(acc) > len(target) + 8:
                break
    return None, None


def _build_transcript_checks(bundle: SessionBundle) -> list[TranscriptCheck]:
    transcript = bundle.full_transcript or ""
    if not transcript.strip():
        return []

    checks: list[TranscriptCheck] = []
    seen: set[str] = set()
    for pattern, suggestion, reason in TRANSCRIPT_CHECK_HINTS:
        for match in re.finditer(pattern, transcript):
            phrase = match.group(0).strip()
            key = _normalize_phrase(phrase)
            if not phrase or key in seen:
                continue
            seen.add(key)
            t_start, t_end = _phrase_time_range(bundle, phrase)
            checks.append(
                TranscriptCheck(
                    phrase=phrase,
                    suggestion=suggestion,
                    reason=reason,
                    t_start=t_start,
                    t_end=t_end,
                )
            )
            if len(checks) >= 3:
                return checks
    return checks


def _soften_stt_uncertainty(text: str | None) -> str | None:
    if not text:
        return text
    if not any(term in text for term in ("발화 오류", "발음 오류", "단어 오용", "비문", "딕션", "발음 훈련", "전사 텍스트", "의미 전달", "논리적 명료성")):
        return text

    softened = re.sub(
        r"전사 텍스트\s*상으로?.*?(?:딕션 훈련이 필요합니다\.|명료성을 저해하고 있습니다\.|확인됩니다\.|보완이 필요합니다\.)",
        "전사 텍스트 일부가 어색하게 기록되어 STT 오인식 가능성이 있으므로 언어와 논리 평가는 참고용으로 확인해야 합니다.",
        text,
    )
    softened = re.sub(
        r"전사 텍스트\s*(?:상에서|상으로|기준으로|기준)?[^.]*?(?:의미 전달이 불분명|논리적 명료성|명료성 개선)[^.]*\.",
        "전사 텍스트에 어색한 표현 후보가 있어 STT 오인식 가능성을 확인해야 하며, 언어와 논리 평가는 참고용으로 보는 것이 안전합니다.",
        softened,
    )
    softened = re.sub(
        r"일부 단어의 발음 오류\s*\([^)]*STT[^)]*\).*?(?:보완이 필요합니다\.|확인됩니다\.)",
        "전사 텍스트 일부가 어색하게 기록되어 STT 오인식 가능성이 있으므로 언어와 논리 평가는 참고용으로 확인해야 합니다.",
        softened,
    )
    softened = softened.replace("더딘 및 ", "")
    softened = softened.replace("발화 오류", "전사 텍스트상 어색한 표현")
    softened = softened.replace("발음 오류", "STT 오인식 가능 표현")
    softened = softened.replace("단어 오용", "STT 오인식 가능 표현")
    softened = softened.replace("비문", "STT 오인식 가능 문장")
    softened = softened.replace("정확한 딕션 훈련이 필요합니다", "실제 발화와 STT 결과를 함께 확인해야 합니다")
    softened = softened.replace("딕션 훈련이 필요합니다", "실제 발화와 STT 결과를 함께 확인해야 합니다")
    softened = softened.replace("발음 훈련이 필요합니다", "실제 발화와 STT 결과를 함께 확인해야 합니다")
    return softened


def _sanitize_stt_uncertainty(report: ComprehensiveReport) -> None:
    report.overall_summary = _soften_stt_uncertainty(report.overall_summary) or ""
    for finding in [*report.top_priorities, *report.strengths, *report.improvements]:
        finding.text = _soften_stt_uncertainty(finding.text) or finding.text
        finding.suggestion = _soften_stt_uncertainty(finding.suggestion)
    for prescription in report.training_prescriptions:
        prescription.addresses = _soften_stt_uncertainty(prescription.addresses) or prescription.addresses
        prescription.steps = [
            _soften_stt_uncertainty(step) or step
            for step in prescription.steps
        ]
    for clip in report.evidence_clips:
        clip.reason = _soften_stt_uncertainty(clip.reason) or clip.reason


def _backfill_from_bundle(report: ComprehensiveReport, bundle: SessionBundle) -> ComprehensiveReport:
    """LLM is told to pass rule-derived dashboard fields through verbatim. We enforce
    that here so a sloppy LLM can't break the dashboard."""
    report.accuracy_overall = bundle.accuracy_overall
    report.accuracy_per_axis = bundle.accuracy_per_axis
    report.quality_buckets = bundle.quality_buckets
    report.score_timeline = bundle.score_timeline
    _sanitize_stt_uncertainty(report)

    # For annotated_moments: keep LLM's coach_comment but force-override the
    # rule-derived fields. Match by the full rule identity, not timestamp alone:
    # multiple moments often start at 00:00, and timestamp-only matching can attach
    # a posture comment to a delivery moment.
    if not report.annotated_moments:
        report.annotated_moments = list(bundle.annotated_moments)
    else:
        by_key = {_moment_key(m): m for m in report.annotated_moments}
        merged = []
        for idx, src in enumerate(bundle.annotated_moments):
            llm_m = by_key.get(_moment_key(src))
            if llm_m is None and idx < len(report.annotated_moments):
                candidate = report.annotated_moments[idx]
                if _same_rule_moment(src, candidate):
                    llm_m = candidate
            if llm_m and llm_m.coach_comment:
                src.coach_comment = llm_m.coach_comment
            if not src.coach_comment:
                src.coach_comment = _fallback_moment_comment(src)
            merged.append(src)
        report.annotated_moments = merged

    # Subtitle segments — built from the bundle's STT segments so the review UI
    # can draw word-timed subtitles over the video. LLM never authors these.
    from packages.schema.report import SubtitleSegment, SubtitleWord
    report.subtitle_segments = [
        SubtitleSegment(
            t_start=seg.t_start,
            t_end=seg.t_end,
            text=seg.text,
            words=[SubtitleWord(t_start=w.t_start, t_end=w.t_end, word=w.word) for w in seg.words],
        )
        for seg in bundle.stt_segments
    ]
    report.transcript_checks = _build_transcript_checks(bundle)
    return report


COMPACT_TRANSCRIPT_CHAR_LIMIT = int(os.environ.get("LLM_TRANSCRIPT_CHAR_LIMIT", "6000"))
COMPACT_TIMELINE_LIMIT = int(os.environ.get("LLM_TIMELINE_LIMIT", "30"))
COMPACT_MOMENT_LIMIT = int(os.environ.get("LLM_MOMENT_LIMIT", "18"))
COMPACT_EVENT_LIMIT = int(os.environ.get("LLM_EVENT_LIMIT", "28"))
COMPACT_SEGMENT_LIMIT = int(os.environ.get("LLM_SEGMENT_LIMIT", "14"))


def _round_metric(value: Any, digits: int = 2) -> Any:
    if isinstance(value, float):
        return round(value, digits)
    return value


def _sample_evenly(items: list[Any], limit: int) -> list[Any]:
    if limit <= 0 or len(items) <= limit:
        return list(items)
    if limit == 1:
        return [items[0]]
    step = (len(items) - 1) / (limit - 1)
    indices: list[int] = []
    for i in range(limit):
        idx = round(i * step)
        if idx not in indices:
            indices.append(idx)
    return [items[i] for i in indices]


def _model_value(value: Any) -> str:
    return getattr(value, "value", str(value))


def _compact_transcript(bundle: SessionBundle) -> dict[str, Any]:
    raw = (bundle.full_transcript or "").strip()
    limit = max(1000, COMPACT_TRANSCRIPT_CHAR_LIMIT)
    truncated = len(raw) > limit
    if truncated:
        head_len = int(limit * 0.65)
        tail_len = int(limit * 0.25)
        text = f"{raw[:head_len].rstrip()}\n...[중간 전사 생략]...\n{raw[-tail_len:].lstrip()}"
    else:
        text = raw

    return {
        "text": text,
        "char_count": len(raw),
        "truncated": truncated,
        "segment_count": len(bundle.stt_segments),
        "word_count": len(bundle.words),
        "segment_samples": [
            {
                "t_start": round(seg.t_start, 2),
                "t_end": round(seg.t_end, 2),
                "text": seg.text,
            }
            for seg in _sample_evenly(list(bundle.stt_segments), COMPACT_SEGMENT_LIMIT)
        ],
    }


def _compact_axis_scores(bundle: SessionBundle) -> list[dict[str, Any]]:
    return [
        {
            "axis": item.axis,
            "score": round(item.score, 1),
            "available": item.available,
            "note": item.note,
        }
        for item in bundle.accuracy_per_axis
    ]


def _compact_moments(bundle: SessionBundle) -> list[dict[str, Any]]:
    ranked = sorted(
        bundle.annotated_moments,
        key=lambda moment: (moment.impact >= 0, -abs(moment.impact), moment.t),
    )[:COMPACT_MOMENT_LIMIT]
    ranked.sort(key=lambda moment: moment.t)
    return [
        {
            "t": round(moment.t, 2),
            "duration_s": round(moment.duration_s, 2) if moment.duration_s is not None else None,
            "axis": moment.axis,
            "quality": _model_value(moment.quality),
            "title": moment.title,
            "impact": moment.impact,
        }
        for moment in ranked
    ]


def _compact_events(bundle: SessionBundle) -> list[dict[str, Any]]:
    sampled = _sample_evenly(
        sorted(bundle.events, key=lambda item: (item.t_start, item.t_end)),
        COMPACT_EVENT_LIMIT,
    )
    return [
        {
            "kind": _model_value(event.kind),
            "t_start": round(event.t_start, 2),
            "t_end": round(event.t_end, 2),
            "text": event.text,
            "transcript_snippet": event.transcript_snippet,
            "metrics": {
                key: _round_metric(value)
                for key, value in (event.metrics or {}).items()
            },
        }
        for event in sampled
    ]


def _compact_timeline(bundle: SessionBundle) -> list[dict[str, Any]]:
    return [
        {
            "t": round(sample.t, 2),
            "score": round(sample.score, 1),
        }
        for sample in _sample_evenly(list(bundle.score_timeline), COMPACT_TIMELINE_LIMIT)
    ]


def _compact_bundle_for_llm(bundle: SessionBundle) -> dict[str, Any]:
    return {
        "session": {
            "session_id": bundle.session_id,
            "scenario": bundle.scenario,
            "duration_s": round(bundle.duration_s, 1),
            "focus_goals": bundle.focus_goals,
        },
        "counts": {
            "events": len(bundle.events),
            "annotated_moments": len(bundle.annotated_moments),
            "score_timeline": len(bundle.score_timeline),
            "stt_segments": len(bundle.stt_segments),
            "words": len(bundle.words),
        },
        "aggregates": bundle.aggregates.model_dump(mode="json"),
        "dashboard": {
            "accuracy_overall": round(bundle.accuracy_overall, 1),
            "accuracy_per_axis": _compact_axis_scores(bundle),
            "quality_buckets": bundle.quality_buckets.model_dump(mode="json"),
            "score_timeline_sample": _compact_timeline(bundle),
        },
        "annotated_moments_for_comment": _compact_moments(bundle),
        "semantic_events_sample": _compact_events(bundle),
        "transcript": _compact_transcript(bundle),
        "note": (
            "긴 영상 안정성을 위해 raw vision frames, full word list, full event list는 생략했습니다. "
            "출력의 dashboard/subtitle 필드는 서버가 원본 bundle에서 backfill하므로, "
            "LLM은 요약 신호를 근거로 overall_summary, priorities, coach_comment, training만 작성하세요."
        ),
    }


def _user_payload(bundle: SessionBundle) -> str:
    inner = json.dumps(_compact_bundle_for_llm(bundle), ensure_ascii=False, indent=2)
    focus = ", ".join(bundle.focus_goals) if bundle.focus_goals else "없음"
    return (
        f"세션 ID: {bundle.session_id}\n"
        f"길이: {bundle.duration_s:.1f}s\n\n"
        f"사용자 선택 포커스: {focus}\n\n"
        f"## CompactSessionBundle (LLM용 요약 신호)\n```json\n{inner}\n```"
    )


def _parse_report_text(text: str) -> ComprehensiveReport:
    """Parse JSON returned by a plain chat-completions endpoint.

    Gemini/Claude paths use SDK-level structured outputs. The Jeonbuk endpoint is
    OpenAI-compatible chat, so we defensively strip common markdown fences before
    validating against the same ComprehensiveReport schema.
    """
    raw = text.strip()
    if raw.startswith("```"):
        lines = raw.splitlines()
        if lines and lines[0].strip().startswith("```"):
            lines = lines[1:]
        if lines and lines[-1].strip() == "```":
            lines = lines[:-1]
        raw = "\n".join(lines).strip()

    start = raw.find("{")
    end = raw.rfind("}")
    if start >= 0 and end > start:
        raw = raw[start : end + 1]
    return ComprehensiveReport.model_validate_json(raw)


JEONBUK_JSON_CONTRACT = """출력은 JSON 객체 하나여야 합니다. Markdown 금지.

필수 최상위 키:
- session_id: string
- rubric: {logic, delivery, gaze, posture, expression} 각 0~5 숫자
- overall_summary: string
- top_priorities: Finding[]
- strengths: Finding[]
- improvements: Finding[]
- training_prescriptions: TrainingPrescription[]
- evidence_clips: EvidenceClip[]
- accuracy_overall, accuracy_per_axis, quality_buckets, annotated_moments, score_timeline

Finding 형식: {"text": string, "evidence_t": [start, end] 또는 null, "suggestion": string 또는 null}
EvidenceClip 형식: {"t_start": number, "t_end": number, "reason": string}
TrainingPrescription 형식: {"title": string, "addresses": string, "steps": string[]}

입력 데이터가 부족해도 키를 생략하지 말고 빈 배열 또는 데이터 부족 설명을 넣으세요.
"""


# --------- Gemini (default) ----------

_gemini_client = None


def _get_gemini():
    global _gemini_client
    if _gemini_client is None:
        from google import genai  # lazy so the SDK isn't required if user runs Claude

        _gemini_client = genai.Client(api_key=os.environ["GOOGLE_API_KEY"])
    return _gemini_client


GEMINI_MODEL = os.environ.get("GEMINI_MODEL", "gemini-2.0-flash")


def generate_with_gemini(bundle: SessionBundle) -> ComprehensiveReport:
    if not os.environ.get("GOOGLE_API_KEY"):
        raise RuntimeError("GOOGLE_API_KEY not set")

    from google.genai import types

    client = _get_gemini()
    response = client.models.generate_content(
        model=GEMINI_MODEL,
        contents=_user_payload(bundle),
        config=types.GenerateContentConfig(
            system_instruction=_compose_system_prompt(bundle.scenario),
            response_mime_type="application/json",
            response_schema=ComprehensiveReport,
            temperature=0.0,
        ),
    )
    parsed: Optional[ComprehensiveReport] = response.parsed
    if parsed is None:
        parsed = ComprehensiveReport.model_validate_json(response.text)
    if not parsed.session_id:
        parsed.session_id = bundle.session_id
    return _backfill_from_bundle(parsed, bundle)


# --------- Claude (alternate) ----------

_claude_client = None


def _get_claude():
    global _claude_client
    if _claude_client is None:
        import anthropic

        _claude_client = anthropic.Anthropic()  # reads ANTHROPIC_API_KEY
    return _claude_client


CLAUDE_MODEL = os.environ.get("CLAUDE_MODEL", "claude-sonnet-4-6")


def generate_with_claude(bundle: SessionBundle) -> ComprehensiveReport:
    if not os.environ.get("ANTHROPIC_API_KEY"):
        raise RuntimeError("ANTHROPIC_API_KEY not set")

    client = _get_claude()
    # System split into two blocks for caching: universal base (highly cacheable across
    # any scenario) + scenario-specific rubric + output rules (cacheable per scenario).
    response = client.messages.parse(
        model=CLAUDE_MODEL,
        max_tokens=8000,
        system=[
            {"type": "text", "text": SYSTEM_PROMPT_BASE},
            {
                "type": "text",
                "text": compose_scenario_block(bundle.scenario) + "\n" + OUTPUT_INSTRUCTION,
                "cache_control": {"type": "ephemeral"},
            },
        ],
        messages=[{"role": "user", "content": _user_payload(bundle)}],
        output_format=ComprehensiveReport,
        temperature=0.0,
    )
    parsed: Optional[ComprehensiveReport] = response.parsed_output
    if parsed is None:
        raise RuntimeError(f"Claude returned unparseable output (stop_reason={response.stop_reason})")
    if not parsed.session_id:
        parsed.session_id = bundle.session_id
    return _backfill_from_bundle(parsed, bundle)


# --------- Jeonbuk AI student API (OpenAI-compatible) ----------

_jeonbuk_client = None

JEONBUK_BASE_URL = os.environ.get(
    "JEONBUK_BASE_URL",
    "https://ai.jb.go.kr/student-api/v1",
)
JEONBUK_CHAT_MODEL = os.environ.get("JEONBUK_CHAT_MODEL", "gemma-4-31b-turbo")


def _get_jeonbuk():
    global _jeonbuk_client
    if _jeonbuk_client is None:
        from openai import OpenAI

        _jeonbuk_client = OpenAI(
            base_url=JEONBUK_BASE_URL,
            api_key=os.environ["JEONBUK_API_KEY"],
        )
    return _jeonbuk_client


def _jeonbuk_chat(messages: list[dict], *, temperature: float = 0.2):
    client = _get_jeonbuk()
    kwargs = {
        "model": JEONBUK_CHAT_MODEL,
        "messages": messages,
        "max_tokens": 8000,
        "temperature": temperature,
    }
    try:
        return client.chat.completions.create(
            **kwargs,
            response_format={"type": "json_object"},
        )
    except Exception as exc:
        # Some OpenAI-compatible gateways expose chat completions but not JSON mode.
        if "response_format" not in str(exc):
            raise
        return client.chat.completions.create(**kwargs)


def _repair_jeonbuk_report(bundle: SessionBundle, raw_text: str, error: ValidationError) -> ComprehensiveReport:
    response = _jeonbuk_chat(
        [
            {
                "role": "system",
                "content": (
                    "당신은 JSON 스키마 수정기입니다. 사용자의 잘못된 평가 JSON을 "
                    "ComprehensiveReport 스키마에 맞는 JSON 객체 하나로만 고치세요."
                ),
            },
            {
                "role": "user",
                "content": (
                    f"{JEONBUK_JSON_CONTRACT}\n\n"
                    f"session_id는 반드시 {bundle.session_id!r} 입니다.\n"
                    "accuracy_overall, accuracy_per_axis, quality_buckets, "
                    "annotated_moments, score_timeline은 아래 SessionBundle의 값을 그대로 사용하세요.\n\n"
                    f"검증 오류:\n{error}\n\n"
                    f"잘못된 JSON 후보:\n{raw_text}\n\n"
                    f"원본 입력:\n{_user_payload(bundle)}"
                ),
            },
        ],
        temperature=0.0,
    )
    return _parse_report_text(response.choices[0].message.content or "")


def generate_with_jeonbuk(bundle: SessionBundle) -> ComprehensiveReport:
    if not os.environ.get("JEONBUK_API_KEY"):
        raise RuntimeError("JEONBUK_API_KEY not set")

    response = _jeonbuk_chat(
        [
            {"role": "system", "content": _compose_system_prompt(bundle.scenario)},
            {
                "role": "user",
                "content": f"{_user_payload(bundle)}\n\n{JEONBUK_JSON_CONTRACT}",
            },
        ],
        temperature=0.0,
    )
    content = response.choices[0].message.content or ""
    try:
        parsed = _parse_report_text(content)
    except ValidationError as exc:
        parsed = _repair_jeonbuk_report(bundle, content, exc)
    if not parsed.session_id:
        parsed.session_id = bundle.session_id
    return _backfill_from_bundle(parsed, bundle)


# --------- Dispatcher ----------

PROVIDER = os.environ.get("LLM_PROVIDER", "gemini").lower()


def generate(bundle: SessionBundle) -> ComprehensiveReport:
    if PROVIDER == "claude":
        return generate_with_claude(bundle)
    if PROVIDER == "gemini":
        return generate_with_gemini(bundle)
    if PROVIDER == "jeonbuk":
        return generate_with_jeonbuk(bundle)
    raise RuntimeError(f"unknown LLM_PROVIDER: {PROVIDER!r}")


def provider_info() -> dict:
    if PROVIDER == "mock":
        return {"provider": "mock", "model": "local-demo"}
    if PROVIDER == "claude":
        return {"provider": "claude", "model": CLAUDE_MODEL}
    if PROVIDER == "jeonbuk":
        return {"provider": "jeonbuk", "model": JEONBUK_CHAT_MODEL, "base_url": JEONBUK_BASE_URL}
    return {"provider": "gemini", "model": GEMINI_MODEL}
