import * as THREE from 'three';
import { type VRM, VRMHumanBoneName, VRMExpressionPresetName } from '@pixiv/three-vrm';
import type {
  FaceLandmarkerResult,
  PoseLandmarkerResult,
  HandLandmarkerResult,
} from '@mediapipe/tasks-vision';

const IDENTITY_Q = new THREE.Quaternion();

// Rest world directions and rotations captured at avatar load.
// We use the bone's actual rest direction to its child rather than assuming a canonical
// local axis, which avoids 180° flips and bone-convention pitfalls.
interface BoneRest {
  restWorldDir: THREE.Vector3;
  initialWorldQ: THREE.Quaternion;
}
const boneRest = new Map<VRMHumanBoneName, BoneRest>();

const ARM_CHAIN: Array<{ bone: VRMHumanBoneName; child: VRMHumanBoneName }> = [
  { bone: VRMHumanBoneName.LeftUpperArm, child: VRMHumanBoneName.LeftLowerArm },
  { bone: VRMHumanBoneName.LeftLowerArm, child: VRMHumanBoneName.LeftHand },
  { bone: VRMHumanBoneName.LeftHand, child: VRMHumanBoneName.LeftMiddleProximal },
  { bone: VRMHumanBoneName.RightUpperArm, child: VRMHumanBoneName.RightLowerArm },
  { bone: VRMHumanBoneName.RightLowerArm, child: VRMHumanBoneName.RightHand },
  { bone: VRMHumanBoneName.RightHand, child: VRMHumanBoneName.RightMiddleProximal },
];

export function initRigForVRM(vrm: VRM): void {
  if (!vrm.humanoid) return;
  boneRest.clear();
  vrm.scene.updateMatrixWorld(true);
  for (const { bone, child } of ARM_CHAIN) {
    const b = vrm.humanoid.getNormalizedBoneNode(bone);
    const c = vrm.humanoid.getNormalizedBoneNode(child);
    if (!b || !c) continue;
    const bWorld = b.getWorldPosition(new THREE.Vector3());
    const cWorld = c.getWorldPosition(new THREE.Vector3());
    const dir = cWorld.sub(bWorld).normalize();
    const q = b.getWorldQuaternion(new THREE.Quaternion());
    boneRest.set(bone, { restWorldDir: dir, initialWorldQ: q });
  }
  // Additional rest capture for the dual-axis basis IK below — per-bone offset that
  // absorbs the rig's bind-pose orientation so live anatomy lookups reproduce the
  // bind pose at rest and match the rig's twist convention automatically.
  captureArmRest(vrm);
}

// MediaPipe FaceLandmarker → ARKit blendshape category names.
// These match VRM 1.0 standard mouth shapes by phonetic mapping.
const BLENDSHAPE_TO_VRM: Array<[string, VRMExpressionPresetName]> = [
  ['jawOpen', VRMExpressionPresetName.Aa],
  ['mouthFunnel', VRMExpressionPresetName.Oh],
  ['mouthPucker', VRMExpressionPresetName.Ou],
  ['mouthSmileLeft', VRMExpressionPresetName.Happy],
  ['mouthSmileRight', VRMExpressionPresetName.Happy],
  ['eyeBlinkLeft', VRMExpressionPresetName.BlinkLeft],
  ['eyeBlinkRight', VRMExpressionPresetName.BlinkRight],
];

export function applyFaceToVRM(vrm: VRM, face: FaceLandmarkerResult): void {
  if (!vrm.expressionManager) return;
  const blendshapes = face.faceBlendshapes?.[0]?.categories;
  if (blendshapes && blendshapes.length > 0) {
    const byName = new Map(blendshapes.map((c) => [c.categoryName, c.score]));
    for (const [mpName, vrmName] of BLENDSHAPE_TO_VRM) {
      const v = byName.get(mpName);
      if (v !== undefined) vrm.expressionManager.setValue(vrmName, v);
    }
    vrm.expressionManager.update();
  }

  // Head rotation from facial transformation matrix (column-major 4x4).
  // MediaPipe gives the face's pose in the camera's frame (X right, Y up, Z toward camera
  // by spec — the docs call this "metric/world" coords).
  // The avatar canvas mirrors the user (camera preview has scaleX(-1)), so the user's
  // right is the avatar's right. We negate Y/Z to convert from MediaPipe's image-frame
  // facing direction to the avatar's local head bone frame (it faces +Z toward camera).
  const matrix = face.facialTransformationMatrixes?.[0]?.data;
  if (matrix && matrix.length === 16 && vrm.humanoid) {
    const m = new THREE.Matrix4().fromArray(matrix);
    const q = new THREE.Quaternion().setFromRotationMatrix(m);
    const head = vrm.humanoid.getNormalizedBoneNode(VRMHumanBoneName.Head);
    if (head) {
      // Yaw (Y) and pitch (X) match user directly; roll (Z) is mirrored to align with
      // the scaleX(-1) preview convention.
      head.quaternion.slerp(new THREE.Quaternion(q.x, q.y, -q.z, q.w), 0.5);
    }
  }
}

