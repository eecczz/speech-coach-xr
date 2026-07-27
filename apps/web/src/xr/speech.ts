// Thin wrappers around the browser Speech APIs, tuned for a Korean interview.
// TTS  = SpeechSynthesis (the interviewer's voice)
// STT  = Web Speech Recognition (the candidate's spoken answer)
//
// Both are the same primitives the verified 2D app uses (see practice.ts); we keep
// them isolated here so the XR session code stays declarative.

// ── Minimal typings (the DOM lib doesn't ship SpeechRecognition) ──
type SpeechRecognitionAlt = { transcript: string };
type SpeechRecognitionResult = { readonly isFinal: boolean; 0: SpeechRecognitionAlt; length: number };
type SpeechRecognitionEvt = Event & {
  resultIndex: number;
  results: { [i: number]: SpeechRecognitionResult; length: number };
};
type SpeechRecognitionErrEvt = Event & { error: string };
type SpeechRecognitionLike = {
  lang: string;
  continuous: boolean;
  interimResults: boolean;
  start: () => void;
  stop: () => void;
  abort: () => void;
  onresult: ((e: SpeechRecognitionEvt) => void) | null;
  onerror: ((e: SpeechRecognitionErrEvt) => void) | null;
  onend: (() => void) | null;
};
type SpeechRecognitionCtor = new () => SpeechRecognitionLike;

function getRecognitionCtor(): SpeechRecognitionCtor | null {
  const w = window as unknown as {
    SpeechRecognition?: SpeechRecognitionCtor;
    webkitSpeechRecognition?: SpeechRecognitionCtor;
  };
  return w.SpeechRecognition ?? w.webkitSpeechRecognition ?? null;
}

// ── TTS ──────────────────────────────────────────────────────────

let cachedKoVoice: SpeechSynthesisVoice | null = null;

function pickKoreanVoice(): SpeechSynthesisVoice | null {
  if (cachedKoVoice) return cachedKoVoice;
  if (!('speechSynthesis' in window)) return null;
  const voices = window.speechSynthesis.getVoices();
  cachedKoVoice =
    voices.find((v) => v.lang?.toLowerCase().startsWith('ko')) ??
    voices.find((v) => /korean|한국/i.test(v.name)) ??
    null;
  return cachedKoVoice;
}

if ('speechSynthesis' in window) {
  // Voices populate asynchronously on first load.
  window.speechSynthesis.onvoiceschanged = () => {
    cachedKoVoice = null;
    pickKoreanVoice();
  };
}

export interface SpeakHandle {
  /** Resolves when the utterance finishes (or is cancelled). */
  done: Promise<void>;
  cancel: () => void;
}

/**
 * Speak `text` in Korean. `onBoundary` fires roughly per word/char so the caller
 * can drive a mouth animation; `getLevel()` on the returned handle is a 0..1
 * pseudo-amplitude that also works when boundary events are unavailable.
 */
export function speak(text: string, opts: { onStart?: () => void; onEnd?: () => void } = {}): SpeakHandle {
  if (!('speechSynthesis' in window) || !text.trim()) {
    opts.onStart?.();
    opts.onEnd?.();
    return { done: Promise.resolve(), cancel: () => {} };
  }
  window.speechSynthesis.cancel();
  const u = new SpeechSynthesisUtterance(text);
  const voice = pickKoreanVoice();
  if (voice) u.voice = voice;
  u.lang = voice?.lang ?? 'ko-KR';
  u.rate = 1.0;
  u.pitch = 1.0;

  let settled = false;
  let resolveDone: () => void;
  const done = new Promise<void>((res) => (resolveDone = res));
  const finish = () => {
    if (settled) return;
    settled = true;
    clearTimeout(watchdog);
    opts.onEnd?.();
    resolveDone();
  };

  // Watchdog: some engines never fire `onend` (no voice loaded, autoplay quirks).
  // Cap the wait at a length-scaled estimate so the interview loop never stalls.
  const maxMs = Math.min(3000 + text.length * 130, 20000);
  const watchdog = setTimeout(finish, maxMs);

  u.onstart = () => opts.onStart?.();
  u.onend = finish;
  u.onerror = finish;

  window.speechSynthesis.speak(u);
  return {
    done,
    cancel: () => {
      window.speechSynthesis.cancel();
      finish();
    },
  };
}

export function ttsAvailable(): boolean {
  return 'speechSynthesis' in window;
}

// ── STT ──────────────────────────────────────────────────────────

export interface ListenHandle {
  stop: () => void;
}

export interface ListenCallbacks {
  onInterim?: (text: string) => void;
  onFinal?: (fullTranscript: string) => void;
  onError?: (err: string) => void;
  onEnd?: () => void;
}

/**
 * Listen for the candidate's answer. Accumulates final segments; `onFinal` is
 * called with the full accumulated transcript whenever a segment finalizes.
 * Returns null if the browser has no SpeechRecognition (caller should fall back
 * to manual advance).
 */
export function listen(cb: ListenCallbacks): ListenHandle | null {
  const Ctor = getRecognitionCtor();
  if (!Ctor) {
    cb.onError?.('speech-recognition-unsupported');
    return null;
  }
  const rec = new Ctor();
  rec.lang = 'ko-KR';
  rec.continuous = true;
  rec.interimResults = true;

  let finalText = '';
  rec.onresult = (e) => {
    let interim = '';
    for (let i = e.resultIndex; i < e.results.length; i++) {
      const r = e.results[i];
      const t = r[0]?.transcript ?? '';
      if (r.isFinal) finalText += t;
      else interim += t;
    }
    if (interim) cb.onInterim?.(interim);
    if (finalText) cb.onFinal?.(finalText.trim());
  };
  rec.onerror = (e) => cb.onError?.(e.error);
  rec.onend = () => cb.onEnd?.();

  try {
    rec.start();
  } catch (err) {
    cb.onError?.(String(err));
    return null;
  }
  return { stop: () => rec.stop() };
}

export function sttAvailable(): boolean {
  return getRecognitionCtor() !== null;
}
