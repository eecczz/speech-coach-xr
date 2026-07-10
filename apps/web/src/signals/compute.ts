import type {
  FaceLandmarkerResult,
  PoseLandmarkerResult,
  HandLandmarkerResult,
} from '@mediapipe/tasks-vision';
import * as THREE from 'three';

// 5-fps vision signal frame, mirrors VisionFrame in packages/schema.
export interface VisionFrame {
  t: number;
  gaze_fixation_ratio: number;
  posture_sway: number;
  shoulder_tilt: number;
  expression_diversity: number;
  hand_gesture_freq: number;
  // Peak wrist speed (image-normalized units/sec) in the last ~1s window. Spikes
  // here indicate a sudden burst of gesture — what the aggregator turns into a
  // "GESTURE_EXCESSIVE" mistake. (hand_gesture_freq, by contrast, is a cumulative
  // ratio and smooths out short bursts.)
  hand_velocity_max: number;
  head_pitch_deg: number;
  head_yaw_deg: number;
  head_roll_deg: number;
  chin_on_hand: boolean;
  mouth_open: number;
  // ── Domain-expansion signals ──
  // Smile blendshape (avg of mouthSmileLeft/Right, 0..1). Drives 소개팅/customer
  // service "warmth" scoring.
  smile_intensity: number;
  // Wrist inside upper-face region (cheek/nose/forehead) but NOT chin — covers
  // touching nose, scratching forehead, pushing hair back. Distinct from chin_on_hand.
  face_touch_other: boolean;
  // 0..1 — both wrists close together + small repetitive motion = fidget (안절부절).
  hand_fidget_score: number;
  // Wrist direction reversals per second in last ~2s window. High = 이중동작/망설임.
  motion_reversal_rate: number;
  // Head-yaw standard deviation in last ~2s window (degrees). High = 시선 좌우 산만.
  gaze_yaw_sway: number;
  // Frame-to-frame blendshape change rate (avg |Δ| of key blendshapes). High = 표정
  // 변화 풍부; low = 무표정.
  expression_change_rate: number;
}

// Internal state — rolling buffers + last positions.
const SHOULDER_BUFFER_S = 1.0;
const HAND_VELOCITY_THRESHOLD = 0.5; // image-normalized units/sec

interface ShoulderSample {
  t: number;
  midY: number;
}
const shoulderBuf: ShoulderSample[] = [];

let lastHand: { t: number; x: number; y: number } | null = null;
let handMovingFrames = 0;
let handTotalFrames = 0;
// Rolling buffer of frame-to-frame wrist speeds — used by hand_velocity_max for
// burst-gesture detection. Same window as posture sway for consistency.
const HAND_SPEED_WINDOW_S = 1.0;
const handSpeedBuf: Array<{ t: number; speed: number }> = [];

// ── Domain-expansion state ──
// Head-yaw rolling window for gaze_yaw_sway (좌우 시선 산만 점수).
const YAW_WINDOW_S = 2.0;
const yawBuf: Array<{ t: number; yaw: number }> = [];

// Wrist velocity history for motion-reversal counting (이중동작/망설임).
// Track last velocity vector; count reversals (dot product < 0).
const REVERSAL_WINDOW_S = 2.0;
let lastWristVel: { vx: number; vy: number } | null = null;
const reversalTimes: number[] = [];

// Both pose wrists for hand_fidget (양손 근접 + 미세 모션).
let lastLeftWrist: { t: number; x: number; y: number } | null = null;
let lastRightWrist: { t: number; x: number; y: number } | null = null;
const FIDGET_WINDOW_S = 1.0;
const fidgetSpeedBuf: Array<{ t: number; lspd: number; rspd: number }> = [];

// Previous frame's key blendshape vector for expression_change_rate.
const EXPRESSION_KEYS = [
  'mouthSmileLeft', 'mouthSmileRight',
  'browDownLeft', 'browDownRight',
  'browInnerUp', 'browOuterUpLeft', 'browOuterUpRight',
  'jawOpen', 'mouthFunnel', 'mouthPucker',
  'eyeBlinkLeft', 'eyeBlinkRight',
  'mouthFrownLeft', 'mouthFrownRight',
];
let lastBs: Map<string, number> | null = null;
let lastBsT = 0;
const EXP_CHANGE_WINDOW_S = 1.0;
const expChangeBuf: Array<{ t: number; delta: number }> = [];