// Applies face rotation + jaw to a non-VRM primitive group (head=children[0], body=children[1]).
// Lets us verify MediaPipe tracking when no .vrm asset is provided.
export function applyFaceToFallback(group: THREE.Object3D, face: FaceLandmarkerResult): void {
  const head = group.children[0];
  if (!head) return;

  const matrix = face.facialTransformationMatrixes?.[0]?.data;
  if (matrix && matrix.length === 16) {
    const m = new THREE.Matrix4().fromArray(matrix);
    const q = new THREE.Quaternion().setFromRotationMatrix(m);
    head.quaternion.set(q.x, -q.y, -q.z, q.w);
  }

  const jaw = face.faceBlendshapes?.[0]?.categories.find((c) => c.categoryName === 'jawOpen')?.score ?? 0;
  head.scale.setScalar(1 + jaw * 0.4);
}

// "차렷" stance: arms straight down at sides. Tiny outward bias (~3°) only to avoid
// clipping the torso mesh; visually it reads as fully vertical.
const A_POSE_UPPER_TARGETS = new Map<VRMHumanBoneName, THREE.Vector3>([
  [VRMHumanBoneName.LeftUpperArm, new THREE.Vector3(0.05, -0.999, 0).normalize()],
  [VRMHumanBoneName.RightUpperArm, new THREE.Vector3(-0.05, -0.999, 0).normalize()],
]);

// Visibility threshold: MediaPipe assigns 0..1 confidence per landmark. Below this,
// the model is mostly hallucinating from upper-body context — skip retargeting that joint.
const VISIBILITY_MIN = 0.5;

// Single-axis aim: rotates a bone so its rest world direction lines up with
// `targetWorldDir`. Kept for the A-pose relax helpers only — arms use the dual-axis
// basis IK below at runtime, which controls twist properly and doesn't need this.
function setBoneWorldDirection(
  bone: THREE.Object3D,
  boneName: VRMHumanBoneName,
  targetWorldDir: THREE.Vector3,
  slerp = 0.35,
): void {
  if (!bone.parent) return;
  const rest = boneRest.get(boneName);
  if (!rest) return;
  const deltaWorldQ = new THREE.Quaternion().setFromUnitVectors(rest.restWorldDir, targetWorldDir);
  const newWorldQ = deltaWorldQ.multiply(rest.initialWorldQ.clone());
  const parentInvWorldQ = bone.parent.getWorldQuaternion(new THREE.Quaternion()).invert();
  const newLocalQ = parentInvWorldQ.multiply(newWorldQ);
  bone.quaternion.slerp(newLocalQ, slerp);
}

// Relax an upper arm toward A-pose at the shoulder.
function relaxUpperToAPose(bone: THREE.Object3D | null, boneName: VRMHumanBoneName, slerp = 0.08): void {
  if (!bone) return;
  const target = A_POSE_UPPER_TARGETS.get(boneName);
  if (!target) return;
  setBoneWorldDirection(bone, boneName, target, slerp);
}

// Relax a lower arm to identity local rotation — it continues straight from its parent
// (upper arm), no elbow bend.
function relaxLowerToStraight(bone: THREE.Object3D | null, slerp = 0.08): void {
  if (!bone) return;
  bone.quaternion.slerp(IDENTITY_Q, slerp);
}

