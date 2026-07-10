"""Python mirror of signal.ts — Pydantic models for vision/audio frames + windowed bundle."""

from __future__ import annotations

from typing import Optional, List

from pydantic import BaseModel, Field


class VisionFrame(BaseModel):
    t: float = Field(ge=0)
    gaze_fixation_ratio: float = Field(ge=0, le=1)
    posture_sway: float = Field(ge=0)
    shoulder_tilt: float
    expression_diversity: float = Field(ge=0)
    hand_gesture_freq: float = Field(ge=0)
    # Peak wrist speed (image-normalized units/sec) in the last ~1s rolling window.
    # Spikes signal sudden burst gestures; sustained spikes become GESTURE_EXCESSIVE.
    hand_velocity_max: float = Field(default=0.0, ge=0)
    # Head pose decomposed from MediaPipe face transformation matrix (Euler angles, degrees).
    # +pitch = looking down (chin to chest), +yaw = head turned to subject's left,
    # +roll = head tilted toward subject's right shoulder.
    head_pitch_deg: float = 0.0
    head_yaw_deg: float = 0.0
    head_roll_deg: float = 0.0
    # True when the user is propping head on hand (wrist landmark close to face bounds).
    chin_on_hand: bool = False
    # Mouth-open ratio (jawOpen blendshape, 0..1). Proxy for whether user is speaking
    # at this instant; combined with audio RMS it lets us distinguish silent-mouth-open
    # vs. talking.
    mouth_open: float = Field(default=0.0, ge=0, le=1)

    # ── Domain-expansion signals (드라이브: 소개팅·고객응대·면접 등 시나리오 평가 확장) ──
    # Smile blendshape (avg of mouthSmileLeft/Right, 0..1). Warmth signal —
    # 소개팅/고객응대 시나리오에서 강한 가중치.
    smile_intensity: float = Field(default=0.0, ge=0, le=1)
    # Wrist inside upper face region (cheek/nose/forehead) but NOT chin — covers
    # nose-touching / hair-pushing / forehead-rubbing (anxiety signals).
    face_touch_other: bool = False
    # 0..1 — both wrists close together with small repetitive motion = 안절부절 fidget.
    hand_fidget_score: float = Field(default=0.0, ge=0)
    # Wrist direction reversals per second in last ~2s. High = 이중동작/망설임.
    motion_reversal_rate: float = Field(default=0.0, ge=0)
    # Head-yaw stddev in last ~2s (degrees). High = 시선 좌우 산만.
    gaze_yaw_sway: float = Field(default=0.0, ge=0)
    # Frame-to-frame blendshape change rate. High = 풍부한 표정 변화; low = 무표정.
    expression_change_rate: float = Field(default=0.0, ge=0)


class SttWord(BaseModel):
    t_start: float
    t_end: float
    word: str
    prob: Optional[float] = None


class SttSegment(BaseModel):
    t_start: float = Field(ge=0)
    t_end: float = Field(ge=0)
    text: str
    words: List[SttWord] = Field(default_factory=list)
    is_final: bool = True


class ProsodyFrame(BaseModel):
    t_start: float = Field(ge=0)
    t_end: float = Field(ge=0)
    wpm: float = Field(default=0.0, ge=0)
    # Number of STT words/eojeols in this frame. This lets the aggregator compute
    # session-level WPM from actual counts instead of averaging per-segment rates.
    word_count: int = Field(default=0, ge=0)
    filler_count: int = Field(default=0, ge=0)
    filler_terms: List[str] = Field(default_factory=list)
    pause_seconds: float = Field(default=0.0, ge=0)
    # Continuous silence duration ending in this window. Browser-side RMS detector
    # populates this even when STT is not yet running.
    silence_seconds: float = Field(default=0.0, ge=0)
    f0_variance: Optional[float] = Field(default=None, ge=0)
    rms_mean: Optional[float] = Field(default=None, ge=0)
    # ── Phase 2 prosody (audio-pipeline /analyze populates these per STT segment) ──
    # Pitch standard deviation in semitones — canonical monotone metric.
    # Low values = 단조로운 톤, high values = 표현력 있는 억양.
    pitch_sd_semitones: Optional[float] = Field(default=None, ge=0)
    # Pitch range (max-min) in semitones — expressiveness ceiling.
    pitch_range_semitones: Optional[float] = Field(default=None, ge=0)
    # Coefficient of variation of intensity envelope — 강약 다이내믹.
    # Low = 평평한 음량, high = 다양한 강조.
    intensity_cv: Optional[float] = Field(default=None, ge=0)
    # Ratio of energy in the last 0.5s to segment mean. <0.7 = 말끝 흐림.
    end_energy_drop: Optional[float] = Field(default=None, ge=0)
    # Mean Whisper word confidence — proxy for pronunciation clarity.
    # 0..1; <0.6 often means slurred/unclear articulation.
    articulation_proxy: Optional[float] = Field(default=None, ge=0, le=1)


class SignalWindow(BaseModel):
    session_id: str
    t_start: float = Field(ge=0)
    t_end: float = Field(ge=0)
    vision: Optional[VisionFrame] = None  # mean of frames in window
    prosody: Optional[ProsodyFrame] = None
    transcript: str = ""
