"""Aggregator service entrypoint.

Responsibilities:
- Accept 5fps vision-signal frames from browser via WebSocket /ws/signals
- Accept STT segments + prosody from the audio-pipeline (TBD wiring)
- Sliding 5-second windowing → forward window to coach /live for L1 HUD
- On session end: derive L2 semantic events + bundle, send to coach /comprehensive
"""

from __future__ import annotations

import asyncio
import json
import os
import time
from typing import Optional

import httpx
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

from packages.schema import VisionFrame, SttSegment, ProsodyFrame
from .session import (
    Session,
    start_session,
    end_session,
    get_session,
    add_vision_frame,
    add_stt_segment,
    add_prosody_frame,
    set_stt_segments,
    set_prosody_frames,
)
from .windowing import WindowingState, close_window
from .events import build_bundle

COACH_URL = os.environ.get("COACH_URL", "http://coach:8002")
COACH_TIMEOUT_S = float(os.environ.get("COACH_TIMEOUT_S", "240"))

app = FastAPI(title="Presentation Coach Aggregator")
app.add_middleware(
    CORSMiddleware,
    allow_origins=[
        "http://127.0.0.1:8000",
        "http://localhost:8000",
        "http://127.0.0.1:5173",
        "http://localhost:5173",
    ],
    allow_credentials=False,
    allow_methods=["*"],
    allow_headers=["*"],
)
windowing = WindowingState()
_hud_broadcasts: list[WebSocket] = []  # clients listening for live HUD pushback


@app.post("/session/start")
async def session_start(payload: dict):
    sid = payload.get("session_id") or "default"
    scenario = payload.get("scenario") or "presentation"
    focus_goals = _normalize_focus_goals(payload.get("focus_goals"))
    start_session(sid, scenario, focus_goals)
    windowing.flush()
    return {"session_id": sid, "scenario": scenario, "focus_goals": focus_goals, "ok": True}


def _normalize_focus_goals(value) -> list[str]:
    if not value:
        return []
    if isinstance(value, str):
        raw = value.split(",")
    elif isinstance(value, list):
        raw = value
    else:
        return []
    out: list[str] = []
    seen = set()
    for item in raw:
        label = str(item).strip()
        if label and label not in seen:
            out.append(label)
            seen.add(label)
    return out[:8]


@app.post("/session/end")
async def session_end(payload: Optional[dict] = None):
    """Build the SessionBundle and forward to coach.

    Body (optional) lets the browser merge audio-pipeline /analyze results into
    the session right before bundling — replaces stt_segments / prosody_frames
    wholesale with the server-side transcription + prosody:

        { stt_segments?: SttSegment[], prosody_frames?: ProsodyFrame[],
          full_transcript?: str }
    """
    s = get_session()
    if not s:
        return JSONResponse({"error": "no active session"}, status_code=404)

    # Audio-pipeline result merge (Phase 2 wiring).
    if payload:
        raw_segs = payload.get("stt_segments")
        if raw_segs:
            try:
                set_stt_segments([SttSegment(**seg) for seg in raw_segs])
            except Exception as e:
                print(f"[session/end] stt_segments parse failed: {e}", flush=True)
        raw_prosody = payload.get("prosody_frames")
        if raw_prosody:
            try:
                set_prosody_frames([ProsodyFrame(**fr) for fr in raw_prosody])
            except Exception as e:
                print(f"[session/end] prosody_frames parse failed: {e}", flush=True)

    # Flush any open window first.
    last = windowing.flush()
    if last:
        await _forward_window_to_coach(close_window(last, s.session_id))

    bundle = build_bundle(s)
    end_session()
    # Forward to coach for L3 comprehensive evaluation.
    bundle_payload = bundle.model_dump(mode="json")
    payload_kb = len(json.dumps(bundle_payload, ensure_ascii=False).encode("utf-8")) / 1024
    started = time.perf_counter()
    print(
        "[session/end] forwarding bundle "
        f"session={bundle.session_id} duration={bundle.duration_s:.1f}s "
        f"events={len(bundle.events)} moments={len(bundle.annotated_moments)} "
        f"stt={len(bundle.stt_segments)} timeline={len(bundle.score_timeline)} "
        f"payload={payload_kb:.1f}KB timeout={COACH_TIMEOUT_S:.0f}s",
        flush=True,
    )
    try:
        async with httpx.AsyncClient(timeout=COACH_TIMEOUT_S) as client:
            r = await client.post(
                f"{COACH_URL}/comprehensive",
                json=bundle_payload,
            )
    except httpx.TimeoutException as e:
        elapsed = time.perf_counter() - started
        print(
            f"[session/end] coach timeout session={bundle.session_id} elapsed={elapsed:.1f}s error={e}",
            flush=True,
        )
        return JSONResponse(
            {
                "error": f"coach timeout after {elapsed:.1f}s",
                "hint": "긴 영상 리포트 생성이 시간 제한을 넘겼습니다.",
                "duration_s": bundle.duration_s,
                "event_count": len(bundle.events),
                "stt_segment_count": len(bundle.stt_segments),
            },
            status_code=502,
        )
    except httpx.HTTPError as e:
        elapsed = time.perf_counter() - started
        print(
            f"[session/end] coach unreachable session={bundle.session_id} elapsed={elapsed:.1f}s error={e}",
            flush=True,
        )
        return JSONResponse(
            {
                "error": f"coach unreachable: {e}",
                "duration_s": bundle.duration_s,
                "event_count": len(bundle.events),
                "stt_segment_count": len(bundle.stt_segments),
            },
            status_code=502,
        )
    elapsed = time.perf_counter() - started
    print(
        f"[session/end] coach responded session={bundle.session_id} status={r.status_code} elapsed={elapsed:.1f}s",
        flush=True,
    )

    try:
        report = r.json()
    except Exception as e:
        return JSONResponse(
            {
                "error": f"coach returned non-JSON (status {r.status_code}): {type(e).__name__}",
                "body_preview": r.text[:500],
            },
            status_code=502,
        )

    if r.status_code >= 400:
        return JSONResponse(
            {
                "coach_status": r.status_code,
                "coach_response": report,
                "duration_s": bundle.duration_s,
                "event_count": len(bundle.events),
                "stt_segment_count": len(bundle.stt_segments),
            },
            status_code=502,
        )

    return {"bundle_event_count": len(bundle.events), "report": report}


