// Layout: the workspace panes (files / terminal / agent), their persisted visibility, the terminal
// Collapse state, the three draggable splitters and the sidebar view switcher.
//
// Manages pane layout, splitters, and responsive drawers.
// Came first: everything here reads or writes ONE piece of state (which panes are visible, whether
// The terminal is collapsed, which sidebar view is active) and the persistence key is derived from
// The workspace, which is why restorePaneLayoutForWorkspace() is called from init_state_response and
// Workspace_changed. Nothing else in the frontend owns that state, so it moved wholesale:
// PaneVisibility/hasStoredPaneLayout/terminalCollapsedPreference (and the localStorage seed that used
// To run at script load, now in init()), paneLayoutKey/restorePaneLayoutForWorkspace/persistPaneLayout,
// SetTerminalCollapsed, toggleWorkspacePane, applyInitialPaneLayout, the focus commands,
// ToggleTerminalPanel, setupResizers (mouse drag + arrow-key alternatives), switchSidebarView and
// SetupSidebarSwitcher, and the six pane open/close buttons.
//
// Three adaptations, all of them the usual module boundaries: `monacoEditor` is a script-scope `let`
// In app.js, so the four "relayout the editor" call sites go through layoutEditor() ->
// GetMonacoEditor(); the stored-layout seed moved into init() so importing the file has no side
// Effects (and works outside a browser); and `activeSidebarView` is a store slice, so its one write
// Goes through the store while the reads stay bare. Everything else is verbatim - including the
// "invert then toggle so toggleWorkspacePane is forced to the target state" trick, which looks odd
// On purpose.

// The console view is loaded on demand, so the expand paths below ask for it.
import { ensureVoidShell } from './shell.js';

// The only way this module touches the editor. app.js keeps the instance; the
// Original code guarded every layout() call with `if (monacoEditor)`, which this keeps equivalent.
function layoutEditor() {
  globalThis.getMonacoEditor?.()?.layout();
}

let paneVisibility = {
  files: true,
  terminal: true,
  agent: true
};
let hasStoredPaneLayout = false;
// The real default is window.innerWidth < 1600, assigned in init() (no DOM/window at import time).
let terminalCollapsedPreference = false;

// Workspace Panes Visibility (Close & Re-open Files, Terminal, Agent)
function paneLayoutKey(ws) {
  const base = ws || currentWorkspace;
  if (!base) return 'voidcode_panes_v1';
  const norm = String(base).replace(/[\\/]+$/, '').toLowerCase();
  return 'voidcode_panes_v2::' + norm;
}

function restorePaneLayoutForWorkspace(ws) {
  let stored = null;
  try { stored = JSON.parse(localStorage.getItem(paneLayoutKey(ws)) || 'null'); } catch (err) { logSwallowed(err, "app.restorePaneLayout"); }
  if (!stored || typeof stored !== 'object') return;

  const target = {
    files: stored.files !== false,
    terminal: stored.terminal !== false,
    agent: stored.agent === true
  };

  // Invert then toggle so toggleWorkspacePane is forced to the target state.
  paneVisibility.files = !target.files;
  paneVisibility.terminal = !target.terminal;
  paneVisibility.agent = !target.agent;
  toggleWorkspacePane('files', target.files);
  toggleWorkspacePane('terminal', target.terminal);
  toggleWorkspacePane('agent', target.agent);

  if (typeof stored.terminalCollapsed === 'boolean') {
    setTerminalCollapsed(stored.terminalCollapsed, false);
  }
  persistPaneLayout();
}

function persistPaneLayout() {
  try {
    const layout = JSON.stringify({
      ...paneVisibility,
      terminalCollapsed: terminalCollapsedPreference
    });
    localStorage.setItem('voidcode_panes_v1', layout);   // last-used fallback
    localStorage.setItem(paneLayoutKey(), layout);       // per-workspace memory
  } catch (err) { logSwallowed(err, "app.savePaneLayout"); }
}

function setTerminalCollapsed(collapsed, persist = true) {
  terminalCollapsedPreference = !!collapsed;
  const panel = document.getElementById('terminal-panel');
  panel?.classList.toggle('collapsed', terminalCollapsedPreference);
  const toggleButton = document.getElementById('btn-toggle-terminal');
  toggleButton?.setAttribute('aria-expanded', terminalCollapsedPreference ? 'false' : 'true');
  toggleButton?.setAttribute('aria-label', terminalCollapsedPreference ? 'Expand terminal' : 'Collapse terminal');
  if (toggleButton) toggleButton.title = terminalCollapsedPreference ? 'Expand terminal' : 'Collapse terminal';
  if (persist) persistPaneLayout();
  // The console (VoidShell) is the panel's default view and it loads lazily, so
  // Revealing the panel is the signal that it is needed. Every expand path funnels through here.
  if (!terminalCollapsedPreference) ensureVoidShell();
  setTimeout(layoutEditor, 50);
  if (!terminalCollapsedPreference) fitActiveTerminal();
}

