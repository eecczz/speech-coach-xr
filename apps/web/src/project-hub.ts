import { createProject, getCurrentProject, getProjects, setCurrentProjectId, deleteProject } from './project-store';

const themeToggle = document.querySelector('[data-theme-toggle]') as HTMLButtonElement | null;
const createToggle = document.getElementById('project-create-toggle') as HTMLButtonElement | null;
const projectShell = document.querySelector('.project-shell') as HTMLDivElement | null;
const sidebarHead = document.querySelector('.project-sidebar-head') as HTMLDivElement | null;
const projectMain = document.querySelector('.project-main') as HTMLElement | null;
const modal = document.getElementById('project-modal') as HTMLDivElement | null;
const createForm = document.getElementById('project-create-form') as HTMLFormElement | null;
const createInput = document.getElementById('project-create-input') as HTMLInputElement | null;
const cancelButton = document.getElementById('project-cancel') as HTMLButtonElement | null;
const listEl = document.getElementById('project-list') as HTMLDivElement | null;
const contextEl = document.getElementById('project-context') as HTMLParagraphElement | null;
const situationInput = document.getElementById('practice-situation') as HTMLInputElement | null;
const nextButton = document.getElementById('btn-next') as HTMLButtonElement | null;

const examples = ['창업 경진대회 발표', '교수님 면담', '면접 준비'];
const SIDEBAR_COLLAPSED_KEY = 'speakup-sidebar-collapsed';
let isProjectModalOpen = false;
let didAutoOpenEmptyState = false;
let isSidebarCollapsed = false;

function syncSidebarState() {
  if (!projectShell) return;
  if (isSidebarCollapsed) projectShell.classList.add('is-collapsed');
  else projectShell.classList.remove('is-collapsed');
}

function syncThemeToggle() {
  if (!themeToggle) return;
  const theme = document.documentElement.dataset.theme || 'light';
  themeToggle.textContent = theme === 'dark' ? '☀️' : '🌙';
  themeToggle.setAttribute('aria-label', theme === 'dark' ? '라이트 모드로 전환' : '다크 모드로 전환');
}

function syncModalState() {
  if (!modal) return;
  modal.hidden = !isProjectModalOpen;
  modal.dataset.open = isProjectModalOpen ? 'true' : 'false';
  modal.setAttribute('aria-hidden', isProjectModalOpen ? 'false' : 'true');
  if (isProjectModalOpen) {
    modal.classList.add('is-open');
    // ensure it's visible even if some CSS doesn't respect the hidden attribute
    modal.style.display = '';
    document.body.style.overflow = 'hidden';
  } else {
    modal.classList.remove('is-open');
    // fallback to force-hide the backdrop if `hidden` isn't picked up by CSS
    modal.style.display = 'none';
    document.body.style.overflow = '';
  }
}

function openModal() {
  isProjectModalOpen = true;
  syncModalState();
  // set a helpful placeholder for project name
  if (createInput) {
    createInput.placeholder = examples[Math.floor(Math.random() * examples.length)];
    createInput.value = '';
  }
  window.setTimeout(() => createInput?.focus(), 10);
}

function closeModal() {
  isProjectModalOpen = false;
  syncModalState();
  if (createInput) {
    createInput.value = '';
    createInput.blur();
  }
}

function syncSituationPlaceholder() {
  if (!situationInput || situationInput.value) return;
  situationInput.placeholder = examples[Math.floor(Math.random() * examples.length)];
}

function renderProjectList() {
  if (!listEl) return;
  const projects = getProjects();
  const current = getCurrentProject();
  listEl.innerHTML = '';

  projects.forEach((project) => {
    const row = document.createElement('div');
    row.className = 'project-list-row';

    const button = document.createElement('button');
    button.type = 'button';
    button.className = `project-list-item project-name-only${current?.id === project.id ? ' is-active' : ''}`;
    // render project name only (no initial badge)
    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = project.name;
    button.appendChild(name);
    button.title = project.name;
    button.addEventListener('click', () => {
      setCurrentProjectId(project.id);
      localStorage.setItem('speakup-project-name', project.name);
      renderMainState();
      renderProjectList();
    });

    const deleteBtn = document.createElement('button');
    deleteBtn.type = 'button';
    deleteBtn.className = 'project-delete-btn';
    deleteBtn.textContent = '×';
    deleteBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      const ok = confirm(`프로젝트 "${project.name}"을(를) 삭제하시겠습니까?`);
      if (!ok) return;
      const becameEmpty = deleteProject(project.id);
      renderProjectList();
      renderMainState();
      if (becameEmpty) openModal();
    });

    row.appendChild(button);
    row.appendChild(deleteBtn);
    listEl.appendChild(row);
  });
}