export function applyPoseToVRM(vrm: VRM, pose: PoseLandmarkerResult): void {
  if (!vrm.humanoid) return;
  const lm = pose.landmarks?.[0]; // image-space, has reliable .visibility

  // --- Spine tilt from shoulder line (gentle, image-space y so unit-agnostic) ---
  if (lm) {
    const lsImg = lm[11];
    const rsImg = lm[12];
    const lsVis = lsImg?.visibility ?? 0;
    const rsVis = rsImg?.visibility ?? 0;
    if (lsImg && rsImg && lsVis >= VISIBILITY_MIN && rsVis >= VISIBILITY_MIN) {
      const dx = rsImg.x - lsImg.x;
      const dy = rsImg.y - lsImg.y;
      if (Math.abs(dx) >= 0.05) {
        const tilt = Math.atan2(dy, Math.abs(dx));
        const clamped = Math.max(-0.4, Math.min(0.4, tilt));
        const spine = vrm.humanoid.getNormalizedBoneNode(VRMHumanBoneName.Spine);
        if (spine) {
          spine.rotation.z = spine.rotation.z * 0.7 + -clamped * 0.3 * 0.3;
        }
      }
    }
  }

  // --- Arms TRACK: shoulder → upper arm → lower arm → hand ---
  // Driven by MediaPipe Pose. Each arm individually falls back to A-pose when
  // MediaPipe can't see it (low landmark visibility), so out-of-frame arms stay clean.
  retargetArms(vrm, pose);
}

function lockArmsToAPose(vrm: VRM, slerpAmt = 0.5): void {
  if (!vrm.humanoid) return;
  for (const upperName of [VRMHumanBoneName.LeftUpperArm, VRMHumanBoneName.RightUpperArm]) {
    const upper = vrm.humanoid.getNormalizedBoneNode(upperName);
    if (upper) relaxUpperToAPose(upper, upperName, slerpAmt);
  }
  for (const lowerName of [VRMHumanBoneName.LeftLowerArm, VRMHumanBoneName.RightLowerArm]) {
    const lower = vrm.humanoid.getNormalizedBoneNode(lowerName);
    if (lower) relaxLowerToStraight(lower, slerpAmt);
  }
  // Also relax hand bones to identity so wrist orientation doesn't drift.
  for (const handName of [VRMHumanBoneName.LeftHand, VRMHumanBoneName.RightHand]) {
    const hand = vrm.humanoid.getNormalizedBoneNode(handName);
    if (hand) hand.quaternion.slerp(IDENTITY_Q, slerpAmt);
  }
}

// ───────── Arm tracking: shoulder → upper arm → lower arm → hand ─────────
//
// Driven by MediaPipe Pose worldLandmarks (metric 3D, hip-centred). Each arm bone is
// aimed with the convention-independent setBoneWorldDirection, so the VRM's own bone
// axes are irrelevant — we only need correct world-space target directions, which
// removes the rotation-offset guesswork that plagued earlier attempts.

interface ArmSpec {
  upper: VRMHumanBoneName;
  lower: VRMHumanBoneName;
  hand: VRMHumanBoneName;
  middleProximal: VRMHumanBoneName; // hand's child — anchors hand rest forward when no index/little
  indexProximal: VRMHumanBoneName;  // index MCP — used with littleProximal to define palm "up"
  littleProximal: VRMHumanBoneName;
  // MediaPipe Pose landmark indices — anatomical naming, unaffected by mirroring.
  shoulder: number;
  elbow: number;
  wrist: number;
  index: number;
  pinky: number;
}

const ARMS: ArmSpec[] = [
  {
    upper: VRMHumanBoneName.LeftUpperArm,
    lower: VRMHumanBoneName.LeftLowerArm,
    hand: VRMHumanBoneName.LeftHand,
    middleProximal: VRMHumanBoneName.LeftMiddleProximal,
    indexProximal: VRMHumanBoneName.LeftIndexProximal,
    littleProximal: VRMHumanBoneName.LeftLittleProximal,
    shoulder: 11, elbow: 13, wrist: 15, index: 19, pinky: 17,
  },
  {
    upper: VRMHumanBoneName.RightUpperArm,
    lower: VRMHumanBoneName.RightLowerArm,
    hand: VRMHumanBoneName.RightHand,
    middleProximal: VRMHumanBoneName.RightMiddleProximal,
    indexProximal: VRMHumanBoneName.RightIndexProximal,
    littleProximal: VRMHumanBoneName.RightLittleProximal,
    shoulder: 12, elbow: 14, wrist: 16, index: 20, pinky: 18,
  },
];

// MediaPipe worldLandmarks frame → avatar world frame, as a direction (from → to).
//   MP:     +x image-right, +y down, +z away from camera.
//   Avatar: +x avatar-left,  +y up,  +z toward camera   (avatar faces +Z).
// The subject's anatomical-left maps to the avatar's anatomical-left (the preview is
// mirrored with scaleX(-1)), so x passes straight through while y and z flip.
// The mapping diag(1,-1,-1) has determinant +1 — a proper rotation, no chirality flip.
function mpDirToAvatar(
  world: ReadonlyArray<{ x: number; y: number; z: number }>,
  fromIdx: number,
  toIdx: number,
  out: THREE.Vector3,
): THREE.Vector3 {
  const a = world[fromIdx];
  const b = world[toIdx];
  return out.set(b.x - a.x, -(b.y - a.y), -(b.z - a.z)).normalize();
}

