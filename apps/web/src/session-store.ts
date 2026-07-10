import type { ComprehensiveReport } from './review/types';

const STORAGE_KEY = 'speakup-completed-sessions';
const PENDING_KEY = 'speakup-pending-analysis';
const DB_NAME = 'speakup-media';
const DB_VERSION = 1;
const STORE_NAME = 'pending-media';

export interface SessionMeta {
  sessionId: string;
  projectId?: string;
  project: string;
  goal: string[];
  type: string;
  situation?: string;
  source: 'live' | 'upload';
  createdAt: string;
}

export interface LiveCoachingEvent {
  id: string;
  t: number;
  duration_s: number;
  axis: string;
  key: string;
  level: 'info' | 'warn' | 'critical';
  title: string;
  message: string;
  impact: number;
  metric?: string;
  value?: number | string | boolean | null;
  window_seconds?: number;
  metadata?: Record<string, unknown>;
}

export interface CompletedSession extends SessionMeta {
  report: ComprehensiveReport;
  mediaId?: string;
  filename?: string;
  mimeType?: string;
  // Second recording: same MediaStream re-rendered through the VRM avatar.
  // Lets the report page swap the playback source to the avatar version.
  avatarMediaId?: string;
  avatarFilename?: string;
  avatarMimeType?: string;
  liveEvents?: LiveCoachingEvent[];
}

export interface PendingAnalysis extends SessionMeta {
  mediaId: string;
  filename: string;
  mimeType: string;
  avatarMediaId?: string;
  avatarFilename?: string;
  avatarMimeType?: string;
  scenario: string;
  liveEvents?: LiveCoachingEvent[];
}

interface StoredMedia {
  id: string;
  blob: Blob;
  filename: string;
  mimeType: string;
  createdAt: string;
}

function canUseDomStorage(): boolean {
  return typeof window !== 'undefined' && typeof localStorage !== 'undefined';
}

function parseJson<T>(raw: string | null, fallback: T): T {
  if (!raw) return fallback;
  try {
    return JSON.parse(raw) as T;
  } catch {
    return fallback;
  }
}

export function getCompletedSessions(): CompletedSession[] {
  if (!canUseDomStorage()) return [];
  const sessions = parseJson<CompletedSession[]>(localStorage.getItem(STORAGE_KEY), []);
  return sessions.sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}

export function getCompletedSession(sessionId: string): CompletedSession | null {
  return getCompletedSessions().find((session) => session.sessionId === sessionId) ?? null;
}

export function saveCompletedSession(session: CompletedSession): void {
  if (!canUseDomStorage()) return;
  const sessions = getCompletedSessions().filter((item) => item.sessionId !== session.sessionId);
  sessions.unshift(session);
  localStorage.setItem(STORAGE_KEY, JSON.stringify(sessions));
}

export function getPendingAnalysis(): PendingAnalysis | null {
  if (!canUseDomStorage()) return null;
  return parseJson<PendingAnalysis | null>(localStorage.getItem(PENDING_KEY), null);
}

export function setPendingAnalysis(payload: PendingAnalysis): void {
  if (!canUseDomStorage()) return;
  localStorage.setItem(PENDING_KEY, JSON.stringify(payload));
}

export function clearPendingAnalysis(): void {
  if (!canUseDomStorage()) return;
  localStorage.removeItem(PENDING_KEY);
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);
    request.onerror = () => reject(request.error ?? new Error('indexedDB open failed'));
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains(STORE_NAME)) {
        db.createObjectStore(STORE_NAME, { keyPath: 'id' });
      }
    };
    request.onsuccess = () => resolve(request.result);
  });
}

async function withStore<T>(
  mode: IDBTransactionMode,
  action: (store: IDBObjectStore, setResult: (value: T) => void, reject: (reason?: unknown) => void) => void,
): Promise<T> {
  const db = await openDb();
  return new Promise<T>((resolve, reject) => {
    const tx = db.transaction(STORE_NAME, mode);
    const store = tx.objectStore(STORE_NAME);
    let result: T;
    let settled = false;
    const fail = (reason?: unknown) => {
      if (settled) return;
      settled = true;
      db.close();
      reject(reason);
    };
    action(store, (value) => {
      result = value;
    }, fail);
    tx.oncomplete = () => {
      if (settled) return;
      settled = true;
      db.close();
      resolve(result);
    };
    tx.onerror = () => fail(tx.error ?? new Error('indexedDB transaction failed'));
    tx.onabort = () => fail(tx.error ?? new Error('indexedDB transaction aborted'));
  });
}

export async function savePendingMedia(blob: Blob, filename: string, mimeType: string): Promise<string> {
  const id = `media_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
  const payload: StoredMedia = {
    id,
    blob,
    filename,
    mimeType,
    createdAt: new Date().toISOString(),
  };
  await withStore<void>('readwrite', (store, resolve, reject) => {
    const request = store.put(payload);
    request.onsuccess = () => resolve();
    request.onerror = () => reject(request.error ?? new Error('media save failed'));
  });
  return id;
}

export async function loadPendingMedia(mediaId: string): Promise<StoredMedia | null> {
  return withStore<StoredMedia | null>('readonly', (store, resolve, reject) => {
    const request = store.get(mediaId);
    request.onsuccess = () => resolve((request.result as StoredMedia | undefined) ?? null);
    request.onerror = () => reject(request.error ?? new Error('media load failed'));
  });
}

export async function clearPendingMedia(mediaId: string): Promise<void> {
  await withStore<void>('readwrite', (store, resolve, reject) => {
    const request = store.delete(mediaId);
    request.onsuccess = () => resolve();
    request.onerror = () => reject(request.error ?? new Error('media delete failed'));
  });
}
