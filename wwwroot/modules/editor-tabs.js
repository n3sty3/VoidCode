// Editor tabs: the open-tab list, the tab bar, activation and close, the save entry points, and the
// Two host responses that build and refresh a tab.
//
// What moved is the tab lifecycle - opening a
// File (model creation, large-file mode, re-open of a clean tab), binding a model to dirty tracking,
// Activating a tab (mounting the model, status slots, breadcrumbs, run config, outline), closing
// (with the unsaved-changes prompt), the tab bar, the per-file-type context actions, and
// SaveTabNow/saveAllDirtyTabs - plus the read_file_response and save_file_response handlers.
//
// What stayed in app.js on purpose: the Monaco instance itself (initMonaco), the split/diff editors,
// LSP, diagnostics, the outline, breadcrumbs, live preview and session/recovery. This module calls
// Them as globals; they are function declarations in app.js, so they are on globalThis and a module
// Can call them by name. Only three things are not visible here, and each is adapted below:
//   - `monacoEditor` is a script-scope `let` in app.js (a module cannot see it), so it is asked for
//     Through the getMonacoEditor() getter that already published for the other modules;
//   - `openTabs`/`activeTab` are store slices published as globals by core/state.js, so the
//     Moved code keeps its identifiers - reads are bare, and the two writes go through the store;
//   - `recoveryPersistTimer` was only ever used by bindTabModel, so the debounce moved with it.
import { html, raw, setHtml } from '../core/markdown.js';
import { hostEvents } from '../core/host-events.js';
// The status bar paints its own slots now, so these are real imports instead of
// Global call-throughs.
import { setStatusSlot, updateLspStatus } from './status-bar.js';

// The tab list is the store's array, held by reference (core/state.js publishes it as `openTabs`).
// The tab code mutates it in place - push/splice/filter - and nobody reassigns the slice, so this
// Alias stays the live list that app.js and the other modules read.
// App.js's sendHost/sendHostRequest are script-scope consts (a module cannot see them); every
// Module declares its own pair the same way (see modules/explorer.js).
let hostSend = (action, data) => window.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

// The tab list is the store's array (core/state.js publishes it as `openTabs`), held by reference:
// The tab code mutates it in place - push/splice/filter - and nobody reassigns the slice, so this
// Alias stays the live list that app.js and the other modules read. init() binds it (rather than the
// Import) so the module also imports cleanly outside a browser.
let openTabs = null;

// `activeTab` IS reassigned, so the writes go through the store explicitly. Reads stay bare: the
// Global accessor resolves in a module the same way it does in app.js.
function setActiveTabSlice(tab) {
  window.VoidState.set('activeTab', tab);
}

function currentEditor() {
  return globalThis.getMonacoEditor ? globalThis.getMonacoEditor() : null;
}

function sendHostRequest(action, data = {}, timeoutMs = 4000) {
  return window.VoidBridge.request(action, data, timeoutMs);
}

// The recovery debounce that used to sit next to bindTabModel; app.js had no other user of it.
let recoveryPersistTimer = null;
/** @type {string[]} */
const recentlyClosedTabs = [];

function reopenLastClosedTab() {
  const path = recentlyClosedTabs.pop();
  if (!path) {
    showToast('No recently closed editors to reopen.', 'info');
    return;
  }
  sendHost('read_file', { path });
}

function bindTabModel(tab) {
  tab._modelSubscription?.dispose?.();
  tab._modelSubscription = tab.model.onDidChangeContent(() => {
    if (tab.suppressDirty) return;
    tab.dirty = true;
    renderTabs();
    notifyLspDidChange(tab);
    clearTimeout(recoveryPersistTimer);
    recoveryPersistTimer = setTimeout(persistRecoveryNow, 750);
    if (tab === activeTab) {
      scheduleLivePreviewSync();
      updateSidebarOutline();
      scheduleDiagnosticsUpdate();
    }
  });
}