// Per-bone smoothed target direction + deadband. MediaPipe landmarks wobble a few
// degrees frame-to-frame even when the user holds perfectly still; without this the
// arm bones visibly twitch. The deadband freezes the direction when the change is
// below a noise threshold (so a held pose is rock-steady), and larger moves pass
// through an EMA for smooth tracking.
const dirSmoothed = new Map<VRMHumanBoneName, THREE.Vector3>();
const DIR_ALPHA = 0.4;

function smoothBoneDir(
  boneName: VRMHumanBoneName,
  raw: THREE.Vector3,
  deadbandRad: number,
): THREE.Vector3 {
  const prev = dirSmoothed.get(boneName);
  if (!prev) {
    const v = raw.clone();
    dirSmoothed.set(boneName, v);
    return v;
  }
  if (prev.angleTo(raw) < deadbandRad) return prev; // held still → freeze, no twitch
  prev.lerp(raw, DIR_ALPHA).normalize();
  return prev;
}

const UPPER_ARM_DEADBAND = 0.045; // ~2.6°
const LOWER_ARM_DEADBAND = 0.045; // ~2.6°
const HAND_DEADBAND = 0.09;       // ~5.2° — pose hand landmarks are noisier

// ───── Dual-axis basis IK (the actually-correct approach) ─────
//
// Single-axis aim (setFromUnitVectors) only fixes the bone's direction — it leaves
// the roll about that direction undetermined, which is why we kept seeing the 180°
// forearm flip even after the aim itself was correct. The fix isn't another patch
// — it's to build the bone's complete orientation from a *pair* of axes, exactly
// like a Unity TwoBoneIKConstraint or Maya IK joint with a pole vector:
//
//   1. lookRotation(forward, upHint) constructs a world quaternion whose local +Y
//      points along `forward` and local +Z points along `upHint` (orthogonalized).
//      VRM normalized humanoid bones use local +Y as their long axis, so this both
//      aims the bone AND pins its twist in one shot.
//   2. At rest, captureArmRest stores per-bone offset = restAnatomyQ⁻¹ * bindWorldQ.
//      The offset absorbs whatever discrepancy exists between our chosen anatomy
//      frame and the rig's actual bind frame (the rest twist that varies per VRM).
//   3. At runtime, desiredWorldQ = lookRotation(liveForward, liveUpHint) * offset.
//      Plug in rest values → you get the bind pose back exactly (no startup snap).
//      Plug in live values → twist tracks the user's actual anatomy.

function lookRotation(forward: THREE.Vector3, upHint: THREE.Vector3): THREE.Quaternion {
  const y = forward.clone().normalize();
  const z = upHint.clone().addScaledVector(y, -upHint.dot(y));
  if (z.lengthSq() < 1e-8) {
    // upHint nearly parallel to forward — pick a deterministic perpendicular so
    // crossing this threshold doesn't pop the orientation.
    z.copy(Math.abs(y.x) < 0.9 ? new THREE.Vector3(1, 0, 0) : new THREE.Vector3(0, 0, 1));
    z.addScaledVector(y, -z.dot(y));
  }
  z.normalize();
  const x = new THREE.Vector3().crossVectors(y, z).normalize();
  const m = new THREE.Matrix4().makeBasis(x, y, z);
  return new THREE.Quaternion().setFromRotationMatrix(m);
}

// Per-bone rest offset captured at avatar load. Empty until captureArmRest runs.
const armRest = new Map<VRMHumanBoneName, THREE.Quaternion>();