const GAZE_CONE_RAD = (15 * Math.PI) / 180; // ±15°

interface HeadPose {
  pitchDeg: number; // +down, -up
  yawDeg: number;   // +subject-left
  rollDeg: number;  // +tilt toward subject-right shoulder
}

function computeHeadPose(face: FaceLandmarkerResult): HeadPose | null {
  const matrix = face.facialTransformationMatrixes?.[0]?.data;
  if (!matrix || matrix.length !== 16) return null;
  const m = new THREE.Matrix4().fromArray(matrix);
  const e = new THREE.Euler().setFromRotationMatrix(m, 'YXZ');
  // YXZ: e.y = yaw, e.x = pitch, e.z = roll
  return {
    pitchDeg: THREE.MathUtils.radToDeg(e.x),
    yawDeg: THREE.MathUtils.radToDeg(e.y),
    rollDeg: THREE.MathUtils.radToDeg(e.z),
  };
}

// Iris offset within each eye, normalized so 0 = pupil centered (looking forward
// relative to the head) and ±1 = at the eye corner. Magnitudes above ~0.3 already
// read as "looking sideways"; we treat 0.05 as no-penalty tolerance for natural
// fixation jitter. Needs the 478-landmark FaceLandmarker output (includes iris).
function computePupilOffset(face: FaceLandmarkerResult): number {
  const fl = face.faceLandmarks?.[0];
  if (!fl || fl.length < 478) return 0;
  // Anatomical naming below (subject's own right/left — independent of mirroring).
  // Subject's right eye:  outer 33, inner 133, top 159, bottom 145, iris center 468
  // Subject's left eye:   outer 263, inner 362, top 386, bottom 374, iris center 473
  const eyeOff = (
    o: { x: number; y: number },
    i: { x: number; y: number },
    t: { x: number; y: number },
    b: { x: number; y: number },
    iris: { x: number; y: number },
  ): number => {
    const cx = (o.x + i.x) / 2;
    const cy = (t.y + b.y) / 2;
    const hw = Math.abs(o.x - i.x) / 2;
    const hh = Math.abs(t.y - b.y) / 2;
    if (hw < 1e-4 || hh < 1e-4) return 0;
    const dx = (iris.x - cx) / hw;
    const dy = (iris.y - cy) / hh;
    return Math.hypot(dx, dy);
  };
  const r = (fl[33] && fl[133] && fl[159] && fl[145] && fl[468])
    ? eyeOff(fl[33], fl[133], fl[159], fl[145], fl[468])
    : 0;
  const l = (fl[263] && fl[362] && fl[386] && fl[374] && fl[473])
    ? eyeOff(fl[263], fl[362], fl[386], fl[374], fl[473])
    : 0;
  return (r + l) / 2;
}

// Combined gaze fixation: BOTH the head and the pupils must point at the camera.
// Multiplicative so any drift on either axis pulls the score down — captures the
// case where the head is forward but the eyes wander off (or vice versa), which
// the previous head-only implementation completely missed.
function computeGazeFixation(face: FaceLandmarkerResult): number {
  const matrix = face.facialTransformationMatrixes?.[0]?.data;
  if (!matrix || matrix.length !== 16) return 0;
  const m = new THREE.Matrix4().fromArray(matrix);
  const fwd = new THREE.Vector3();
  m.extractBasis(new THREE.Vector3(), new THREE.Vector3(), fwd);
  const cameraFwd = new THREE.Vector3(0, 0, 1);
  const headAngle = fwd.angleTo(cameraFwd);
  const headFix = Math.max(0, 1 - headAngle / GAZE_CONE_RAD);

  // Pupil contribution — small tolerance for natural micro-fixation; beyond that
  // the score falls linearly to 0 around an iris-edge offset.
  const pupilOff = computePupilOffset(face);
  const pupilFix = Math.max(0, 1 - Math.max(0, pupilOff - 0.05) / 0.35);

  return headFix * pupilFix;
}

