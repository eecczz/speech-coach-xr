"""L2 — semantic event derivation from raw session signals.

Run once at session end. Output is a short list of timestamped events (~50-100) that
the LLM can cite directly. Goal: replace the LLM's need to interpret raw numbers.

Heuristics here are intentionally simple and inspectable. Tune thresholds per coaching
domain (presentation vs dating vs interview).
"""

from __future__ import annotations

import os
import statistics
from typing import List

from packages.schema import (
    SemanticEvent,
    EventKind,
    SessionBundle,
)
from packages.schema.event import SessionAggregates, WordSpan
from .session import Session

WPM_SPIKE_MULTIPLIER = 1.4
WPM_EVENT_MIN_SEGMENT_S = 2.0
WPM_EVENT_MIN_DELTA = 45.0
WPM_SLOW_CUTOFF = 95.0
LONG_PAUSE_S = 3.0
FILLER_BURST_PER_MIN = 10
FILLER_BURST_MIN_COUNT = 2
FILLER_BURST_MIN_WINDOW_MIN = 0.25
SMILE_SIGNAL_SCENARIOS = {"dating", "customer_service", "casual"}
GAZE_LAPSE_RATIO = 0.4          # was 0.3 — with pupil-aware gaze, head-only "OK"
                                # frames now also need centered eyes; mild drift counts
GAZE_LAPSE_MIN_S = 3.0          # was 5.0 — shorter dwell catches brief look-aways
POSTURE_SWAY_THRESHOLD = 0.06   # was 0.08
EXPRESSION_FLAT_THRESHOLD = 0.4
HAND_FREEZE_THRESHOLD = 0.05
HAND_FREEZE_MIN_S = 5.0         # was 8.0 — catch shorter still-hand spans

# Sudden / excessive gesture — peak wrist speed sustained above threshold.
GESTURE_EXCESSIVE_SPEED = 1.2
GESTURE_EXCESSIVE_MIN_S = 1.0   # was 1.5 — even shorter bursts read as "갑작스러운"

# ── Domain-expansion thresholds ──
# Face touch (cheek/nose/forehead, not chin) — anxiety / 이중동작 신호.
FACE_TOUCH_MIN_S = 2.0
# Hand fidget — composite score sustained.
FIDGET_THRESHOLD = 0.35
FIDGET_MIN_S = 3.0
# Motion hesitation — wrist direction reversals/sec.
MOTION_HESITATION_RATE = 1.2     # ≥1.2 reversals per second
MOTION_HESITATION_MIN_S = 2.0
# Low smile — sustained low smile intensity (시나리오별로 의미 다름; 발표보단 소개팅에서 강).
LOW_SMILE_THRESHOLD = 0.05       # essentially "no smile"
LOW_SMILE_MIN_S = 10.0           # long stretches only — short serious moments are fine
# Gaze yaw sway — head-yaw stddev (degrees) sustained.
GAZE_WANDER_DEG = 8.0
GAZE_WANDER_MIN_S = 3.0

# Head pose thresholds — softened so short test sessions still surface posture issues.
HEAD_TILT_DEG = 12.0            # was 15° — milder tilts still register
HEAD_TILT_MIN_S = 3.0           # was 4.0
HEAD_PITCH_DOWN_DEG = 12.0      # was 15° — slight downward read still flagged
GAZE_DOWN_MIN_S = 3.0           # was 4.0
CHIN_ON_HAND_MIN_S = 2.0        # was 3.0
NOD_OSC_PER_S = 1.5
NOD_MIN_S = 2.5                 # was 3.0
NOD_PITCH_AMPLITUDE_DEG = 5.0   # was 6.0

# Audio silence
SILENCE_LONG_S = 4.0            # silence_seconds ≥ 4 in a window = long pause

# Phase 2 prosody thresholds (audio-pipeline /analyze populates these per segment).
MONOTONE_PITCH_SD_ST = 2.0       # ≤ 2 semitones SD = noticeably monotone
MONOTONE_MIN_SEGMENTS = 3        # need ≥ 3 consecutive low-SD segments to flag
SENTENCE_TRAILING_RATIO = 0.55   # end-0.5s rms < 55% of segment mean = trailing off
SENTENCE_TRAILING_MIN_COUNT = 3  # ≥ 3 trailing endings in session ⇒ pattern
SLURRED_ARTICULATION_PROB = 0.55 # mean word probability < 0.55 = unclear
SLURRED_MIN_SEGMENTS = 3
DEBUG_EVENTS = os.environ.get("AGGREGATOR_DEBUG_EVENTS") == "1"


