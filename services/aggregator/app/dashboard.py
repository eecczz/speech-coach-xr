"""Rule-based dashboard data (chess-style) — accuracy per axis, quality buckets,
annotated moments, score timeline. No LLM.

The LLM later receives all of this in the SessionBundle and adds 1-line coach_comment
to each AnnotatedMoment.
"""

from __future__ import annotations

from typing import Dict, List, Tuple

from packages.schema import (
    SemanticEvent,
    EventKind,
    AxisAccuracy,
    QualityBuckets,
    TimelineSample,
    AnnotatedMoment,
    QualityLevel,
)
from .session import Session


# ───────── Axis accuracies ─────────
# Each is 0..100. Returns False/available=False when the underlying signal stream
# wasn't populated (e.g., delivery/logic before STT lands).

POSTURE_SWAY_REFERENCE = 0.06    # sway above this = 0 score
EXPRESSION_DIVERSITY_TARGET = 1.5
GESTURE_IDEAL_LO = 0.15           # fraction of frames with hand movement
GESTURE_IDEAL_HI = 0.55
DELIVERY_WPM_IDEAL_LO = 110.0
DELIVERY_WPM_IDEAL_HI = 170.0
FILLER_OK_PER_MIN = 4.0
LONG_PAUSE_S = 3.0


def _avg(xs):
    xs = list(xs)
    return sum(xs) / len(xs) if xs else 0.0


def _clamp(v: float, lo: float = 0.0, hi: float = 100.0) -> float:
    return max(lo, min(hi, v))


def _delivery_score(session: Session) -> tuple[float, str]:
    pfs = session.prosody_frames
    duration_min = max(0.001, session.duration_s / 60.0)
    word_count = sum(getattr(p, "word_count", 0) for p in pfs)
    if word_count > 0:
        avg_wpm = word_count / duration_min
    else:
        weighted_seconds = sum(max(0.0, p.t_end - p.t_start) for p in pfs if p.wpm > 0)
        avg_wpm = (
            sum(p.wpm * max(0.0, p.t_end - p.t_start) for p in pfs if p.wpm > 0)
            / weighted_seconds
            if weighted_seconds > 0 else 0.0
        )

    speed_error = max(DELIVERY_WPM_IDEAL_LO - avg_wpm, avg_wpm - DELIVERY_WPM_IDEAL_HI, 0.0)
    speed_score = _clamp(100.0 - speed_error * 1.2)

    filler_per_min = sum(p.filler_count for p in pfs) / duration_min
    filler_score = _clamp(100.0 - max(0.0, filler_per_min - FILLER_OK_PER_MIN) * 10.0)

    pauses = [p.pause_seconds for p in pfs if p.pause_seconds >= LONG_PAUSE_S]
    pause_penalty = min(45.0, len(pauses) * 12.0 + max(0.0, sum(pauses) - 6.0) * 2.0)
    pause_score = _clamp(100.0 - pause_penalty)

    pitch_sds = [p.pitch_sd_semitones for p in pfs if p.pitch_sd_semitones is not None]
    if pitch_sds:
        pitch_sd = _avg(pitch_sds)
        expressiveness_score = _clamp((pitch_sd - 1.0) / 2.5 * 100.0)
    else:
        expressiveness_score = 70.0

    score = (
        speed_score * 0.35
        + filler_score * 0.25
        + pause_score * 0.25
        + expressiveness_score * 0.15
    )
    note = f"평균 말 속도 분당 {avg_wpm:.0f}어절, 필러 {filler_per_min:.1f}회/분, 긴 침묵 {len(pauses)}회"
    return round(_clamp(score), 1), note