function openFileInEditor(path, content, metadata = null) {
  const editor = currentEditor();
  if (!editor) return;
  const fileName = path.split(/[\\/]/).pop();
  const langInfo = getLanguageInfo(path);
  let existing = openTabs.find(t => normalizePath(t.path) === normalizePath(path));

  const isLargeFile = (content || '').length > 2 * 1024 * 1024;
  if (isLargeFile) {
    showToast(`Large file (${Math.round((content || '').length / 1024 / 1024)}MB): performance mode active`, 'info');
  }

  if (!existing) {
    const uri = monaco.Uri.file(path);
    const priorModel = monaco.editor.getModel(uri);
    const model = priorModel || monaco.editor.createModel(content, isLargeFile ? 'plaintext' : langInfo.id, uri);
    existing = {
      path,
      name: fileName,
      dirty: false,
      model,
      langLabel: isLargeFile ? 'Large File' : langInfo.label,
      isLargeFile,
      lastSavedContent: content,
      encoding: metadata?.encoding || 'UTF-8',
      eol: metadata?.eol || (content.includes('\r\n') ? 'CRLF' : (content.includes('\n') ? 'LF' : 'None'))
    };
    bindTabModel(existing);
    openTabs.push(existing);
  } else if (!existing.dirty) {
    existing.suppressDirty = true;
    existing.model.setValue(content);
    existing.dirty = false;
    existing.lastSavedContent = content;
    if (metadata) {
      existing.encoding = metadata.encoding;
      existing.eol = metadata.eol;
    }
    existing.suppressDirty = false;
  }
  // A dirty tab keeps the user's unsaved edits when the same file is opened again.

  setActiveTab(existing);
  notifyLspDidOpen(existing);
}

function refreshOpenTabIfLoaded(path, content) {
  const existing = openTabs.find(t => normalizePath(t.path) === normalizePath(path));
  if (existing && existing.model) {
    existing.suppressDirty = true;
    existing.model.setValue(content);
    existing.dirty = false;
    existing.lastSavedContent = content;
    existing.suppressDirty = false;
    renderTabs();
  }
}

function openToolTab(spec) {
  if (!spec || !spec.id || !spec.mountEl) return null;
  const virtualPath = `voidtool://${spec.id}`;
  let existing = openTabs.find(t => normalizePath(t.path) === normalizePath(virtualPath));
  if (!existing) {
    existing = {
      path: virtualPath,
      name: spec.name || spec.id,
      dirty: false,
      model: null,
      isVirtual: true,
      toolId: spec.id,
      toolKind: spec.toolKind || 'Tool',
      icon: spec.icon || '',
      mountEl: spec.mountEl,
      onActivate: spec.onActivate || null,
      onClose: spec.onClose || null
    };
    openTabs.push(existing);
  } else {
    if (spec.name) existing.name = spec.name;
    if (spec.icon !== undefined) existing.icon = spec.icon;
    if (spec.toolKind) existing.toolKind = spec.toolKind;
    existing.mountEl = spec.mountEl;
    if (spec.onActivate) existing.onActivate = spec.onActivate;
    if (spec.onClose) existing.onClose = spec.onClose;
  }
  setActiveTab(existing);
  return existing;
}

function closeToolTab(id) {
  if (!id) return;
  closeTab(`voidtool://${id}`);
}

