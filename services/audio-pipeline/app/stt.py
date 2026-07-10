"""STT provider wrapper.

Supports the existing local faster-whisper path and the Jeonbuk AI student API
(`cohere-transcribe`), which exposes an OpenAI-compatible transcription endpoint.
"""

from __future__ import annotations

import os
import re
import time
import wave
from typing import Any


class STT:
    def __init__(self, model_name: str = "large-v3", device: str = "auto",
                 compute_type: str = "auto", provider: str = "local",
                 jeonbuk_model: str = "cohere-transcribe",
                 jeonbuk_base_url: str = "https://ai.jb.go.kr/student-api/v1",
                 jeonbuk_api_key: str | None = None):
        self.provider = provider.lower()
        self._client = None

        if self.provider == "jeonbuk":
            self.model_name = jeonbuk_model
            self.base_url = jeonbuk_base_url
            self.api_key = jeonbuk_api_key or os.environ.get("JEONBUK_API_KEY", "")
            state = "set" if self.api_key else "empty"
            print(
                f"[STT] using Jeonbuk API model={self.model_name} base_url={self.base_url} api_key={state}",
                flush=True,
            )
            return

        if self.provider != "local":
            raise ValueError(f"unknown STT_PROVIDER: {provider!r}")

        from faster_whisper import WhisperModel

        if device == "auto":
            try:
                import torch
                device = "cuda" if torch.cuda.is_available() else "cpu"
            except ImportError:
                device = "cpu"
        if compute_type == "auto":
            compute_type = "float16" if device == "cuda" else "int8"

        print(f"[STT] loading {model_name} device={device} compute_type={compute_type}", flush=True)
        t0 = time.perf_counter()
        self.model = WhisperModel(model_name, device=device, compute_type=compute_type)
        print(f"[STT] loaded in {time.perf_counter() - t0:.1f}s", flush=True)

    def transcribe(self, path: str, language: str = "ko") -> dict:
        if self.provider == "jeonbuk":
            return self._transcribe_jeonbuk(path, language=language)
        return self._transcribe_local(path, language=language)

    def _transcribe_local(self, path: str, language: str = "ko") -> dict:
        segments_iter, info = self.model.transcribe(
            path,
            language=language,
            word_timestamps=True,
            vad_filter=True,
            vad_parameters={"min_silence_duration_ms": 500},
        )
        segments = []
        for seg in segments_iter:
            words = [
                {"start": w.start, "end": w.end, "word": w.word, "prob": w.probability}
                for w in (seg.words or [])
            ]
            segments.append({
                "start": seg.start,
                "end": seg.end,
                "text": seg.text,
                "no_speech_prob": seg.no_speech_prob,
                "words": words,
            })
        full_text = " ".join(s["text"].strip() for s in segments).strip()
        word_count = sum(len(s["words"]) for s in segments)
        wpm = (word_count / info.duration * 60) if info.duration else 0.0
        return {
            "language": info.language,
            "language_probability": info.language_probability,
            "duration_s": info.duration,
            "wpm": wpm,
            "word_count": word_count,
            "full_text": full_text,
            "segments": segments,
        }

    def _get_jeonbuk_client(self):
        if not self.api_key:
            raise RuntimeError("JEONBUK_API_KEY not set")
        if self._client is None:
            from openai import OpenAI

            self._client = OpenAI(base_url=self.base_url, api_key=self.api_key)
        return self._client

    def _transcribe_jeonbuk(self, path: str, language: str = "ko") -> dict:
        client = self._get_jeonbuk_client()
        t0 = time.perf_counter()
        with open(path, "rb") as f:
            result = client.audio.transcriptions.create(
                model=self.model_name,
                file=f,
                language=language,
            )
        elapsed = time.perf_counter() - t0
        plain = _to_plain(result)
        out = _normalize_openai_transcription(plain, path)
        out["provider"] = "jeonbuk"
        out["model"] = self.model_name
        out["language"] = plain.get("language") or language
        out["server_elapsed_s"] = elapsed
        return out


def _to_plain(obj: Any) -> dict:
    if isinstance(obj, dict):
        return obj
    if hasattr(obj, "model_dump"):
        return obj.model_dump()
    if hasattr(obj, "dict"):
        return obj.dict()
    if hasattr(obj, "text"):
        return {"text": obj.text}
    return {"text": str(obj)}


def _normalize_openai_transcription(payload: dict, path: str) -> dict:
    duration_s = _wav_duration_s(path)
    segments_raw = payload.get("segments") or []
    segments = []

    if segments_raw:
        for raw in segments_raw:
            seg = _to_plain(raw)
            start = float(seg.get("start", seg.get("t_start", 0.0)) or 0.0)
            end = float(seg.get("end", seg.get("t_end", start)) or start)
            text = str(seg.get("text", "") or "").strip()
            words = _normalize_words(seg.get("words") or [], text, start, end)
            segments.append({
                "start": start,
                "end": end,
                "text": text,
                "no_speech_prob": seg.get("no_speech_prob"),
                "words": words,
            })
    else:
        text = str(payload.get("text") or payload.get("transcript") or "").strip()
        if text:
            top_words = payload.get("words") or []
            segments.append({
                "start": 0.0,
                "end": duration_s,
                "text": text,
                "no_speech_prob": None,
                "words": _normalize_words(top_words, text, 0.0, duration_s),
            })

    full_text = " ".join(s["text"].strip() for s in segments).strip()
    word_count = sum(len(s["words"]) for s in segments)
    wpm = (word_count / duration_s * 60) if duration_s else 0.0
    return {
        "duration_s": duration_s,
        "wpm": wpm,
        "word_count": word_count,
        "full_text": full_text,
        "segments": segments,
    }


def _normalize_words(raw_words: list, fallback_text: str, start: float, end: float) -> list[dict]:
    words = []
    for raw in raw_words:
        item = _to_plain(raw)
        word = str(item.get("word") or item.get("text") or "").strip()
        if not word:
            continue
        words.append({
            "start": float(item.get("start", item.get("t_start", start)) or start),
            "end": float(item.get("end", item.get("t_end", end)) or end),
            "word": word,
            "prob": _optional_float(item.get("prob", item.get("probability", item.get("confidence")))),
        })
    return words or _approx_words(fallback_text, start, end)


def _approx_words(text: str, start: float, end: float) -> list[dict]:
    tokens = re.findall(r"\S+", text.strip())
    if not tokens:
        return []
    duration = max(0.001, end - start)
    step = duration / len(tokens)
    return [
        {
            "start": start + i * step,
            "end": start + (i + 1) * step,
            "word": token,
            "prob": None,
        }
        for i, token in enumerate(tokens)
    ]


def _wav_duration_s(path: str) -> float:
    try:
        with wave.open(path, "rb") as wf:
            rate = wf.getframerate()
            return wf.getnframes() / rate if rate else 0.0
    except Exception:
        return 0.0


def _optional_float(value: Any) -> float | None:
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None