def compute_axis_accuracies(session: Session) -> List[AxisAccuracy]:
    vfs = session.vision_frames
    has_transcript = bool(session.stt_segments)

    out: List[AxisAccuracy] = []

    if not vfs:
        return [
            AxisAccuracy(axis="gaze", score=0, available=False, note="vision 신호 없음"),
            AxisAccuracy(axis="posture", score=0, available=False, note="vision 신호 없음"),
            AxisAccuracy(axis="expression", score=0, available=False, note="vision 신호 없음"),
            AxisAccuracy(axis="gesture", score=0, available=False, note="vision 신호 없음"),
            AxisAccuracy(axis="delivery", score=0, available=False, note="STT 미수집"),
            AxisAccuracy(axis="logic", score=0, available=False, note="STT 미수집"),
        ]

    # Gaze: central fixation ratio averaged, scaled to 100.
    gaze = _avg(f.gaze_fixation_ratio for f in vfs) * 100
    out.append(AxisAccuracy(axis="gaze", score=round(gaze, 1), note=f"정면을 바라본 비율 {gaze:.0f}%"))

    # Posture: penalty for sway + head tilt + chin-on-hand.
    sway = _avg(f.posture_sway for f in vfs)
    sway_score = max(0.0, 1 - sway / POSTURE_SWAY_REFERENCE) * 100
    # Heavy penalty for chin_on_hand and large tilts.
    chin_frames = sum(1 for f in vfs if f.chin_on_hand)
    chin_penalty = min(50, chin_frames / max(1, len(vfs)) * 200)
    tilt_frames = sum(1 for f in vfs if abs(f.head_roll_deg) > 12)
    tilt_penalty = min(30, tilt_frames / max(1, len(vfs)) * 120)
    posture = max(0.0, sway_score - chin_penalty - tilt_penalty)
    out.append(
        AxisAccuracy(
            axis="posture",
            score=round(posture, 1),
            note=f"자세 흔들림 {sway:.3f}, 턱 괴기 {chin_frames}프레임, 머리 기울임 {tilt_frames}프레임",
        )
    )

    # Expression: normalize diversity entropy to 0..100.
    exp_mean = _avg(f.expression_diversity for f in vfs)
    exp = min(100.0, exp_mean / EXPRESSION_DIVERSITY_TARGET * 100)
    out.append(AxisAccuracy(axis="expression", score=round(exp, 1), note=f"표정 변화량 {exp_mean:.2f}"))

    # Gesture: fraction of frames where hand_gesture_freq is in the ideal band.
    in_band = sum(1 for f in vfs if GESTURE_IDEAL_LO <= f.hand_gesture_freq <= GESTURE_IDEAL_HI)
    gesture = (in_band / len(vfs)) * 100
    out.append(AxisAccuracy(axis="gesture", score=round(gesture, 1), note=f"적정 손동작 구간 {gesture:.0f}%"))

    # Delivery: score from measured WPM, filler density, long pauses, and pitch variety.
    if has_transcript and session.prosody_frames:
        score, note = _delivery_score(session)
        out.append(AxisAccuracy(axis="delivery", score=score, note=note))
    else:
        out.append(AxisAccuracy(axis="delivery", score=0, available=False, note="STT 미수집"))

    if has_transcript:
        out.append(AxisAccuracy(axis="logic", score=50.0, note="전사 텍스트 기반 구조 평가는 간이 추정"))
    else:
        out.append(AxisAccuracy(axis="logic", score=0, available=False, note="STT 미수집"))

    return out


def compute_overall_accuracy(axes: List[AxisAccuracy]) -> float:
    avail = [a for a in axes if a.available]
    if not avail:
        return 0.0
    return round(sum(a.score for a in avail) / len(avail), 1)


# ───────── Event → AnnotatedMoment classification ─────────

# Map EventKind to (axis, default_quality, impact, title_template).
# Quality may be upgraded based on event.metrics (e.g., longer chin_on_hand → blunder).
EVENT_QUALITY: Dict[EventKind, Tuple[str, QualityLevel, int]] = {
    EventKind.WPM_SPIKE: ("delivery", QualityLevel.MISTAKE, -10),
    EventKind.WPM_SLOW: ("delivery", QualityLevel.INACCURACY, -7),
    EventKind.LONG_PAUSE: ("delivery", QualityLevel.INACCURACY, -5),
    EventKind.SILENCE_LONG: ("delivery", QualityLevel.MISTAKE, -10),
    EventKind.FILLER_BURST: ("delivery", QualityLevel.MISTAKE, -10),
    EventKind.GAZE_LAPSE: ("gaze", QualityLevel.INACCURACY, -8),
    EventKind.GAZE_DOWNWARD: ("gaze", QualityLevel.MISTAKE, -10),
    EventKind.POSTURE_SWAY: ("posture", QualityLevel.INACCURACY, -7),
    EventKind.HEAD_TILT_SUSTAINED: ("posture", QualityLevel.MISTAKE, -10),
    EventKind.HEAD_NODDING: ("posture", QualityLevel.INACCURACY, -5),
    EventKind.CHIN_ON_HAND: ("posture", QualityLevel.BLUNDER, -20),
    EventKind.EXPRESSION_FLAT: ("expression", QualityLevel.INACCURACY, -5),
    EventKind.HAND_FREEZE: ("gesture", QualityLevel.INACCURACY, -5),
    EventKind.GESTURE_EXCESSIVE: ("gesture", QualityLevel.MISTAKE, -10),
    # Domain-expansion (neutral defaults — scenario YAMLs override severities).
    EventKind.FACE_TOUCH: ("posture", QualityLevel.INACCURACY, -5),
    EventKind.HAND_FIDGET: ("posture", QualityLevel.INACCURACY, -5),
    EventKind.MOTION_HESITATION: ("gesture", QualityLevel.INACCURACY, -5),
    EventKind.LOW_SMILE: ("expression", QualityLevel.INACCURACY, -3),
    EventKind.GAZE_WANDER: ("gaze", QualityLevel.INACCURACY, -5),
    # Phase 2 prosody (audio-pipeline) — delivery axis 핵심 evidence.
    EventKind.MONOTONE: ("delivery", QualityLevel.MISTAKE, -8),
    EventKind.SENTENCE_TRAILING: ("delivery", QualityLevel.INACCURACY, -5),
    EventKind.SLURRED_ARTICULATION: ("delivery", QualityLevel.MISTAKE, -8),
    EventKind.VOICE_FLAT: ("delivery", QualityLevel.INACCURACY, -5),
}