function setActiveTab(tab) {
  setActiveTabSlice(tab);
  const emptyEl = control('no-file-state');
  if (emptyEl) emptyEl.style.display = 'none';
  const diffMount = control('monaco-diff-mount');
  if (diffMount) diffMount.style.display = 'none';
  const toolPane = document.getElementById('editor-tool-pane');
  const splitWrap = document.getElementById('editor-workspace-split');
  const mount = control('monaco-mount');

  if (tab.isVirtual) {
    if (splitWrap) splitWrap.style.display = 'none';
    if (mount) mount.style.display = 'none';
    if (toolPane) {
      toolPane.style.display = 'flex';
      Array.from(toolPane.children).forEach((child) => {
        if (child !== tab.mountEl) /** @type {HTMLElement} */ (child).style.display = 'none';
      });
      if (tab.mountEl && tab.mountEl.parentElement !== toolPane) {
        toolPane.appendChild(tab.mountEl);
      }
      if (tab.mountEl) tab.mountEl.style.display = 'flex';
    }
    const langText = tab.toolKind || 'Tool';
    if (control('status-lang')) control('status-lang').textContent = langText;
    setStatusSlot('status-lang', langText);
    setStatusSlot('status-eol', '');
    setStatusSlot('status-encoding', '');
    setStatusSlot('status-cursor', '');
    updateLspStatus({ language: langText, status: 'builtin', detail: 'Built-in VoidCode workbench tab.' });
    renderTabs();
    updateEditorContextActions(null);
    updateBreadcrumbs(null);
    tab.onActivate?.();
    return;
  }

  if (toolPane) toolPane.style.display = 'none';
  if (splitWrap) splitWrap.style.display = '';
  if (mount) mount.style.display = 'block';
  const editor = currentEditor();
  if (editor) {
    editor.setModel(tab.model);
    editor.updateOptions({
      minimap: { enabled: !tab.isLargeFile },
      folding: !tab.isLargeFile
    });
    editor.layout();
  }
  control('status-lang').textContent = tab.langLabel || 'Plain text';
  setStatusSlot('status-lang', tab.langLabel || '');
  setStatusSlot('status-eol', tab.eol ? `EOL: ${tab.eol}` : '');
  setStatusSlot('status-encoding', tab.encoding ? `Encoding: ${tab.encoding}` : '');
  setStatusSlot('status-cursor', 'Ln 1, Col 1');
  const languageId = getLanguageInfo(tab.path).id;
  const lspLanguages = ['python', 'csharp', 'rust', 'go', 'cpp', 'typescript', 'javascript'];
  updateLspStatus(lspLanguages.includes(languageId)
    ? { language: tab.langLabel, status: 'starting', detail: 'Checking language server availability.' }
    : { language: tab.langLabel, status: 'builtin', detail: 'Syntax support is provided by the built-in Monaco language service.' });
  renderTabs();
  highlightActiveTreeNode(tab.path);
  updateEditorContextActions(tab);
  updateBreadcrumbs(tab.path);
  applyPendingLocation();
  scheduleLivePreviewSync();
  refreshRunConfig();
  syncRunAvailability();
  updateSidebarOutline();
  scheduleDiagnosticsUpdate();
  const dbgTarget = control('debug-target-file');
  if (dbgTarget) dbgTarget.textContent = tab.name;
  renderEditorBreakpoints();
}

function closeTab(path) {
  const idx = openTabs.findIndex(t => normalizePath(t.path) === normalizePath(path));
  if (idx === -1) return;

  const [removed] = openTabs.splice(idx, 1);
  if (removed.isVirtual) {
    try { removed.onClose?.(); } catch (err) { globalThis.logSwallowed?.(err, 'editor-tabs.toolClose'); }
    if (removed.mountEl && removed.mountEl.parentElement?.id === 'editor-tool-pane') {
      removed.mountEl.style.display = 'none';
      removed.mountEl.remove();
    }
  } else {
    notifyLspDidClose(path);
    if (removed?.path) {
      recentlyClosedTabs.push(removed.path);
      if (recentlyClosedTabs.length > 25) recentlyClosedTabs.shift();
    }
    removed._modelSubscription?.dispose?.();
    if (removed.model) removed.model.dispose();
  }

  const current = activeTab;
  if (current && normalizePath(current.path) === normalizePath(path)) {
    if (openTabs.length > 0) {
      setActiveTab(openTabs[Math.max(0, idx - 1)]);
    } else {
      setActiveTabSlice(null);
      const toolPane = document.getElementById('editor-tool-pane');
      if (toolPane) toolPane.style.display = 'none';
      const splitWrap = document.getElementById('editor-workspace-split');
      if (splitWrap) splitWrap.style.display = '';
      control('no-file-state').style.display = 'flex';
      control('monaco-mount').style.display = 'none';
      control('status-lang').textContent = 'Plain text';
      setStatusSlot('status-lang', '');
      setStatusSlot('status-eol', '');
      setStatusSlot('status-encoding', '');
      setStatusSlot('status-cursor', '');
      const lspStatus = control('status-lsp');
      if (lspStatus) {
        lspStatus.textContent = 'LSP: Idle';
        lspStatus.className = '';
        lspStatus.title = 'Language server status';
      }
      updateEditorContextActions(null);
      updateBreadcrumbs(null);
      refreshRunConfig();
      syncRunAvailability();
    }
  }
  renderTabs();
  persistSessionNow();
  persistRecoveryNow(true);
}

