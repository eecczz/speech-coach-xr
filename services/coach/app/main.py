"""Coach service — L1 rule engine + L3 LLM comprehensive evaluation.

LLM provider is selected via LLM_PROVIDER env var (gemini | claude). See llm.py.
"""

from __future__ import annotations

import time

from fastapi import FastAPI
from fastapi.responses import JSONResponse

from packages.schema import (
    SignalWindow,
    SessionBundle,
    ComprehensiveReport,
    LiveHudResponse,
)
from .rules import evaluate as evaluate_window
from .llm import generate, provider_info
from .agent import AgentTriggerRequest, AgentFeedbackResponse, generate_agent_feedback

app = FastAPI(title="Presentation Coach")


@app.get("/healthz")
async def healthz():
    return {"ok": True, **provider_info()}


@app.post("/live", response_model=LiveHudResponse)
async def live(window: SignalWindow):
    """L1 — rule-based per-5s-window HUD signals. No LLM."""
    return evaluate_window(window)


@app.post("/comprehensive", response_model=ComprehensiveReport)
async def comprehensive(bundle: SessionBundle):
    """L3 — LLM-based structured evaluation of the entire session."""
    started = time.perf_counter()
    print(
        "[coach/comprehensive] start "
        f"session={bundle.session_id} duration={bundle.duration_s:.1f}s "
        f"events={len(bundle.events)} moments={len(bundle.annotated_moments)} "
        f"stt={len(bundle.stt_segments)}",
        flush=True,
    )
    try:
        report = generate(bundle)
        elapsed = time.perf_counter() - started
        print(
            f"[coach/comprehensive] done session={bundle.session_id} elapsed={elapsed:.1f}s",
            flush=True,
        )
        return report
    except Exception as e:
        elapsed = time.perf_counter() - started
        print(
            f"[coach/comprehensive] failed session={bundle.session_id} elapsed={elapsed:.1f}s error={type(e).__name__}: {e}",
            flush=True,
        )
        return JSONResponse(
            {
                "error": f"LLM call failed: {type(e).__name__}: {e}",
                "duration_s": bundle.duration_s,
                "event_count": len(bundle.events),
                "stt_segment_count": len(bundle.stt_segments),
                "elapsed_s": round(elapsed, 1),
            },
            status_code=502,
        )


@app.post("/agent-feedback", response_model=AgentFeedbackResponse)
async def agent_feedback(req: AgentTriggerRequest):
    """Real-time per-trigger nudge (≤30자, 한국어).

    Browser fires this each time one of the agent triggers (silence, gaze,
    smile_absence, motion_absence, filler, speech_rate, content) passes its
    threshold + cooldown. We do a single small Gemini call and return a one-line
    Korean message — or `null` to let the browser skip showing anything.
    """
    started = time.perf_counter()
    try:
        result = generate_agent_feedback(req)
        elapsed = time.perf_counter() - started
        if result.message:
            print(
                f"[agent-feedback] {req.kind} -> {result.tone or '?'} "
                f"({elapsed*1000:.0f}ms): {result.message}",
                flush=True,
            )
        else:
            print(
                f"[agent-feedback] {req.kind} -> skip ({elapsed*1000:.0f}ms)",
                flush=True,
            )
        return result
    except Exception as e:
        elapsed = time.perf_counter() - started
        print(
            f"[agent-feedback] {req.kind} failed ({elapsed*1000:.0f}ms) "
            f"error={type(e).__name__}: {e}",
            flush=True,
        )
        # Soft-fail: returning a 200 with null message keeps the browser quiet
        # instead of throwing red errors on transient LLM hiccups.
        return AgentFeedbackResponse(message=None, tone=None)
