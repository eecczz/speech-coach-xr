import * as THREE from 'three';
import { VRButton } from 'three/examples/jsm/webxr/VRButton.js';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { buildRoomShell, buildDesk, buildInterviewerChair, ROOM } from './interview-room';
import { loadInterviewer, type Interviewer } from './interviewer';
import { createInterviewUI } from './interview-ui';
import { createInterviewSession, type InterviewSession, type SessionState } from './interview-session';
import { createScriptBrain } from './interview-brain';
import { createLlmBrain } from './llm-brain';
import type { InterviewBrain, QAExchange } from './interview-brain';

export interface XRInterviewStage {
  scene: THREE.Scene;
  renderer: THREE.WebGLRenderer;
  interviewer: Interviewer | null;
  /** The interview loop controller — null if the interviewer failed to load. */
  session: InterviewSession | null;
  dispose: () => void;
}

export interface XRInterviewHooks {
  onStatus?: (msg: string) => void;
  onSessionState?: (state: SessionState) => void;
  onDone?: (history: QAExchange[]) => void;
  /** 'llm' asks the coach service for answer-aware questions; 'script' is offline. */
  brainMode?: 'script' | 'llm';
}

// Seated eye position for the desktop (non-XR) preview camera. In an XR session the
// headset overrides the camera pose, so this only matters on a flat screen.
const DESKTOP_EYE = new THREE.Vector3(0, 1.24, 0.55);
const INTERVIEWER_HEAD = new THREE.Vector3(0, 1.16, ROOM.interviewerZ);

/**
 * Create the WebXR interview stage on a full-window canvas.
 * Renders on desktop immediately and exposes an "Enter VR" button for Quest browsers.
 */
export async function createXRInterview(
  container: HTMLElement,
  vrmUrl: string,
  hooks: XRInterviewHooks = {},
): Promise<XRInterviewStage> {
  const { onStatus, onSessionState, onDone, brainMode = 'script' } = hooks;
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0xc9ced6);

  const camera = new THREE.PerspectiveCamera(
    55,
    window.innerWidth / window.innerHeight,
    0.05,
    50,
  );
  camera.position.copy(DESKTOP_EYE);
  camera.lookAt(INTERVIEWER_HEAD);

  const renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.setSize(window.innerWidth, window.innerHeight);
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.xr.enabled = true;
  // Seated/standing room-scale: y=0 is the physical floor.
  renderer.xr.setReferenceSpaceType('local-floor');
  container.appendChild(renderer.domElement);

  // "Enter VR" button (shows on WebXR-capable browsers, e.g. Meta Quest).
  const vrButton = VRButton.createButton(renderer);
  document.body.appendChild(vrButton);

  // Desktop-only orbit inspection; auto-bypassed once an XR session drives the camera.
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.target.copy(INTERVIEWER_HEAD);
  controls.enableDamping = true;
  controls.maxDistance = 4;
  controls.update();

  // Lighting
  scene.add(new THREE.HemisphereLight(0xffffff, 0x808080, 1.1));
  const key = new THREE.DirectionalLight(0xffffff, 1.2);
  key.position.set(1.5, 3, 1.5);
  scene.add(key);
  const fill = new THREE.DirectionalLight(0xffffff, 0.4);
  fill.position.set(-2, 2, 1);
  scene.add(fill);

  // Static environment
  buildRoomShell(scene);
  buildInterviewerChair(scene);
  buildDesk(scene);

  // In-world question / status panel.
  const ui = createInterviewUI(scene);
  ui.setQuestion('“면접 시작”을 누르면 면접관이 질문을 시작합니다.');
  ui.setStatus('대기 중', 'ask');

  onStatus?.('가상 면접관을 불러오는 중…');
  let interviewer: Interviewer | null = null;
  let session: InterviewSession | null = null;
  try {
    interviewer = await loadInterviewer(scene, vrmUrl);
    const brain: InterviewBrain =
      brainMode === 'llm'
        ? createLlmBrain({ difficulty: '보통', maxQuestions: 5 })
        : createScriptBrain({ maxBaseQuestions: 4, followupEvery: 2 });
    session = createInterviewSession({
      brain,
      interviewer,
      ui,
      onStateChange: onSessionState,
      onDone,
    });
    onStatus?.(`면접관 준비 완료 (${brainMode === 'llm' ? 'AI' : '스크립트'}). "면접 시작"을 누르세요.`);
  } catch (err) {
    console.error('[xr] interviewer load failed:', err);
    onStatus?.('면접관 로드 실패 — 콘솔을 확인하세요.');
  }

  // Reusable scratch vector for the interviewer's gaze target (the user's head).
  const headWorld = new THREE.Vector3();
  const xrCam = new THREE.Vector3();

  renderer.setAnimationLoop((_time, _frame) => {
    const dt = clockDelta();

    // Where is the user's head? In XR, read the active headset camera; otherwise
    // use the desktop preview camera position.
    if (renderer.xr.isPresenting) {
      renderer.xr.getCamera().getWorldPosition(xrCam);
      headWorld.copy(xrCam);
    } else {
      controls.update();
      camera.getWorldPosition(headWorld);
    }

    interviewer?.update(dt, headWorld);
    ui.faceCamera(headWorld);
    renderer.render(scene, camera);
  });

  const onResize = () => {
    camera.aspect = window.innerWidth / window.innerHeight;
    camera.updateProjectionMatrix();
    renderer.setSize(window.innerWidth, window.innerHeight);
  };
  window.addEventListener('resize', onResize);

  function dispose() {
    window.removeEventListener('resize', onResize);
    renderer.setAnimationLoop(null);
    renderer.dispose();
    vrButton.remove();
    renderer.domElement.remove();
  }

  return { scene, renderer, interviewer, session, dispose };
}

// Simple frame-delta clock (setAnimationLoop's time arg is absolute).
let _last = 0;
function clockDelta(): number {
  const now = performance.now() / 1000;
  if (_last === 0) _last = now;
  const dt = Math.min(now - _last, 0.05);
  _last = now;
  return dt;
}
