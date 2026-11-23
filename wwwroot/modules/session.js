// Session restore, crash recovery, safe close and external-file-change handling.
//
// . This is the slice where "move verbatim" needed two documented seams rather
// Than a clean cut, because the data it works on is *built from* state another module owns:
//
//   1. `pendingRecoveryState`/`recoveryReady` are written by app.js's host handlers
//      (init_state_response and workspace_changed both assign them from payload.recovery), and read
//      By restoreSessionForWorkspace(). They moved here with the restore flow, so app.js now calls
//      SetRecoveryPayload(recovery) instead of assigning them - the handler is a one-line seam.
//   2. `closePending` is written by handleCloseRequest() and read by app.js's reload wrapper on
//      Save_file_response. The wrapper now calls handlePendingCloseSaveResult(payload); the state
//      And the timeout stay private to this module.
//
// Everything else moved as-is: collectSessionState/collectRecoveryState (both read openTabs/activeTab
// And the editor selection), persistSessionNow/persistRecoveryNow with their "only write when the
// JSON changed" guards, restoreSessionForWorkspace with its 20x400ms wait for Monaco,
// AdvanceSessionRestore, finishSessionRestore, handleCloseRequest (the save-all-then-close handshake)
// And handleExternalFileChange (reload vs. conflict prompt).
//
// Two adaptations: `monacoEditor` is a script-scope `let`, so the eleven editor touches go through
// Editor() -> getMonacoEditor(); and `isSplitEditorActive` is another script-scope `let` that the
// Original guarded with `typeof`, which in a module would silently evaluate to false and stop
// Persisting the split state - app.js grew a getIsSplitEditorActive() accessor instead (the same
// Pattern as getMonacoEditor ).
//
// Out of scope on purpose: `savedSessions`/`renderHistorySessions`/`loadSessionIntoChat` are the CHAT
// Session history (modules/agent-chat.js territory), not the editor session - a different feature
// That happens to share the word.

// App.js's sendHost is a script-scope const (a module cannot see it); same pattern as the others.
let hostSend = (action, data) => window.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

// The only way this module touches the editor
function editor() {
  return globalThis.getMonacoEditor ? globalThis.getMonacoEditor() : null;
}

let pendingRecoveryState = null;
let recoveryReady = false;
let recoveryPromptedFor = null;
let restoringSession = null;
let sessionRestoredFor = null;
let closePending = null;

// Seam 1 (): the host handlers used to assign these two directly.
export function setRecoveryPayload(recovery) {
  pendingRecoveryState = recovery || null;
  recoveryReady = !pendingRecoveryState;
}

// Seam 2 (): app.js's reload wrapper reports each save result through this, so
// The pending-close bookkeeping (and its 10s timeout) stays private.
export function handlePendingCloseSaveResult(payload) {
  if (!closePending) return;
  if (payload.error) closePending.onFailed(payload.path);
  else closePending.onSaved(payload.path);
}

// ---------- safe close ----------
async function handleCloseRequest() {
  let dirty = [];
  try {
    dirty = openTabs.filter(t => t.dirty && t.model && !t.model.isDisposed());
  } catch { dirty = []; }

  if (dirty.length === 0) {
    // Nothing to lose: tell the host to close immediately, then persist best-effort.
    sendHost('confirm_close');
    try { persistSessionNow(); persistRecoveryNow(true); } catch (err) { logSwallowed(err, "app.closePersist"); }
    return;
  }

  try {
    sendHost('close_ack');
    persistSessionNow();
    const names = dirty.slice(0, 5).map(t => t.name).join(', ') + (dirty.length > 5 ? `, and ${dirty.length - 5} more` : '');
    const choice = await showConfirmDialog(
      'Unsaved changes',
      `${dirty.length} file${dirty.length === 1 ? ' has' : 's have'} unsaved changes: ${names}.`,
      'Save all and close',
      'Discard and close'
    );

    if (choice === 'alt') {
      sendHost('clear_recovery_state');
      sendHost('confirm_close');
    } else if (choice === true) {
      const waiting = new Set(dirty.map(t => normalizePath(t.path)));
      const timeout = setTimeout(() => {
        closePending = null;
        showToast('Some files did not finish saving. Close was cancelled.', 'error');
      }, 10000);
      closePending = {
        onSaved(path) {
          waiting.delete(normalizePath(path));
          if (waiting.size === 0) finish();
        },
        onFailed() {
          clearTimeout(timeout);
          closePending = null;
          showToast('A file could not be saved. Close was cancelled.', 'error');
        }
      };
      function finish() {
        clearTimeout(timeout);
        closePending = null;
        persistRecoveryNow(true);
        sendHost('confirm_close');
      }
      dirty.forEach(saveTabNow);
    }
  } catch {
    // Never trap the user behind the host's force-close fallback.
    sendHost('confirm_close');
  }
}

