import * as THREE from 'three';

// A minimal but readable 1:1 interview room.
//
// Convention for the whole XR interview scene:
//   - WebXR reference space is 'local-floor', so y=0 is the physical floor.
//   - The user (headset) sits near the origin looking toward -Z.
//   - The interviewer sits across the desk at negative Z, facing +Z (toward the user).
// Keeping every actor on the -Z side of the origin means a seated user in a real
// room maps naturally onto the virtual desk in front of them.

export const ROOM = {
  width: 4.2, // X
  depth: 4.4, // Z
  height: 2.7, // Y
  deskZ: -0.82, // front edge of the desk relative to the user at origin
  deskTopY: 0.74,
  interviewerZ: -1.55, // where the interviewer's chair sits
} as const;

function makeWall(
  width: number,
  height: number,
  color: number,
): THREE.Mesh {
  const geo = new THREE.PlaneGeometry(width, height);
  const mat = new THREE.MeshStandardMaterial({
    color,
    roughness: 0.95,
    metalness: 0.0,
    side: THREE.FrontSide,
  });
  return new THREE.Mesh(geo, mat);
}

/** Build the static room shell (floor, walls, ceiling) and add it to the scene. */
export function buildRoomShell(scene: THREE.Scene): void {
  const room = new THREE.Group();
  room.name = 'room-shell';

  const halfW = ROOM.width / 2;
  const halfD = ROOM.depth / 2;
  // Center the room's Z so both the user (~0) and interviewer (~-1.55) sit inside it.
  const centerZ = -ROOM.depth / 2 + 0.9;

  // Floor
  const floor = new THREE.Mesh(
    new THREE.PlaneGeometry(ROOM.width, ROOM.depth),
    new THREE.MeshStandardMaterial({ color: 0x8a8f98, roughness: 1.0 }),
  );
  floor.rotation.x = -Math.PI / 2;
  floor.position.set(0, 0, centerZ);
  room.add(floor);

  // Ceiling
  const ceiling = new THREE.Mesh(
    new THREE.PlaneGeometry(ROOM.width, ROOM.depth),
    new THREE.MeshStandardMaterial({ color: 0xf3f4f6, roughness: 1.0 }),
  );
  ceiling.rotation.x = Math.PI / 2;
  ceiling.position.set(0, ROOM.height, centerZ);
  room.add(ceiling);

  const wallColor = 0xdfe3ea;

  // Back wall (behind the interviewer)
  const back = makeWall(ROOM.width, ROOM.height, wallColor);
  back.position.set(0, ROOM.height / 2, centerZ - halfD);
  room.add(back);

  // Front wall (behind the user) — faces -Z
  const front = makeWall(ROOM.width, ROOM.height, wallColor);
  front.rotation.y = Math.PI;
  front.position.set(0, ROOM.height / 2, centerZ + halfD);
  room.add(front);

  // Left wall — faces +X
  const left = makeWall(ROOM.depth, ROOM.height, wallColor);
  left.rotation.y = Math.PI / 2;
  left.position.set(-halfW, ROOM.height / 2, centerZ);
  room.add(left);

  // Right wall — faces -X
  const right = makeWall(ROOM.depth, ROOM.height, wallColor);
  right.rotation.y = -Math.PI / 2;
  right.position.set(halfW, ROOM.height / 2, centerZ);
  room.add(right);

  scene.add(room);
}

/**
 * Build the interview desk. The desk has a solid modesty panel on the user-facing
 * side so it also occludes the interviewer's lower body — this lets us present a
 * standing-rig VRM as "seated" in the Stage-1 prototype without a full seated pose.
 */
export function buildDesk(scene: THREE.Scene): void {
  const desk = new THREE.Group();
  desk.name = 'desk';

  const woodMat = new THREE.MeshStandardMaterial({ color: 0x6b4f3a, roughness: 0.6 });
  const panelMat = new THREE.MeshStandardMaterial({ color: 0x5a4230, roughness: 0.8 });

  const deskW = 1.6;
  const deskD = 0.7;
  const topThick = 0.04;

  // Desk sits between the user (0) and the interviewer (-1.55).
  const deskCenterZ = -1.05;

  const top = new THREE.Mesh(
    new THREE.BoxGeometry(deskW, topThick, deskD),
    woodMat,
  );
  top.position.set(0, ROOM.deskTopY, deskCenterZ);
  desk.add(top);

  // Modesty panel on the user-facing (front, +Z) side.
  const panel = new THREE.Mesh(
    new THREE.BoxGeometry(deskW, ROOM.deskTopY - 0.02, 0.03),
    panelMat,
  );
  panel.position.set(0, (ROOM.deskTopY - 0.02) / 2, deskCenterZ + deskD / 2);
  desk.add(panel);

  scene.add(desk);
}

/** A simple chair placeholder behind the desk for the interviewer. */
export function buildInterviewerChair(scene: THREE.Scene): void {
  const chair = new THREE.Group();
  chair.name = 'interviewer-chair';
  const mat = new THREE.MeshStandardMaterial({ color: 0x2f3640, roughness: 0.7 });

  const seat = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.06, 0.5), mat);
  seat.position.set(0, 0.48, ROOM.interviewerZ);
  const backrest = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.6, 0.06), mat);
  backrest.position.set(0, 0.78, ROOM.interviewerZ - 0.22);
  chair.add(seat, backrest);
  scene.add(chair);
}