function captureArmRest(vrm: VRM): void {
  armRest.clear();
  if (!vrm.humanoid) return;
  for (const arm of ARMS) {
    const upper = vrm.humanoid.getNormalizedBoneNode(arm.upper);
    const lower = vrm.humanoid.getNormalizedBoneNode(arm.lower);
    const hand = vrm.humanoid.getNormalizedBoneNode(arm.hand);
    const middle = vrm.humanoid.getNormalizedBoneNode(arm.middleProximal);
    if (!upper || !lower || !hand) continue;

    const sPos = upper.getWorldPosition(new THREE.Vector3());
    const ePos = lower.getWorldPosition(new THREE.Vector3());
    const wPos = hand.getWorldPosition(new THREE.Vector3());

    // Upper arm rest anatomy: forward = shoulder→elbow, up = shoulder→wrist.
    const uAnatomy = lookRotation(ePos.clone().sub(sPos), wPos.clone().sub(sPos));
    const uInit = upper.getWorldQuaternion(new THREE.Quaternion());
    armRest.set(arm.upper, uAnatomy.invert().multiply(uInit));

    // Lower arm rest anatomy: forward = elbow→wrist, up = elbow→shoulder (anti-parallel
    // to the upper arm's forward — keeps both bones referencing the same bend plane).
    const lAnatomy = lookRotation(wPos.clone().sub(ePos), sPos.clone().sub(ePos));
    const lInit = lower.getWorldQuaternion(new THREE.Quaternion());
    armRest.set(arm.lower, lAnatomy.invert().multiply(lInit));

    // Hand rest anatomy: forward = wrist→midpoint(index_MCP, little_MCP), up = little→index
    // (across the palm). The "up" rotates with wrist supination/pronation, which is what
    // lets the runtime version detect the user flipping their palm vs. back of hand.
    // The forearm-direction up that we used before is parallel to forward when the hand
    // continues straight from the arm — degenerate basis, no twist signal.
    const indexProx = vrm.humanoid.getNormalizedBoneNode(arm.indexProximal);
    const littleProx = vrm.humanoid.getNormalizedBoneNode(arm.littleProximal);
    let hF: THREE.Vector3;
    let hU: THREE.Vector3;
    if (indexProx && littleProx) {
      const iPos = indexProx.getWorldPosition(new THREE.Vector3());
      const lPos = littleProx.getWorldPosition(new THREE.Vector3());
      hF = iPos.clone().add(lPos).multiplyScalar(0.5).sub(wPos);
      hU = iPos.clone().sub(lPos); // little → index
    } else if (middle) {
      // Fallback if the rig is missing index/little — use middle finger + forearm.
      hF = middle.getWorldPosition(new THREE.Vector3()).sub(wPos);
      hU = wPos.clone().sub(ePos);
    } else {
      continue; // can't define hand basis at all
    }
    const hAnatomy = lookRotation(hF, hU);
    const hInit = hand.getWorldQuaternion(new THREE.Quaternion());
    armRest.set(arm.hand, hAnatomy.invert().multiply(hInit));
  }

  // Snap arms to A-pose once at load. After this, retargetArms uses a hold-last
  // policy on missing pose data — never falls back to A-pose mid-session — so the
  // avatar can't suddenly "drop" to arms-down while the user is holding a pose.
  lockArmsToAPose(vrm, 1.0);
}

// Set bone.quaternion (local) so the bone reaches `desiredWorldQ` in world space.
// Uses the parent's *fresh* world quaternion, so the chain composes correctly when
// callers process bones in parent→child order within the same frame (the upper-arm
// quaternion set above is already visible to the lower arm's parentInv lookup).
function applyWorldQuaternion(
  bone: THREE.Object3D,
  desiredWorldQ: THREE.Quaternion,
  slerpAmt: number,
): void {
  if (!bone.parent) return;
  const parentInv = bone.parent.getWorldQuaternion(new THREE.Quaternion()).invert();
  const desiredLocal = parentInv.multiply(desiredWorldQ);
  bone.quaternion.slerp(desiredLocal, slerpAmt);
}

// Visibility hysteresis — stricter to START tracking (don't chase low-confidence
// noise), looser to STOP (so a single low-confidence frame doesn't snap the arm
// back to a default pose). This is the direct fix for the "arms randomly drop to
// A-pose while holding a static pose" regression: every brief visibility dip used
// to fire the relax-to-A-pose branch even though VISIBILITY_MIN=0.5 is right at
// the noise threshold for MediaPipe Pose Lite.
const TRACK_ENTER_VIS = 0.5;
const TRACK_EXIT_VIS = 0.2;
const chainTracking = new Map<VRMHumanBoneName, boolean>(); // keyed by arm.upper
const handTracking = new Map<VRMHumanBoneName, boolean>();  // keyed by arm.hand

function trackingHysteresis(
  state: Map<VRMHumanBoneName, boolean>,
  key: VRMHumanBoneName,
  minVis: number,
): boolean {
  const wasOn = state.get(key) ?? false;
  const threshold = wasOn ? TRACK_EXIT_VIS : TRACK_ENTER_VIS;
  const on = minVis >= threshold;
  state.set(key, on);
  return on;
}

