import { getCompletedSessions, type CompletedSession } from './session-store';

const PROJECTS_KEY = 'speakup-projects';
const CURRENT_PROJECT_KEY = 'speakup-current-project-id';

export interface SpeakUpProject {
  id: string;
  name: string;
  createdAt: string;
  updatedAt: string;
  sessions?: string[];
}

const DEFAULT_PROJECTS: SpeakUpProject[] = [
  {
    id: 'demo_startup_pitch',
    name: '창업 경진대회 발표',
    createdAt: '2026-05-30T09:00:00.000Z',
    updatedAt: '2026-05-30T09:00:00.000Z',
    sessions: [],
  },
  {
    id: 'demo_grad_interview',
    name: '대학원 면접 준비',
    createdAt: '2026-05-29T09:00:00.000Z',
    updatedAt: '2026-05-29T09:00:00.000Z',
    sessions: [],
  },
];

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

function persistProjects(projects: SpeakUpProject[]): void {
  if (!canUseDomStorage()) return;
  localStorage.setItem(PROJECTS_KEY, JSON.stringify(projects));
}

function normalizeName(name: string): string {
  return name.trim().replace(/\s+/g, ' ');
}

function createProjectId(): string {
  return `proj_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
}

function hydrateLegacyProjects(): SpeakUpProject[] {
  const sessions = getCompletedSessions();
  if (sessions.length === 0) return [];

  const byName = new Map<string, CompletedSession[]>();
  sessions.forEach((session) => {
    const key = normalizeName(session.project || '지난 연습');
    const rows = byName.get(key) ?? [];
    rows.push(session);
    byName.set(key, rows);
  });

  const projects = [...byName.entries()].map(([name, rows], index) => {
    const createdAt = rows.reduce((earliest, row) => (row.createdAt < earliest ? row.createdAt : earliest), rows[0].createdAt);
    const updatedAt = rows.reduce((latest, row) => (row.createdAt > latest ? row.createdAt : latest), rows[0].createdAt);
    return {
      id: `legacy_${index}_${Math.random().toString(36).slice(2, 6)}`,
      name,
      createdAt,
      updatedAt,
    } satisfies SpeakUpProject;
  });

  persistProjects(projects);
  return projects;
}

function hydrateDefaultProjects(): SpeakUpProject[] {
  const projects = DEFAULT_PROJECTS.map((project) => ({ ...project, sessions: [...(project.sessions ?? [])] }));
  persistProjects(projects);
  if (canUseDomStorage() && !localStorage.getItem(CURRENT_PROJECT_KEY) && projects[0]) {
    localStorage.setItem(CURRENT_PROJECT_KEY, projects[0].id);
  }
  return projects;
}

export function getProjects(): SpeakUpProject[] {
  if (!canUseDomStorage()) return [];
  const stored = parseJson<SpeakUpProject[]>(localStorage.getItem(PROJECTS_KEY), []);
  let projects = stored;
  if (projects.length === 0) {
    projects = hydrateLegacyProjects();
  }
  if (projects.length === 0) {
    projects = hydrateDefaultProjects();
  }
  return projects.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
}

export function getProjectById(projectId: string | null | undefined): SpeakUpProject | null {
  if (!projectId) return null;
  return getProjects().find((project) => project.id === projectId) ?? null;
}

export function getCurrentProjectId(): string | null {
  if (!canUseDomStorage()) return null;
  const current = localStorage.getItem(CURRENT_PROJECT_KEY);
  if (current && getProjectById(current)) return current;
  const first = getProjects()[0];
  if (!first) return null;
  localStorage.setItem(CURRENT_PROJECT_KEY, first.id);
  return first.id;
}

export function getCurrentProject(): SpeakUpProject | null {
  return getProjectById(getCurrentProjectId());
}

export function setCurrentProjectId(projectId: string): void {
  if (!canUseDomStorage()) return;
  localStorage.setItem(CURRENT_PROJECT_KEY, projectId);
}

export function createProject(name: string): SpeakUpProject {
  const normalized = normalizeName(name) || '새 프로젝트';
  const existing = getProjects().find((project) => project.name.toLowerCase() === normalized.toLowerCase());
  if (existing) {
    setCurrentProjectId(existing.id);
    return existing;
  }

  const now = new Date().toISOString();
  const project: SpeakUpProject = {
    id: createProjectId(),
    name: normalized,
    createdAt: now,
    updatedAt: now,
  };
  const nextProjects = [project, ...getProjects()];
  persistProjects(nextProjects);
  setCurrentProjectId(project.id);
  return project;
}

export function touchProject(projectId: string | null | undefined): void {
  if (!projectId) return;
  const projects = getProjects();
  const nextProjects = projects.map((project) =>
    project.id === projectId ? { ...project, updatedAt: new Date().toISOString() } : project,
  );
  persistProjects(nextProjects);
}

export function deleteProject(projectId: string): boolean {
  if (!canUseDomStorage()) return false;
  const projects = getProjects();
  const nextProjects = projects.filter((p) => p.id !== projectId);
  persistProjects(nextProjects);

  const current = localStorage.getItem(CURRENT_PROJECT_KEY);
  if (current === projectId) {
    if (nextProjects.length > 0) {
      localStorage.setItem(CURRENT_PROJECT_KEY, nextProjects[0].id);
      localStorage.setItem('speakup-project-name', nextProjects[0].name);
    } else {
      localStorage.removeItem(CURRENT_PROJECT_KEY);
      localStorage.removeItem('speakup-project-name');
    }
  }

  // return true if no projects remain
  return nextProjects.length === 0;
}

export function getSessionsForProject(projectId: string, projectName?: string): CompletedSession[] {
  const normalizedName = normalizeName(projectName || '');
  return getCompletedSessions().filter((session) => {
    if (session.projectId && session.projectId === projectId) return true;
    if (!session.projectId && normalizedName) {
      return normalizeName(session.project) === normalizedName;
    }
    return false;
  });
}