function computeMouthOpen(face: FaceLandmarkerResult): number {
  const cats = face.faceBlendshapes?.[0]?.categories;
  if (!cats) return 0;
  const jaw = cats.find((c) => c.categoryName === 'jawOpen');
  return jaw?.score ?? 0;
}

// Returns true when a hand wrist landmark sits inside the face bounding box —
// proxy for "propping head on hand" (턱 괴기). Uses pose landmark 15/16 (wrist)
// and face landmark 152 (chin tip) + 10 (forehead) for the face bounds.
function computeChinOnHand(
  face: FaceLandmarkerResult,
  pose: PoseLandmarkerResult,
): boolean {
  const fl = face.faceLandmarks?.[0];
  const pl = pose.landmarks?.[0];
  if (!fl || !pl || fl.length < 200 || pl.length < 17) return false;
  // Face image-space bounds — landmark indices: 10=forehead top, 152=chin tip,
  // 234=left cheek, 454=right cheek (image-mirrored = subject's anatomical right).
  const top = fl[10];
  const bottom = fl[152];
  const left = fl[234];
  const right = fl[454];
  if (!top || !bottom || !left || !right) return false;
  const minX = Math.min(left.x, right.x);
  const maxX = Math.max(left.x, right.x);
  const minY = Math.min(top.y, bottom.y);
  const maxY = Math.max(top.y, bottom.y);
  // Expand bounds by 20% so hands resting on cheek/chin still count.
  const padX = (maxX - minX) * 0.2;
  const padY = (maxY - minY) * 0.2;
  const inBox = (x: number, y: number) =>
    x >= minX - padX && x <= maxX + padX && y >= minY - padY && y <= maxY + padY;
  // Pose-detected wrists (15=anatomical left, 16=anatomical right). Image-space x/y.
  const lw = pl[15];
  const rw = pl[16];
  return (lw && inBox(lw.x, lw.y)) || (rw && inBox(rw.x, rw.y)) || false;
}

function computePostureSway(pose: PoseLandmarkerResult, tSec: number): number {
  const lm = pose.landmarks?.[0];
  if (!lm || lm.length < 13) return 0;
  const ls = lm[11];
  const rs = lm[12];
  if (!ls || !rs) return 0;
  const midY = (ls.y + rs.y) / 2;
  shoulderBuf.push({ t: tSec, midY });
  // Drop samples older than window.
  while (shoulderBuf.length && shoulderBuf[0].t < tSec - SHOULDER_BUFFER_S) {
    shoulderBuf.shift();
  }
  if (shoulderBuf.length < 3) return 0;
  const mean = shoulderBuf.reduce((s, p) => s + p.midY, 0) / shoulderBuf.length;
  const variance =
    shoulderBuf.reduce((s, p) => s + (p.midY - mean) ** 2, 0) / shoulderBuf.length;
  return Math.sqrt(variance);
}

function computeShoulderTilt(pose: PoseLandmarkerResult): number {
  const lm = pose.landmarks?.[0];
  if (!lm) return 0;
  const ls = lm[11];
  const rs = lm[12];
  if (!ls || !rs) return 0;
  const dx = rs.x - ls.x;
  const dy = rs.y - ls.y;
  if (Math.abs(dx) < 0.01) return 0;
  return dy / Math.abs(dx);
}

function computeExpressionDiversity(face: FaceLandmarkerResult): number {
  // Entropy of active (>0.1) blendshape categories.
  const bs = face.faceBlendshapes?.[0]?.categories;
  if (!bs || bs.length === 0) return 0;
  const active = bs.filter((c) => c.score > 0.1);
  if (active.length === 0) return 0;
  const total = active.reduce((s, c) => s + c.score, 0);
  if (total <= 0) return 0;
  let entropy = 0;
  for (const c of active) {
    const p = c.score / total;
    if (p > 0) entropy -= p * Math.log2(p);
  }
  return entropy;
}