def _upgrade_quality(ev: SemanticEvent, base: QualityLevel) -> QualityLevel:
    """Boost severity by 1 level when duration/intensity exceeds extreme threshold."""
    dur = (ev.metrics or {}).get("duration_s") or (ev.metrics or {}).get("silence_s") or 0
    if ev.kind == EventKind.CHIN_ON_HAND and dur >= 6:
        return QualityLevel.BLUNDER
    if ev.kind == EventKind.SILENCE_LONG and dur >= 8:
        return QualityLevel.BLUNDER
    if ev.kind == EventKind.GAZE_LAPSE and dur >= 12:
        return QualityLevel.MISTAKE
    if ev.kind == EventKind.GAZE_DOWNWARD and dur >= 15:
        return QualityLevel.BLUNDER
    if ev.kind == EventKind.HEAD_TILT_SUSTAINED and dur >= 12:
        return QualityLevel.BLUNDER
    if ev.kind == EventKind.POSTURE_SWAY and dur >= 15:
        return QualityLevel.MISTAKE
    return base


def annotate_moments(
    session: Session,
    max_moments: int = 20,
    events: List[SemanticEvent] | None = None,
) -> List[AnnotatedMoment]:
    """Convert L2 events → AnnotatedMoments. Also synthesize positive moments where
    multiple axes look great simultaneously."""
    out: List[AnnotatedMoment] = []

    # Negative moments from events
    for ev in (events if events is not None else _flatten_events(session)):
        if ev.kind not in EVENT_QUALITY:
            continue
        axis, base_q, impact = EVENT_QUALITY[ev.kind]
        q = _upgrade_quality(ev, base_q)
        # Stronger impact for upgraded quality
        if q == QualityLevel.BLUNDER:
            impact = -20
        elif q == QualityLevel.MISTAKE:
            impact = min(impact, -10)
        dur = (ev.metrics or {}).get("duration_s") or (ev.metrics or {}).get("silence_s")
        out.append(
            AnnotatedMoment(
                t=ev.t_start,
                axis=axis,
                quality=q,
                title=ev.text,
                impact=impact,
                duration_s=dur,
            )
        )

    # Positive moments: scan 5s windows, find ones where 3+ axes are simultaneously good.
    out.extend(_positive_moments(session))

    # Sort by time, cap.
    out.sort(key=lambda m: m.t)
    return out[:max_moments]


def _flatten_events(session: Session) -> List[SemanticEvent]:
    # Imported here to avoid circular import
    from .events import derive_events
    return [e for e in derive_events(session) if e.kind != EventKind.AGGREGATE]


def _positive_moments(session: Session) -> List[AnnotatedMoment]:
    """Surface "잘한 점" moments alongside the mistakes.

    Two layers:
      1. Per-axis EXCELLENT spans — one axis at a time. Catches users who are
         great at gaze even if their gesture is off (the all-axes check was so
         strict it rarely fired and the user concluded "잘한건 안 잡나").
      2. All-axes BRILLIANT windows — rarer; left in but with slightly looser
         thresholds so they can actually trigger.
    """
    out: List[AnnotatedMoment] = []
    vfs = session.vision_frames
    if not vfs:
        return out

    out.extend(_per_axis_excellent_spans(session))
    out.extend(_brilliant_combined_windows(session))
    return out


# Per-axis positive thresholds + minimum sustained duration to count as "EXCELLENT".
# Deliberately reachable for a presenter doing ONE thing well consistently.
_AXIS_POSITIVE = {
    "gaze":       {"min_s": 8.0,  "predicate": lambda f: f.gaze_fixation_ratio > 0.80, "title": "안정적인 시선 유지"},
    "posture":    {"min_s": 15.0, "predicate": lambda f: f.posture_sway < 0.04 and not f.chin_on_hand and abs(f.head_roll_deg) < 8, "title": "흔들림 없는 자세"},
    "expression": {"min_s": 5.0,  "predicate": lambda f: getattr(f, "smile_intensity", 0.0) > 0.15 or f.expression_diversity > 1.2, "title": "풍부한 표정"},
    "gesture":    {"min_s": 8.0,  "predicate": lambda f: GESTURE_IDEAL_LO <= f.hand_gesture_freq <= GESTURE_IDEAL_HI, "title": "적정한 손동작"},
}


