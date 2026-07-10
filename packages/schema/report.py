"""Python mirror of report.ts — live HUD + comprehensive report types."""

from __future__ import annotations

from enum import Enum
from typing import List, Literal, Optional

from pydantic import BaseModel, Field


class LiveHudSignal(BaseModel):
    level: Literal["info", "warn", "critical"]
    text: str
    kind: str


class LiveHudResponse(BaseModel):
    window_t_start: float
    window_t_end: float
    signals: List[LiveHudSignal]


class Rubric(BaseModel):
    logic: float = Field(ge=0, le=5)
    delivery: float = Field(ge=0, le=5)
    gaze: float = Field(ge=0, le=5)
    posture: float = Field(ge=0, le=5)
    expression: float = Field(ge=0, le=5)


class Finding(BaseModel):
    text: str
    # [t_start, t_end] seconds (2 elements). We use List instead of Tuple because
    # not all LLM schema converters accept JSON-Schema "prefixItems".
    evidence_t: Optional[List[float]] = None
    suggestion: Optional[str] = None


class EvidenceClip(BaseModel):
    t_start: float = Field(ge=0)
    t_end: float = Field(ge=0)
    reason: str


class TrainingPrescription(BaseModel):
    """One concrete training drill the user should do, tied to a specific improvement."""
    title: str            # 짧은 이름 — "쉼 호흡 훈련" 같은
    addresses: str        # 어떤 문제를 다루는지 — "쉼 없이 말이 흘러가는 습관"
    steps: List[str]      # 실행 단계 (순서)


class QualityLevel(str, Enum):
    """Chess-style 6-level move quality, applied to presentation moments."""
    BRILLIANT = "brilliant"     # 탁월 — exceptional positive (multiple axes great simultaneously)
    EXCELLENT = "excellent"     # 우수 — solidly above average
    GOOD = "good"               # 무난 — baseline
    INACCURACY = "inaccuracy"   # 주의 — small negative signal
    MISTAKE = "mistake"         # 실수 — clear problem
    BLUNDER = "blunder"         # 심각 — critical issue (e.g., chin-on-hand)


class AxisAccuracy(BaseModel):
    """Per-axis accuracy %, like chess's per-color accuracy."""
    axis: str                   # gaze | posture | expression | gesture | delivery | logic
    score: float = Field(ge=0, le=100)
    available: bool = True      # False when data isn't collected (e.g., delivery before STT)
    note: Optional[str] = None


class QualityBuckets(BaseModel):
    """Count of moments in each quality level."""
    brilliant: int = 0
    excellent: int = 0
    good: int = 0
    inaccuracy: int = 0
    mistake: int = 0
    blunder: int = 0


class TimelineSample(BaseModel):
    """One point on the continuous evaluation graph."""
    t: float = Field(ge=0)      # window-end timestamp in seconds
    score: float = Field(ge=0, le=100)


class AnnotatedMoment(BaseModel):
    """A clickable moment on the timeline / move list (chess-move analog).

    Rule-engine sets t/axis/quality/title/impact. LLM enriches coach_comment.
    """
    t: float = Field(ge=0)
    axis: str                   # gaze | posture | expression | gesture | delivery | logic
    quality: QualityLevel
    title: str                  # short label, e.g., "턱 괴기 8초"
    impact: int = 0             # -25..+25 score delta vs baseline
    coach_comment: Optional[str] = None  # LLM-generated 1-2 sentence explanation
    duration_s: Optional[float] = None   # span if applicable


class SubtitleWord(BaseModel):
    """A single STT word + its timestamp — used by the review UI to draw subtitles
    on the video and highlight problem words (filler bursts, etc.)."""
    t_start: float
    t_end: float
    word: str


class SubtitleSegment(BaseModel):
    """STT segment as carried into the review (subset of full SttSegment so the
    JSON shipped to the browser stays compact and free of LLM-irrelevant fields)."""
    t_start: float
    t_end: float
    text: str
    words: List[SubtitleWord] = Field(default_factory=list)


class TranscriptCheck(BaseModel):
    """A transcript phrase that looks worth reviewing against the original audio.

    This is not a pronunciation verdict. It flags likely STT/domain-term mismatch
    candidates so the user can compare the transcript with what they actually said.
    """
    phrase: str
    suggestion: Optional[str] = None
    reason: str
    t_start: Optional[float] = None
    t_end: Optional[float] = None


class ComprehensiveReport(BaseModel):
    session_id: str
    rubric: Rubric
    overall_summary: str
    top_priorities: List[Finding] = Field(default_factory=list)
    strengths: List[Finding]
    improvements: List[Finding]
    training_prescriptions: List[TrainingPrescription] = Field(default_factory=list)
    evidence_clips: List[EvidenceClip]
    # === Chess-style dashboard surfaces ===
    accuracy_overall: float = Field(default=0.0, ge=0, le=100)
    accuracy_per_axis: List[AxisAccuracy] = Field(default_factory=list)
    quality_buckets: QualityBuckets = Field(default_factory=QualityBuckets)
    annotated_moments: List[AnnotatedMoment] = Field(default_factory=list)
    score_timeline: List[TimelineSample] = Field(default_factory=list)
    # STT segments (with word timestamps) for review-time subtitle rendering +
    # word-level highlighting of verbal mistake moments. Not consumed by the LLM.
    subtitle_segments: List[SubtitleSegment] = Field(default_factory=list)
    # STT/domain-term mismatch candidates for the report UI.
    transcript_checks: List[TranscriptCheck] = Field(default_factory=list)