@app.websocket("/ws/signals")
async def ws_signals(ws: WebSocket):
    """Browser pushes VisionFrame JSON messages here at ~5fps."""
    await ws.accept()
    try:
        while True:
            msg = await ws.receive_json()
            kind = msg.get("kind", "vision")
            if kind == "vision":
                frame = VisionFrame(**msg["data"])
                add_vision_frame(frame)
                windowing.add_vision(frame)
            elif kind == "stt":
                seg = SttSegment(**msg["data"])
                add_stt_segment(seg)
                windowing.add_stt(seg)
            elif kind == "prosody":
                frame = ProsodyFrame(**msg["data"])
                add_prosody_frame(frame)
                windowing.add_prosody(frame)
            else:
                continue

            now_t = _latest_t()
            closed = windowing.maybe_close(now_t)
            if closed is not None:
                s = get_session()
                if s:
                    await _forward_window_to_coach(close_window(closed, s.session_id))
    except WebSocketDisconnect:
        return


@app.websocket("/ws/hud")
async def ws_hud(ws: WebSocket):
    """Browser opens this to receive HUD signal pushbacks from coach /live."""
    await ws.accept()
    _hud_broadcasts.append(ws)
    try:
        while True:
            await ws.receive_text()  # keepalive
    except WebSocketDisconnect:
        if ws in _hud_broadcasts:
            _hud_broadcasts.remove(ws)


async def _forward_window_to_coach(window) -> None:
    try:
        async with httpx.AsyncClient(timeout=5.0) as client:
            r = await client.post(f"{COACH_URL}/live", json=window.model_dump(mode="json"))
            hud = r.json()
    except httpx.HTTPError:
        return
    # Fan out to any connected HUD listeners.
    dead = []
    for ws in _hud_broadcasts:
        try:
            await ws.send_json(hud)
        except Exception:
            dead.append(ws)
    for d in dead:
        if d in _hud_broadcasts:
            _hud_broadcasts.remove(d)


def _latest_t() -> float:
    s = get_session()
    if not s:
        return 0.0
    t = 0.0
    if s.vision_frames:
        t = max(t, s.vision_frames[-1].t)
    if s.stt_segments:
        t = max(t, s.stt_segments[-1].t_end)
    if s.prosody_frames:
        t = max(t, s.prosody_frames[-1].t_end)
    return t


@app.get("/healthz")
async def healthz():
    return {"ok": True}