async function requestCloseTab(tab) {
  if (!tab) return;
  if (!tab.dirty || !tab.model || tab.model.isDisposed()) {
    closeTab(tab.path);
    return;
  }

  const choice = await showConfirmDialog(
    'Unsaved changes',
    `Save changes to “${tab.name}” before closing?`,
    'Save and close',
    'Discard'
  );

  if (choice === 'alt') {
    closeTab(tab.path);
    return;
  }
  if (choice !== true) return;

  const result = await sendHostRequest('save_file', {
    path: tab.path,
    content: tab.model.getValue()
  }, 10000);

  if (result && !result.error) {
    closeTab(tab.path);
  } else {
    showToast(result?.error ? `Could not save: ${result.error}` : 'Save timed out. The tab was kept open.', 'error');
  }
}

// Smart File-Type Context Actions (the per-file-type buttons beside the tab strip)
function updateEditorContextActions(tab) {
  const container = control('editor-context-actions');
  if (!container) return;
  setHtml(container, '');
  if (!tab || !tab.path || tab.isVirtual) return;

  const ext = tab.path.split('.').pop().toLowerCase();

  if (['html', 'htm', 'svg'].includes(ext)) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn--primary btn--xs';
    btn.textContent = 'Live preview';
    btn.addEventListener('click', () => {
      if (globalThis.openWebPreviewTab) {
        globalThis.openWebPreviewTab({ mode: 'active-file' });
      } else {
        toggleMiniWindow('win-preview', true);
        renderLivePreviewFromActiveTab();
      }
    });
    container.appendChild(btn);
  } else if (['md', 'markdown'].includes(ext)) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn--primary btn--xs';
    btn.textContent = 'Read rendered';
    btn.addEventListener('click', () => {
      if (globalThis.openWebPreviewTab) {
        globalThis.openWebPreviewTab({ mode: 'active-file' });
      } else {
        toggleMiniWindow('win-preview', true);
        renderLivePreviewFromActiveTab();
      }
    });
    container.appendChild(btn);
  }
  globalThis.renderTestContextAction?.(container, tab);
  // Runnable files use the single Smart Run button (F5) in the tabs bar.
}

let ctxTargetTab = null;

function showTabContextMenu(e, tab) {
  e.preventDefault();
  e.stopPropagation();
  ctxTargetTab = tab;
  const menu = control('tab-context-menu');
  if (!menu) return;
  menu.style.display = 'block';
  menu.style.left = `${Math.min(e.clientX, window.innerWidth - 220)}px`;
  menu.style.top = `${Math.min(e.clientY, window.innerHeight - 260)}px`;
}

export function togglePinTab(tab = activeTab) {
  const target = tab || activeTab;
  if (!target) return;
  target.pinned = !target.pinned;
  openTabs.sort((a, b) => (b.pinned ? 1 : 0) - (a.pinned ? 1 : 0));
  renderTabs();
}

function updateDocumentTitle() {
  const ws = window.VoidState?.get?.('workspace') || '';
  const wsName = ws ? ws.split(/[\\/]/).filter(Boolean).pop() : '';
  const tabPart = activeTab ? `${activeTab.dirty ? '\u25cf ' : ''}${activeTab.name} \u2014 ` : '';
  const wsPart = wsName ? `${wsName} \u2014 ` : '';
  document.title = `${tabPart}${wsPart}VoidCode`;
}

export async function closeOtherTabs(keepPath) {
  const targetKey = normalizePath(keepPath || (activeTab ? activeTab.path : ''));
  if (!targetKey) return;
  const others = openTabs.filter(t => !t.pinned && normalizePath(t.path) !== targetKey);
  for (const t of others) {
    await requestCloseTab(t);
  }
}

export function closeSavedTabs() {
  const saved = openTabs.filter(t => !t.dirty && !t.pinned);
  saved.forEach(t => closeTab(t.path));
}

export async function closeAllTabs() {
  const snapshot = openTabs.filter(t => !t.pinned);
  for (const t of snapshot) {
    await requestCloseTab(t);
  }
}

