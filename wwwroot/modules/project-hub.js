// Project hub: the launcher card shown after sign-in and from the top bar - the recent-workspace
// List, "open folder", "new project", "resume" and "customize".
//
// What moved is the three functions that paint
// It (openProjectHub/closeProjectHub/renderProjectHubRecents) and the whole block of listeners that
// Drive it. The recents list itself is not owned here: it is `appSettings.RecentWorkspaces`, which
// The host fills on init_state_response / workspace_changed, so this module only reads the settings
// Slice and re-renders - the writers stay in app.js where the host payloads land.
import { html, raw, setHtml } from '../core/markdown.js';

// App.js's sendHost is a script-scope const (a module cannot see it); same pattern as the other
// Modules.
let hostSend = (action, data) => window.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

// Stage 2: Project Launcher Hub
function openProjectHub() {
  renderProjectHubRecents();
  document.getElementById('project-hub').style.display = 'flex';
}

function closeProjectHub() {
  document.getElementById('project-hub').style.display = 'none';
}

function renderProjectHubRecents() {
  const containers = [
    document.getElementById('hub-recents-list'),
    document.getElementById('empty-recents-list')
  ].filter(Boolean);
  if (containers.length === 0) return;

  const recents = Array.isArray(appSettings.RecentWorkspaces) && appSettings.RecentWorkspaces.length > 0
    ? appSettings.RecentWorkspaces
    : (currentWorkspace ? [currentWorkspace] : []);

  const resumeButton = document.getElementById('btn-hub-resume');
  if (resumeButton) resumeButton.style.display = 'inline-flex';

  containers.forEach(container => {
    setHtml(container, '');
    if (recents.length === 0) {
      setHtml(container, '<div class="recent-empty">No recent workspaces recorded.</div>');
      return;
    }

    recents.forEach(folderPath => {
      const folderName = folderPath.split(/[\\/]/).pop() || folderPath;
      const isCurrent = currentWorkspace && normalizePath(currentWorkspace) === normalizePath(folderPath);
      const item = document.createElement('button');
      item.type = 'button';
      item.className = 'recent-project-row' + (isCurrent ? ' is-active' : '');
      item.title = folderPath;
      setHtml(item, html`
        <span class="recent-project-name">${folderName}</span>
        <span class="recent-project-path">${folderPath}</span>
        ${isCurrent ? raw('<span class="val-badge">Active</span>') : ''}
      `);
      item.addEventListener('click', () => {
        sendHost('select_workspace_path', { path: folderPath });
        closeProjectHub();
      });
      container.appendChild(item);
    });
  });
}

// App.js calls this once while wiring the DOM, in place of the block of hub
// Listeners that used to sit inline in the DOMContentLoaded handler.
export function setupProjectHub() {
  document.getElementById('btn-open-hub').addEventListener('click', openProjectHub);
  document.getElementById('btn-empty-choose-project').addEventListener('click', () => {
    sendHost('open_workspace_dialog');
  });
  document.getElementById('btn-explorer-open-folder')?.addEventListener('click', () => {
    sendHost('open_workspace_dialog');
  });
  document.getElementById('btn-hub-resume').addEventListener('click', closeProjectHub);
  document.getElementById('project-hub')?.addEventListener('click', (e) => {
    if (/** @type {HTMLInputElement} */ (e.target).id === 'project-hub') closeProjectHub();
  });
  document.getElementById('btn-hub-open-pc').addEventListener('click', () => {
    sendHost('open_workspace_dialog');
  });
  document.getElementById('btn-hub-new-project').addEventListener('click', async () => {
    closeProjectHub();
    const name = await showInputDialog('New project folder name', 'my-project', 'Select parent folder');
    if (name) {
      sendHost('create_project_folder', { name });
    }
  });
  document.getElementById('btn-hub-customize').addEventListener('click', () => {
    closeProjectHub();
    document.getElementById('settings-modal').style.display = 'flex';
  });
  // No Enter/Space shim needed - the tiles are real <button>s now, so the browser
  // Turns Enter and Space into a click on its own. The old keydown handler would have fired twice.
}

// Main.js calls this once. The three entry points stay published because
// App.js still opens/closes the hub from the host handlers (init_state_response, workspace_changed),
// From completeAuth() and from the empty-state button.
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  Object.assign(globalThis, {
    openProjectHub,
    closeProjectHub,
    renderProjectHubRecents,
    setupProjectHub
  });

  return {
    openProjectHub,
    closeProjectHub,
    renderProjectHubRecents,
    setupProjectHub
  };
}