// Updates the rolling wrist-speed buffer + per-frame counters; returns nothing.
// Split into a single state-update call so both freq and max-speed signals share
// one source of truth (and one speed computation).
function updateHandSpeed(hand: HandLandmarkerResult, tSec: number): void {
  const lm = hand.landmarks?.[0];
  if (!lm || lm.length === 0) {
    lastHand = null;
    return;
  }
  const wrist = lm[0];
  handTotalFrames++;
  if (lastHand) {
    const dt = Math.max(1e-3, tSec - lastHand.t);
    const dx = wrist.x - lastHand.x;
    const dy = wrist.y - lastHand.y;
    const speed = Math.hypot(dx, dy) / dt;
    if (speed > HAND_VELOCITY_THRESHOLD) handMovingFrames++;
    handSpeedBuf.push({ t: tSec, speed });
    while (handSpeedBuf.length && handSpeedBuf[0].t < tSec - HAND_SPEED_WINDOW_S) {
      handSpeedBuf.shift();
    }
  }
  lastHand = { t: tSec, x: wrist.x, y: wrist.y };
}

function computeHandGestureFreq(): number {
  return handTotalFrames > 0 ? handMovingFrames / handTotalFrames : 0;
}

function computeHandVelocityMax(): number {
  let max = 0;
  for (const s of handSpeedBuf) if (s.speed > max) max = s.speed;
  return max;
}

// ── Domain-expansion signal helpers ──

function computeSmileIntensity(face: FaceLandmarkerResult): number {
  const cats = face.faceBlendshapes?.[0]?.categories;
  if (!cats) return 0;
  const l = cats.find((c) => c.categoryName === 'mouthSmileLeft')?.score ?? 0;
  const r = cats.find((c) => c.categoryName === 'mouthSmileRight')?.score ?? 0;
  return (l + r) / 2;
}

// Wrist inside the upper face region (cheek/nose/forehead — but NOT the chin/jaw
// area that chin_on_hand already covers). Catches "코 만지기 / 머리 쓰다듬기 /
// 이마 짚기" — classic anxiety/uncertainty signals.
function computeFaceTouchOther(
  face: FaceLandmarkerResult,
  pose: PoseLandmarkerResult,
): boolean {
  const fl = face.faceLandmarks?.[0];
  const pl = pose.landmarks?.[0];
  if (!fl || !pl || fl.length < 200 || pl.length < 17) return false;
  const top = fl[10], bottom = fl[152], left = fl[234], right = fl[454];
  if (!top || !bottom || !left || !right) return false;
  const minX = Math.min(left.x, right.x);
  const maxX = Math.max(left.x, right.x);
  const minY = Math.min(top.y, bottom.y);
  const maxY = Math.max(top.y, bottom.y);
  // Upper 65% of the face = cheek / nose / forehead. Lower 35% is the chin/jaw
  // region already handled by chin_on_hand. Split at y = minY + (maxY-minY)*0.65.
  const upperLimitY = minY + (maxY - minY) * 0.65;
  const padX = (maxX - minX) * 0.2;
  const inUpperFace = (x: number, y: number): boolean =>
    x >= minX - padX && x <= maxX + padX && y >= minY - 0.05 && y <= upperLimitY;
  const lw = pl[15], rw = pl[16];
  return Boolean((lw && inUpperFace(lw.x, lw.y)) || (rw && inUpperFace(rw.x, rw.y)));
}