function retargetArms(vrm: VRM, pose: PoseLandmarkerResult): void {
  if (!vrm.humanoid) return;
  const img = pose.landmarks?.[0];
  const world = pose.worldLandmarks?.[0];
  // No detection this frame — HOLD LAST orientation. We never relax to A-pose
  // mid-session; the initial snap happens once in captureArmRest.
  if (!img || !world) return;

  const vis = (i: number) => img[i]?.visibility ?? 0;
  const ta = new THREE.Vector3();
  const tb = new THREE.Vector3();

  for (const arm of ARMS) {
    const upper = vrm.humanoid.getNormalizedBoneNode(arm.upper);
    const lower = vrm.humanoid.getNormalizedBoneNode(arm.lower);
    const hand = vrm.humanoid.getNormalizedBoneNode(arm.hand);

    // Upper + lower share the shoulder/elbow/wrist chain — gate them together so the
    // two bones stay in a consistent state (don't end up with upper tracking and
    // lower frozen at a half-stale orientation, which would look broken at the elbow).
    const chainMinVis = Math.min(vis(arm.shoulder), vis(arm.elbow), vis(arm.wrist));
    if (trackingHysteresis(chainTracking, arm.upper, chainMinVis)) {
      // Upper arm: forward = shoulder→elbow, up hint = shoulder→wrist. The
      // perpendicular of (shoulder→wrist) vs (shoulder→elbow) is the elbow bend
      // direction — the same information a Unity TwoBoneIK pole supplies.
      const upperOffset = armRest.get(arm.upper);
      if (upper && upperOffset) {
        const fwdRaw = mpDirToAvatar(world, arm.shoulder, arm.elbow, ta);
        const fwd = smoothBoneDir(arm.upper, fwdRaw, UPPER_ARM_DEADBAND);
        const upHint = mpDirToAvatar(world, arm.shoulder, arm.wrist, tb);
        const desired = lookRotation(fwd, upHint).multiply(upperOffset);
        applyWorldQuaternion(upper, desired, 0.35);
      }

      // Lower arm: forward = elbow→wrist, up hint = elbow→shoulder. Shares the
      // bend plane with the upper arm so twist is continuous across the elbow.
      const lowerOffset = armRest.get(arm.lower);
      if (lower && lowerOffset) {
        const fwdRaw = mpDirToAvatar(world, arm.elbow, arm.wrist, ta);
        const fwd = smoothBoneDir(arm.lower, fwdRaw, LOWER_ARM_DEADBAND);
        const upHint = mpDirToAvatar(world, arm.elbow, arm.shoulder, tb);
        const desired = lookRotation(fwd, upHint).multiply(lowerOffset);
        applyWorldQuaternion(lower, desired, 0.35);
      }
    }
    // else: hold last upper/lower orientation — no snap.

    // Hand has its own hysteresis (needs wrist + index + pinky visible).
    const handMinVis = Math.min(vis(arm.wrist), vis(arm.index), vis(arm.pinky));
    if (trackingHysteresis(handTracking, arm.hand, handMinVis)) {
      const handOffset = armRest.get(arm.hand);
      if (hand && handOffset) {
        const w = world[arm.wrist];
        const i = world[arm.index];
        const p = world[arm.pinky];
        const fwdRaw = ta.set(
          (i.x + p.x) / 2 - w.x,
          -((i.y + p.y) / 2 - w.y),
          -((i.z + p.z) / 2 - w.z),
        ).normalize();
        const fwd = smoothBoneDir(arm.hand, fwdRaw, HAND_DEADBAND);
        const upHint = tb.set(i.x - p.x, -(i.y - p.y), -(i.z - p.z));
        const desired = lookRotation(fwd, upHint).multiply(handOffset);
        applyWorldQuaternion(hand, desired, 0.3);
      }
    }
    // else: hold last hand orientation.
  }
}

// === Fingers ===
//
// MediaPipe HandLandmarker outputs 21 landmarks per hand. We compute joint flex angles
// (angle between successive segments) and apply them to VRM finger bones as rotation
// around the bone's bending axis.
//
// Landmark indices per finger:
//   thumb:  1 (CMC) → 2 (MCP) → 3 (IP) → 4 (TIP)
//   index:  5 (MCP) → 6 (PIP) → 7 (DIP) → 8 (TIP)
//   middle: 9 → 10 → 11 → 12
//   ring:   13 → 14 → 15 → 16
//   little: 17 → 18 → 19 → 20
// VRM 1.0 finger bone naming uses {Side}{Finger}{Joint} where Joint is one of
// Metacarpal|Proximal|Distal for thumb, Proximal|Intermediate|Distal for others.