// ---------- external changes ----------
async function handleExternalFileChange(payload) {
  if (!payload || !payload.path || typeof payload.content !== 'string') return;
  const tab = openTabs.find(t => normalizePath(t.path) === normalizePath(payload.path));
  if (!tab || !tab.model || tab.model.isDisposed()) return;

  const current = tab.model.getValue();
  if (payload.content === current) return;
  if (payload.content === tab.lastSavedContent) return;

  if (!tab.dirty) {
    const isActive = activeTab === tab && !!editor();
    const view = isActive ? editor().saveViewState() : null;
    tab.suppressDirty = true;
    tab.model.setValue(payload.content);
    tab.suppressDirty = false;
    tab.dirty = false;
    tab.lastSavedContent = payload.content;
    if (view) editor()?.restoreViewState(view);
    renderTabs();
    setActivityStatus(`${tab.name} reloaded from disk`);
    return;
  }

  if (tab.extPromptedFor === payload.content) return;
  tab.extPromptedFor = payload.content;
  const reload = await showConfirmDialog(
    'File changed on disk',
    `${tab.name} was changed outside the editor, and you also have unsaved edits. Reload loses your edits.`,
    'Reload from disk'
  );
  if (reload === true && !tab.model.isDisposed()) {
    tab.suppressDirty = true;
    tab.model.setValue(payload.content);
    tab.suppressDirty = false;
    tab.dirty = false;
    tab.lastSavedContent = payload.content;
    renderTabs();
  }
}

// ---------- session restore ----------
function sessionKey(ws) { return 'voidcode_session_v1:' + normalizePath(ws || ''); }

function collectSessionState() {
  if (!currentWorkspace) return null;
  const tabs = openTabs.filter(t => t.path && t.model && !t.model.isDisposed()).map(t => t.path);
  const cursors = {};
  openTabs.forEach(t => {
    if (!t.path || !t.model || t.model.isDisposed()) return;
    const currentEditor = editor();
    if (t === activeTab && currentEditor) {
      const p = currentEditor.getPosition();
      if (p) t._cursor = { line: p.lineNumber, col: p.column };
    }
    if (t._cursor) cursors[t.path] = t._cursor;
  });
  return {
    tabs,
    active: activeTab ? activeTab.path : null,
    cursors,
    split: globalThis.getIsSplitEditorActive ? globalThis.getIsSplitEditorActive() : false
  };
}

let lastPersistedSession = '';
let lastPersistedRecovery = '';
function persistSessionNow() {
  if (restoringSession) return;
  const state = collectSessionState();
  if (!state) return;
  const json = JSON.stringify(state);
  if (json === lastPersistedSession) return;
  lastPersistedSession = json;
  try { localStorage.setItem(sessionKey(currentWorkspace), json); } catch (err) { logSwallowed(err, "app.persistSession"); }
}

