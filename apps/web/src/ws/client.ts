import type { VisionFrame } from '../signals/compute';

export interface ProsodyFrame {
  t_start: number;
  t_end: number;
  wpm?: number;
  word_count?: number;
  filler_count?: number;
  filler_terms?: string[];
  pause_seconds?: number;
  silence_seconds?: number;
  rms_mean?: number;
}

export interface AudioAnalysisResult {
  session_id?: string;
  full_transcript?: string;
  stt_segments?: unknown[];
  prosody_frames?: unknown[];
  elapsed_s?: number;
}

export interface LiveHudSignal {
  level: 'info' | 'warn' | 'critical';
  text: string;
  kind: string;
}

export interface LiveHudResponse {
  window_t_start: number;
  window_t_end: number;
  signals: LiveHudSignal[];
}

export interface AggregatorClient {
  start(sessionId: string, scenario?: string, focusGoals?: string[]): Promise<void>;
  sendVision(frame: VisionFrame): void;
  sendProsody(frame: ProsodyFrame): void;
  end(audioResult?: AudioAnalysisResult | null): Promise<unknown>;
  close(): void;
}

export interface HudClient {
  connect(onMessage: (payload: LiveHudResponse) => void): Promise<void>;
  close(): void;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function errorTextFromPayload(payload: unknown, status: number): string {
  if (isRecord(payload)) {
    if (typeof payload.error === 'string') return payload.error;
    if (typeof payload.body_preview === 'string') return payload.body_preview;
    const coachResponse = payload.coach_response;
    if (isRecord(coachResponse)) {
      if (typeof coachResponse.error === 'string') return coachResponse.error;
      if (typeof coachResponse.detail === 'string') return coachResponse.detail;
    }
  }
  return `리포트 생성 서버가 ${status} 상태를 반환했습니다.`;
}

export async function finalizeSession(
  sessionId: string,
  audioResult?: AudioAnalysisResult | null,
  httpBase = defaultAggregatorHttpBase(),
): Promise<unknown> {
  const body: Record<string, unknown> = { session_id: sessionId };
  if (audioResult) {
    if (audioResult.stt_segments) body.stt_segments = audioResult.stt_segments;
    if (audioResult.prosody_frames) body.prosody_frames = audioResult.prosody_frames;
    if (audioResult.full_transcript) body.full_transcript = audioResult.full_transcript;
  }
  try {
    const r = await fetch(`${httpBase}/session/end`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const text = await r.text();
    let payload: unknown = null;
    if (text) {
      try {
        payload = JSON.parse(text);
      } catch {
        payload = { error: text };
      }
    }
    if (!r.ok) {
      throw new Error(errorTextFromPayload(payload, r.status));
    }
    return payload;
  } catch (e) {
    console.warn('[ws] session/end failed', e);
    if (e instanceof Error) throw e;
    throw new Error('분석 서버에 연결하지 못했습니다.');
  }
}

export function createAggregatorClient(opts: {
  httpBase?: string;
  wsBase?: string;
} = {}): AggregatorClient {
  const httpBase = opts.httpBase ?? defaultAggregatorHttpBase();
  const wsBase = opts.wsBase ?? defaultAggregatorWsUrl();
  let ws: WebSocket | null = null;
  let sessionId: string | null = null;

  return {
    async start(sid, scenario = 'presentation', focusGoals: string[] = []) {
      sessionId = sid;
      try {
        const r = await fetch(`${httpBase}/session/start`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ session_id: sid, scenario, focus_goals: focusGoals }),
        });
        if (!r.ok) console.warn('[ws] session/start failed', r.status);
      } catch (e) {
        console.warn('[ws] session/start error — aggregator unreachable?', e);
        return;
      }

      const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
      const url = wsBase || `${proto}//${location.host}/ws/signals`;
      ws = new WebSocket(url);
      await new Promise<void>((resolve) => {
        if (!ws) return resolve();
        ws.onopen = () => {
          console.log('[ws] aggregator connected');
          resolve();
        };
        ws.onerror = (e) => {
          console.warn('[ws] connect error', e);
          resolve();
        };
      });
    },

    sendVision(frame) {
      if (!ws || ws.readyState !== WebSocket.OPEN) return;
      try {
        ws.send(JSON.stringify({ kind: 'vision', data: frame }));
      } catch {
        // Drop frame quietly.
      }
    },

    sendProsody(frame) {
      if (!ws || ws.readyState !== WebSocket.OPEN) return;
      try {
        ws.send(JSON.stringify({ kind: 'prosody', data: frame }));
      } catch {
        // Drop frame quietly.
      }
    },

    async end(audioResult?: AudioAnalysisResult | null) {
      if (!sessionId) return null;
      return finalizeSession(sessionId, audioResult, httpBase);
    },

    close() {
      if (ws) {
        ws.close();
        ws = null;
      }
    },
  };
}

export function createHudClient(opts: { wsBase?: string } = {}): HudClient {
  const wsBase = opts.wsBase ?? defaultHudWsUrl();
  let ws: WebSocket | null = null;
  let keepalive: number | null = null;

  return {
    async connect(onMessage) {
      const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
      const url = wsBase || `${proto}//${location.host}/ws/hud`;
      ws = new WebSocket(url);
      await new Promise<void>((resolve) => {
        if (!ws) return resolve();
        ws.onopen = () => {
          keepalive = window.setInterval(() => {
            if (ws?.readyState === WebSocket.OPEN) ws.send('ping');
          }, 15000);
          resolve();
        };
        ws.onerror = () => resolve();
        ws.onmessage = (event) => {
          try {
            onMessage(JSON.parse(event.data) as LiveHudResponse);
          } catch (error) {
            console.warn('[hud] parse error', error);
          }
        };
      });
    },

    close() {
      if (keepalive) {
        window.clearInterval(keepalive);
        keepalive = null;
      }
      if (ws) {
        ws.close();
        ws = null;
      }
    },
  };
}

function defaultAggregatorHttpBase(): string {
  return location.port === '8000' ? originWithPort('8001') : '';
}

function defaultAggregatorWsUrl(): string {
  return location.port === '8000' ? wsUrlWithPort('8001', '/ws/signals') : '';
}

function defaultHudWsUrl(): string {
  return location.port === '8000' ? wsUrlWithPort('8001', '/ws/hud') : '';
}

function wsUrlWithPort(port: string, pathname: string): string {
  const url = new URL(location.href);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  url.port = port;
  url.pathname = pathname;
  url.search = '';
  url.hash = '';
  return url.toString();
}

function originWithPort(port: string): string {
  const url = new URL(location.href);
  url.port = port;
  url.pathname = '';
  url.search = '';
  url.hash = '';
  return url.origin;
}