interface FingerSpec {
  // Landmark indices: a → b → c → d  (a is "base / palm-ward", d is fingertip)
  lm: [number, number, number, number];
  // VRM bone names for the 3 joints, in order (rotation at a, b, c).
  // For thumb: Metacarpal, Proximal, Distal.
  // For others: Proximal, Intermediate, Distal.
  leftBones: [VRMHumanBoneName, VRMHumanBoneName, VRMHumanBoneName];
  rightBones: [VRMHumanBoneName, VRMHumanBoneName, VRMHumanBoneName];
  // Local axis the joint bends about. Non-thumb fingers curl about local +Z. The
  // thumb's local axes are rotated ~90° relative to other fingers (it lies oblique
  // to the palm), so rotating about +Z just twists it along its length — the curl
  // axis is local +X instead.
  bendAxis: THREE.Vector3;
}

// Shared axis constants — reused by every spec so we don't allocate per frame.
const NON_THUMB_BEND_AXIS = new THREE.Vector3(0, 0, 1);
const THUMB_BEND_AXIS = new THREE.Vector3(1, 0, 0);

const FINGERS: FingerSpec[] = [
  {
    lm: [1, 2, 3, 4],
    leftBones: [
      VRMHumanBoneName.LeftThumbMetacarpal,
      VRMHumanBoneName.LeftThumbProximal,
      VRMHumanBoneName.LeftThumbDistal,
    ],
    rightBones: [
      VRMHumanBoneName.RightThumbMetacarpal,
      VRMHumanBoneName.RightThumbProximal,
      VRMHumanBoneName.RightThumbDistal,
    ],
    bendAxis: THUMB_BEND_AXIS,
  },
  {
    lm: [5, 6, 7, 8],
    leftBones: [
      VRMHumanBoneName.LeftIndexProximal,
      VRMHumanBoneName.LeftIndexIntermediate,
      VRMHumanBoneName.LeftIndexDistal,
    ],
    rightBones: [
      VRMHumanBoneName.RightIndexProximal,
      VRMHumanBoneName.RightIndexIntermediate,
      VRMHumanBoneName.RightIndexDistal,
    ],
    bendAxis: NON_THUMB_BEND_AXIS,
  },
  {
    lm: [9, 10, 11, 12],
    leftBones: [
      VRMHumanBoneName.LeftMiddleProximal,
      VRMHumanBoneName.LeftMiddleIntermediate,
      VRMHumanBoneName.LeftMiddleDistal,
    ],
    rightBones: [
      VRMHumanBoneName.RightMiddleProximal,
      VRMHumanBoneName.RightMiddleIntermediate,
      VRMHumanBoneName.RightMiddleDistal,
    ],
    bendAxis: NON_THUMB_BEND_AXIS,
  },
  {
    lm: [13, 14, 15, 16],
    leftBones: [
      VRMHumanBoneName.LeftRingProximal,
      VRMHumanBoneName.LeftRingIntermediate,
      VRMHumanBoneName.LeftRingDistal,
    ],
    rightBones: [
      VRMHumanBoneName.RightRingProximal,
      VRMHumanBoneName.RightRingIntermediate,
      VRMHumanBoneName.RightRingDistal,
    ],
    bendAxis: NON_THUMB_BEND_AXIS,
  },
  {
    lm: [17, 18, 19, 20],
    leftBones: [
      VRMHumanBoneName.LeftLittleProximal,
      VRMHumanBoneName.LeftLittleIntermediate,
      VRMHumanBoneName.LeftLittleDistal,
    ],
    rightBones: [
      VRMHumanBoneName.RightLittleProximal,
      VRMHumanBoneName.RightLittleIntermediate,
      VRMHumanBoneName.RightLittleDistal,
    ],
    bendAxis: NON_THUMB_BEND_AXIS,
  },
];

// Finger curl smoothing — fingers jitter a lot from per-frame noise.
const fingerAngleSmoothed = new Map<VRMHumanBoneName, number>();
const FINGER_SMOOTH_ALPHA = 0.4;
// Deadband: below this angular change the hand is effectively held still, so freeze
// the joint instead of chasing noise — same anti-twitch fix as the arm bones.
const FINGER_DEADBAND = 0.06; // ~3.4°

