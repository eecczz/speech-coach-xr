"""Shared signal/event/report schemas — Python (Pydantic) mirror of TypeScript (Zod) defs.

Both sides must stay in sync; treat as a single source-of-truth contract.
"""

from .signal import VisionFrame, SttSegment, ProsodyFrame, SignalWindow
from .event import EventKind, SemanticEvent, SessionBundle
from .report import (
    LiveHudSignal,
    LiveHudResponse,
    Rubric,
    Finding,
    EvidenceClip,
    TrainingPrescription,
    QualityLevel,
    AxisAccuracy,
    QualityBuckets,
    TimelineSample,
    AnnotatedMoment,
    SubtitleSegment,
    SubtitleWord,
    TranscriptCheck,
    ComprehensiveReport,
)

__all__ = [
    "VisionFrame",
    "SttSegment",
    "ProsodyFrame",
    "SignalWindow",
    "EventKind",
    "SemanticEvent",
    "SessionBundle",
    "LiveHudSignal",
    "LiveHudResponse",
    "Rubric",
    "Finding",
    "EvidenceClip",
    "TrainingPrescription",
    "QualityLevel",
    "AxisAccuracy",
    "QualityBuckets",
    "TimelineSample",
    "AnnotatedMoment",
    "SubtitleSegment",
    "SubtitleWord",
    "TranscriptCheck",
    "ComprehensiveReport",
]
