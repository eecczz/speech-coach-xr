import * as THREE from 'three';
import { ROOM } from './interview-room';

// A world-space panel floating above the desk that shows the current interviewer
// question plus a small status line ("듣는 중…", "생각 중…"). Rendered to a 2D
// canvas and mapped onto a plane so it reads crisply in both desktop and XR.

const PANEL_W_PX = 1024;
const PANEL_H_PX = 320;
const PANEL_W_M = 1.05; // world width in metres
const PANEL_H_M = (PANEL_H_PX / PANEL_W_PX) * PANEL_W_M;

export interface InterviewUI {
  group: THREE.Group;
  setQuestion: (text: string) => void;
  setStatus: (text: string, tone?: 'ask' | 'listen' | 'think' | 'done') => void;
  setInterim: (text: string) => void;
  /** Keep the panel facing the user (yaw only). Call each frame. */
  faceCamera: (camPos: THREE.Vector3) => void;
}

const TONE_COLOR: Record<string, string> = {
  ask: '#6ea8fe',
  listen: '#4ade80',
  think: '#fbbf24',
  done: '#cbd5e1',
};

export function createInterviewUI(scene: THREE.Scene): InterviewUI {
  const canvas = document.createElement('canvas');
  canvas.width = PANEL_W_PX;
  canvas.height = PANEL_H_PX;
  const ctx = canvas.getContext('2d')!;

  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.anisotropy = 4;

  const geo = new THREE.PlaneGeometry(PANEL_W_M, PANEL_H_M);
  const mat = new THREE.MeshBasicMaterial({ map: texture, transparent: true });
  const mesh = new THREE.Mesh(geo, mat);

  const group = new THREE.Group();
  group.name = 'interview-ui';
  group.add(mesh);
  // Above and slightly in front of the desk, clear of the interviewer's head.
  group.position.set(0, 1.72, ROOM.interviewerZ + 0.55);
  // Slight downward tilt so a seated user reads it comfortably.
  mesh.rotation.x = -0.1;
  scene.add(group);

  let question = '';
  let status = '';
  let statusTone: string = 'ask';
  let interim = '';

  function wrapText(text: string, maxWidth: number): string[] {
    // Korean has no spaces at every break point, so wrap greedily per character
    // while preferring to break on spaces.
    const lines: string[] = [];
    let line = '';
    for (const ch of text) {
      const test = line + ch;
      if (ctx.measureText(test).width > maxWidth && line) {
        lines.push(line);
        line = ch === ' ' ? '' : ch;
      } else {
        line = test;
      }
    }
    if (line) lines.push(line);
    return lines;
  }

  function redraw() {
    ctx.clearRect(0, 0, PANEL_W_PX, PANEL_H_PX);

    // Rounded card background
    const r = 28;
    ctx.fillStyle = 'rgba(16, 24, 32, 0.82)';
    ctx.beginPath();
    ctx.moveTo(r, 0);
    ctx.arcTo(PANEL_W_PX, 0, PANEL_W_PX, PANEL_H_PX, r);
    ctx.arcTo(PANEL_W_PX, PANEL_H_PX, 0, PANEL_H_PX, r);
    ctx.arcTo(0, PANEL_H_PX, 0, 0, r);
    ctx.arcTo(0, 0, PANEL_W_PX, 0, r);
    ctx.closePath();
    ctx.fill();

    // Status pill (top-left)
    if (status) {
      ctx.font = '600 30px system-ui, sans-serif';
      const padX = 22;
      const w = ctx.measureText(status).width + padX * 2;
      ctx.fillStyle = TONE_COLOR[statusTone] ?? '#6ea8fe';
      const pillY = 26;
      ctx.beginPath();
      ctx.roundRect(36, pillY, w, 50, 25);
      ctx.fill();
      ctx.fillStyle = '#0b1016';
      ctx.textBaseline = 'middle';
      ctx.fillText(status, 36 + padX, pillY + 26);
    }

    // Question text
    ctx.fillStyle = '#f5f7fa';
    ctx.font = '600 46px system-ui, sans-serif';
    ctx.textBaseline = 'top';
    const lines = wrapText(question, PANEL_W_PX - 96);
    let y = 108;
    for (const l of lines.slice(0, 3)) {
      ctx.fillText(l, 48, y);
      y += 58;
    }

    // Interim (candidate's live answer), dimmed at the bottom
    if (interim) {
      ctx.fillStyle = 'rgba(148, 163, 184, 0.9)';
      ctx.font = '400 30px system-ui, sans-serif';
      const il = wrapText('“' + interim + '”', PANEL_W_PX - 96);
      ctx.fillText(il[il.length - 1] ?? '', 48, PANEL_H_PX - 52);
    }

    texture.needsUpdate = true;
  }

  redraw();

  return {
    group,
    setQuestion(text) {
      question = text;
      interim = '';
      redraw();
    },
    setStatus(text, tone = 'ask') {
      status = text;
      statusTone = tone;
      redraw();
    },
    setInterim(text) {
      interim = text;
      redraw();
    },
    faceCamera(camPos) {
      const p = group.position;
      group.rotation.y = Math.atan2(camPos.x - p.x, camPos.z - p.z);
    },
  };
}
