"""Audio analysis server — STT + prosody (librosa).

Endpoints:
  POST /transcribe — legacy PoC, raw STT result (kept for ad-hoc testing).
  POST /analyze    — full pipeline: webm → ffmpeg → wav → STT + prosody per segment.
                     Returns SessionBundle-compatible payload for the aggregator.

The avatar/practice flow uses /analyze at session end and forwards the result into
aggregator /session/end body.
"""

import os
import subprocess
import tempfile
import time
from pathlib import Path

import psycopg
from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from starlette.background import BackgroundTask

from prosody import analyze_prosody
from stt import STT
from db import (
    DatabaseUnavailable,
    authenticate_user,
    create_user,
    init_db,
    list_messages,
    list_sessions,
    save_message,
    upsert_session,
)

app = FastAPI(title="Presentation Coach Audio Pipeline")

stt = STT(
    model_name=os.environ.get("WHISPER_MODEL", "large-v3"),
    device=os.environ.get("WHISPER_DEVICE", "auto"),
    compute_type=os.environ.get("WHISPER_COMPUTE_TYPE", "auto"),
    provider=os.environ.get("STT_PROVIDER", "local"),
    jeonbuk_model=os.environ.get("JEONBUK_STT_MODEL", "cohere-transcribe"),
    jeonbuk_base_url=os.environ.get("JEONBUK_BASE_URL", "https://ai.jb.go.kr/student-api/v1"),
    jeonbuk_api_key=os.environ.get("JEONBUK_API_KEY"),
)


@app.on_event("startup")
async def startup() -> None:
    try:
        init_db()
    except Exception as e:
        # The media/STT pipeline can still run without DB during local debugging.
        print(f"[db] init skipped: {type(e).__name__}: {e}", flush=True)


def _require_text(payload: dict, key: str, label: str) -> str:
    value = str(payload.get(key) or "").strip()
    if not value:
        raise HTTPException(status_code=400, detail=f"{label} is required")
    return value


async def _json_payload(request: Request) -> dict:
    try:
        payload = await request.json()
    except Exception:
        raise HTTPException(status_code=400, detail="invalid JSON")
    if not isinstance(payload, dict):
        raise HTTPException(status_code=400, detail="JSON object required")
    return payload


def _db_error_response(error: Exception) -> JSONResponse:
    if isinstance(error, DatabaseUnavailable):
        return JSONResponse({"error": str(error)}, status_code=503)
    if isinstance(error, psycopg.errors.UniqueViolation):
        return JSONResponse({"error": "already exists"}, status_code=409)
    return JSONResponse({"error": f"{type(error).__name__}: {error}"}, status_code=500)


def _webm_to_wav(src: str, dst: str) -> None:
    """16kHz mono WAV — what both faster-whisper and librosa want. ffmpeg is
    already in the container (Dockerfile installs ffmpeg + libsndfile1)."""
    subprocess.run(
        ["ffmpeg", "-y", "-i", src, "-ar", "16000", "-ac", "1", dst],
        check=True, capture_output=True,
    )


def _video_to_mp4(src: str, dst: str) -> None:
    """Convert a browser-recorded video into a broadly shareable MP4 file."""
    subprocess.run(
        [
            "ffmpeg",
            "-y",
            "-i",
            src,
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-pix_fmt",
            "yuv420p",
            "-c:a",
            "aac",
            "-movflags",
            "+faststart",
            dst,
        ],
        check=True,
        capture_output=True,
    )


def _safe_download_stem(filename: str | None) -> str:
    stem = Path(filename or "speakup-video").stem.strip() or "speakup-video"
    return "".join("_" if c in '\\/:*?"<>|' else c for c in stem)


@app.get("/api/health/db")
async def db_health():
    try:
        init_db()
        return {"ok": True}
    except Exception as e:
        return _db_error_response(e)


@app.post("/api/auth/signup")
async def signup(request: Request):
    payload = await _json_payload(request)
    email = _require_text(payload, "email", "email")
    password = _require_text(payload, "password", "password")
    display_name = str(payload.get("display_name") or email.split("@")[0]).strip()
    if len(password) < 4:
        raise HTTPException(status_code=400, detail="password must be at least 4 characters")
    try:
        return {"user": create_user(email, password, display_name)}
    except Exception as e:
        return _db_error_response(e)


@app.post("/api/auth/login")
async def login(request: Request):
    payload = await _json_payload(request)
    email = _require_text(payload, "email", "email")
    password = _require_text(payload, "password", "password")
    try:
        user = authenticate_user(email, password)
    except Exception as e:
        return _db_error_response(e)
    if not user:
        raise HTTPException(status_code=401, detail="invalid email or password")
    return {"user": user}


@app.get("/api/sessions")
async def api_list_sessions(user_id: str):
    try:
        return {"sessions": list_sessions(user_id)}
    except Exception as e:
        return _db_error_response(e)


@app.post("/api/sessions")
async def api_create_session(request: Request):
    payload = await _json_payload(request)
    for key in ("user_id", "title", "scenario"):
        _require_text(payload, key, key)
    try:
        return {"session": upsert_session(payload)}
    except Exception as e:
        return _db_error_response(e)


@app.patch("/api/sessions/{session_id}")
async def api_update_session(session_id: str, request: Request):
    payload = await _json_payload(request)
    payload["id"] = session_id
    for key in ("user_id", "title", "scenario"):
        _require_text(payload, key, key)
    try:
        return {"session": upsert_session(payload)}
    except Exception as e:
        return _db_error_response(e)


@app.get("/api/sessions/{session_id}/messages")
async def api_list_messages(session_id: str):
    try:
        return {"messages": list_messages(session_id)}
    except Exception as e:
        return _db_error_response(e)