export function cycleNextTab() {
  if (openTabs.length < 2) return;
  const idx = activeTab ? openTabs.findIndex(t => normalizePath(t.path) === normalizePath(activeTab.path)) : 0;
  const next = openTabs[(idx + 1) % openTabs.length];
  if (next) setActiveTab(next);
}

let draggedTabPath = null;

function renderTabs() {
  updateDocumentTitle();
  const container = control('tabs-list');
  setHtml(container, '');

  openTabs.forEach(tab => {
    const isAct = activeTab && normalizePath(activeTab.path) === normalizePath(tab.path);
    const el = document.createElement('div');
    el.className = 'tab-item' + (isAct ? ' active' : '') + (tab.pinned ? ' is-pinned' : '') + (tab.isVirtual ? ' tab-item--tool' : '');
    el.title = tab.isVirtual ? `${tab.name} (${tab.toolKind || 'Tool Tab'})` : (tab.pinned ? `${tab.path} (Pinned)` : tab.path);
    el.draggable = true;
    const iconMarkup = tab.isVirtual
      ? ''
      : (globalThis.fileTypeIcon ? globalThis.fileTypeIcon(tab.name) : '');
    setHtml(el, html`
      ${tab.isVirtual ? raw(`<span class="tab-tool-glyph" aria-hidden="true">${tab.icon || '\u25c8'}</span>`) : ''}
      ${iconMarkup ? raw(`<span class="tab-file-icon" aria-hidden="true">${iconMarkup}</span>`) : ''}
      ${tab.dirty ? raw('<span class="tab-dirty-dot" aria-label="Unsaved changes"></span>') : ''}
      <span>${tab.pinned ? '\ud83d\udccc ' : ''}${tab.name}</span>
      <span class="tab-close-btn" role="button" aria-label="Close ${tab.name}">×</span>
    `);

    el.addEventListener('dragstart', (e) => {
      draggedTabPath = tab.path;
      if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
    });

    el.addEventListener('dragover', (e) => {
      if (!draggedTabPath || normalizePath(draggedTabPath) === normalizePath(tab.path)) return;
      e.preventDefault();
      if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
    });

    el.addEventListener('drop', (e) => {
      e.preventDefault();
      if (!draggedTabPath || normalizePath(draggedTabPath) === normalizePath(tab.path)) return;
      const fromIdx = openTabs.findIndex(t => normalizePath(t.path) === normalizePath(draggedTabPath));
      const toIdx = openTabs.findIndex(t => normalizePath(t.path) === normalizePath(tab.path));
      draggedTabPath = null;
      if (fromIdx === -1 || toIdx === -1) return;
      const [moved] = openTabs.splice(fromIdx, 1);
      openTabs.splice(toIdx, 0, moved);
      renderTabs();
    });

    el.addEventListener('click', (e) => {
      if (/** @type {HTMLInputElement} */ (e.target).classList.contains('tab-close-btn')) {
        requestCloseTab(tab);
      } else if (e.altKey && !tab.isVirtual) {
        openSplitEditor(tab);
      } else {
        setActiveTab(tab);
      }
    });

    el.addEventListener('auxclick', (e) => {
      if (e.button === 1) {
        e.preventDefault();
        requestCloseTab(tab);
      }
    });

    el.addEventListener('contextmenu', (e) => showTabContextMenu(e, tab));

    container.appendChild(el);
  });
}

async function saveTabNow(tab) {
  if (!tab || !tab.model || tab.model.isDisposed() || !tab.path) return;
  if (tab === activeTab && (appSettings.FormatOnSave || control('settings-format-on-save')?.checked)) {
    await formatActiveDocument();
  }
  sendHost('save_file', { path: tab.path, content: tab.model.getValue() });
}

function saveAllDirtyTabs() {
  const dirty = openTabs.filter(t => t.dirty && t.model && !t.model.isDisposed());
  dirty.forEach(saveTabNow);
  return dirty;
}

// Main.js calls this once, before the classic scripts load. The tab entry
// Points are published for app.js and for the modules that already ask for them by name
// (modules/settings.js autosave calls globalThis.saveTabNow/saveAllDirtyTabs).
// One typed lookup instead of a cast per call site - the controls this module reads
// By id are inputs, and tsc only knows getElementById returns a plain HTMLElement.
/**
 * @param {string} id
 * @returns {HTMLInputElement | null}
 */
