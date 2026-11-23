// Daily-driver workflow tools:
//   - Tasks runner modal (.voidcode/tasks.json & .vscode/tasks.json) + problem markers
//   - Git inline blame annotations
//   - Merge-conflict detector + Monaco CodeLens Accept Current / Incoming / Both
//   - Editor basics: Save As, Compare Active File With..., drag-and-drop open
import { html, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

let blameEnabled = false;
/** @type {string[]} */
let blameDecorations = [];
let conflictProviderInstalled = false;

function byId(id) {
  return document.getElementById(id);
}

function getEditor() {
  return globalThis.getMonacoEditor ? globalThis.getMonacoEditor() : null;
}

// ---------------------------------------------------------------------------
// Merge-conflict parser & resolver (pure helpers + Monaco CodeLens)
// ---------------------------------------------------------------------------

/**
 * Finds standard Git conflict blocks (`<<<<<<<` ... `=======` ... `>>>>>>>`) in text.
 * Line numbers are 1-based inclusive.
 * @param {string} text
 */
export function findMergeConflicts(text) {
  const lines = String(text || '').replace(/\r\n/g, '\n').split('\n');
  const conflicts = [];
  let i = 0;
  while (i < lines.length) {
    if (lines[i].startsWith('<<<<<<<')) {
      const startLine = i + 1;
      let sepIdx = -1;
      let endIdx = -1;
      for (let j = i + 1; j < lines.length; j++) {
        if (sepIdx === -1 && lines[j].startsWith('=======')) {
          sepIdx = j;
        } else if (sepIdx !== -1 && lines[j].startsWith('>>>>>>>')) {
          endIdx = j;
          break;
        }
      }
      if (sepIdx !== -1 && endIdx !== -1) {
        conflicts.push({
          index: conflicts.length,
          startLine,
          separatorLine: sepIdx + 1,
          endLine: endIdx + 1,
          currentLabel: lines[i].slice(7).trim() || 'HEAD',
          incomingLabel: lines[endIdx].slice(7).trim() || 'Incoming',
          currentLines: lines.slice(i + 1, sepIdx),
          incomingLines: lines.slice(sepIdx + 1, endIdx)
        });
        i = endIdx + 1;
        continue;
      }
    }
    i++;
  }
  return conflicts;
}

/**
 * Resolves a single conflict block by index with 'current' | 'incoming' | 'both'.
 * @param {string} text
 * @param {number} conflictIndex
 * @param {'current' | 'incoming' | 'both'} choice
 */
export function resolveMergeConflict(text, conflictIndex, choice) {
  const usesCrLf = String(text || '').includes('\r\n');
  const lines = String(text || '').replace(/\r\n/g, '\n').split('\n');
  const conflicts = findMergeConflicts(text);
  const target = conflicts[conflictIndex];
  if (!target) return text;

  let replacement;
  if (choice === 'current') replacement = target.currentLines;
  else if (choice === 'incoming') replacement = target.incomingLines;
  else replacement = [...target.currentLines, ...target.incomingLines];

  const rebuilt = [
    ...lines.slice(0, target.startLine - 1),
    ...replacement,
    ...lines.slice(target.endLine)
  ];
  return rebuilt.join(usesCrLf ? '\r\n' : '\n');
}

function applyConflictResolutionToEditor(conflictIndex, choice) {
  const editor = getEditor();
  const model = editor?.getModel?.();
  if (!model) return;
  const updated = resolveMergeConflict(model.getValue(), conflictIndex, choice);
  model.pushEditOperations([], [{ range: model.getFullModelRange(), text: updated }], () => null);
  globalThis.showToast?.(`Resolved merge conflict (${choice})`, 'info');
}

export function installMergeConflictCodeLens() {
  if (conflictProviderInstalled || !window.monaco?.languages?.registerCodeLensProvider) return;
  conflictProviderInstalled = true;

  const editor = getEditor();
  const cmdCurrent = editor?.addCommand?.(0, (_, idx) => applyConflictResolutionToEditor(idx, 'current')) || 'voidcode.conflict.current';
  const cmdIncoming = editor?.addCommand?.(0, (_, idx) => applyConflictResolutionToEditor(idx, 'incoming')) || 'voidcode.conflict.incoming';
  const cmdBoth = editor?.addCommand?.(0, (_, idx) => applyConflictResolutionToEditor(idx, 'both')) || 'voidcode.conflict.both';

  window.monaco.languages.registerCodeLensProvider('*', {
    provideCodeLenses(model) {
      const conflicts = findMergeConflicts(model.getValue());
      const lenses = [];
      conflicts.forEach((c) => {
        const range = { startLineNumber: c.startLine, startColumn: 1, endLineNumber: c.startLine, endColumn: 1 };
        lenses.push(
          { range, command: { id: cmdCurrent, title: `Accept Current (${c.currentLabel})`, arguments: [c.index] } },
          { range, command: { id: cmdIncoming, title: `Accept Incoming (${c.incomingLabel})`, arguments: [c.index] } },
          { range, command: { id: cmdBoth, title: 'Accept Both', arguments: [c.index] } }
        );
      });
      return { lenses, dispose() {} };
    }
  });
}

// ---------------------------------------------------------------------------
// Git Inline Blame
// ---------------------------------------------------------------------------

export function clearGitBlameDecorations() {
  const editor = getEditor();
  if (editor && blameDecorations.length) {
    blameDecorations = editor.deltaDecorations(blameDecorations, []);
  }
}

export function toggleGitBlame() {
  const tab = window.VoidState?.get?.('activeTab');
  if (!tab || !tab.path) {
    globalThis.showToast?.('Open a file to view Git blame.', 'info');
    return;
  }
  if (blameEnabled) {
    blameEnabled = false;
    clearGitBlameDecorations();
    globalThis.showToast?.('Git inline blame hidden.', 'info');
    return;
  }
  blameEnabled = true;
  sendHost('git_get_blame', { path: tab.path });
}

function handleGitBlameResponse(payload) {
  if (!payload) return;
  if (payload.error) {
    blameEnabled = false;
    clearGitBlameDecorations();
    globalThis.showToast?.(payload.error, 'error');
    return;
  }
  const editor = getEditor();
  if (!editor || !window.monaco) return;

  const lines = Array.isArray(payload.lines) ? payload.lines : [];
  const decorations = lines.map(entry => ({
    range: new window.monaco.Range(entry.line, 1, entry.line, 1),
    options: {
      isWholeLine: true,
      after: {
        content: `   ${entry.author} · ${entry.shortHash}${entry.date ? ` · ${entry.date}` : ''}${entry.summary ? ` — ${entry.summary}` : ''}`,
        inlineClassName: 'git-blame-inline'
      }
    }
  }));
  blameDecorations = editor.deltaDecorations(blameDecorations, decorations);
  globalThis.showToast?.(`Showing Git blame for ${lines.length} line${lines.length === 1 ? '' : 's'}.`, 'info');
}

// ---------------------------------------------------------------------------
// Tasks Runner Modal & Problem Matchers
// ---------------------------------------------------------------------------

export function openTasksModal() {
  const modal = byId('tasks-modal');
  if (!modal) return;
  modal.style.display = 'flex';
  const list = byId('tasks-modal-list');
  if (list) setHtml(list, html`<div class="empty-hint">Scanning .voidcode/tasks.json and .vscode/tasks.json...</div>`);
  sendHost('list_tasks', {});
}

export function closeTasksModal() {
  const modal = byId('tasks-modal');
  if (modal) modal.style.display = 'none';
}

function handleTasksListResponse(payload) {
  const list = byId('tasks-modal-list');
  if (!list) return;
  setHtml(list, '');

  const tasks = Array.isArray(payload?.tasks) ? payload.tasks : [];
  if (tasks.length === 0) {
    setHtml(list, html`
      <div class="empty-state">
        <p>No workspace tasks configured</p>
        <p class="empty-hint">Add a <code>.voidcode/tasks.json</code> or <code>.vscode/tasks.json</code> file with a <code>tasks</code> array to run build/test tasks here.</p>
      </div>
    `);
    return;
  }

  tasks.forEach(task => {
    const row = document.createElement('div');
    row.className = 'inspector-row';
    row.dataset.taskLabel = task.label;

    const left = document.createElement('div');
    left.className = 'inspector-row-main';
    const cmdPreview = [task.command, ...(task.args || [])].join(' ');
    setHtml(left, html`
      <div class="inspector-row-title">${task.label}</div>
      <div class="inspector-row-detail">${cmdPreview} \u00b7 ${task.sourceFile || 'tasks.json'}</div>
    `);

    const runBtn = document.createElement('button');
    runBtn.type = 'button';
    runBtn.className = 'btn btn--primary btn--xs';
    runBtn.dataset.runTask = task.label;
    runBtn.textContent = 'Run';
    runBtn.addEventListener('click', () => {
      const tab = window.VoidState?.get?.('activeTab');
      closeTasksModal();
      globalThis.toggleWorkspacePane?.('terminal', true);
      sendHost('run_task', { label: task.label, activeFile: tab?.path || '' });
    });

    row.append(left, runBtn);
    list.appendChild(row);
  });
}

function handleTaskProblems(payload) {
  if (!window.monaco || !payload || !Array.isArray(payload.problems)) return;
  const byPath = new Map();
  payload.problems.forEach(p => {
    const key = String(p.path || '');
    if (!key) return;
    if (!byPath.has(key)) byPath.set(key, []);
    byPath.get(key).push({
      startLineNumber: p.line || 1,
      startColumn: p.column || 1,
      endLineNumber: p.line || 1,
      endColumn: (p.column || 1) + 1,
      severity: p.severity === 1 ? 8 : (p.severity === 2 ? 4 : 2),
      message: p.message || '',
      code: p.code || undefined,
      source: p.source || 'task'
    });
  });

  byPath.forEach((markers, filePath) => {
    const uri = window.monaco.Uri.file(filePath);
    const model = window.monaco.editor.getModel(uri);
    if (model) {
      window.monaco.editor.setModelMarkers(model, 'voidcode-task', markers);
    }
  });
}

function handleTaskRunCompleted(payload) {
  if (!payload) return;
  const count = Array.isArray(payload.problems) ? payload.problems.length : 0;
  const problemSuffix = count > 0 ? ` (${count} problem${count === 1 ? '' : 's'})` : '';
  globalThis.showToast?.(
    payload.success
      ? `Task "${payload.label}" succeeded${problemSuffix}`
      : `Task "${payload.label}" failed (exit ${payload.exitCode})${problemSuffix}`,
    payload.success ? 'info' : 'error'
  );
}

// ---------------------------------------------------------------------------
// Editor basics (Save As, Compare Active File, Drag-and-Drop)
// ---------------------------------------------------------------------------

export async function saveActiveFileAs() {
  const tab = window.VoidState?.get?.('activeTab');
  const editor = getEditor();
  if (!tab || !editor) {
    globalThis.showToast?.('No active file to save.', 'info');
    return;
  }
  const dest = await globalThis.showInputDialog?.('Save file as...', tab.path || '', 'Save As');
  if (!dest || !dest.trim()) return;
  const targetPath = dest.trim();
  sendHost('save_file', { path: targetPath, content: editor.getValue() });
  sendHost('read_file', { path: targetPath });
}

export async function compareActiveFileWith() {
  const tabs = window.VoidState?.get?.('openTabs') || [];
  const active = window.VoidState?.get?.('activeTab');
  if (!active || tabs.length < 2) {
    globalThis.showToast?.('Open at least two tabs to compare files.', 'info');
    return;
  }
  const other = tabs.find(t => t.path !== active.path);
  if (!other || !other.model || !active.model) return;
  globalThis.openCheckpointDiff?.(
    `${other.name} ↔ ${active.name}`,
    other.model.getValue(),
    active.model.getValue()
  );
}

function setupModalAndDragDrop() {
  const modal = byId('tasks-modal');
  if (modal) {
    byId('btn-close-tasks-modal')?.addEventListener('click', closeTasksModal);
    modal.addEventListener('click', (e) => { if (e.target === modal) closeTasksModal(); });
    modal.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        closeTasksModal();
      }
    });
  }

  // Drag-and-drop file opening when a path is dropped on the editor area
  const editorHost = byId('editor-panel') || byId('monaco-mount');
  if (editorHost) {
    editorHost.addEventListener('dragover', (e) => { e.preventDefault(); });
    editorHost.addEventListener('drop', (e) => {
      const dt = /** @type {DragEvent} */ (e).dataTransfer;
      const droppedPath = dt?.getData('text/voidcode-path') || dt?.getData('text/plain');
      if (droppedPath && (/^[a-zA-Z]:[\\/]/.test(droppedPath.trim()) || droppedPath.trim().startsWith('/'))) {
        e.preventDefault();
        sendHost('read_file', { path: droppedPath.trim() });
      }
    });
  }

  // Register conflict CodeLens once Monaco loads
  const waitMonaco = () => {
    if (window.monaco?.languages?.registerCodeLensProvider) {
      installMergeConflictCodeLens();
    } else {
      setTimeout(waitMonaco, 600);
    }
  };
  waitMonaco();
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  window.VoidHostEvents?.register?.({
    git_blame_response: handleGitBlameResponse,
    tasks_list_response: handleTasksListResponse,
    task_problems: handleTaskProblems,
    task_run_completed: handleTaskRunCompleted
  });

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', setupModalAndDragDrop, { once: true });
  } else {
    setupModalAndDragDrop();
  }

  Object.assign(globalThis, {
    findMergeConflicts,
    resolveMergeConflict,
    toggleGitBlame,
    openTasksModal,
    closeTasksModal,
    saveActiveFileAs,
    compareActiveFileWith
  });

  return {
    findMergeConflicts,
    resolveMergeConflict,
    toggleGitBlame,
    openTasksModal,
    closeTasksModal,
    saveActiveFileAs,
    compareActiveFileWith
  };
}
