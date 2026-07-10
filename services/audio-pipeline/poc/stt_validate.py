"""
W1 PoC — faster-whisper 한국어 발표 STT 검증

목적:
- 한국어 발표 wav/mp3을 faster-whisper large-v3로 전사
- segment/word 타임스탬프 추출 (evidence_clip 가능성 확인)
- 첫 segment 도착 지연·RTF 측정 (라이브성 평가)
- (옵션) reference transcript로 WER 측정

전체 파일 일괄 처리 + VAD 필터 사용. 진짜 청크 스트리밍은
whisper_streaming / Pipecat WhisperSTTService 단계에서 별도 검증.

사용법 (PowerShell):
    python stt_validate.py --audio ..\samples\sample.wav --ref ..\samples\sample.txt
"""

import argparse
import json
import time
from pathlib import Path

from faster_whisper import WhisperModel


def wer(ref: str, hyp: str) -> float:
    r = ref.split()
    h = hyp.split()
    if not r:
        return 0.0
    d = [[0] * (len(h) + 1) for _ in range(len(r) + 1)]
    for i in range(len(r) + 1):
        d[i][0] = i
    for j in range(len(h) + 1):
        d[0][j] = j
    for i in range(1, len(r) + 1):
        for j in range(1, len(h) + 1):
            cost = 0 if r[i - 1] == h[j - 1] else 1
            d[i][j] = min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + cost)
    return d[len(r)][len(h)] / len(r)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--audio", required=True, help="입력 오디오 (wav/mp3)")
    parser.add_argument("--model", default="large-v3")
    parser.add_argument("--device", default="auto", choices=["auto", "cuda", "cpu"])
    parser.add_argument("--compute-type", default="auto",
                        help="float16(GPU)/int8(CPU)/float32 — auto=device에 맞춰 선택")
    parser.add_argument("--language", default="ko")
    parser.add_argument("--ref", default=None, help="reference transcript txt")
    args = parser.parse_args()

    if args.device == "auto":
        try:
            import torch
            device = "cuda" if torch.cuda.is_available() else "cpu"
        except ImportError:
            device = "cpu"
    else:
        device = args.device

    if args.compute_type == "auto":
        compute_type = "float16" if device == "cuda" else "int8"
    else:
        compute_type = args.compute_type

    print(f"[Init] model={args.model} device={device} compute_type={compute_type}")
    t0 = time.perf_counter()
    model = WhisperModel(args.model, device=device, compute_type=compute_type)
    print(f"[Init] loaded in {time.perf_counter() - t0:.1f}s")

    t0 = time.perf_counter()
    segments_iter, info = model.transcribe(
        args.audio,
        language=args.language,
        word_timestamps=True,
        vad_filter=True,
        vad_parameters={"min_silence_duration_ms": 500},
    )

    segments = []
    first_segment_latency = None
    prev_t = t0
    for seg in segments_iter:
        now = time.perf_counter()
        if first_segment_latency is None:
            first_segment_latency = now - t0
        words = [
            {"start": w.start, "end": w.end, "word": w.word, "prob": w.probability}
            for w in (seg.words or [])
        ]
        segments.append({
            "start": seg.start,
            "end": seg.end,
            "text": seg.text,
            "avg_logprob": seg.avg_logprob,
            "no_speech_prob": seg.no_speech_prob,
            "words": words,
            "wallclock_since_prev_s": now - prev_t,
        })
        prev_t = now
        print(f"[{seg.start:6.2f}-{seg.end:6.2f}] (+{now - t0:5.2f}s wall) {seg.text}")

    total_elapsed = time.perf_counter() - t0
    audio_duration = info.duration
    rtf = total_elapsed / audio_duration if audio_duration else 0.0
    full_text = " ".join(s["text"].strip() for s in segments).strip()

    print("\n=== 결과 요약 ===")
    print(f"Detected language: {info.language} (prob={info.language_probability:.2f})")
    print(f"Audio duration:    {audio_duration:.1f}s")
    print(f"Total elapsed:     {total_elapsed:.1f}s  (RTF={rtf:.3f})")
    print(f"First seg latency: {first_segment_latency:.2f}s")
    print(f"Segments: {len(segments)}")
    print(f"\n전사:\n{full_text}\n")

    wer_score = None
    if args.ref:
        ref_text = Path(args.ref).read_text(encoding="utf-8").strip()
        wer_score = wer(ref_text, full_text)
        print(f"WER vs reference: {wer_score * 100:.2f}%")

    out = Path(args.audio).with_suffix(".transcript.json")
    out.write_text(json.dumps({
        "audio": str(args.audio),
        "model": args.model,
        "device": device,
        "compute_type": compute_type,
        "language_detected": info.language,
        "language_probability": info.language_probability,
        "audio_duration_s": audio_duration,
        "total_elapsed_s": total_elapsed,
        "rtf": rtf,
        "first_segment_latency_s": first_segment_latency,
        "wer": wer_score,
        "segments": segments,
        "full_text": full_text,
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Saved: {out}")


if __name__ == "__main__":
    main()