def _per_axis_excellent_spans(session: Session) -> List[AnnotatedMoment]:
    out: List[AnnotatedMoment] = []
    for axis, cfg in _AXIS_POSITIVE.items():
        pred = cfg["predicate"]  # type: ignore[assignment]
        min_s = cfg["min_s"]
        title = cfg["title"]
        span_start, last_t = None, None
        for f in session.vision_frames:
            if pred(f):
                if span_start is None:
                    span_start = f.t
                last_t = f.t
            else:
                if span_start is not None and last_t is not None and (last_t - span_start) >= min_s:
                    out.append(
                        AnnotatedMoment(
                            t=span_start, axis=axis,
                            quality=QualityLevel.EXCELLENT,
                            title=title,
                            impact=10,
                            duration_s=last_t - span_start,
                        )
                    )
                span_start, last_t = None, None
        # Tail
        if span_start is not None and last_t is not None and (last_t - span_start) >= min_s:
            out.append(
                AnnotatedMoment(
                    t=span_start, axis=axis,
                    quality=QualityLevel.EXCELLENT,
                    title=title,
                    impact=10,
                    duration_s=last_t - span_start,
                )
            )
    return out


def _brilliant_combined_windows(session: Session) -> List[AnnotatedMoment]:
    """All four vision axes simultaneously good in a 5s window = BRILLIANT.
    Loosened thresholds vs. the original so it can actually trigger occasionally."""
    out: List[AnnotatedMoment] = []
    window_s = 5.0
    cur_start = 0.0
    cur_end = window_s
    while cur_start < session.duration_s:
        frames = [f for f in session.vision_frames if cur_start <= f.t < cur_end]
        if len(frames) >= 3:
            gaze = _avg(f.gaze_fixation_ratio for f in frames)
            sway = _avg(f.posture_sway for f in frames)
            exp = _avg(f.expression_diversity for f in frames)
            gest = _avg(f.hand_gesture_freq for f in frames)
            chin = any(f.chin_on_hand for f in frames)
            tilt = _avg(abs(f.head_roll_deg) for f in frames)
            if (
                not chin
                and tilt < 12
                and gaze > 0.65
                and sway < 0.05
                and exp > 0.8
                and GESTURE_IDEAL_LO <= gest <= GESTURE_IDEAL_HI
            ):
                out.append(
                    AnnotatedMoment(
                        t=cur_start, axis="overall",
                        quality=QualityLevel.BRILLIANT,
                        title="시선·자세·표정·제스처 모두 양호",
                        impact=15,
                        duration_s=window_s,
                    )
                )
        cur_start = cur_end
        cur_end += window_s
    return out


def compute_quality_buckets(moments: List[AnnotatedMoment]) -> QualityBuckets:
    b = QualityBuckets()
    for m in moments:
        if m.quality == QualityLevel.BRILLIANT:
            b.brilliant += 1
        elif m.quality == QualityLevel.EXCELLENT:
            b.excellent += 1
        elif m.quality == QualityLevel.GOOD:
            b.good += 1
        elif m.quality == QualityLevel.INACCURACY:
            b.inaccuracy += 1
        elif m.quality == QualityLevel.MISTAKE:
            b.mistake += 1
        elif m.quality == QualityLevel.BLUNDER:
            b.blunder += 1
    return b


# ───────── Score timeline ─────────

TIMELINE_WINDOW_S = 5.0
BASELINE_SCORE = 75.0


def compute_score_timeline(session: Session, moments: List[AnnotatedMoment]) -> List[TimelineSample]:
    """One sample per TIMELINE_WINDOW_S. Baseline 75, modulated by:
      - +/- of vision axes for this window
      - sum of impacts from moments within window
    """
    samples: List[TimelineSample] = []
    if session.duration_s <= 0:
        return samples
    cur_end = TIMELINE_WINDOW_S
    while cur_end <= session.duration_s + TIMELINE_WINDOW_S:
        cur_start = max(0.0, cur_end - TIMELINE_WINDOW_S)
        frames = [f for f in session.vision_frames if cur_start <= f.t < cur_end]
        local = BASELINE_SCORE
        if frames:
            gaze = _avg(f.gaze_fixation_ratio for f in frames)
            sway = _avg(f.posture_sway for f in frames)
            exp = _avg(f.expression_diversity for f in frames)
            local = (
                BASELINE_SCORE
                + (gaze - 0.6) * 30
                - max(0.0, sway - 0.03) * 200
                + min(15.0, exp * 10)
            )
        # Apply moment impacts inside this window.
        for m in moments:
            if cur_start <= m.t < cur_end:
                local += m.impact
        local = max(0.0, min(100.0, local))
        samples.append(TimelineSample(t=cur_end, score=round(local, 1)))
        cur_end += TIMELINE_WINDOW_S
    return samples