function collectRecoveryState() {
  if (!currentWorkspace) return null;
  const workspaceKey = normalizePath(currentWorkspace).replace(/[\\/]+$/, '') + '\\';
  const buffers = openTabs
    .filter(tab => tab.dirty && tab.path && tab.model && !tab.model.isDisposed())
    .filter(tab => {
      const normalized = normalizePath(tab.path);
      return normalized === normalizePath(currentWorkspace) || normalized.startsWith(workspaceKey);
    })
    .filter(tab => tab.model.getValueLength() <= 4 * 1024 * 1024)
    .map(tab => {
      let cursor = tab._cursor || { line: 1, col: 1 };
      const currentEditor = editor();
      if (tab === activeTab && currentEditor) {
        const position = currentEditor.getPosition();
        if (position) cursor = { line: position.lineNumber, col: position.column };
      }
      return {
        path: tab.path,
        content: tab.model.getValue(),
        cursorLine: cursor.line || 1,
        cursorColumn: cursor.col || 1
      };
    });

  return {
    workspace: currentWorkspace,
    activePath: activeTab?.path || null,
    buffers
  };
}

function persistRecoveryNow(force = false) {
  if (!recoveryReady || restoringSession) return;
  const state = collectRecoveryState();
  if (!state) return;
  const json = JSON.stringify(state);
  if (!force && json === lastPersistedRecovery) return;
  lastPersistedRecovery = json;
  sendHost('save_recovery_state', { state });
}

async function restoreSessionForWorkspace(ws, attempt = 0) {
  if (!ws || sessionRestoredFor === normalizePath(ws)) {
    if (!pendingRecoveryState) recoveryReady = true;
    return;
  }
  if (!editor()) {
    if (attempt < 20) {
      setTimeout(() => restoreSessionForWorkspace(ws, attempt + 1), 400);
    } else {
      recoveryReady = true;
    }
    return;
  }
  sessionRestoredFor = normalizePath(ws);
  if (openTabs.length > 0) {
    pendingRecoveryState = null;
    recoveryReady = true;
    return;
  }

  let state = null;
  try { state = JSON.parse(localStorage.getItem(sessionKey(ws)) || 'null'); } catch (err) { logSwallowed(err, "app.restoreSession"); }

  const recovery = pendingRecoveryState && normalizePath(pendingRecoveryState.workspace) === normalizePath(ws)
    ? pendingRecoveryState
    : null;
  const recoveryBuffers = Array.isArray(recovery?.buffers) ? recovery.buffers : [];
  const recoveryByPath = new Map(recoveryBuffers.map(buffer => [normalizePath(buffer.path), buffer]));
  const sessionTabs = Array.isArray(state?.tabs) ? state.tabs : [];
  const paths = [...new Set([...sessionTabs, ...recoveryBuffers.map(buffer => buffer.path)].filter(Boolean))];

  // Review recovered unsaved buffers before applying them. Restore is the safe default;
  // "Discard" reopens the files from disk without applying the recovered content.
  let discardRecovery = false;
  if (recoveryBuffers.length > 0 && recoveryPromptedFor !== normalizePath(ws)) {
    recoveryPromptedFor = normalizePath(ws);
    const names = recoveryBuffers.slice(0, 6).map(b => (b.path || '').split(/[\\/]/).pop()).join(', ')
      + (recoveryBuffers.length > 6 ? `, +${recoveryBuffers.length - 6} more` : '');
    const choice = await showConfirmDialog(
      'Recover unsaved changes',
      `${recoveryBuffers.length} file${recoveryBuffers.length === 1 ? '' : 's'} had unsaved changes when VoidCode last closed: ${names}. Restore them?`,
      'Restore',
      'Discard'
    );
    if (choice === 'alt') {
      discardRecovery = true;
      try { sendHost('clear_recovery_state'); } catch (err) { logSwallowed(err, "app.clearRecoveryState"); }
    }
  }

  if (paths.length === 0) {
    pendingRecoveryState = null;
    recoveryReady = true;
    return;
  }

  restoringSession = {
    remaining: new Set(paths.map(normalizePath)),
    active: recovery?.activePath || state?.active,
    cursors: state?.cursors || {},
    split: !!state?.split,
    recoveryByPath,
    recoveredCount: 0,
    discardRecovery
  };
  paths.forEach(path => sendHost('read_file', { path }));

  // Never stay in "restoring" forever if a file no longer exists
  setTimeout(() => finishSessionRestore(), 4000);
}