@app.post("/api/sessions/{session_id}/messages")
async def api_save_message(session_id: str, request: Request):
    payload = await _json_payload(request)
    role = _require_text(payload, "role", "role")
    content = _require_text(payload, "content", "content")
    t = payload.get("t")
    numeric_t = float(t) if isinstance(t, (int, float)) else None
    try:
        return {
            "message": save_message(
                session_id=session_id,
                role=role,
                content=content,
                t=numeric_t,
                metadata=payload.get("metadata") if isinstance(payload.get("metadata"), dict) else {},
            )
        }
    except Exception as e:
        return _db_error_response(e)


def _to_stt_segments(whisper_segments: list) -> list[dict]:
    """Convert stt.transcribe() segments → packages.schema.SttSegment shape.
    Maps Whisper's {start,end,text,words:[{start,end,word,prob}]} →
    SttSegment {t_start,t_end,text,words:[{t_start,t_end,word,prob}],is_final}."""
    out = []
    for seg in whisper_segments:
        words = [
            {
                "t_start": float(w.get("start", 0.0)),
                "t_end": float(w.get("end", 0.0)),
                "word": str(w.get("word", "")).strip(),
                "prob": float(w.get("prob")) if w.get("prob") is not None else None,
            }
            for w in seg.get("words", [])
        ]
        out.append({
            "t_start": float(seg.get("start", 0.0)),
            "t_end": float(seg.get("end", 0.0)),
            "text": str(seg.get("text", "")).strip(),
            "words": words,
            "is_final": True,
        })
    return out


@app.post("/transcribe")
async def transcribe(audio: UploadFile = File(...), language: str = "ko"):
    """Legacy PoC — raw STT result. Kept for ad-hoc verification."""
    suffix = Path(audio.filename or "rec.webm").suffix or ".webm"
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
        tmp.write(await audio.read())
        path = tmp.name
    try:
        t0 = time.perf_counter()
        result = stt.transcribe(path, language=language)
        result["server_elapsed_s"] = time.perf_counter() - t0
        return JSONResponse(result)
    finally:
        try:
            os.unlink(path)
        except OSError:
            pass


@app.post("/convert/mp4")
async def convert_mp4(video: UploadFile = File(...)):
    """Convert a stored practice video to MP4 for easier sharing/download."""
    suffix = Path(video.filename or "rec.webm").suffix or ".webm"
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as src_tmp:
        src_tmp.write(await video.read())
        src_path = src_tmp.name
    mp4_path = src_path + ".mp4"

    def cleanup() -> None:
        for p in (src_path, mp4_path):
            try:
                os.unlink(p)
            except OSError:
                pass

    try:
        _video_to_mp4(src_path, mp4_path)
        return FileResponse(
            mp4_path,
            media_type="video/mp4",
            filename=f"{_safe_download_stem(video.filename)}.mp4",
            background=BackgroundTask(cleanup),
        )
    except subprocess.CalledProcessError as e:
        cleanup()
        return JSONResponse(
            {"error": f"ffmpeg failed: {e.stderr.decode(errors='ignore')[:500]}"},
            status_code=400,
        )
    except Exception as e:
        cleanup()
        return JSONResponse(
            {"error": f"{type(e).__name__}: {e}"},
            status_code=500,
        )


@app.post("/analyze")
async def analyze(
    audio: UploadFile = File(...),
    session_id: str = Form("default"),
    language: str = Form("ko"),
):
    """Full audio analysis — STT + per-segment prosody. Returns a payload that
    the aggregator /session/end can merge into the SessionBundle.

    Schema:
      {
        session_id: str,
        full_transcript: str,
        stt_segments: SttSegment[],
        prosody_frames: ProsodyFrame[],   # one per STT segment
        elapsed_s: float,
        stt_elapsed_s: float,
      }
    """
    suffix = Path(audio.filename or "rec.webm").suffix or ".webm"
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as src_tmp:
        src_tmp.write(await audio.read())
        src_path = src_tmp.name
    wav_path = src_path + ".wav"

    try:
        t_total = time.perf_counter()

        # 1) ffmpeg → 16kHz mono WAV
        _webm_to_wav(src_path, wav_path)

        # 2) STT transcribe (local faster-whisper or Jeonbuk API provider)
        t_w = time.perf_counter()
        result = stt.transcribe(wav_path, language=language)
        stt_elapsed = time.perf_counter() - t_w

        stt_segments = _to_stt_segments(result.get("segments", []))

        # 3) Prosody per STT segment (F0, intensity, end-energy, fillers, WPM)
        prosody_frames = analyze_prosody(wav_path, result.get("segments", []))

        return JSONResponse({
            "session_id": session_id,
            "full_transcript": result.get("full_text", ""),
            "stt_segments": stt_segments,
            "prosody_frames": prosody_frames,
            "elapsed_s": time.perf_counter() - t_total,
            "stt_elapsed_s": stt_elapsed,
            # Backward-compatible field name kept for existing debug tooling.
            "whisper_elapsed_s": stt_elapsed,
        })
    except subprocess.CalledProcessError as e:
        return JSONResponse(
            {"error": f"ffmpeg failed: {e.stderr.decode(errors='ignore')[:500]}"},
            status_code=400,
        )
    except Exception as e:
        return JSONResponse(
            {"error": f"{type(e).__name__}: {e}"},
            status_code=500,
        )
    finally:
        for p in (src_path, wav_path):
            try:
                os.unlink(p)
            except OSError:
                pass


# 정적 HTML은 라우트 등록 이후 mount — /transcribe, /analyze가 가려지지 않게
app.mount("/", StaticFiles(directory="static", html=True), name="static")
