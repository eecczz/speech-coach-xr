"""Sliding 5-second window aggregator. On each window close, builds a SignalWindow
and (eventually) forwards it to coach /live.

Single active session; window state is global per-process.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import List, Optional

from packages.schema import VisionFrame, SttSegment, ProsodyFrame, SignalWindow

WINDOW_S = 5.0


@dataclass
class WindowBuffer:
    t_start: float = 0.0
    vision_frames: List[VisionFrame] = field(default_factory=list)
    stt_segments: List[SttSegment] = field(default_factory=list)
    prosody_frames: List[ProsodyFrame] = field(default_factory=list)

    @property
    def t_end(self) -> float:
        return self.t_start + WINDOW_S


def _mean_vision(frames: List[VisionFrame]) -> Optional[VisionFrame]:
    if not frames:
        return None
    n = len(frames)
    return VisionFrame(
        t=frames[-1].t,
        gaze_fixation_ratio=sum(f.gaze_fixation_ratio for f in frames) / n,
        posture_sway=sum(f.posture_sway for f in frames) / n,
        shoulder_tilt=sum(f.shoulder_tilt for f in frames) / n,
        expression_diversity=sum(f.expression_diversity for f in frames) / n,
        hand_gesture_freq=sum(f.hand_gesture_freq for f in frames) / n,
    )


def _merge_prosody(frames: List[ProsodyFrame], t_start: float, t_end: float) -> Optional[ProsodyFrame]:
    if not frames:
        return None
    # In V1 prosody arrives once per N seconds already aggregated; if multiple frames
    # land in the same window, weight by their duration.
    total_dur = sum(max(0.0, f.t_end - f.t_start) for f in frames) or 1.0
    return ProsodyFrame(
        t_start=t_start,
        t_end=t_end,
        wpm=sum(f.wpm * max(0.0, f.t_end - f.t_start) for f in frames) / total_dur,
        filler_count=sum(f.filler_count for f in frames),
        filler_terms=[t for f in frames for t in f.filler_terms],
        pause_seconds=sum(f.pause_seconds for f in frames),
        f0_variance=next((f.f0_variance for f in frames if f.f0_variance is not None), None),
        rms_mean=next((f.rms_mean for f in frames if f.rms_mean is not None), None),
    )


def close_window(buf: WindowBuffer, session_id: str) -> SignalWindow:
    transcript = " ".join(seg.text.strip() for seg in buf.stt_segments if seg.is_final).strip()
    return SignalWindow(
        session_id=session_id,
        t_start=buf.t_start,
        t_end=buf.t_end,
        vision=_mean_vision(buf.vision_frames),
        prosody=_merge_prosody(buf.prosody_frames, buf.t_start, buf.t_end),
        transcript=transcript,
    )


class WindowingState:
    """Holds the current open window for a single active session."""

    def __init__(self) -> None:
        self.buffer: Optional[WindowBuffer] = None

    def ensure_open(self, t: float) -> WindowBuffer:
        if self.buffer is None:
            self.buffer = WindowBuffer(t_start=(t // WINDOW_S) * WINDOW_S)
        return self.buffer

    def maybe_close(self, t_now: float) -> Optional[WindowBuffer]:
        """If t_now has crossed the current window's end, return the closed buffer and
        open a new one. Otherwise return None."""
        if self.buffer is None:
            return None
        if t_now >= self.buffer.t_end:
            closed = self.buffer
            self.buffer = WindowBuffer(t_start=closed.t_end)
            return closed
        return None

    def flush(self) -> Optional[WindowBuffer]:
        b = self.buffer
        self.buffer = None
        return b

    def add_vision(self, frame: VisionFrame) -> None:
        self.ensure_open(frame.t).vision_frames.append(frame)

    def add_stt(self, seg: SttSegment) -> None:
        self.ensure_open(seg.t_start).stt_segments.append(seg)

    def add_prosody(self, frame: ProsodyFrame) -> None:
        self.ensure_open(frame.t_start).prosody_frames.append(frame)
