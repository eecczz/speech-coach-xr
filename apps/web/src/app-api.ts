const ACTIVE_USER_KEY = 'speakup-active-user';

export interface SpeakupUser {
  id: string;
  email: string;
  display_name: string;
  created_at: string;
}

export interface SpeakupSession {
  id: string;
  user_id: string;
  title: string;
  scenario: string;
  situation: string;
  focus_goals: string[];
  source: string;
  status: string;
  last_report: unknown;
  created_at: string;
  updated_at: string;
}

export interface AgentMessage {
  id: string;
  session_id: string;
  role: 'agent' | 'user' | 'system';
  content: string;
  t: number | null;
  metadata: Record<string, unknown>;
  created_at: string;
}

async function apiJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    throw new Error(payload.detail || payload.error || `HTTP ${response.status}`);
  }
  return payload as T;
}

export function getActiveUser(): SpeakupUser | null {
  try {
    const raw = localStorage.getItem(ACTIVE_USER_KEY);
    return raw ? JSON.parse(raw) as SpeakupUser : null;
  } catch {
    return null;
  }
}

export function setActiveUser(user: SpeakupUser): void {
  localStorage.setItem(ACTIVE_USER_KEY, JSON.stringify(user));
}

export function clearActiveUser(): void {
  localStorage.removeItem(ACTIVE_USER_KEY);
}

export async function signupUser(email: string, password: string, displayName: string): Promise<SpeakupUser> {
  const result = await apiJson<{ user: SpeakupUser }>('/api/auth/signup', {
    method: 'POST',
    body: JSON.stringify({ email, password, display_name: displayName }),
  });
  setActiveUser(result.user);
  return result.user;
}

export async function loginUser(email: string, password: string): Promise<SpeakupUser> {
  const result = await apiJson<{ user: SpeakupUser }>('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify({ email, password }),
  });
  setActiveUser(result.user);
  return result.user;
}

export async function listRemoteSessions(userId: string): Promise<SpeakupSession[]> {
  const result = await apiJson<{ sessions: SpeakupSession[] }>(`/api/sessions?user_id=${encodeURIComponent(userId)}`);
  return result.sessions;
}

export async function saveRemoteSession(payload: {
  id?: string;
  user_id: string;
  title: string;
  scenario: string;
  situation: string;
  focus_goals: string[];
  source?: string;
  status?: string;
  last_report?: unknown;
}): Promise<SpeakupSession> {
  const result = await apiJson<{ session: SpeakupSession }>('/api/sessions', {
    method: 'POST',
    body: JSON.stringify(payload),
  });
  return result.session;
}

export async function listAgentMessages(sessionId: string): Promise<AgentMessage[]> {
  const result = await apiJson<{ messages: AgentMessage[] }>(`/api/sessions/${encodeURIComponent(sessionId)}/messages`);
  return result.messages;
}

export async function saveAgentMessage(payload: {
  session_id: string;
  role: AgentMessage['role'];
  content: string;
  t?: number | null;
  metadata?: Record<string, unknown>;
}): Promise<AgentMessage> {
  const result = await apiJson<{ message: AgentMessage }>(`/api/sessions/${encodeURIComponent(payload.session_id)}/messages`, {
    method: 'POST',
    body: JSON.stringify({
      role: payload.role,
      content: payload.content,
      t: payload.t ?? null,
      metadata: payload.metadata ?? {},
    }),
  });
  return result.message;
}