// At 1099px and below a side pane is a drawer over the editor (layout.css), so opening
// One has to arm the outside-click closer and keep the body flag in sync on resize. Above that width
// Nothing here runs - the panes are docked columns and the resizers own the width.
const DRAWER_MAX_WIDTH = 1099;

function isDrawerMode() {
  return window.innerWidth <= DRAWER_MAX_WIDTH;
}

function syncDrawerState() {
  const open = isDrawerMode() && (paneVisibility.files || paneVisibility.agent);
  document.body.classList.toggle('is-drawer-open', open);
}

function closeDrawers() {
  if (paneVisibility.files) toggleWorkspacePane('files', false);
  if (paneVisibility.agent) toggleWorkspacePane('agent', false);
}

function setupDrawerDismiss() {
  // The rail is excluded: its toggle is the control that opened the drawer, so pressing it must reach
  // The click handler rather than being swallowed as an outside press.
  document.addEventListener('pointerdown', (event) => {
    if (!isDrawerMode()) return;
    const target = event.target instanceof Element ? event.target : null;
    if (!target) return;
    if (target.closest('#sidebar-explorer, #sidebar-agent, #activity-rail')) return;
    closeDrawers();
  }, true);

  window.addEventListener('resize', () => {
    syncDrawerState();
    setTimeout(layoutEditor, 30);
  });
}

function toggleWorkspacePane(paneKey, forceState = null) {
  const next = forceState !== null ? forceState : !paneVisibility[paneKey];

  // Preserve a usable editor width on normal laptop/desktop windows.
  if (next && window.innerWidth < 1600) {
    if (paneKey === 'agent' && paneVisibility.files) toggleWorkspacePane('files', false);
    if (paneKey === 'files' && paneVisibility.agent) toggleWorkspacePane('agent', false);
  }

  paneVisibility[paneKey] = next;

  if (paneKey === 'files') {
    document.getElementById('sidebar-explorer').style.display = next ? 'flex' : 'none';
    document.getElementById('resizer-left').style.display = next ? 'block' : 'none';
    document.getElementById('btn-toggle-pane-files').classList.toggle('is-open', next);
    document.getElementById('btn-toggle-pane-files').setAttribute('aria-pressed', next ? 'true' : 'false');
  } else if (paneKey === 'terminal') {
    document.getElementById('terminal-panel').style.display = next ? 'flex' : 'none';
    document.getElementById('resizer-terminal').style.display = next ? 'block' : 'none';
    document.getElementById('btn-toggle-pane-terminal').classList.toggle('is-open', next);
    document.getElementById('btn-toggle-pane-terminal').setAttribute('aria-pressed', next ? 'true' : 'false');
    // No console load here: showing the pane says nothing about the collapse state (the panel starts
    // Uncollapsed in the HTML), and setTerminalCollapsed below is what tells the user opened it.
  } else if (paneKey === 'agent') {
    document.getElementById('sidebar-agent').style.display = next ? 'flex' : 'none';
    document.getElementById('resizer-right').style.display = next ? 'block' : 'none';
    document.getElementById('btn-toggle-pane-agent').classList.toggle('is-open', next);
    document.getElementById('btn-toggle-pane-agent').setAttribute('aria-pressed', next ? 'true' : 'false');
  }

  persistPaneLayout();

  syncDrawerState();
  setTimeout(layoutEditor, 30);
}

function applyInitialPaneLayout() {
  if (!hasStoredPaneLayout) {
    paneVisibility.files = true;
    paneVisibility.terminal = true;
    paneVisibility.agent = window.innerWidth >= 1600;
  }

  // Apply without relying on the initial HTML state.
  const initial = { ...paneVisibility };
  paneVisibility.files = !initial.files;
  paneVisibility.terminal = !initial.terminal;
  paneVisibility.agent = !initial.agent;
  toggleWorkspacePane('files', initial.files);
  toggleWorkspacePane('terminal', initial.terminal);
  toggleWorkspacePane('agent', initial.agent);

  setTerminalCollapsed(terminalCollapsedPreference, false);
  persistPaneLayout();
}

// Focus commands: give the editor (or the agent) the room without losing the layout.
function focusEditorView() {
  toggleWorkspacePane('agent', false);
  setTerminalCollapsed(true);
}

function focusAgentView() {
  toggleWorkspacePane('agent', true);
  setTerminalCollapsed(true);
  document.getElementById('agent-input-text')?.focus();
}

function toggleTerminalPanel() {
  if (!paneVisibility.terminal) {
    toggleWorkspacePane('terminal', true);
    setTerminalCollapsed(false);
  } else {
    setTerminalCollapsed(!document.getElementById('terminal-panel').classList.contains('collapsed'));
  }
}

