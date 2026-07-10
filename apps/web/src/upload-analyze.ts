import { detect, type Landmarkers } from './mediapipe/landmarkers';
import { computeVisionFrame, resetSignalState } from './signals/compute';
import type { AggregatorClient, AudioAnalysisResult } from './ws/client';
import { uploadForAnalysis } from './audio-upload';
import type { ComprehensiveReport } from './review/types';

const SAMPLE_INTERVAL_MS = 200;
const PLAYBACK_RATE = 2;
const META_TIMEOUT_MS = 15000;
const SEEK_FALLBACK_TIMEOUT_MS = 3000;

export interface UploadAnalyzeContext {
  scenario: string;
  focusGoals: string[];
  landmarkers: Landmarkers;
  aggregator: AggregatorClient;
  setStatus: (msg: string) => void;
  onPhaseChange?: (phase: 'video' | 'audio' | 'content' | 'done') => void;
}

export async function analyzeUploadedVideo(
  file: File,
  ctx: UploadAnalyzeContext,
): Promise<ComprehensiveReport | null> {
  const url = URL.createObjectURL(file);

  const probe = document.createElement('video');
  probe.src = url;
  probe.muted = true;
  probe.preload = 'auto';
  probe.playsInline = true;
  probe.crossOrigin = 'anonymous';
  probe.style.position = 'fixed';
  probe.style.left = '-99999px';
  probe.style.top = '0';
  probe.style.width = '320px';
  probe.style.height = '240px';
  document.body.appendChild(probe);

  try {
    ctx.onPhaseChange?.('video');
    ctx.setStatus('영상 로딩 중…');
    await waitFor(probe, META_TIMEOUT_MS);
    const duration = isFinite(probe.duration) && probe.duration > 0 ? probe.duration : 0;
    if (duration <= 0) {
      throw new Error('영상 길이를 알 수 없습니다 (재인코딩이 필요할 수 있어요)');
    }

    const sessionId = `upload_${Date.now()}`;
    resetSignalState();
    await ctx.aggregator.start(sessionId, ctx.scenario, ctx.focusGoals);

    probe.playbackRate = PLAYBACK_RATE;
    await probe.play();

    let lastTs = -1;
    let lastReportedPct = -1;
    while (!probe.ended && !probe.paused) {
      const t = probe.currentTime;
      const ts = Math.max(Math.floor(performance.now()), lastTs + 1);
      lastTs = ts;
      try {
        const { face, pose, hand } = detect(ctx.landmarkers, probe, ts);
        const frame = computeVisionFrame(t, face, pose, hand);
        ctx.aggregator.sendVision(frame);
      } catch (e) {
        console.warn('[upload] detect error', e);
      }
      const pct = Math.min(99, Math.round((t / duration) * 100));
      if (pct !== lastReportedPct) {
        ctx.setStatus(`비전 분석 중… ${pct}% (${t.toFixed(1)} / ${duration.toFixed(1)}s)`);
        lastReportedPct = pct;
      }
      await sleep(SAMPLE_INTERVAL_MS);
    }

    ctx.onPhaseChange?.('audio');
    ctx.setStatus('음성 분석 중… (Whisper + 운율, 최대 ~1분)');
    let audioResult: AudioAnalysisResult | null = null;
    try {
      audioResult = await uploadForAnalysis(file, sessionId, file.name);
      console.log('[upload] audio analyze', audioResult);
    } catch (e) {
      console.warn('[upload] /analyze failed — proceeding without server audio', e);
    }

    ctx.onPhaseChange?.('content');
    ctx.setStatus('평가 생성 중…');
    const result = await ctx.aggregator.end(audioResult);
    ctx.aggregator.close();

    if (result && (result as { report?: ComprehensiveReport }).report) {
      const report = (result as { report: ComprehensiveReport }).report;
      ctx.onPhaseChange?.('done');
      ctx.setStatus(
        `평가 완료 — 종합 ${report.accuracy_overall?.toFixed(1) ?? '?'}점, 순간 ${report.annotated_moments?.length ?? 0}개`,
      );
      return report;
    }

    ctx.setStatus('평가 실패 — 콘솔 확인');
    console.warn('[upload] coach result missing or malformed', result);
    return null;
  } finally {
    try {
      probe.pause();
    } catch {
      // ignore
    }
    URL.revokeObjectURL(url);
    probe.remove();
  }
}

function waitFor(video: HTMLVideoElement, timeoutMs: number): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    const onMeta = () => {
      cleanup();
      resolve();
    };
    const onErr = () => {
      cleanup();
      reject(new Error('영상 로드 실패'));
    };
    const cleanup = () => {
      video.removeEventListener('loadedmetadata', onMeta);
      video.removeEventListener('error', onErr);
      clearTimeout(timer);
    };
    video.addEventListener('loadedmetadata', onMeta);
    video.addEventListener('error', onErr);
    const timer = setTimeout(() => {
      cleanup();
      reject(new Error('영상 메타데이터 타임아웃'));
    }, timeoutMs);
    if (video.readyState >= 1) onMeta();
  });
}

function sleep(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms));
}

void SEEK_FALLBACK_TIMEOUT_MS;
