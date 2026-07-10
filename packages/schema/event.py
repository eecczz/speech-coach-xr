"""Python mirror of event.ts — semantic events + session bundle for L3 LLM call."""

from __future__ import annotations

from enum import Enum
from typing import Dict, List, Optional

from pydantic import BaseModel, Field

from .report import (
    AxisAccuracy,
    QualityBuckets,
    TimelineSample,
    AnnotatedMoment,
)
from .signal import SttSegment


class EventKind(str, Enum):
    WPM_SPIKE = "wpm_spike"
    WPM_SLOW = "wpm_slow"
    LONG_PAUSE = "long_pause"
    FILLER_BURST = "filler_burst"
    GAZE_LAPSE = "gaze_lapse"
    GAZE_DOWNWARD = "gaze_downward"          # head pitch sustained downward (looking at script)
    POSTURE_SWAY = "posture_sway"
    HEAD_TILT_SUSTAINED = "head_tilt_sustained"  # head roll > threshold for extended period
    HEAD_NODDING = "head_nodding"             # rapid pitch oscillation (긴장성 끄덕임)
    CHIN_ON_HAND = "chin_on_hand"             # hand propping head (slouched / disengaged)
    EXPRESSION_FLAT = "expression_flat"
    HAND_FREEZE = "hand_freeze"
    GESTURE_EXCESSIVE = "gesture_excessive"   # sustained sudden burst of arm/hand movement
    # ── Phase 2 prosody events (need audio-pipeline /analyze) ──
    MONOTONE = "monotone"                     # pitch_sd low across segments — 단조로운 톤
    SENTENCE_TRAILING = "sentence_trailing"   # end_energy_drop repeated — 말끝 흐림
    SLURRED_ARTICULATION = "slurred_articulation"  # low whisper word-prob avg — 발음 뭉개짐
    # ── Domain-expansion events (situational coaching: 소개팅·고객응대·면접 등) ──
    FACE_TOUCH = "face_touch"                 # nose/forehead/hair touching (anxiety)
    HAND_FIDGET = "hand_fidget"               # hands clasped + restless small motion (안절부절)
    MOTION_HESITATION = "motion_hesitation"   # repeated direction reversals (이중동작/망설임)
    LOW_SMILE = "low_smile"                   # sustained low smile_intensity (warmth missing)
    GAZE_WANDER = "gaze_wander"               # head-yaw sway sustained (시선 좌우 산만)
    SILENCE_LONG = "silence_long"             # silent (no speech audio) for extended period
    VOICE_FLAT = "voice_flat"
    AGGREGATE = "aggregate"


class SemanticEvent(BaseModel):
    kind: EventKind
    t_start: float = Field(ge=0)
    t_end: float = Field(ge=0)
    text: str  # Korean human-readable
    transcript_snippet: Optional[str] = None
    metrics: Optional[Dict[str, float]] = None


class WordSpan(BaseModel):
    t_start: float
    t_end: float
    word: str


class SessionAggregates(BaseModel):
    avg_wpm: float
    filler_per_minute: float
    gaze_central_fraction: float
    posture_sway_mean: float
    expression_diversity_mean: float


class SessionBundle(BaseModel):
    session_id: str
    # Scenario rubric id (presentation | interview | vocal | dating | ...) — coach
    # selects the matching YAML rubric from services/coach/rubrics/ and composes the
    # LLM prompt around it. Defaults to "presentation" for backward compatibility.
    scenario: str = "presentation"
    # User-selected coaching focus labels from the setup page, e.g. ["말 속도", "시선 처리"].
    # These don't override the scenario rubric; they tell the coach which measured
    # evidence should be prioritized when multiple issues have similar impact.
    focus_goals: List[str] = Field(default_factory=list)
    duration_s: float = Field(ge=0)
    full_transcript: str
    words: List[WordSpan]
    events: List[SemanticEvent]
    aggregates: SessionAggregates
    # === Pre-computed dashboard data (rule-engine, no LLM) ===
    # LLM receives these as input and is expected to ground its commentary in them
    # rather than reinvent. annotated_moments will have empty coach_comment;
    # LLM fills it in.
    accuracy_overall: float = Field(default=0.0, ge=0, le=100)
    accuracy_per_axis: List[AxisAccuracy] = Field(default_factory=list)
    quality_buckets: QualityBuckets = Field(default_factory=QualityBuckets)
    annotated_moments: List[AnnotatedMoment] = Field(default_factory=list)
    score_timeline: List[TimelineSample] = Field(default_factory=list)
    # STT segments (with word timestamps) — carried through to the review UI so
    # subtitles can render synced to video, and verbal moments can highlight the
    # specific words at fault (filler bursts, etc.). Not consumed by the LLM
    # directly — the LLM gets `full_transcript` + `events`.
    stt_segments: List[SttSegment] = Field(default_factory=list)