// Resizable Splitters
function setupResizers() {
  const leftResizer = document.getElementById('resizer-left');
  const rightResizer = document.getElementById('resizer-right');
  const termResizer = document.getElementById('resizer-terminal');
  const leftPanel = document.getElementById('sidebar-explorer');
  const rightPanel = document.getElementById('sidebar-agent');
  const termPanel = document.getElementById('terminal-panel');

  function makeDrag(resizer, onMove, onEnd = null) {
    resizer.addEventListener('mousedown', (e) => {
      e.preventDefault();
      resizer.classList.add('dragging');
      const moveHandler = (ev) => {
        onMove(ev);
        layoutEditor();
      };
      const upHandler = () => {
        resizer.classList.remove('dragging');
        window.removeEventListener('mousemove', moveHandler);
        window.removeEventListener('mouseup', upHandler);
        onEnd?.();
      };
      window.addEventListener('mousemove', moveHandler);
      window.addEventListener('mouseup', upHandler);
    });
  }

  makeDrag(leftResizer, (e) => {
    const w = Math.max(180, Math.min(480, e.clientX - 8));
    leftPanel.style.width = `${w}px`;
    leftResizer.setAttribute('aria-valuenow', String(Math.round(w)));
  });

  makeDrag(rightResizer, (e) => {
    const w = Math.max(320, Math.min(760, window.innerWidth - e.clientX - 8));
    rightPanel.style.width = `${w}px`;
    rightResizer.setAttribute('aria-valuenow', String(Math.round(w)));
  });

  makeDrag(termResizer, (e) => {
    const h = Math.max(80, Math.min(540, window.innerHeight - e.clientY - 36));
    setTerminalCollapsed(false, false);
    termPanel.style.height = `${h}px`;
    termResizer.setAttribute('aria-valuenow', String(Math.round(h)));
  }, persistPaneLayout);

  leftResizer.addEventListener('keydown', (event) => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    event.preventDefault();
    const current = leftPanel.getBoundingClientRect().width;
    const width = Math.max(180, Math.min(480, current + (event.key === 'ArrowRight' ? 16 : -16)));
    leftPanel.style.width = `${width}px`;
    leftResizer.setAttribute('aria-valuenow', String(Math.round(width)));
    layoutEditor();
  });

  rightResizer.addEventListener('keydown', (event) => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    event.preventDefault();
    const current = rightPanel.getBoundingClientRect().width;
    const width = Math.max(320, Math.min(760, current + (event.key === 'ArrowLeft' ? 16 : -16)));
    rightPanel.style.width = `${width}px`;
    rightResizer.setAttribute('aria-valuenow', String(Math.round(width)));
    layoutEditor();
  });

  termResizer.addEventListener('keydown', (event) => {
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
    event.preventDefault();
    const current = termPanel.getBoundingClientRect().height;
    const height = Math.max(80, Math.min(540, current + (event.key === 'ArrowUp' ? 16 : -16)));
    setTerminalCollapsed(false);
    termPanel.style.height = `${height}px`;
    termResizer.setAttribute('aria-valuenow', String(Math.round(height)));
    layoutEditor();
    fitActiveTerminal();
  });
}

// Sidebar View Switcher (Files, Search, Git) - the view lives in core/state.js

function switchSidebarView(viewName) {
  window.VoidState.set('activeSidebarView', viewName);
  const viewFiles = document.getElementById('view-files');
  const viewSearch = document.getElementById('view-search');
  const viewGit = document.getElementById('view-git');
  const viewDebug = document.getElementById('view-debug');

  const tabFiles = document.getElementById('tab-nav-files');
  const tabSearch = document.getElementById('tab-nav-search');
  const tabGit = document.getElementById('tab-nav-git');
  const tabDebug = document.getElementById('tab-nav-debug');

  const dockSearch = document.getElementById('btn-toggle-pane-search');
  const dockGit = document.getElementById('btn-toggle-pane-git');

  // Ensure left sidebar is open
  const sidebar = document.getElementById('sidebar-explorer');
  if (sidebar && sidebar.style.display === 'none') {
    toggleWorkspacePane('files', true);
  }

  if (viewFiles) viewFiles.style.display = viewName === 'files' ? 'flex' : 'none';
  if (viewSearch) viewSearch.style.display = viewName === 'search' ? 'flex' : 'none';
  if (viewGit) viewGit.style.display = viewName === 'git' ? 'flex' : 'none';
  if (viewDebug) viewDebug.style.display = viewName === 'debug' ? 'flex' : 'none';

  if (tabFiles) {
    tabFiles.classList.toggle('is-active', viewName === 'files');
    tabFiles.setAttribute('aria-selected', viewName === 'files' ? 'true' : 'false');
  }
  if (tabSearch) {
    tabSearch.classList.toggle('is-active', viewName === 'search');
    tabSearch.setAttribute('aria-selected', viewName === 'search' ? 'true' : 'false');
  }
  if (tabGit) {
    tabGit.classList.toggle('is-active', viewName === 'git');
    tabGit.setAttribute('aria-selected', viewName === 'git' ? 'true' : 'false');
  }
  if (tabDebug) {
    tabDebug.classList.toggle('is-active', viewName === 'debug');
    tabDebug.setAttribute('aria-selected', viewName === 'debug' ? 'true' : 'false');
  }

  if (dockSearch) dockSearch.classList.toggle('is-open', viewName === 'search');
  if (dockGit) dockGit.classList.toggle('is-open', viewName === 'git');

  if (viewName === 'search') {
    const sInput = document.getElementById('global-search-input');
    if (sInput) setTimeout(() => sInput.focus(), 60);
  } else if (viewName === 'git') {
    refreshGitStatus();
  } else if (viewName === 'debug') {
    renderBreakpointsList();
    if (activeTab) {
      const dbgTarget = document.getElementById('debug-target-file');
      if (dbgTarget) dbgTarget.textContent = activeTab.name;
    }
  }
}

