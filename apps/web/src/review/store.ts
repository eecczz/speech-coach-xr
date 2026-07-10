import type { LandmarkSnapshot } from '../signals/compute';
import type { ComprehensiveReport } from './types';

const DB_NAME = 'speakup-review-store';
const DB_VERSION = 1;
const STORE_NAME = 'sessions';

export interface StoredReviewSession {
  id: string;
  createdAt: string;
  report: ComprehensiveReport;
  videoBlob: Blob;
  videoName?: string;
  videoType?: string;
  project?: string;
  goal?: string;
  scenario?: string;
  source?: 'live' | 'upload';
  landmarks: LandmarkSnapshot[];
}

export type SaveReviewSessionInput = Omit<StoredReviewSession, 'id' | 'createdAt'>;

export async function saveReviewSession(input: SaveReviewSessionInput): Promise<string> {
  const db = await openDb();
  const id = `review_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
  const value: StoredReviewSession = {
    ...input,
    id,
    createdAt: new Date().toISOString(),
  };

  await requestToPromise(
    db
      .transaction(STORE_NAME, 'readwrite')
      .objectStore(STORE_NAME)
      .put(value),
  );
  db.close();
  return id;
}

export async function loadReviewSession(id: string): Promise<StoredReviewSession | null> {
  const db = await openDb();
  const result = await requestToPromise<StoredReviewSession | undefined>(
    db
      .transaction(STORE_NAME, 'readonly')
      .objectStore(STORE_NAME)
      .get(id),
  );
  db.close();
  return result ?? null;
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(STORE_NAME)) {
        db.createObjectStore(STORE_NAME, { keyPath: 'id' });
      }
    };
    req.onerror = () => reject(req.error ?? new Error('리포트 저장소를 열 수 없습니다'));
    req.onsuccess = () => resolve(req.result);
  });
}

function requestToPromise<T>(req: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    req.onerror = () => reject(req.error ?? new Error('리포트 저장 요청이 실패했습니다'));
    req.onsuccess = () => resolve(req.result);
  });
}
