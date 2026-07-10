import {
  FilesetResolver,
  FaceLandmarker,
  PoseLandmarker,
  HandLandmarker,
  type FaceLandmarkerResult,
  type PoseLandmarkerResult,
  type HandLandmarkerResult,
} from '@mediapipe/tasks-vision';

const WASM_BASE = 'https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@0.10.35/wasm';
const FACE_MODEL =
  'https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/1/face_landmarker.task';
const POSE_MODEL =
  'https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_lite/float16/1/pose_landmarker_lite.task';
const HAND_MODEL =
  'https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task';

export interface Landmarkers {
  face: FaceLandmarker;
  pose: PoseLandmarker;
  hand: HandLandmarker;
}

export async function createLandmarkers(): Promise<Landmarkers> {
  const fileset = await FilesetResolver.forVisionTasks(WASM_BASE);
  const [face, pose, hand] = await Promise.all([
    FaceLandmarker.createFromOptions(fileset, {
      baseOptions: { modelAssetPath: FACE_MODEL, delegate: 'GPU' },
      outputFaceBlendshapes: true,
      outputFacialTransformationMatrixes: true,
      runningMode: 'VIDEO',
      numFaces: 1,
    }),
    PoseLandmarker.createFromOptions(fileset, {
      baseOptions: { modelAssetPath: POSE_MODEL, delegate: 'GPU' },
      runningMode: 'VIDEO',
      numPoses: 1,
    }),
    HandLandmarker.createFromOptions(fileset, {
      baseOptions: { modelAssetPath: HAND_MODEL, delegate: 'GPU' },
      runningMode: 'VIDEO',
      numHands: 2,
    }),
  ]);
  return { face, pose, hand };
}

export interface FrameResult {
  face: FaceLandmarkerResult;
  pose: PoseLandmarkerResult;
  hand: HandLandmarkerResult;
}

export function detect(landmarkers: Landmarkers, video: HTMLVideoElement, tMs: number): FrameResult {
  const face = landmarkers.face.detectForVideo(video, tMs);
  const pose = landmarkers.pose.detectForVideo(video, tMs);
  const hand = landmarkers.hand.detectForVideo(video, tMs);
  return { face, pose, hand };
}