function setupSidebarSwitcher() {
  document.getElementById('tab-nav-files')?.addEventListener('click', () => switchSidebarView('files'));
  document.getElementById('tab-nav-search')?.addEventListener('click', () => switchSidebarView('search'));
  document.getElementById('tab-nav-git')?.addEventListener('click', () => switchSidebarView('git'));
  document.getElementById('tab-nav-debug')?.addEventListener('click', () => switchSidebarView('debug'));

  document.getElementById('btn-toggle-pane-search')?.addEventListener('click', () => {
    if (activeSidebarView === 'search' && document.getElementById('sidebar-explorer').style.display !== 'none') {
      toggleWorkspacePane('files', false);
    } else {
      switchSidebarView('search');
    }
  });

  document.getElementById('btn-toggle-pane-git')?.addEventListener('click', () => {
    if (activeSidebarView === 'git' && document.getElementById('sidebar-explorer').style.display !== 'none') {
      toggleWorkspacePane('files', false);
    } else {
      switchSidebarView('git');
    }
  });
}

// The six pane open/close buttons, wired where they used to be wired inline.
export function setupPaneButtons() {
  // Workspace Pane Close & Re-open Toggles
  document.getElementById('btn-toggle-pane-files').addEventListener('click', () => toggleWorkspacePane('files'));
  document.getElementById('btn-close-pane-files').addEventListener('click', () => toggleWorkspacePane('files', false));

  document.getElementById('btn-toggle-pane-terminal').addEventListener('click', () => toggleWorkspacePane('terminal'));
  document.getElementById('btn-close-pane-terminal').addEventListener('click', () => toggleWorkspacePane('terminal', false));

  document.getElementById('btn-toggle-pane-agent').addEventListener('click', () => toggleWorkspacePane('agent'));
  document.getElementById('btn-close-pane-agent').addEventListener('click', () => toggleWorkspacePane('agent', false));

  // The drawer band's outside-click closer and resize sync.
  setupDrawerDismiss();
}

// Main.js calls this once, before the classic scripts load. It carries the
// Stored-layout seed that used to run at the top of app.js, and publishes the entry points the rest
// Of the frontend calls by name (toggleWorkspacePane alone has 15 call sites in other modules).
export function init() {
  terminalCollapsedPreference = window.innerWidth < 1600;
  try {
    const stored = JSON.parse(window.localStorage.getItem('voidcode_panes_v1') || 'null');
    if (stored && typeof stored === 'object') {
      paneVisibility.files = stored.files !== false;
      paneVisibility.terminal = stored.terminal !== false;
      paneVisibility.agent = stored.agent === true;
      if (typeof stored.terminalCollapsed === 'boolean') {
        terminalCollapsedPreference = stored.terminalCollapsed;
      }
      hasStoredPaneLayout = true;
    }
  } catch (err) { globalThis.logSwallowed?.(err, "app.loadStoredPaneLayout"); }

  Object.assign(globalThis, {
    toggleWorkspacePane, setTerminalCollapsed, toggleTerminalPanel, restorePaneLayoutForWorkspace,
    applyInitialPaneLayout, persistPaneLayout, setupResizers, switchSidebarView,
    setupSidebarSwitcher, focusEditorView, focusAgentView, setupPaneButtons
  });

  return {
    toggleWorkspacePane, setTerminalCollapsed, toggleTerminalPanel, restorePaneLayoutForWorkspace,
    applyInitialPaneLayout, persistPaneLayout, setupResizers, switchSidebarView,
    setupSidebarSwitcher, focusEditorView, focusAgentView, setupPaneButtons
  };
}