def _avg(xs: List[float]) -> float:
    return sum(xs) / len(xs) if xs else 0.0


def _debug_signal_snapshot(session: Session) -> None:
    vf = session.vision_frames
    pf = session.prosody_frames
    print(f"[derive] vision_frames={len(vf)} prosody_frames={len(pf)} stt_segments={len(session.stt_segments)}", flush=True)
    if vf:
        sample = vf[len(vf) // 2]
        print(
            f"[derive] vision sample @ t={sample.t:.1f}s: "
            f"gaze={sample.gaze_fixation_ratio:.2f} sway={sample.posture_sway:.3f} "
            f"tilt_deg={sample.shoulder_tilt:.2f} exp={sample.expression_diversity:.2f} "
            f"gest_freq={sample.hand_gesture_freq:.2f} gest_vmax={getattr(sample, 'hand_velocity_max', 0):.2f} "
            f"head_p={sample.head_pitch_deg:.0f}° y={sample.head_yaw_deg:.0f}° r={sample.head_roll_deg:.0f}° "
            f"chin={sample.chin_on_hand} mouth={sample.mouth_open:.2f}",
            flush=True,
        )

        def rng(name, getter):
            xs = [getter(f) for f in vf]
            return f"{name}=[{min(xs):.3f}, {max(xs):.3f}]"

        print(
            f"[derive] vision ranges: "
            f"{rng('gaze', lambda f: f.gaze_fixation_ratio)} "
            f"{rng('sway', lambda f: f.posture_sway)} "
            f"{rng('exp', lambda f: f.expression_diversity)} "
            f"{rng('gest_freq', lambda f: f.hand_gesture_freq)} "
            f"{rng('gest_vmax', lambda f: getattr(f, 'hand_velocity_max', 0))} "
            f"{rng('head_pitch', lambda f: f.head_pitch_deg)} "
            f"{rng('head_roll', lambda f: f.head_roll_deg)} "
            f"chin_frames={sum(1 for f in vf if f.chin_on_hand)}",
            flush=True,
        )
    else:
        print("[derive] WARNING — no vision frames in session. Browser→/ws/signals likely never sent them, "
              "or the WS disconnected before frames flowed.", flush=True)


def derive_events(session: Session) -> List[SemanticEvent]:
    events: List[SemanticEvent] = []

    if DEBUG_EVENTS:
        _debug_signal_snapshot(session)

    # --- Prosody-driven events ---
    if session.prosody_frames:
        wpms = [p.wpm for p in session.prosody_frames if p.wpm > 0]
        reliable_wpms = [
            p.wpm for p in session.prosody_frames
            if p.wpm > 0 and (p.t_end - p.t_start) >= WPM_EVENT_MIN_SEGMENT_S
        ]
        baseline_wpm = (
            statistics.median(reliable_wpms)
            if len(reliable_wpms) >= 3
            else _avg(wpms)
        )
        for p in session.prosody_frames:
            seg_dur = max(0.0, p.t_end - p.t_start)
            spike_cutoff = max(
                baseline_wpm * WPM_SPIKE_MULTIPLIER,
                baseline_wpm + WPM_EVENT_MIN_DELTA,
            )
            if (
                baseline_wpm > 0
                and seg_dur >= WPM_EVENT_MIN_SEGMENT_S
                and p.wpm > spike_cutoff
            ):
                events.append(
                    SemanticEvent(
                        kind=EventKind.WPM_SPIKE,
                        t_start=p.t_start,
                        t_end=p.t_end,
                        text=f"말 속도 급상승: WPM {p.wpm:.0f} (기준 {baseline_wpm:.0f}, +{(p.wpm/baseline_wpm - 1)*100:.0f}%)",
                        transcript_snippet=_transcript_around(session, p.t_start + seg_dur / 2),
                        metrics={"wpm": p.wpm, "baseline_wpm": baseline_wpm, "duration_s": seg_dur},
                    )
                )
            if (
                seg_dur >= WPM_EVENT_MIN_SEGMENT_S
                and p.wpm > 0
                and p.wpm < WPM_SLOW_CUTOFF
            ):
                snippet = _transcript_around(session, p.t_start + seg_dur / 2)
                events.append(
                    SemanticEvent(
                        kind=EventKind.WPM_SLOW,
                        t_start=p.t_start,
                        t_end=p.t_end,
                        text=f"말 속도 느림: 분당 {p.wpm:.0f}어절",
                        transcript_snippet=snippet,
                        metrics={"wpm": p.wpm, "cutoff_wpm": WPM_SLOW_CUTOFF, "duration_s": seg_dur},
                    )
                )
            if p.pause_seconds >= LONG_PAUSE_S:
                snippet = _transcript_around(session, p.t_start)
                events.append(
                    SemanticEvent(
                        kind=EventKind.LONG_PAUSE,
                        t_start=p.t_start,
                        t_end=p.t_end,
                        text=f"{p.pause_seconds:.1f}초 침묵",
                        transcript_snippet=snippet,
                        metrics={"pause_s": p.pause_seconds},
                    )
                )
            window_minutes = max(FILLER_BURST_MIN_WINDOW_MIN, seg_dur / 60.0)
            if (
                p.filler_count >= FILLER_BURST_MIN_COUNT
                and (p.filler_count / window_minutes) >= FILLER_BURST_PER_MIN
            ):
                snippet = _transcript_around(session, p.t_start + seg_dur / 2)
                events.append(
                    SemanticEvent(
                        kind=EventKind.FILLER_BURST,
                        t_start=p.t_start,
                        t_end=p.t_end,
                        text=f"필러 집중: {p.filler_count}개 ({', '.join(p.filler_terms)})",
                        transcript_snippet=snippet,
                        metrics={
                            "filler_count": float(p.filler_count),
                            "filler_per_min": p.filler_count / window_minutes,
                            "duration_s": seg_dur,
                        },
                    )
                )

    # --- Vision-driven events ---
    events.extend(_gaze_lapses(session))
    events.extend(_gaze_downward_events(session))
    events.extend(_posture_sway_events(session))
    events.extend(_head_tilt_events(session))
    events.extend(_head_nodding_events(session))
    events.extend(_chin_on_hand_events(session))
    events.extend(_expression_flat_events(session))
    events.extend(_hand_freeze_events(session))
    events.extend(_gesture_excessive_events(session))
    # Domain-expansion detectors — neutral here; per-scenario rubric YAML decides
    # how heavily each one weighs. Low-smile is only emitted for warmth-driven
    # contexts; in presentations it created confusing "무미소" moments even when
    # expression diversity was high.
    events.extend(_face_touch_events(session))
    events.extend(_hand_fidget_events(session))
    events.extend(_motion_hesitation_events(session))
    if session.scenario in SMILE_SIGNAL_SCENARIOS:
        events.extend(_low_smile_events(session))
    events.extend(_gaze_wander_events(session))

    # --- Audio silence (from browser RMS detector OR Phase 2 prosody) ---
    # TEMP DISABLED: silence was flooding the moments list and drowning out the
    # non-verbal events. Re-enable when non-verbal coverage is verified.
    # events.extend(_silence_long_events(session))

    # --- Phase 2 prosody events (need audio-pipeline /analyze) ---
    events.extend(_monotone_events(session))
    events.extend(_sentence_trailing_events(session))
    events.extend(_slurred_articulation_events(session))

    # --- Session-wide aggregate event ---
    aggs = compute_aggregates(session)
    events.append(
        SemanticEvent(
            kind=EventKind.AGGREGATE,
            t_start=0.0,
            t_end=session.duration_s,
            text=(
                f"세션 평균 말 속도 분당 {aggs.avg_wpm:.0f}어절, "
                f"필러 {aggs.filler_per_minute:.1f}회/분, "
                f"정면을 바라본 비율 {aggs.gaze_central_fraction*100:.0f}%, "
                f"표정 다양성 {aggs.expression_diversity_mean:.2f}"
            ),
            metrics={
                "avg_wpm": aggs.avg_wpm,
                "filler_per_min": aggs.filler_per_minute,
                "gaze_central_frac": aggs.gaze_central_fraction,
                "posture_sway_mean": aggs.posture_sway_mean,
                "expression_diversity_mean": aggs.expression_diversity_mean,
            },
        )
    )

    if DEBUG_EVENTS:
        from collections import Counter
        tally = Counter(e.kind.value for e in events)
        print(f"[derive] events emitted: {dict(tally)}", flush=True)

    return events


def _transcript_around(session: Session, t: float, span_s: float = 4.0) -> str:
    """Return nearby STT text for grounding verbal events."""
    snippets = []
    for seg in session.stt_segments:
        if seg.t_end >= t - span_s and seg.t_start <= t + span_s:
            snippets.append(seg.text.strip())
    return " ".join(snippets).strip()


def _gaze_lapses(session: Session) -> List[SemanticEvent]:
    """Find continuous spans where gaze_fixation_ratio stays below threshold."""
    out: List[SemanticEvent] = []
    if not session.vision_frames:
        return out
    span_start = None
    last_t = None
    for f in session.vision_frames:
        if f.gaze_fixation_ratio < GAZE_LAPSE_RATIO:
            if span_start is None:
                span_start = f.t
            last_t = f.t
        else:
            if span_start is not None and last_t is not None and (last_t - span_start) >= GAZE_LAPSE_MIN_S:
                out.append(
                    SemanticEvent(
                        kind=EventKind.GAZE_LAPSE,
                        t_start=span_start,
                        t_end=last_t,
                        text=f"시선 이탈 {last_t - span_start:.1f}초",
                        metrics={"duration_s": last_t - span_start},
                    )
                )
            span_start = None
            last_t = None
    # Trailing span at session end
    if span_start is not None and last_t is not None and (last_t - span_start) >= GAZE_LAPSE_MIN_S:
        out.append(
            SemanticEvent(
                kind=EventKind.GAZE_LAPSE,
                t_start=span_start,
                t_end=last_t,
                text=f"시선 이탈 {last_t - span_start:.1f}초",
                metrics={"duration_s": last_t - span_start},
            )
        )
    return out


def _posture_sway_events(session: Session) -> List[SemanticEvent]:
    out: List[SemanticEvent] = []
    span_start, last_t = None, None
    for f in session.vision_frames:
        if f.posture_sway > POSTURE_SWAY_THRESHOLD:
            if span_start is None:
                span_start = f.t
            last_t = f.t
        else:
            if span_start is not None and last_t is not None and (last_t - span_start) >= 5.0:
                out.append(
                    SemanticEvent(
                        kind=EventKind.POSTURE_SWAY,
                        t_start=span_start,
                        t_end=last_t,
                        text=f"어깨 흔들림 지속 {last_t - span_start:.1f}초",
                        metrics={"duration_s": last_t - span_start},
                    )
                )
            span_start, last_t = None, None
    return out


def _expression_flat_events(session: Session) -> List[SemanticEvent]:
    out: List[SemanticEvent] = []
    span_start, last_t = None, None
    for f in session.vision_frames:
        if f.expression_diversity < EXPRESSION_FLAT_THRESHOLD:
            if span_start is None:
                span_start = f.t
            last_t = f.t
        else:
            if span_start is not None and last_t is not None and (last_t - span_start) >= 15.0:
                out.append(
                    SemanticEvent(
                        kind=EventKind.EXPRESSION_FLAT,
                        t_start=span_start,
                        t_end=last_t,
                        text=f"표정 단조 {last_t - span_start:.0f}초",
                    )
                )
            span_start, last_t = None, None
    return out


def _hand_freeze_events(session: Session) -> List[SemanticEvent]:
    out: List[SemanticEvent] = []
    span_start, last_t = None, None
    for f in session.vision_frames:
        if f.hand_gesture_freq < HAND_FREEZE_THRESHOLD:
            if span_start is None:
                span_start = f.t
            last_t = f.t
        else:
            if span_start is not None and last_t is not None and (last_t - span_start) >= HAND_FREEZE_MIN_S:
                out.append(
                    SemanticEvent(
                        kind=EventKind.HAND_FREEZE,
                        t_start=span_start,
                        t_end=last_t,
                        text=f"제스처 정지 {last_t - span_start:.0f}초",
                    )
                )
            span_start, last_t = None, None
    return out


def _gesture_excessive_events(session: Session) -> List[SemanticEvent]:
    """Detect spans where the peak wrist speed sustained above an "excessive" threshold —
    sudden frantic gestures the user wanted flagged as mistakes."""
    out: List[SemanticEvent] = []
    span_start, last_t, peak_speed = None, None, 0.0
    for f in session.vision_frames:
        v = getattr(f, "hand_velocity_max", 0.0)
        if v > GESTURE_EXCESSIVE_SPEED:
            if span_start is None:
                span_start = f.t
            last_t = f.t
            if v > peak_speed:
                peak_speed = v
        else:
            if span_start is not None and last_t is not None and (last_t - span_start) >= GESTURE_EXCESSIVE_MIN_S:
                dur = last_t - span_start
                out.append(
                    SemanticEvent(
                        kind=EventKind.GESTURE_EXCESSIVE,
                        t_start=span_start,
                        t_end=last_t,
                        text=f"과도한 손동작 {dur:.1f}초 (최대 {peak_speed:.2f})",
                        metrics={"duration_s": dur, "peak_speed": peak_speed},
                    )
                )
            span_start, last_t, peak_speed = None, None, 0.0
    # Tail span — same emit rule for an excessive burst that runs to session end.
    if span_start is not None and last_t is not None and (last_t - span_start) >= GESTURE_EXCESSIVE_MIN_S:
        dur = last_t - span_start
        out.append(
            SemanticEvent(
                kind=EventKind.GESTURE_EXCESSIVE,
                t_start=span_start,
                t_end=last_t,
                text=f"과도한 손동작 {dur:.1f}초 (최대 {peak_speed:.2f})",
                metrics={"duration_s": dur, "peak_speed": peak_speed},
            )
        )
    return out


# ───── Domain-expansion detectors ─────
# Share a single span-detector helper: walk frames, accumulate while a per-frame
# predicate is true, emit once when the run ends if it lasted ≥ min_s.

def _emit_span_events(
    session: Session,
    predicate,                            # f -> bool
    kind: EventKind,
    min_s: float,
    text_fn,                              # (duration_s) -> str
) -> List[SemanticEvent]:
    out: List[SemanticEvent] = []
    span_start, last_t = None, None
    for f in session.vision_frames:
        if predicate(f):
            if span_start is None:
                span_start = f.t
            last_t = f.t
        else:
            if span_start is not None and last_t is not None and (last_t - span_start) >= min_s:
                dur = last_t - span_start
                out.append(SemanticEvent(
                    kind=kind, t_start=span_start, t_end=last_t,
                    text=text_fn(dur), metrics={"duration_s": dur},
                ))
            span_start, last_t = None, None
    # Tail
    if span_start is not None and last_t is not None and (last_t - span_start) >= min_s:
        dur = last_t - span_start
        out.append(SemanticEvent(
            kind=kind, t_start=span_start, t_end=last_t,
            text=text_fn(dur), metrics={"duration_s": dur},
        ))
    return out


def _face_touch_events(session: Session) -> List[SemanticEvent]:
    """Wrist near upper face (not chin) sustained — nose/forehead/hair touching."""
    return _emit_span_events(
        session,
        lambda f: bool(getattr(f, "face_touch_other", False)),
        EventKind.FACE_TOUCH,
        FACE_TOUCH_MIN_S,
        lambda dur: f"얼굴(코·이마·머리) 만지기 {dur:.1f}초",
    )


def _hand_fidget_events(session: Session) -> List[SemanticEvent]:
    """Hands clasped + small repetitive motion = 안절부절 (fidget)."""
    return _emit_span_events(
        session,
        lambda f: getattr(f, "hand_fidget_score", 0.0) >= FIDGET_THRESHOLD,
        EventKind.HAND_FIDGET,
        FIDGET_MIN_S,
        lambda dur: f"안절부절 손 만지작 {dur:.1f}초",
    )


def _motion_hesitation_events(session: Session) -> List[SemanticEvent]:
    """Repeated wrist direction reversals = 이중동작/망설임."""
    return _emit_span_events(
        session,
        lambda f: getattr(f, "motion_reversal_rate", 0.0) >= MOTION_HESITATION_RATE,
        EventKind.MOTION_HESITATION,
        MOTION_HESITATION_MIN_S,
        lambda dur: f"망설임·이중동작 패턴 {dur:.1f}초",
    )


def _low_smile_events(session: Session) -> List[SemanticEvent]:
    """Sustained low smile_intensity — warmth deficit. Heavy in dating/customer
    scenarios, light/ignored in presentation (which rubric weighting handles)."""
    return _emit_span_events(
        session,
        lambda f: getattr(f, "smile_intensity", 0.0) < LOW_SMILE_THRESHOLD,
        EventKind.LOW_SMILE,
        LOW_SMILE_MIN_S,
        lambda dur: f"무미소 지속 {dur:.0f}초",
    )


def _gaze_wander_events(session: Session) -> List[SemanticEvent]:
    """Head-yaw stddev high sustained — eyes wandering side-to-side."""
    return _emit_span_events(
        session,
        lambda f: getattr(f, "gaze_yaw_sway", 0.0) >= GAZE_WANDER_DEG,
        EventKind.GAZE_WANDER,
        GAZE_WANDER_MIN_S,
        lambda dur: f"시선 좌우 산만 {dur:.1f}초",
    )


def _head_tilt_events(session: Session) -> List[SemanticEvent]:
    """Detect spans where the head is tilted sideways (|roll| > threshold) for ≥ N seconds."""
    out: List[SemanticEvent] = []
    span_start, last_t, peak_deg = None, None, 0.0
    for f in session.vision_frames:
        deg = abs(f.head_roll_deg)
        if deg > HEAD_TILT_DEG:
            if span_start is None:
                span_start = f.t
            last_t = f.t
            peak_deg = max(peak_deg, deg)
        else:
            if span_start is not None and last_t is not None and (last_t - span_start) >= HEAD_TILT_MIN_S:
                direction = "오른쪽" if peak_deg > 0 else "왼쪽"
                out.append(
                    SemanticEvent(
                        kind=EventKind.HEAD_TILT_SUSTAINED,
                        t_start=span_start,
                        t_end=last_t,
                        text=f"머리 {direction}으로 기울임 {last_t - span_start:.1f}초 (최대 {peak_deg:.0f}°)",
                        metrics={"duration_s": last_t - span_start, "peak_deg": peak_deg},
                    )
                )
            span_start, last_t, peak_deg = None, None, 0.0
    return out


def _gaze_downward_events(session: Session) -> List[SemanticEvent]:
    """Detect spans where head pitch is sustained downward — proxy for reading from
    notes / desk."""
    out: List[SemanticEvent] = []
    span_start, last_t = None, None
    for f in session.vision_frames:
        if f.head_pitch_deg > HEAD_PITCH_DOWN_DEG:
            if span_start is None:
                span_start = f.t
            last_t = f.t
        else:
            if span_start is not None and last_t is not None and (last_t - span_start) >= GAZE_DOWN_MIN_S:
                out.append(
                    SemanticEvent(
                        kind=EventKind.GAZE_DOWNWARD,
                        t_start=span_start,
                        t_end=last_t,
                        text=f"시선이 아래로 향함 {last_t - span_start:.1f}초 (원고/책상 응시 가능성)",
                        metrics={"duration_s": last_t - span_start},
                    )
                )
            span_start, last_t = None, None
    return out


def _chin_on_hand_events(session: Session) -> List[SemanticEvent]:
    """Detect spans where a hand is propping the face."""
    out: List[SemanticEvent] = []
    span_start, last_t = None, None
    for f in session.vision_frames:
        if f.chin_on_hand:
            if span_start is None:
                span_start = f.t
            last_t = f.t
        else:
            if span_start is not None and last_t is not None and (last_t - span_start) >= CHIN_ON_HAND_MIN_S:
                out.append(
                    SemanticEvent(
                        kind=EventKind.CHIN_ON_HAND,
                        t_start=span_start,
                        t_end=last_t,
                        text=f"턱 괴기 자세 유지 {last_t - span_start:.1f}초",
                        metrics={"duration_s": last_t - span_start},
                    )
                )
            span_start, last_t = None, None
    if span_start is not None and last_t is not None and (last_t - span_start) >= CHIN_ON_HAND_MIN_S:
        out.append(
            SemanticEvent(
                kind=EventKind.CHIN_ON_HAND,
                t_start=span_start,
                t_end=last_t,
                text=f"턱 괴기 자세 유지 {last_t - span_start:.1f}초",
                metrics={"duration_s": last_t - span_start},
            )
        )
    return out


def _head_nodding_events(session: Session) -> List[SemanticEvent]:
    """Detect rapid head pitch oscillation (긴장성 끄덕임 패턴) — sustained span of
    pitch reversals exceeding NOD_OSC_PER_S frequency."""
    out: List[SemanticEvent] = []
    vfs = session.vision_frames
    if len(vfs) < 4:
        return out

    span_start, span_count, last_t, last_dir = None, 0, None, 0
    last_pitch = vfs[0].head_pitch_deg
    prev_t = vfs[0].t
    direction_changes_window: List[float] = []
    AMPL_OK = NOD_PITCH_AMPLITUDE_DEG

    for f in vfs[1:]:
        dpitch = f.head_pitch_deg - last_pitch
        cur_dir = 1 if dpitch > 0 else (-1 if dpitch < 0 else last_dir)
        if last_dir != 0 and cur_dir != 0 and cur_dir != last_dir and abs(dpitch) > AMPL_OK / 2:
            direction_changes_window.append(f.t)
        last_dir = cur_dir
        last_pitch = f.head_pitch_deg

        # Drop changes older than 2s window
        while direction_changes_window and direction_changes_window[0] < f.t - 2.0:
            direction_changes_window.pop(0)

        rate = len(direction_changes_window) / 2.0
        if rate >= NOD_OSC_PER_S:
            if span_start is None:
                span_start = f.t
                span_count = 0
            span_count += 1
            last_t = f.t
        else:
            if span_start is not None and last_t is not None and (last_t - span_start) >= NOD_MIN_S:
                out.append(
                    SemanticEvent(
                        kind=EventKind.HEAD_NODDING,
                        t_start=span_start,
                        t_end=last_t,
                        text=f"고개 끄덕임 반복 {last_t - span_start:.1f}초",
                        metrics={"duration_s": last_t - span_start},
                    )
                )
            span_start, last_t = None, None

        prev_t = f.t

    if span_start is not None and last_t is not None and (last_t - span_start) >= NOD_MIN_S:
        out.append(
            SemanticEvent(
                kind=EventKind.HEAD_NODDING,
                t_start=span_start,
                t_end=last_t,
                text=f"고개 끄덕임 반복 {last_t - span_start:.1f}초",
                metrics={"duration_s": last_t - span_start},
            )
        )
    return out


def _silence_long_events(session: Session) -> List[SemanticEvent]:
    """The browser-side detector reports `silence_seconds` as a *monotonically
    growing* counter while a silence is ongoing, dropping to ~0 when speech
    resumes. The old logic re-emitted every time the counter grew by 0.5s, which
    turned a single 15-second silence into ~12 moments (all at the same t_start
    because t_start = t_end − silence_seconds stays put while both ends grow in
    lockstep). Here we instead track the silence-above-threshold *span* and emit
    exactly one event per span, using the peak value."""
    out: List[SemanticEvent] = []
    in_silence = False
    peak = 0.0
    peak_t_end = 0.0

    def emit() -> None:
        snippet = _transcript_around(session, peak_t_end - peak)
        out.append(
            SemanticEvent(
                kind=EventKind.SILENCE_LONG,
                t_start=max(0.0, peak_t_end - peak),
                t_end=peak_t_end,
                text=f"{peak:.1f}초 침묵",
                transcript_snippet=snippet,
                metrics={"silence_s": peak, "duration_s": peak},
            )
        )

    for p in session.prosody_frames:
        if p.silence_seconds >= SILENCE_LONG_S:
            in_silence = True
            if p.silence_seconds > peak:
                peak = p.silence_seconds
                peak_t_end = p.t_end
        else:
            if in_silence and peak > 0:
                emit()
            in_silence = False
            peak = 0.0
            peak_t_end = 0.0
    # Tail — silence ran all the way to session end.
    if in_silence and peak > 0:
        emit()
    return out


# ───── Phase 2 prosody detectors (from audio-pipeline /analyze) ─────

def _monotone_events(session: Session) -> List[SemanticEvent]:
    """Span of ≥N consecutive segments with low pitch SD — 단조로운 톤.
    Emits one event per span, marking the run's start/end and worst SD."""
    out: List[SemanticEvent] = []
    span_start, last_t, worst_sd = None, None, 999.0
    streak = 0
    for p in session.prosody_frames:
        sd = p.pitch_sd_semitones
        if sd is not None and sd < MONOTONE_PITCH_SD_ST:
            if streak == 0:
                span_start = p.t_start
                worst_sd = sd
            last_t = p.t_end
            worst_sd = min(worst_sd, sd)
            streak += 1
        else:
            if streak >= MONOTONE_MIN_SEGMENTS and span_start is not None and last_t is not None:
                out.append(SemanticEvent(
                    kind=EventKind.MONOTONE,
                    t_start=span_start, t_end=last_t,
                    text=f"단조로운 톤 {last_t - span_start:.0f}초 (피치 SD {worst_sd:.1f}st)",
                    metrics={"pitch_sd_st": worst_sd, "duration_s": last_t - span_start},
                ))
            streak = 0
            span_start = None
            last_t = None
            worst_sd = 999.0
    # Tail
    if streak >= MONOTONE_MIN_SEGMENTS and span_start is not None and last_t is not None:
        out.append(SemanticEvent(
            kind=EventKind.MONOTONE,
            t_start=span_start, t_end=last_t,
            text=f"단조로운 톤 {last_t - span_start:.0f}초 (피치 SD {worst_sd:.1f}st)",
            metrics={"pitch_sd_st": worst_sd, "duration_s": last_t - span_start},
        ))
    return out


def _sentence_trailing_events(session: Session) -> List[SemanticEvent]:
    """말끝 흐림 — multiple sentence endings with low end_energy_drop ratio.
    Emits ONE aggregate event listing the affected timestamps."""
    drop_times: List[float] = []
    for p in session.prosody_frames:
        d = p.end_energy_drop
        if d is not None and d < SENTENCE_TRAILING_RATIO:
            drop_times.append(p.t_end)
    if len(drop_times) < SENTENCE_TRAILING_MIN_COUNT:
        return []
    return [SemanticEvent(
        kind=EventKind.SENTENCE_TRAILING,
        t_start=drop_times[0], t_end=drop_times[-1],
        text=f"말끝 흐림 {len(drop_times)}회 (문장 끝 음량이 평균의 {SENTENCE_TRAILING_RATIO*100:.0f}% 이하)",
        metrics={"count": float(len(drop_times))},
    )]


def _slurred_articulation_events(session: Session) -> List[SemanticEvent]:
    """발음 명료도 — span of segments with low Whisper word-prob (proxy for slurring)."""
    out: List[SemanticEvent] = []
    span_start, last_t, worst_prob = None, None, 1.0
    streak = 0
    for p in session.prosody_frames:
        ap = p.articulation_proxy
        if ap is not None and ap < SLURRED_ARTICULATION_PROB:
            if streak == 0:
                span_start = p.t_start
                worst_prob = ap
            last_t = p.t_end
            worst_prob = min(worst_prob, ap)
            streak += 1
        else:
            if streak >= SLURRED_MIN_SEGMENTS and span_start is not None and last_t is not None:
                out.append(SemanticEvent(
                    kind=EventKind.SLURRED_ARTICULATION,
                    t_start=span_start, t_end=last_t,
                    text=f"발음 명료도 낮음 {last_t - span_start:.0f}초 (단어 신뢰도 {worst_prob:.2f})",
                    metrics={"word_prob": worst_prob, "duration_s": last_t - span_start},
                ))
            streak = 0
            span_start = None
            last_t = None
            worst_prob = 1.0
    if streak >= SLURRED_MIN_SEGMENTS and span_start is not None and last_t is not None:
        out.append(SemanticEvent(
            kind=EventKind.SLURRED_ARTICULATION,
            t_start=span_start, t_end=last_t,
            text=f"발음 명료도 낮음 {last_t - span_start:.0f}초 (단어 신뢰도 {worst_prob:.2f})",
            metrics={"word_prob": worst_prob, "duration_s": last_t - span_start},
        ))
    return out


def compute_aggregates(session: Session) -> SessionAggregates:
    vfs = session.vision_frames
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
    return SessionAggregates(
        avg_wpm=avg_wpm,
        filler_per_minute=sum(p.filler_count for p in pfs) / duration_min,
        gaze_central_fraction=_avg([f.gaze_fixation_ratio for f in vfs]),
        posture_sway_mean=_avg([f.posture_sway for f in vfs]),
        expression_diversity_mean=_avg([f.expression_diversity for f in vfs]),
    )


def build_bundle(session: Session) -> SessionBundle:
    from .dashboard import (
        compute_axis_accuracies,
        compute_overall_accuracy,
        annotate_moments,
        compute_quality_buckets,
        compute_score_timeline,
    )

    words: List[WordSpan] = []
    for seg in session.stt_segments:
        for w in seg.words:
            words.append(WordSpan(t_start=w.t_start, t_end=w.t_end, word=w.word))
    full_transcript = " ".join(seg.text.strip() for seg in session.stt_segments if seg.is_final).strip()

    events = derive_events(session)
    axes = compute_axis_accuracies(session)
    moments = annotate_moments(session, events=events)
    buckets = compute_quality_buckets(moments)
    timeline = compute_score_timeline(session, moments)

    return SessionBundle(
        session_id=session.session_id,
        scenario=session.scenario,
        focus_goals=list(session.focus_goals),
        duration_s=session.duration_s,
        full_transcript=full_transcript,
        words=words,
        events=events,
        aggregates=compute_aggregates(session),
        accuracy_overall=compute_overall_accuracy(axes),
        accuracy_per_axis=axes,
        quality_buckets=buckets,
        annotated_moments=moments,
        score_timeline=timeline,
        stt_segments=list(session.stt_segments),
    )