function advanceSessionRestore(path, diskContent, _readError) {
  if (!restoringSession) return;
  const cursor = restoringSession.cursors[path] || restoringSession.cursors[Object.keys(restoringSession.cursors).find(k => normalizePath(k) === normalizePath(path))];
  const recovered = restoringSession.recoveryByPath.get(normalizePath(path));
  let tab = openTabs.find(t => normalizePath(t.path) === normalizePath(path));

  if (recovered) {
    if (!tab && editor()) {
      openFileInEditor(path, typeof diskContent === 'string' ? diskContent : '');
      tab = openTabs.find(t => normalizePath(t.path) === normalizePath(path));
    }
    if (tab && tab.model && !tab.model.isDisposed() && !restoringSession.discardRecovery) {
      tab.suppressDirty = true;
      tab.model.setValue(recovered.content || '');
      tab.suppressDirty = false;
      tab.dirty = true;
      tab.lastSavedContent = typeof diskContent === 'string' ? diskContent : '';
      tab.eol = recovered.content?.includes('\r\n') ? 'CRLF' : (recovered.content?.includes('\n') ? 'LF' : 'None');
      tab._cursor = {
        line: recovered.cursorLine || 1,
        col: recovered.cursorColumn || 1
      };
      restoringSession.recoveredCount += 1;
      renderTabs();
    }
  }

  if (tab && cursor) tab._cursor = cursor;
  restoringSession.remaining.delete(normalizePath(path));
  if (restoringSession.remaining.size === 0) finishSessionRestore();
}

function finishSessionRestore() {
  if (!restoringSession) return;
  const r = restoringSession;
  restoringSession = null;
  pendingRecoveryState = null;
  recoveryReady = true;

  const target = openTabs.find(t => r.active && normalizePath(t.path) === normalizePath(r.active)) || openTabs[openTabs.length - 1];
  if (target) {
    setActiveTab(target);
    const currentEditor = editor();
    if (target._cursor && currentEditor) {
      currentEditor.setPosition({ lineNumber: target._cursor.line, column: target._cursor.col });
      currentEditor.revealLineInCenter(target._cursor.line);
    }
  }
  if (r.split && typeof openSplitEditor === 'function' && openTabs.length > 0) openSplitEditor();
  if (r.recoveredCount > 0) {
    showToast(`Recovered ${r.recoveredCount} unsaved file${r.recoveredCount === 1 ? '' : 's'} from the previous session.`, 'success');
  }
  persistSessionNow();
  persistRecoveryNow(true);
}

// Main.js calls this once, before the classic scripts load. The 2s autosave
// Tick and the unload flush used to be installed at app.js's top level; they are installed here so
// Importing the module has no side effects.
export function init() {
  Object.assign(globalThis, {
    persistSessionNow,
    persistRecoveryNow,
    restoreSessionForWorkspace,
    advanceSessionRestore,
    handleCloseRequest,
    handleExternalFileChange,
    setRecoveryPayload,
    handlePendingCloseSaveResult
  });

  setInterval(() => {
    persistSessionNow();
    persistRecoveryNow();
  }, 2000);
  window.addEventListener('beforeunload', () => {
    persistSessionNow();
    persistRecoveryNow(true);
  });

  return {
    persistSessionNow,
    persistRecoveryNow,
    restoreSessionForWorkspace,
    advanceSessionRestore,
    handleCloseRequest,
    handleExternalFileChange,
    setRecoveryPayload,
    handlePendingCloseSaveResult
  };
}