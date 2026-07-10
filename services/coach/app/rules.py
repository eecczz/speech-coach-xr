"""L1 — rule-based live HUD signals.

Stateless per-window evaluation. No LLM. Latency target <100ms.
"""

from __future__ import annotations

from typing import List

from packages.schema import SignalWindow, LiveHudSignal, LiveHudResponse

WPM_HIGH = 200
WPM_VERY_HIGH = 240
GAZE_LAPSE_RATIO = 0.4   # central fixation < 40% in this window
POSTURE_SWAY_HIGH = 0.10
FILLER_BURST_PER_WINDOW = 3  # >=3 fillers in a 5s window


def evaluate(window: SignalWindow) -> LiveHudResponse:
    signals: List[LiveHudSignal] = []

    if window.prosody:
        wpm = window.prosody.wpm
        if wpm >= WPM_VERY_HIGH:
            signals.append(LiveHudSignal(
                level="critical", kind="wpm_very_high",
                text=f"말이 매우 빠릅니다 (WPM {wpm:.0f})"
            ))
        elif wpm >= WPM_HIGH:
            signals.append(LiveHudSignal(
                level="warn", kind="wpm_high",
                text=f"말이 빠릅니다 (WPM {wpm:.0f})"
            ))

        if window.prosody.filler_count >= FILLER_BURST_PER_WINDOW:
            terms = ", ".join(window.prosody.filler_terms[:3]) or "filler"
            signals.append(LiveHudSignal(
                level="warn", kind="filler_burst",
                text=f"군더더기 표현 {window.prosody.filler_count}회 ({terms})"
            ))

    if window.vision:
        if (window.vision.gaze_fixation_ratio or 0) < GAZE_LAPSE_RATIO:
            signals.append(LiveHudSignal(
                level="warn", kind="gaze_lapse",
                text="시선이 카메라를 벗어났습니다"
            ))
        if (window.vision.posture_sway or 0) > POSTURE_SWAY_HIGH:
            signals.append(LiveHudSignal(
                level="warn", kind="posture_sway",
                text="어깨가 흔들리고 있습니다"
            ))

    return LiveHudResponse(
        window_t_start=window.t_start,
        window_t_end=window.t_end,
        signals=signals,
    )