// Hand-fidget: both wrists close together (e.g. clasped on lap, in front of body)
// AND small recent motion (twiddling thumbs / picking at nails). Composite 0..1.
function computeHandFidget(pose: PoseLandmarkerResult, tSec: number): number {
  const pl = pose.landmarks?.[0];
  if (!pl || pl.length < 17) return 0;
  const lw = pl[15], rw = pl[16];
  const ls = pl[11], rs = pl[12];
  if (!lw || !rw || !ls || !rs) return 0;
  // Track each wrist's speed in a small rolling buffer.
  let lspd = 0, rspd = 0;
  if (lastLeftWrist) {
    const dt = Math.max(1e-3, tSec - lastLeftWrist.t);
    lspd = Math.hypot(lw.x - lastLeftWrist.x, lw.y - lastLeftWrist.y) / dt;
  }
  if (lastRightWrist) {
    const dt = Math.max(1e-3, tSec - lastRightWrist.t);
    rspd = Math.hypot(rw.x - lastRightWrist.x, rw.y - lastRightWrist.y) / dt;
  }
  lastLeftWrist = { t: tSec, x: lw.x, y: lw.y };
  lastRightWrist = { t: tSec, x: rw.x, y: rw.y };
  fidgetSpeedBuf.push({ t: tSec, lspd, rspd });
  while (fidgetSpeedBuf.length && fidgetSpeedBuf[0].t < tSec - FIDGET_WINDOW_S) {
    fidgetSpeedBuf.shift();
  }

  // Are hands close together?
  const wristDist = Math.hypot(rw.x - lw.x, rw.y - lw.y);
  const shoulderWidth = Math.max(0.001, Math.abs(rs.x - ls.x));
  const proximity = wristDist / shoulderWidth; // <1 = close
  // Score for closeness: 1 at proximity≤0.4, 0 at proximity≥0.9
  const closeness = Math.max(0, Math.min(1, (0.9 - proximity) / 0.5));

  // Average recent motion of both wrists.
  let totalMotion = 0;
  for (const s of fidgetSpeedBuf) totalMotion += (s.lspd + s.rspd) / 2;
  const avgMotion = fidgetSpeedBuf.length ? totalMotion / fidgetSpeedBuf.length : 0;
  // Score for "small but non-zero motion" (fidget = movement without purpose).
  // Sweet spot ~0.02–0.15 image-units/sec. Outside is either still or big gesture.
  let motionScore = 0;
  if (avgMotion > 0.01 && avgMotion < 0.2) {
    motionScore = avgMotion < 0.1
      ? avgMotion / 0.1                  // ramp up
      : Math.max(0, (0.2 - avgMotion) / 0.1); // ramp down
  }
  return closeness * motionScore;
}

// Reversals/sec of the primary wrist's velocity over the last REVERSAL_WINDOW_S.
// High = 이중동작 (start, stop, restart) or hesitation. Uses HandLandmarker's
// landmark 0 (wrist) — same source as hand_gesture_freq for consistency.
function computeMotionReversalRate(hand: HandLandmarkerResult, tSec: number): number {
  const lm = hand.landmarks?.[0];
  if (!lm || lm.length === 0) {
    // No hand detected — decay reversal buffer naturally (drop old) but no new entries.
    while (reversalTimes.length && reversalTimes[0] < tSec - REVERSAL_WINDOW_S) {
      reversalTimes.shift();
    }
    return reversalTimes.length / REVERSAL_WINDOW_S;
  }
  const wrist = lm[0];
  if (lastHand) {
    const dt = Math.max(1e-3, tSec - lastHand.t);
    const vx = (wrist.x - lastHand.x) / dt;
    const vy = (wrist.y - lastHand.y) / dt;
    const magnitude = Math.hypot(vx, vy);
    // Only count reversals when motion is non-trivial (filter pure noise).
    if (lastWristVel && magnitude > 0.05) {
      const prevMag = Math.hypot(lastWristVel.vx, lastWristVel.vy);
      if (prevMag > 0.05) {
        const dot = vx * lastWristVel.vx + vy * lastWristVel.vy;
        // Direction reversal: cosine < -0.3 (>108°, clearly opposite-ish).
        const cos = dot / (magnitude * prevMag);
        if (cos < -0.3) reversalTimes.push(tSec);
      }
    }
    lastWristVel = { vx, vy };
  }
  // Drop old reversals outside the window.
  while (reversalTimes.length && reversalTimes[0] < tSec - REVERSAL_WINDOW_S) {
    reversalTimes.shift();
  }
  return reversalTimes.length / REVERSAL_WINDOW_S;
}