function smoothAngle(boneName: VRMHumanBoneName, target: number): number {
  const prev = fingerAngleSmoothed.get(boneName);
  if (prev === undefined) {
    fingerAngleSmoothed.set(boneName, target);
    return target;
  }
  if (Math.abs(target - prev) < FINGER_DEADBAND) return prev; // held still → freeze
  const next = prev * (1 - FINGER_SMOOTH_ALPHA) + target * FINGER_SMOOTH_ALPHA;
  fingerAngleSmoothed.set(boneName, next);
  return next;
}

export function applyHandsToVRM(vrm: VRM, handResult: HandLandmarkerResult): void {
  if (!vrm.humanoid) return;
  const lists = handResult.landmarks ?? [];
  const handednesses = handResult.handednesses ?? [];

  for (let h = 0; h < lists.length; h++) {
    const lm = lists[h];
    if (!lm || lm.length < 21) continue;

    // MediaPipe HandLandmarker handedness assumes a mirrored selfie input.
    // Our MediaStream is NOT mirrored, so MP's "Left" is actually the subject's
    // anatomical RIGHT. We swap accordingly.
    const handedness = handednesses[h]?.[0]?.categoryName;
    const side: 'left' | 'right' = handedness === 'Left' ? 'right' : 'left';

    applyFingersForHand(vrm, lm, side);
  }
}

function applyFingersForHand(
  vrm: VRM,
  lm: Array<{ x: number; y: number; z: number }>,
  side: 'left' | 'right',
): void {
  if (!vrm.humanoid) return;

  // Curl direction sign — empirical: most VRM normalized finger bones bend with a
  // negative rotation.z for the left hand and positive for the right. Adjust if a
  // specific VRM model bends backwards.
  const curlSign = side === 'left' ? -1 : 1;

  for (const f of FINGERS) {
    const [a, b, c, d] = f.lm;
    const bones = side === 'left' ? f.leftBones : f.rightBones;

    // 3D vectors in MediaPipe hand-local frame.
    const va = new THREE.Vector3(lm[a].x, lm[a].y, lm[a].z);
    const vb = new THREE.Vector3(lm[b].x, lm[b].y, lm[b].z);
    const vc = new THREE.Vector3(lm[c].x, lm[c].y, lm[c].z);
    const vd = new THREE.Vector3(lm[d].x, lm[d].y, lm[d].z);

    // Joint flex = angle between successive bone segments.
    // angleA: at joint a — between (palm baseline) and (a→b). We approximate the palm
    //   baseline as (a−wrist=lm[0]) direction; landmark 0 is wrist.
    const wrist = new THREE.Vector3(lm[0].x, lm[0].y, lm[0].z);
    const palmBaseline = va.clone().sub(wrist);
    const segAB = vb.clone().sub(va);
    const segBC = vc.clone().sub(vb);
    const segCD = vd.clone().sub(vc);

    if (palmBaseline.lengthSq() < 1e-6 || segAB.lengthSq() < 1e-6) continue;
    if (segBC.lengthSq() < 1e-6 || segCD.lengthSq() < 1e-6) continue;

    const angleA = palmBaseline.angleTo(segAB); // rotation at proximal-most joint
    const angleB = segAB.angleTo(segBC);        // middle joint
    const angleC = segBC.angleTo(segCD);        // distal joint

    applyFingerJoint(vrm, bones[0], curlSign * angleA, f.bendAxis);
    applyFingerJoint(vrm, bones[1], curlSign * angleB, f.bendAxis);
    applyFingerJoint(vrm, bones[2], curlSign * angleC, f.bendAxis);
  }
}

function applyFingerJoint(
  vrm: VRM,
  boneName: VRMHumanBoneName,
  rawAngle: number,
  axis: THREE.Vector3,
): void {
  if (!vrm.humanoid) return;
  const bone = vrm.humanoid.getNormalizedBoneNode(boneName);
  if (!bone) return;

  // Clamp to physically reasonable curl (~ -100° .. +100°).
  const clamped = Math.max(-1.75, Math.min(1.75, rawAngle));
  const smoothed = smoothAngle(boneName, clamped);

  // setFromAxisAngle gives us a clean rotation about the spec's local axis — no
  // Euler-order assumptions, and works whether the axis is Z (other fingers) or X
  // (thumb). Replaces the bone's local quaternion entirely, which is correct for
  // normalized VRM finger bones (their rest local rotation is identity).
  bone.quaternion.setFromAxisAngle(axis, smoothed);
}
