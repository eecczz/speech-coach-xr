// Records video + mic audio into a single webm blob.
//
// NOTE: this used to record the avatar CANVAS (privacy pivot — only the VRM render
// left the browser). Evaluation now needs the real user's frames (pupils for gaze,
// fine gestures, etc.) so we record the user's MediaStream directly. The avatar
// canvas is hidden in CSS but still rendered live for any future use.

export interface AvatarRecording {
  blob: Blob;
  url: string;
  durationMs: number;
}

export class AvatarRecorder {
  private recorder: MediaRecorder | null = null;
  private chunks: Blob[] = [];
  private startedAt = 0;
  private stoppedResolve: ((rec: AvatarRecording) => void) | null = null;

  get state(): RecordingState {
    return this.recorder?.state ?? 'inactive';
  }

  // Pause/resume mirror MediaRecorder's own methods. We don't try to subtract
  // paused time from durationMs — the recorded webm's own metadata is the
  // authoritative duration, and the UI computes paused-adjusted session time
  // separately.
  pause(): void {
    if (this.recorder && this.recorder.state === 'recording') this.recorder.pause();
  }

  resume(): void {
    if (this.recorder && this.recorder.state === 'paused') this.recorder.resume();
  }

  // Records the user's MediaStream (video + audio) as-is. Pass the same stream
  // returned by getUserMedia — both tracks travel into the recorder together so
  // audio-video sync is whatever the browser captures, no extra plumbing needed.
  start(userStream: MediaStream): void {
    const mime = MediaRecorder.isTypeSupported('video/webm;codecs=vp9,opus')
      ? 'video/webm;codecs=vp9,opus'
      : 'video/webm';

    this.chunks = [];
    this.recorder = new MediaRecorder(userStream, { mimeType: mime });
    this.recorder.ondataavailable = (e) => {
      if (e.data.size > 0) this.chunks.push(e.data);
    };
    this.recorder.onstop = () => {
      const blob = new Blob(this.chunks, { type: mime });
      const url = URL.createObjectURL(blob);
      const rec: AvatarRecording = {
        blob,
        url,
        durationMs: performance.now() - this.startedAt,
      };
      this.stoppedResolve?.(rec);
    };
    this.startedAt = performance.now();
    this.recorder.start(1000);
  }

  stop(): Promise<AvatarRecording> {
    return new Promise((resolve) => {
      if (!this.recorder || this.recorder.state === 'inactive') {
        resolve({ blob: new Blob(), url: '', durationMs: 0 });
        return;
      }
      this.stoppedResolve = resolve;
      this.recorder.stop();
    });
  }
}