function computeGazeYawSway(yawDeg: number, tSec: number): number {
  yawBuf.push({ t: tSec, yaw: yawDeg });
  while (yawBuf.length && yawBuf[0].t < tSec - YAW_WINDOW_S) yawBuf.shift();
  if (yawBuf.length < 3) return 0;
  const mean = yawBuf.reduce((s, p) => s + p.yaw, 0) / yawBuf.length;
  const variance = yawBuf.reduce((s, p) => s + (p.yaw - mean) ** 2, 0) / yawBuf.length;
  return Math.sqrt(variance);
}

function computeExpressionChangeRate(face: FaceLandmarkerResult, tSec: number): number {
  const cats = face.faceBlendshapes?.[0]?.categories;
  if (!cats) {
    lastBs = null;
    return 0;
  }
  // Pull key blendshapes into a map for stable delta computation.
  const curr = new Map<string, number>();
  for (const key of EXPRESSION_KEYS) curr.set(key, 0);
  for (const c of cats) {
    if (curr.has(c.categoryName)) curr.set(c.categoryName, c.score);
  }
  if (lastBs && tSec > lastBsT) {
    let delta = 0;
    for (const key of EXPRESSION_KEYS) {
      delta += Math.abs((curr.get(key) ?? 0) - (lastBs.get(key) ?? 0));
    }
    expChangeBuf.push({ t: tSec, delta });
    while (expChangeBuf.length && expChangeBuf[0].t < tSec - EXP_CHANGE_WINDOW_S) {
      expChangeBuf.shift();
    }
  }
  lastBs = curr;
  lastBsT = tSec;
  if (!expChangeBuf.length) return 0;
  // Average delta per frame in window, scaled by sample rate to ~Δ per second.
  const sum = expChangeBuf.reduce((s, p) => s + p.delta, 0);
  return sum; // already "total change over the window"
}

export function computeVisionFrame(
  tSec: number,
  face: FaceLandmarkerResult,
  pose: PoseLandmarkerResult,
  hand: HandLandmarkerResult,
): VisionFrame {
  const headPose = computeHeadPose(face);
  updateHandSpeed(hand, tSec); // refresh wrist-speed buffer once per frame
  snapshotLandmarks(tSec, face, pose); // remember positions for the review UI
  const yawDeg = headPose?.yawDeg ?? 0;
  return {
    t: tSec,
    gaze_fixation_ratio: computeGazeFixation(face),
    posture_sway: computePostureSway(pose, tSec),
    shoulder_tilt: computeShoulderTilt(pose),
    expression_diversity: computeExpressionDiversity(face),
    hand_gesture_freq: computeHandGestureFreq(),
    hand_velocity_max: computeHandVelocityMax(),
    head_pitch_deg: headPose?.pitchDeg ?? 0,
    head_yaw_deg: yawDeg,
    head_roll_deg: headPose?.rollDeg ?? 0,
    chin_on_hand: computeChinOnHand(face, pose),
    mouth_open: computeMouthOpen(face),
    smile_intensity: computeSmileIntensity(face),
    face_touch_other: computeFaceTouchOther(face, pose),
    hand_fidget_score: computeHandFidget(pose, tSec),
    motion_reversal_rate: computeMotionReversalRate(hand, tSec),
    gaze_yaw_sway: computeGazeYawSway(yawDeg, tSec),
    expression_change_rate: computeExpressionChangeRate(face, tSec),
  };
}

export function resetSignalState(): void {
  shoulderBuf.length = 0;
  lastHand = null;
  handMovingFrames = 0;
  handTotalFrames = 0;
  handSpeedBuf.length = 0;
  yawBuf.length = 0;
  lastWristVel = null;
  reversalTimes.length = 0;
  lastLeftWrist = null;
  lastRightWrist = null;
  fidgetSpeedBuf.length = 0;
  lastBs = null;
  lastBsT = 0;
  expChangeBuf.length = 0;
  landmarkBuf.length = 0;
}


