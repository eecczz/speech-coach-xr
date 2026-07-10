// Browser-side silence detector via Web Audio AnalyserNode RMS.
// Lets us flag long pauses even before STT (Phase 2) is wired up.
//
// Usage:
//   const det = new SilenceDetector(micStream);
//   det.start();
//   ...
//   const { silenceSeconds, rmsMean } = det.snapshot();  // call once per push
//   det.resetWindow();

const SILENCE_RMS_THRESHOLD = 0.015;  // empirical — quiet room baseline
const POLL_INTERVAL_MS = 100;

export class SilenceDetector {
  private stream: MediaStream;
  private ctx: AudioContext | null = null;
  private analyser: AnalyserNode | null = null;
  private buf: Float32Array<ArrayBuffer> = new Float32Array(new ArrayBuffer(0));
  private timer: number | null = null;
  // continuous silence so far (seconds) — resets when sound > threshold
  private currentSilence = 0;
  // peak silence within the current reporting window
  private windowPeakSilence = 0;
  // RMS sum + sample count for window mean
  private windowRmsSum = 0;
  private windowSamples = 0;
  // wall-clock of last sample
  private lastSampleMs = 0;

  constructor(stream: MediaStream) {
    this.stream = stream;
  }

  start(): void {
    if (this.ctx) return;
    const audioTracks = this.stream.getAudioTracks();
    if (audioTracks.length === 0) return;
    this.ctx = new AudioContext();
    const source = this.ctx.createMediaStreamSource(this.stream);
    this.analyser = this.ctx.createAnalyser();
    this.analyser.fftSize = 2048;
    source.connect(this.analyser);
    // Allocate a fresh ArrayBuffer-backed Float32Array (not SharedArrayBuffer-backed)
    // so it satisfies the AnalyserNode.getFloatTimeDomainData type signature.
    this.buf = new Float32Array(new ArrayBuffer(this.analyser.fftSize * 4));
    this.lastSampleMs = performance.now();
    this.timer = window.setInterval(() => this.sample(), POLL_INTERVAL_MS);
  }

  private sample(): void {
    if (!this.analyser) return;
    this.analyser.getFloatTimeDomainData(this.buf);
    let sum = 0;
    for (let i = 0; i < this.buf.length; i++) sum += this.buf[i] * this.buf[i];
    const rms = Math.sqrt(sum / this.buf.length);

    const now = performance.now();
    const dt = (now - this.lastSampleMs) / 1000;
    this.lastSampleMs = now;

    if (rms < SILENCE_RMS_THRESHOLD) {
      this.currentSilence += dt;
    } else {
      this.currentSilence = 0;
    }
    this.windowPeakSilence = Math.max(this.windowPeakSilence, this.currentSilence);
    this.windowRmsSum += rms;
    this.windowSamples++;
  }

  snapshot(): { silenceSeconds: number; rmsMean: number } {
    const rmsMean = this.windowSamples > 0 ? this.windowRmsSum / this.windowSamples : 0;
    return { silenceSeconds: this.windowPeakSilence, rmsMean };
  }

  resetWindow(): void {
    this.windowPeakSilence = this.currentSilence; // keep the running silence carry-over
    this.windowRmsSum = 0;
    this.windowSamples = 0;
  }

  stop(): void {
    if (this.timer != null) {
      window.clearInterval(this.timer);
      this.timer = null;
    }
    if (this.ctx) {
      this.ctx.close();
      this.ctx = null;
    }
    this.analyser = null;
  }
}
