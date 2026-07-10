"""Prosody extraction — F0, intensity, energy envelope.

Runs once per session on the full WAV (after ffmpeg conversion from webm). Produces
one ProsodyFrame per STT segment so the rest of the pipeline gets prosody info
aligned to natural sentence boundaries.

Heavy lifting via librosa: pyin for F0 (handles unvoiced via voicing flags), RMS
for intensity envelope. No SER/emotion model in this pass — those are v2 candidates
that depend on the wav2vec2 family.
"""

from __future__ import annotations

import math
from typing import Dict, List

import librosa  # type: ignore
import numpy as np  # type: ignore


# Korean filler dictionary — extend as we encounter new patterns. Conservative on
# purpose: words like "그" are common standalone fillers but also legitimate
# parts of phrases ("그리고"), so we match only the bare standalone form.
KOREAN_FILLERS = {
    "음", "어", "아", "에", "저", "저기", "그", "그게",
    "그니까", "그러니까", "이제", "뭐", "약간", "막", "근데",
    "음...", "어...", "에...",
}

# Pitch search range (Hz) for Korean voice — covers male low to female high.
F0_MIN = 80.0
F0_MAX = 400.0
TARGET_SR = 16000  # match faster-whisper input
END_SLICE_S = 0.5  # last 0.5s of a segment for end_energy_drop
MIN_WPM_DURATION_S = 2.0  # avoids 1-word/0.4s segments becoming fake 150 WPM spikes
LONG_SILENCE_S = 2.0


def _hz_to_semitones(f0_hz: np.ndarray, ref_hz: float) -> np.ndarray:
    # Standard semitone conversion: 12 * log2(f / ref). Reference is the segment
    # median so the result represents *within-segment* pitch variation, not absolute
    # speaker pitch (which varies by gender/individual and we don't want to score).
    safe = np.where(f0_hz > 0, f0_hz, np.nan)
    with np.errstate(divide="ignore", invalid="ignore"):
        return 12.0 * np.log2(safe / max(ref_hz, 1e-3))


def _norm_word(w: str) -> str:
    return w.strip().strip(".,!?…~\"'`“”‘’()[]{}<>").strip()


def _filler_match(words: List[Dict]) -> tuple[int, List[str]]:
    matched: List[str] = []
    for w in words:
        wc = _norm_word(str(w.get("word", "")))
        if wc and wc in KOREAN_FILLERS:
            matched.append(wc)
    return len(matched), matched


def analyze_prosody(wav_path: str, segments: List[Dict]) -> List[Dict]:
    """Returns a list of ProsodyFrame-shaped dicts, one per STT segment."""
    # Load once for the whole session — librosa.pyin is the slow part (~5-15s for
    # 1-2 min audio on CPU).
    y, sr = librosa.load(wav_path, sr=TARGET_SR, mono=True)
    duration = len(y) / sr if sr > 0 else 0.0

    # F0 via pyin (handles silence by returning NaN at unvoiced frames).
    f0, voiced_flag, _ = librosa.pyin(  # type: ignore[no-untyped-call]
        y, fmin=F0_MIN, fmax=F0_MAX, sr=sr,
        frame_length=2048, hop_length=512,
    )
    f0_times = librosa.times_like(f0, sr=sr, hop_length=512)

    # Intensity envelope (RMS per short frame).
    rms = librosa.feature.rms(y=y, frame_length=1024, hop_length=256)[0]
    rms_times = librosa.times_like(rms, sr=sr, hop_length=256)

    frames: List[Dict] = []
    n_segs = len(segments)
    if n_segs == 0:
        return [{
            "t_start": 0.0,
            "t_end": duration,
            "wpm": 0.0,
            "word_count": 0,
            "filler_count": 0,
            "filler_terms": [],
            "pause_seconds": duration if duration >= LONG_SILENCE_S else 0.0,
            "silence_seconds": duration if duration >= LONG_SILENCE_S else 0.0,
            "f0_variance": None,
            "rms_mean": float(np.mean(rms)) if rms.size else None,
            "pitch_sd_semitones": None,
            "pitch_range_semitones": None,
            "intensity_cv": None,
            "end_energy_drop": None,
            "articulation_proxy": None,
        }]

    for i, seg in enumerate(segments):
        t_start = float(seg.get("start", 0.0))
        t_end = float(seg.get("end", t_start))
        words = seg.get("words", []) or []
        seg_dur = max(1e-3, t_end - t_start)

        # Pitch stats — slice f0 to this segment's time range.
        f0_mask = (f0_times >= t_start) & (f0_times < t_end)
        f0_seg = f0[f0_mask]
        f0_voiced = f0_seg[~np.isnan(f0_seg)]
        if f0_voiced.size >= 5:
            median_hz = float(np.median(f0_voiced))
            semis = _hz_to_semitones(f0_voiced, median_hz)
            pitch_sd_st = float(np.nanstd(semis))
            pitch_range_st = float(np.nanmax(semis) - np.nanmin(semis))
        else:
            pitch_sd_st = None
            pitch_range_st = None

        # Intensity stats — slice rms.
        rms_mask = (rms_times >= t_start) & (rms_times < t_end)
        rms_seg = rms[rms_mask]
        if rms_seg.size >= 3:
            rms_mean = float(np.mean(rms_seg))
            rms_std = float(np.std(rms_seg))
            intensity_cv = float(rms_std / rms_mean) if rms_mean > 1e-6 else None
            # End-energy drop: ratio of mean RMS in last 0.5s to whole-segment mean.
            end_mask = (rms_times >= max(t_start, t_end - END_SLICE_S)) & (rms_times < t_end)
            rms_end = rms[end_mask]
            end_energy_drop = float(np.mean(rms_end) / rms_mean) if rms_end.size and rms_mean > 1e-6 else None
        else:
            rms_mean = None
            intensity_cv = None
            end_energy_drop = None

        # WPM from word count over segment duration (in minutes).
        word_count = len(words)
        rate_dur = max(seg_dur, MIN_WPM_DURATION_S)
        wpm = (word_count / rate_dur) * 60.0

        # Filler match.
        filler_count, filler_terms = _filler_match(words)

        # Articulation proxy: mean Whisper word probability for this segment.
        probs = [w.get("prob") for w in words if isinstance(w.get("prob"), (int, float))]
        articulation_proxy = float(np.mean(probs)) if probs else None

        # Pause-to-next: gap until next segment's start (if any). Beyond last segment
        # we leave it 0 — silence detector handles trailing silence separately.
        if i + 1 < n_segs:
            pause_seconds = max(0.0, float(segments[i + 1].get("start", t_end)) - t_end)
        else:
            pause_seconds = max(0.0, duration - t_end)

        # Silence_seconds: report only meaningful gaps. The final segment can also
        # carry trailing silence because this server has the full WAV duration.
        silence_seconds = pause_seconds if pause_seconds >= LONG_SILENCE_S else 0.0

        frames.append({
            "t_start": t_start,
            "t_end": t_end,
            "wpm": wpm,
            "word_count": word_count,
            "filler_count": filler_count,
            "filler_terms": filler_terms,
            "pause_seconds": pause_seconds,
            "silence_seconds": silence_seconds,
            "f0_variance": pitch_sd_st,        # kept for backward compatibility
            "rms_mean": rms_mean,
            "pitch_sd_semitones": pitch_sd_st,
            "pitch_range_semitones": pitch_range_st,
            "intensity_cv": intensity_cv,
            "end_energy_drop": end_energy_drop,
            "articulation_proxy": articulation_proxy,
        })

    return frames