// ── Per-frame landmark snapshot buffer ──
//
// The review UI wants to draw a circle on the *actual body part* tied to each
// moment (e.g., "raised left arm at 12s" → circle on the left wrist in that
// frame, not a generic "gesture region" point). To do that without sending raw
// landmarks over the wire, we keep a compact per-frame snapshot of the few
// landmark positions the marker UI cares about, in image-normalized coords [0,1].
// getLandmarksAtTime(t) returns the closest snapshot for the review.

export interface Point2 { x: number; y: number }

export interface LandmarkSnapshot {
  t: number;
  // Face bbox + key points (image-normalized 0..1)
  face?: {
    bbox: { minX: number; minY: number; maxX: number; maxY: number };
    nose: Point2;
    mouth: Point2;
    leftEye: Point2;
    rightEye: Point2;
  };
  pose?: {
    head: Point2;
    leftShoulder: Point2;
    rightShoulder: Point2;
    leftElbow: Point2;
    rightElbow: Point2;
    leftWrist: Point2;
    rightWrist: Point2;
    leftHip: Point2;
    rightHip: Point2;
  };
}

const landmarkBuf: LandmarkSnapshot[] = [];

function snapshotLandmarks(
  tSec: number,
  face: FaceLandmarkerResult,
  pose: PoseLandmarkerResult,
): void {
  const snap: LandmarkSnapshot = { t: tSec };
  const fl = face.faceLandmarks?.[0];
  if (fl && fl.length >= 200) {
    const top = fl[10], bottom = fl[152], left = fl[234], right = fl[454];
    const nose = fl[1];
    const mouth = fl[13];
    const leftEye = fl[468] ?? fl[33];   // iris if 478-pt model, else corner
    const rightEye = fl[473] ?? fl[263];
    if (top && bottom && left && right && nose && mouth) {
      snap.face = {
        bbox: {
          minX: Math.min(left.x, right.x),
          minY: Math.min(top.y, bottom.y),
          maxX: Math.max(left.x, right.x),
          maxY: Math.max(top.y, bottom.y),
        },
        nose: { x: nose.x, y: nose.y },
        mouth: { x: mouth.x, y: mouth.y },
        leftEye: leftEye ? { x: leftEye.x, y: leftEye.y } : { x: nose.x, y: nose.y },
        rightEye: rightEye ? { x: rightEye.x, y: rightEye.y } : { x: nose.x, y: nose.y },
      };
    }
  }
  const pl = pose.landmarks?.[0];
  if (pl && pl.length >= 25) {
    const head = pl[0], ls = pl[11], rs = pl[12];
    const le = pl[13], re = pl[14];
    const lw = pl[15], rw = pl[16], lh = pl[23], rh = pl[24];
    if (head && ls && rs && le && re && lw && rw && lh && rh) {
      snap.pose = {
        head: { x: head.x, y: head.y },
        leftShoulder: { x: ls.x, y: ls.y },
        rightShoulder: { x: rs.x, y: rs.y },
        leftElbow: { x: le.x, y: le.y },
        rightElbow: { x: re.x, y: re.y },
        leftWrist: { x: lw.x, y: lw.y },
        rightWrist: { x: rw.x, y: rw.y },
        leftHip: { x: lh.x, y: lh.y },
        rightHip: { x: rh.x, y: rh.y },
      };
    }
  }
  landmarkBuf.push(snap);
}

/** Closest landmark snapshot to the requested time (within ±tolSec). null if
 *  no snapshot within tolerance. */
export function getLandmarksAtTime(tSec: number, tolSec = 1.5): LandmarkSnapshot | null {
  if (landmarkBuf.length === 0) return null;
  let best = landmarkBuf[0];
  let bestDiff = Math.abs(best.t - tSec);
  for (const snap of landmarkBuf) {
    const diff = Math.abs(snap.t - tSec);
    if (diff < bestDiff) {
      bestDiff = diff;
      best = snap;
    }
  }
  return bestDiff <= tolSec ? best : null;
}

export function getLandmarkBuffer(): readonly LandmarkSnapshot[] {
  return landmarkBuf;
}

export function replaceLandmarkBuffer(snapshots: LandmarkSnapshot[]): void {
  landmarkBuf.length = 0;
  landmarkBuf.push(...snapshots);
}
