"""Coach service — L1 rule engine + L3 LLM comprehensive evaluation.

LLM provider is selected via LLM_PROVIDER env var (gemini | claude). See llm.py.
"""

from __future__ import annotations

import time

from fastapi import FastAPI
from fastapi.responses import JSONResponse
from fastapi.responses import Response

from packages.schema import (
    SignalWindow,
    SessionBundle,
    ComprehensiveReport,
    LiveHudResponse,
)
from .rules import evaluate as evaluate_window
from .llm import generate, provider_info
from .agent import AgentTriggerRequest, AgentFeedbackResponse, generate_agent_feedback
from .interview import (
    InterviewNextRequest,
    InterviewNextResponse,
    InterviewReportRequest,
    InterviewReportResponse,
    generate_next_question,
    generate_interview_report,
)
from .tts import TtsRequest, synthesize_audio

app = FastAPI(title="Presentation Coach")

# The XR interview client is served from the vite dev server (5173) or a static
# host; allow cross-origin so the browser can call these endpoints directly if
# it isn't proxied.
from fastapi.middleware.cors import CORSMiddleware  # noqa: E402

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


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


@app.post("/interview/next", response_model=InterviewNextResponse)
async def interview_next(req: InterviewNextRequest):
    """XR interviewer — generate the next question from the running Q&A history.

    The XR client calls this each turn; the model plays the interviewer and
    returns an answer-aware follow-up / pressure / base question (or done=true).
    """
    started = time.perf_counter()
    try:
        result = generate_next_question(req)
        elapsed = time.perf_counter() - started
        print(
            f"[interview/next] n={req.asked_count}/{req.max_questions} "
            f"-> {result.kind} done={result.done} ({elapsed*1000:.0f}ms): "
            f"{(result.question or '')[:60]}",
            flush=True,
        )
        return result
    except Exception as e:
        elapsed = time.perf_counter() - started
        print(
            f"[interview/next] failed ({elapsed*1000:.0f}ms) "
            f"error={type(e).__name__}: {e}",
            flush=True,
        )
        # Soft-fail: end the interview gracefully rather than 500-ing the client.
        return InterviewNextResponse(question=None, kind="closing", done=True)


@app.post("/interview/report", response_model=InterviewReportResponse)
async def interview_report(req: InterviewReportRequest):
    """XR interviewer — text-only answer-structure report at interview end.

    Evaluates 직접성·근거·사례(STAR)·결론 from the transcript. Multimodal (gaze/
    posture) scoring is combined later once XR signals feed in (Stage 3).
    """
    started = time.perf_counter()
    try:
        result = generate_interview_report(req)
        elapsed = time.perf_counter() - started
        print(
            f"[interview/report] q={len(req.history)} ({elapsed*1000:.0f}ms) ok",
            flush=True,
        )
        return result
    except Exception as e:
        elapsed = time.perf_counter() - started
        print(
            f"[interview/report] failed ({elapsed*1000:.0f}ms) "
            f"error={type(e).__name__}: {e}",
            flush=True,
        )
        return JSONResponse(
            {"error": f"{type(e).__name__}: {e}"}, status_code=502
        )


@app.post("/interview/tts")
async def interview_tts(req: TtsRequest):
    """Synthesize one panel utterance as 24 kHz mono WAV."""
    try:
        return Response(await synthesize_audio(req), media_type="audio/mpeg")
    except ValueError as e:
        return JSONResponse({"error": str(e)}, status_code=400)
    except Exception as e:
        return JSONResponse({"error": str(e)}, status_code=503)
