// Shared multipart upload to audio-pipeline /analyze.
// Used by both the live recording flow (practice.ts) and the uploaded-video
// flow (upload-analyze.ts) — same payload shape, same response.

import type { AudioAnalysisResult } from './ws/client';

export async function uploadForAnalysis(
  blobOrFile: Blob,
  sessionId: string,
  filename = 'rec.webm',
  language = 'ko',
): Promise<AudioAnalysisResult | null> {
  const fd = new FormData();
  // Use the file's own name if it's a File (preserves extension hint for ffmpeg).
  const name = (blobOrFile as File).name || filename;
  fd.append('audio', blobOrFile, name);
  fd.append('session_id', sessionId);
  fd.append('language', language);
  const r = await fetch('/analyze', { method: 'POST', body: fd });
  if (!r.ok) {
    const detail = await r.text();
    console.warn('[audio] /analyze HTTP', r.status, detail);
    throw new Error(detail || `음성 분석 서버가 ${r.status} 상태를 반환했습니다.`);
  }
  return (await r.json()) as AudioAnalysisResult;
}
