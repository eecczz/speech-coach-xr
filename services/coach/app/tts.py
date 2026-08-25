"""Server-side Korean TTS proxy for the Unity client.

Secrets stay on the coach server. Azure Speech is used when credentials are
configured. Local prototypes fall back to the key-free online Edge TTS client,
using the same Korean neural voice names and rate/pitch controls.
"""

from __future__ import annotations

import html
import asyncio
import os
import urllib.error
import urllib.request
from typing import Literal

import edge_tts
from pydantic import BaseModel, Field


ALLOWED_VOICES = {
    "ko-KR-SunHiNeural",
    "ko-KR-InJoonNeural",
    "ko-KR-HyunsuNeural",
    "ko-KR-BongJinNeural",
    "ko-KR-GookMinNeural",
    "ko-KR-JiMinNeural",
    "ko-KR-SeoHyeonNeural",
    "ko-KR-SoonBokNeural",
    "ko-KR-YuJinNeural",
    "ko-KR-HyunsuMultilingualNeural",
    "en-US-AndrewMultilingualNeural",
    "en-US-BrianMultilingualNeural",
}

EDGE_FALLBACK_VOICE = "ko-KR-HyunsuMultilingualNeural"


class TtsRequest(BaseModel):
    text: str = Field(min_length=1, max_length=600)
    voice: str = "ko-KR-SunHiNeural"
    rate_percent: int = Field(default=0, ge=-30, le=30)
    pitch_percent: int = Field(default=0, ge=-20, le=20)
    tone: Literal["warm", "neutral", "challenging"] = "neutral"


async def synthesize_audio(req: TtsRequest) -> bytes:
    key = os.environ.get("AZURE_SPEECH_KEY", "").strip()
    region = os.environ.get("AZURE_SPEECH_REGION", "koreacentral").strip()
    if req.voice not in ALLOWED_VOICES:
        raise ValueError(f"unsupported voice: {req.voice}")

    # Persona tone is deliberately subtle; this should still sound like a real
    # interview, not a cartoon performance.
    tone_pitch = {"warm": 2, "neutral": 0, "challenging": -2}[req.tone]
    pitch = max(-20, min(20, req.pitch_percent + tone_pitch))
    rate = max(-30, min(30, req.rate_percent))
    if not key:
        # Key-free online fallback for local/Quest prototyping. It uses the same
        # Korean neural voice names, so interviewer casting stays consistent.
        # Production deployments can set AZURE_SPEECH_KEY to use the supported API.
        return await _synthesize_edge(req.text, req.voice, rate, pitch)

    return await asyncio.to_thread(_synthesize_azure, req, key, region, rate, pitch)


async def _synthesize_edge(text: str, requested_voice: str, rate: int, pitch: int) -> bytes:
    """Generate prototype audio and recover from temporarily unavailable voices."""
    voices = list(dict.fromkeys((requested_voice, EDGE_FALLBACK_VOICE)))
    failures: list[str] = []
    for voice in voices:
        for attempt in range(2):
            chunks: list[bytes] = []
            try:
                communicate = edge_tts.Communicate(
                    text,
                    voice,
                    rate=f"{rate:+d}%",
                    pitch=f"{pitch * 2:+d}Hz",
                )
                async for message in communicate.stream():
                    if message["type"] == "audio":
                        chunks.append(message["data"])
                if chunks:
                    return b"".join(chunks)
                failures.append(f"{voice} attempt {attempt + 1}: no audio")
            except Exception as exc:  # edge service errors vary by package version
                failures.append(f"{voice} attempt {attempt + 1}: {type(exc).__name__}")
            await asyncio.sleep(0.15)
    raise RuntimeError("Edge TTS failed: " + "; ".join(failures))


def _synthesize_azure(req: TtsRequest, key: str, region: str, rate: int, pitch: int) -> bytes:
    ssml = (
        "<speak version='1.0' xml:lang='ko-KR'>"
        f"<voice name='{req.voice}'>"
        f"<prosody rate='{rate:+d}%' pitch='{pitch:+d}%'>"
        f"{html.escape(req.text)}"
        "</prosody></voice></speak>"
    ).encode("utf-8")
    url = f"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1"
    request = urllib.request.Request(url, data=ssml, method="POST")
    request.add_header("Ocp-Apim-Subscription-Key", key)
    request.add_header("Content-Type", "application/ssml+xml")
    request.add_header("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3")
    request.add_header("User-Agent", "SpeakUpXR")
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            return response.read()
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")[:300]
        raise RuntimeError(f"Azure Speech {exc.code}: {body}") from exc