function renderMainState() {
  const current = getCurrentProject();
  if (contextEl) {
    contextEl.textContent = current ? `${current.name} 프로젝트에서 이어갈 연습을 적어보세요.` : '';
  }
  if (current) {
    localStorage.setItem('speakup-current-project-id', current.id);
    localStorage.setItem('speakup-project-name', current.name);
  }
  if (nextButton) {
    nextButton.disabled = !current;
  }
}

function render() {
  renderProjectList();
  renderMainState();
  syncSituationPlaceholder();
}

function maybeAutoOpenProjectModal() {
  if (didAutoOpenEmptyState) return;
  if (getProjects().length > 0) return;
  didAutoOpenEmptyState = true;
  openModal();
}

themeToggle?.addEventListener('click', () => {
  const nextTheme = (document.documentElement.dataset.theme || 'light') === 'dark' ? 'light' : 'dark';
  document.documentElement.dataset.theme = nextTheme;
  localStorage.setItem('speakup-theme', nextTheme);
  syncThemeToggle();
});

createToggle?.addEventListener('click', () => {
  openModal();
});

// inject sidebar open button into main (visible only when sidebar is collapsed)
if (projectMain) {
  const openBtn = document.createElement('button');
  openBtn.type = 'button';
  openBtn.className = 'sidebar-open-btn';
  openBtn.title = '사이드바 열기';
  openBtn.textContent = '☰';
  openBtn.addEventListener('click', () => {
    isSidebarCollapsed = false;
    localStorage.setItem(SIDEBAR_COLLAPSED_KEY, 'false');
    syncSidebarState();
  });
  projectMain.prepend(openBtn);
}

// inject sidebar collapse button
if (sidebarHead) {
  const collapseBtn = document.createElement('button');
  collapseBtn.type = 'button';
  collapseBtn.className = 'sidebar-collapse-btn';
  collapseBtn.title = '사이드바 접기/펼치기';
  collapseBtn.textContent = '☰';
  collapseBtn.addEventListener('click', () => {
    isSidebarCollapsed = !isSidebarCollapsed;
    localStorage.setItem(SIDEBAR_COLLAPSED_KEY, isSidebarCollapsed ? 'true' : 'false');
    syncSidebarState();
  });
  // place it before the theme toggle for visibility
  sidebarHead.insertBefore(collapseBtn, sidebarHead.firstChild);
}

// initialize sidebar collapsed state from storage
try {
  isSidebarCollapsed = localStorage.getItem(SIDEBAR_COLLAPSED_KEY) === 'true';
} catch (e) {
  isSidebarCollapsed = false;
}
syncSidebarState();

cancelButton?.addEventListener('click', () => {
  closeModal();
});

modal?.addEventListener('click', (event) => {
  if (event.target === modal) closeModal();
});

createForm?.addEventListener('submit', (event) => {
  event.preventDefault();
  const value = createInput?.value.trim() || '';
  // simple validation: require at least 2 characters
  if (!value || value.length < 2) {
    alert('프로젝트 이름을 2자 이상 입력해주세요.');
    createInput?.focus();
    return;
  }
  const project = createProject(value);
  setCurrentProjectId(project.id);
  localStorage.setItem('speakup-project-name', project.name);
  renderProjectList();
  renderMainState();
  closeModal();
});

situationInput?.addEventListener('input', () => {
  localStorage.setItem('speakup-practice-situation', situationInput.value.trim());
});

nextButton?.addEventListener('click', () => {
  const current = getCurrentProject();
  if (!current) {
    openModal();
    return;
  }
  const value = situationInput?.value.trim() || situationInput?.placeholder || '자유 연습';
  localStorage.setItem('speakup-practice-situation', value);
  const next = new URL('focus-selection.html', location.href);
  next.searchParams.set('projectId', current.id);
  location.href = next.toString();
});

syncThemeToggle();
if (situationInput) {
  const savedSituation = localStorage.getItem('speakup-practice-situation') || '';
  if (savedSituation) situationInput.value = savedSituation;
}
render();
syncModalState();
maybeAutoOpenProjectModal();
