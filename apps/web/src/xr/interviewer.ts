import * as THREE from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { VRMLoaderPlugin, VRMUtils, VRMHumanBoneName, type VRM } from '@pixiv/three-vrm';
import { ROOM } from './interview-room';

export interface Interviewer {
  vrm: VRM;
  /** Advance idle motion, blink, gaze, mouth, and nod. `headTarget` = where to look. */
  update: (deltaSec: number, headTarget: THREE.Vector3) => void;
  /** Toggle talking mouth animation while the interviewer's TTS is playing. */
  setSpeaking: (on: boolean) => void;
  /** Trigger a single acknowledging nod (e.g. after the candidate answers). */
  nod: () => void;
}

// The default VRM is authored as a standing rig with feet at y=0. We sink it so the
// head lands near seated eye height (~1.15 m) and the desk's modesty panel hides the
// legs — a reliable Stage-1 stand-in for a real seated pose (tracked as a later task).
const SEATED_SINK = 0.34;

const _blinkProxy = { t: 0, next: 2.5 };

/**
 * Pose the normalized humanoid arms from T-pose down to a natural seated rest:
 * upper arms down at the sides, elbows softly bent so the forearms angle inward
 * toward the lap/desk. Values are in radians and tuned for a VRM 1.0 rig.
 */
function applyRestingPose(vrm: VRM): void {
  const h = vrm.humanoid;
  if (!h) return;
  const set = (bone: VRMHumanBoneName, x = 0, y = 0, z = 0) => {
    const node = h.getNormalizedBoneNode(bone);
    if (node) node.rotation.set(x, y, z);
  };
  // Upper arms: rotate about Z to drop them from horizontal down to the sides.
  set(VRMHumanBoneName.LeftUpperArm, 0.05, 0, -1.3);
  set(VRMHumanBoneName.RightUpperArm, 0.05, 0, 1.3);
  // Elbows: small forward bend so the forearms angle toward the lap/desk.
  set(VRMHumanBoneName.LeftLowerArm, 0.2, 0, 0);
  set(VRMHumanBoneName.RightLowerArm, 0.2, 0, 0);
}

/** Load the interviewer VRM, seat it behind the desk facing the user, and return an updater. */
export async function loadInterviewer(scene: THREE.Scene, vrmUrl: string): Promise<Interviewer> {
  const loader = new GLTFLoader();
  loader.register((parser) => new VRMLoaderPlugin(parser));

  const gltf = await loader.loadAsync(vrmUrl);
  const vrm = gltf.userData.vrm as VRM;

  VRMUtils.removeUnnecessaryVertices(gltf.scene);
  VRMUtils.combineSkeletons(gltf.scene);
  // VRM 1.0 faces +Z by default; VRM 0.x faces -Z and needs this fix (no-op for 1.0).
  // We WANT the interviewer to face +Z (toward the user at the origin), so after
  // normalizing we leave the default orientation as-is.
  VRMUtils.rotateVRM0(vrm);

  vrm.scene.position.set(0, -SEATED_SINK, ROOM.interviewerZ);
  scene.add(vrm.scene);

  // Slight forward lean at the hips reads as an attentive, seated posture and further
  // sells the "seated" illusion above the desk line.
  const hips = vrm.humanoid?.getNormalizedBoneNode(VRMHumanBoneName.Hips);
  hips?.rotateX(0.12);

  // The VRM ships in a T-pose (the live app poses arms from MediaPipe; the interviewer
  // has no such input). Lower the arms to a calm, hands-near-lap resting posture.
  applyRestingPose(vrm);

  // Drive eye gaze toward a moving target (the user's head) via three-vrm's lookAt.
  const lookProxy = new THREE.Object3D();
  scene.add(lookProxy);
  if (vrm.lookAt) vrm.lookAt.target = lookProxy;

  const chest =
    vrm.humanoid?.getNormalizedBoneNode(VRMHumanBoneName.Chest) ??
    vrm.humanoid?.getNormalizedBoneNode(VRMHumanBoneName.Spine);
  const chestBaseX = chest?.rotation.x ?? 0;

  const neck = vrm.humanoid?.getNormalizedBoneNode(VRMHumanBoneName.Neck);
  const neckBaseX = neck?.rotation.x ?? 0;

  let clock = 0;
  let speaking = false;
  let nodT = -1; // <0 = not nodding; otherwise elapsed seconds into the nod

  function update(deltaSec: number, headTarget: THREE.Vector3): void {
    clock += deltaSec;

    // Gentle breathing: subtle chest pitch oscillation.
    if (chest) chest.rotation.x = chestBaseX + Math.sin(clock * 1.4) * 0.012;

    // Eye contact: point the lookAt proxy at the user's head.
    lookProxy.position.copy(headTarget);

    // Acknowledging nod (one ~0.6s dip of the neck).
    if (nodT >= 0) {
      nodT += deltaSec;
      const dip = Math.sin((nodT / 0.6) * Math.PI); // 0→1→0
      if (neck) neck.rotation.x = neckBaseX + dip * 0.18;
      if (nodT >= 0.6) {
        nodT = -1;
        if (neck) neck.rotation.x = neckBaseX;
      }
    }

    // Talking mouth: oscillate the 'aa' viseme while speaking.
    const mouth = speaking ? (Math.sin(clock * 14) * 0.5 + 0.5) * 0.7 : 0;
    vrm.expressionManager?.setValue('aa', mouth);

    // Periodic blink.
    _blinkProxy.t += deltaSec;
    let blink = 0;
    if (_blinkProxy.t > _blinkProxy.next) {
      const p = (_blinkProxy.t - _blinkProxy.next) / 0.12; // 120ms blink
      blink = p < 1 ? Math.sin(p * Math.PI) : 0;
      if (p >= 1) {
        _blinkProxy.t = 0;
        _blinkProxy.next = 2 + Math.random() * 3;
      }
    }
    vrm.expressionManager?.setValue('blink', blink);

    vrm.update(deltaSec);
  }

  return {
    vrm,
    update,
    setSpeaking: (on: boolean) => {
      speaking = on;
    },
    nod: () => {
      nodT = 0;
    },
  };
}