function control(id) {
  return /** @type {HTMLInputElement | null} */ (document.getElementById(id));
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  openTabs = window.VoidState.get('openTabs');

  hostEvents.on('read_file_response', (payload) => {
    if (payload.error) {
      showToast(payload.error, 'error');
    } else {
      if (typeof payload.line === 'number' && payload.line > 0) {
        globalThis.setPendingNavigateLocation?.({
          line: payload.line,
          col: typeof payload.column === 'number' && payload.column > 0 ? payload.column : 1
        });
      }
      openFileInEditor(payload.path, payload.content, payload.metadata);
    }
  });

  // The save pipeline's response: resolves a pending sendHostRequest('save_file') first, then
  // Clears the dirty flag on the tab it belongs to. ( the pending map is the
  // Bridge's, keyed by reqId.)
  hostEvents.on('save_file_response', (payload) => {
    const pending = window.VoidBridge ? window.VoidBridge.pending : null;
    if (payload && payload.reqId && pending && pending.has(payload.reqId)) {
      const callback = pending.get(payload.reqId);
      pending.delete(payload.reqId);
      callback(payload);
    }
    if (payload.error) {
      showToast('Could not save: ' + payload.error, 'error');
    } else {
      const tab = openTabs.find(t => normalizePath(t.path) === normalizePath(payload.path));
      if (tab) {
        tab.dirty = false;
        tab.lastSavedContent = tab.model && !tab.model.isDisposed() ? tab.model.getValue() : tab.lastSavedContent;
        renderTabs();
      }
      persistRecoveryNow();
      notifyLspDidSave(payload.path);
      if (payload.checkpointId) {
        recordTimelineEvent('Saved', payload.path, payload.checkpointId);
      }
      setActivityStatus('Saved ' + payload.path.split(/[\\/]/).pop());
      showToast('Saved', 'success');
    }
  });

  const wireTabContextMenu = () => {
    const tabsList = control('tabs-list');
    tabsList?.addEventListener('dblclick', (e) => {
      if (e.target === tabsList) globalThis.promptNewFile?.();
    });
    const menu = control('tab-context-menu');
    if (!menu) return;
    window.addEventListener('click', () => { menu.style.display = 'none'; });
    menu.querySelectorAll('[data-tab-act]').forEach(item => {
      item.addEventListener('click', () => {
        menu.style.display = 'none';
        const t = ctxTargetTab || activeTab;
        if (!t) return;
        const act = item.getAttribute('data-tab-act');
        if (act === 'pin') togglePinTab(t);
        else if (act === 'close') requestCloseTab(t);
        else if (act === 'close-others') closeOtherTabs(t.path);
        else if (act === 'close-saved') closeSavedTabs();
        else if (act === 'close-all') closeAllTabs();
        else if (act === 'split') {
          if (t.isVirtual) showToast('Split view is only available for file tabs', 'info');
          else openSplitEditor(t);
        } else if (act === 'reveal') {
          if (!t.isVirtual) sendHost('reveal_in_explorer', { path: t.path });
        } else if (act === 'copy-path') {
          const ws = window.VoidState?.get?.('workspace') || '';
          const rel = (ws && t.path.startsWith(ws)) ? t.path.slice(ws.length).replace(/^[\\/]/, '') : t.path;
          navigator.clipboard?.writeText?.(rel);
          showToast('Copied relative path', 'info');
        }
      });
    });

    wireDockButtons();
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', wireTabContextMenu, { once: true });
  else wireTabContextMenu();

  Object.assign(globalThis, {
    openFileInEditor,
    openToolTab,
    closeToolTab,
    dockSettingsAsTab,
    dockDiffAsTab,
    refreshOpenTabIfLoaded,
    setActiveTab,
    closeTab,
    closeOtherTabs,
    closeSavedTabs,
    closeAllTabs,
    cycleNextTab,
    togglePinTab,
    reopenLastClosedTab,
    requestCloseTab,
    renderTabs,
    updateEditorContextActions,
    saveTabNow,
    saveAllDirtyTabs
  });

  return {
    openFileInEditor,
    openToolTab,
    closeToolTab,
    dockSettingsAsTab,
    dockDiffAsTab,
    refreshOpenTabIfLoaded,
    setActiveTab,
    closeTab,
    closeOtherTabs,
    closeSavedTabs,
    closeAllTabs,
    cycleNextTab,
    togglePinTab,
    reopenLastClosedTab,
    requestCloseTab,
    renderTabs,
    updateEditorContextActions,
    saveTabNow,
    saveAllDirtyTabs
  };
}

let dockedSettingsState = null;
function dockSettingsAsTab() {
  const modal = document.getElementById('settings-modal');
  const card = document.getElementById('win-settings');
  if (!modal || !card) return;
  if (dockedSettingsState) {
    closeToolTab('settings');
    return;
  }
  const btn = document.getElementById('btn-dock-settings-tab');
  const origParent = modal.parentElement;
  const origNext = modal.nextSibling;
  const hostWrap = document.createElement('div');
  hostWrap.className = 'tool-tab-view tool-tab-view--docked';
  dockedSettingsState = { modal, card, origParent, origNext, btn };
  modal.classList.add('is-tab-docked');
  card.classList.add('is-tab-docked');
  modal.style.display = 'flex';
  hostWrap.appendChild(modal);
  if (btn) btn.textContent = 'Undock';
  openToolTab({
    id: 'settings',
    name: 'Settings',
    icon: '\u2699',
    toolKind: 'Settings',
    mountEl: hostWrap,
    onActivate: () => { modal.style.display = 'flex'; },
    onClose: () => {
      if (!dockedSettingsState) return;
      const st = dockedSettingsState;
      dockedSettingsState = null;
      st.modal.classList.remove('is-tab-docked');
      st.card.classList.remove('is-tab-docked');
      st.modal.style.display = 'none';
      if (st.origParent) st.origParent.insertBefore(st.modal, st.origNext || null);
      if (st.btn) st.btn.textContent = 'Open as Tab';
    }
  });
}

let dockedDiffState = null;
function dockDiffAsTab() {
  const win = document.getElementById('win-diff');
  if (!win) return;
  if (dockedDiffState) {
    closeToolTab('diff-review');
    return;
  }
  const btn = document.getElementById('btn-dock-diff-tab');
  const origParent = win.parentElement;
  const origNext = win.nextSibling;
  const origStyle = win.getAttribute('style') || '';
  const hostWrap = document.createElement('div');
  hostWrap.className = 'tool-tab-view tool-tab-view--docked';
  dockedDiffState = { win, origParent, origNext, origStyle, btn };
  win.classList.add('is-tab-docked');
  win.style.cssText = 'display:flex;';
  hostWrap.appendChild(win);
  if (btn) btn.textContent = 'Undock';
  const titleText = document.getElementById('diff-win-title')?.textContent?.trim() || 'Diff Review';
  openToolTab({
    id: 'diff-review',
    name: titleText,
    icon: '\u21c4',
    toolKind: 'Diff',
    mountEl: hostWrap,
    onActivate: () => setTimeout(() => window.dispatchEvent(new Event('resize')), 40),
    onClose: () => {
      if (!dockedDiffState) return;
      const st = dockedDiffState;
      dockedDiffState = null;
      st.win.classList.remove('is-tab-docked');
      st.win.setAttribute('style', st.origStyle);
      st.win.style.display = 'none';
      if (st.origParent) st.origParent.insertBefore(st.win, st.origNext || null);
      if (st.btn) st.btn.textContent = 'Open as Tab';
    }
  });
}

function wireDockButtons() {
  document.getElementById('btn-dock-settings-tab')?.addEventListener('click', dockSettingsAsTab);
  document.getElementById('btn-dock-diff-tab')?.addEventListener('click', dockDiffAsTab);
  document.getElementById('btn-close-settings')?.addEventListener('click', () => {
    if (dockedSettingsState) closeToolTab('settings');
  });
  document.querySelector('[data-close-window="win-diff"]')?.addEventListener('click', () => {
    if (dockedDiffState) closeToolTab('diff-review');
  });
}