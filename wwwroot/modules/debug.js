// Debugger & REPL engine: debugger setup, breakpoints, execution line, call stack,
// Variables, watch, debug console, and debug event handlers.
//
// Main.js calls init(), and the entry points are exported + published
// On globalThis for the app.js. The debug_unsupported bridge subscription moved into
// Init() because module bodies run before installBridge() has published VoidBridge.
import { html, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

// ==========================================
// PHASE 7: REAL DEBUGGER & REPL ENGINE
// ==========================================
let debugSessionActive = false;
let debugSessionPaused = false;
let breakpointsByFile = {}; // { [filePath]: [{ line, condition, enabled }] }
let currentCallFrames = [];
let currentVariables = [];
let currentActiveFrameId = null;
let watchExpressions = [];
let monacoBreakpointDecorations = [];
let monacoCurrentLineDecorations = [];

// One typed lookup instead of a cast per call site - the controls this module reads
// By id are inputs, and tsc only knows getElementById returns a plain HTMLElement.
/**
 * @param {string} id
 * @returns {HTMLInputElement | null}
 */
function control(id) {
  return /** @type {HTMLInputElement | null} */ (document.getElementById(id));
}

export function setupDebugger() {
  // Sidebar Run & Debug controls
  control('btn-sidebar-debug-start')?.addEventListener('click', startDebuggingActiveFile);
  control('btn-sidebar-debug-stop')?.addEventListener('click', () => sendHost('debug_stop'));
  control('btn-debug-clear-all-bps')?.addEventListener('click', clearAllBreakpoints);

  // Floating toolbar controls
  control('btn-dbg-continue')?.addEventListener('click', () => sendHost('debug_continue'));
  control('btn-dbg-pause')?.addEventListener('click', () => sendHost('debug_pause'));
  control('btn-dbg-step-over')?.addEventListener('click', () => sendHost('debug_step_over'));
  control('btn-dbg-step-into')?.addEventListener('click', () => sendHost('debug_step_into'));
  control('btn-dbg-step-out')?.addEventListener('click', () => sendHost('debug_step_out'));
  control('btn-dbg-restart')?.addEventListener('click', () => {
    sendHost('debug_stop');
    setTimeout(() => startDebuggingActiveFile(), 400);
  });
  control('btn-dbg-stop')?.addEventListener('click', () => sendHost('debug_stop'));

  // Watch section
  control('btn-add-watch')?.addEventListener('click', async () => {
    const expr = await globalThis.showInputDialog?.('Add Watch Expression', '', 'Add');
    if (expr && !watchExpressions.includes(expr)) {
      watchExpressions.push(expr);
      renderWatchList();
      if (debugSessionPaused) evaluateWatchExpressions();
    }
  });

  // Debug Console input (REPL)
  const dbgInput = control('debug-console-input');
  dbgInput?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      const expr = dbgInput.value.trim();
      if (!expr) return;
      appendDebugConsoleLine(`> ${expr}`, 'console');
      dbgInput.value = '';
      sendHost('debug_evaluate', { expression: expr, frameId: currentActiveFrameId });
    }
  });

  // Section headers collapse toggle
  ['variables', 'watch', 'callstack', 'breakpoints'].forEach(sec => {
    control(`debug-head-${sec}`)?.addEventListener('click', () => {
      const body = control(`debug-${sec}-list`) || control(`debug-breakpoints-list`);
      if (body) {
        body.style.display = body.style.display === 'none' ? 'flex' : 'none';
      }
    });
  });
}

// Honest debug capabilities (): starts with Node/V8 enabled and dynamically
// Unlocks .NET (netcoredbg) and Python (debugpy) when capabilities_response confirms them.
const DEBUG_NODE_EXTS = ['.js', '.mjs', '.cjs', '.ts'];
const DEBUG_RUNTIME_LABELS = { cs: 'C#', dll: '.NET', py: 'Python', java: 'Java', go: 'Go', rs: 'Rust', cpp: 'C++', c: 'C' };
let debugRuntimeCaps = { node: true, dotnet: false, python: false };

export function applyDebugCapabilities(caps) {
  if (!caps || typeof caps !== 'object') return;
  debugRuntimeCaps = {
    node: caps.node !== false,
    dotnet: !!caps.dotnet,
    python: !!caps.python
  };
  syncDebugAvailability();
}

export function debugRuntimeForPath(path) {
  const m = String(path || '').match(/\.([^.\\/]+)$/);
  return m ? m[1].toLowerCase() : '';
}

export function debugSupportForPath(path) {
  const ext = debugRuntimeForPath(path);
  if (DEBUG_NODE_EXTS.includes('.' + ext)) {
    return debugRuntimeCaps.node
      ? { supported: true, runtime: 'node', reason: '' }
      : { supported: false, runtime: 'node', reason: 'Node.js is not installed on PATH.' };
  }
  if (ext === 'cs' || ext === 'dll') {
    return debugRuntimeCaps.dotnet
      ? { supported: true, runtime: 'dotnet', reason: '' }
      : { supported: false, runtime: 'dotnet', reason: 'Debugging for C# is not available yet (install netcoredbg).' };
  }
  if (ext === 'py') {
    return debugRuntimeCaps.python
      ? { supported: true, runtime: 'python', reason: '' }
      : { supported: false, runtime: 'python', reason: 'Debugging for Python is not available yet (install debugpy).' };
  }
  const label = DEBUG_RUNTIME_LABELS[ext] || (ext ? ext.toUpperCase() : 'this file type');
  return { supported: false, runtime: ext || 'unknown', reason: `Debugging for ${label} is not available yet.` };
}

export function syncDebugAvailability() {
  const btn = control('btn-sidebar-debug-start');
  if (!btn) return;
  if (debugSessionActive) {
    btn.disabled = false;
    btn.setAttribute('aria-disabled', 'false');
    btn.title = 'A debug session is active';
    return;
  }
  const tab = window.VoidState.get('activeTab'); // activeTab lives in the store
  const support = tab ? debugSupportForPath(tab.path) : { supported: false, reason: 'Open a file to debug.' };
  btn.disabled = !support.supported;
  btn.setAttribute('aria-disabled', support.supported ? 'false' : 'true');
  btn.title = support.supported ? 'Start debugging (F5)' : support.reason;
}

export function startDebuggingActiveFile() {
  const tab = window.VoidState.get('activeTab');
  if (!tab) {
    globalThis.showToast?.('Open a runnable file (JS or TS) to debug.', 'info');
    return;
  }
  const support = debugSupportForPath(tab.path);
  if (!support.supported) {
    globalThis.switchBottomView?.('debug');
    globalThis.toggleWorkspacePane?.('terminal', true);
    appendDebugConsoleLine(`[Debugger] ${support.reason}`, 'stderr');
    globalThis.showToast?.(support.reason, 'info');
    return;
  }

  const file = tab.path;
  const targetLabel = control('debug-target-file');
  if (targetLabel) targetLabel.textContent = file.split(/[\\/]/).pop();

  appendDebugConsoleLine(`[Debugger] Launching ${file}...`, 'console');
  globalThis.switchBottomView?.('debug');
  globalThis.toggleWorkspacePane?.('terminal', true);

  sendHost('debug_start', {
    file: file,
    stopOnEntry: false
  });
}

export function handleDebugUnsupported(payload) {
  debugSessionActive = false;
  debugSessionPaused = false;
  const reason = (payload && payload.reason) ||
    `Debugging for ${(payload && payload.runtime) || 'this runtime'} is not available yet.`;

  appendDebugConsoleLine(`[Debugger] ${reason}`, 'stderr');
  globalThis.showToast?.(reason, 'info');

  const toolbar = control('debug-floating-toolbar');
  if (toolbar) toolbar.style.display = 'none';
  const btnStart = control('btn-sidebar-debug-start');
  const btnStop = control('btn-sidebar-debug-stop');
  if (btnStart) btnStart.style.display = 'inline-flex';
  if (btnStop) btnStop.style.display = 'none';
  syncDebugAvailability();
}

export function toggleEditorBreakpoint(line, opts = {}) {
  const tab = window.VoidState.get('activeTab');
  if (!tab || tab.isVirtual) return;
  if (!opts?.forceBreakpoint && globalThis.handleTestGutterLineClick?.(line)) return;
  const path = tab.path;
  if (!breakpointsByFile[path]) breakpointsByFile[path] = [];

  const existingIdx = breakpointsByFile[path].findIndex(b => b.line === line);
  if (existingIdx !== -1) {
    breakpointsByFile[path].splice(existingIdx, 1);
  } else {
    breakpointsByFile[path].push({ line, condition: null, enabled: true });
  }

  renderEditorBreakpoints();
  renderBreakpointsList();
  sendHost('debug_toggle_breakpoint', { file: path, line });
}

export function clearAllBreakpoints() {
  breakpointsByFile = {};
  renderEditorBreakpoints();
  renderBreakpointsList();
  sendHost('debug_clear_breakpoints', {});
  globalThis.showToast?.('Cleared all breakpoints', 'info');
}

export function renderEditorBreakpoints() {
  const editor = globalThis.getMonacoEditor?.();
  const tab = window.VoidState.get('activeTab');
  if (!editor || !tab) return;
  const bps = breakpointsByFile[tab.path] || [];
  const newDecorations = bps.filter(b => b.enabled).map(bp => ({
    range: new monaco.Range(bp.line, 1, bp.line, 1),
    options: {
      isWholeLine: false,
      glyphMarginClassName: 'debug-bp-glyph',
      glyphMarginHoverMessage: { value: bp.condition ? `Breakpoint (${bp.condition})` : `Breakpoint line ${bp.line}` }
    }
  }));
  monacoBreakpointDecorations = editor.deltaDecorations(monacoBreakpointDecorations, newDecorations);
}

export function renderCurrentExecutionLine(file, line) {
  const editor = globalThis.getMonacoEditor?.();
  if (!editor) return;
  if (!file || !line) {
    monacoCurrentLineDecorations = editor.deltaDecorations(monacoCurrentLineDecorations, []);
    return;
  }

  // If paused file isn't open, open it
  const tab = window.VoidState.get('activeTab');
  if (!tab || globalThis.normalizePath?.(tab.path) !== globalThis.normalizePath?.(file)) {
    sendHost('read_file', { path: file });
    setTimeout(() => renderCurrentExecutionLine(file, line), 150);
    return;
  }

  const newDecorations = [{
    range: new monaco.Range(line, 1, line, 1),
    options: {
      isWholeLine: true,
      className: 'debug-current-line-bg',
      glyphMarginClassName: 'debug-current-glyph'
    }
  }];
  monacoCurrentLineDecorations = editor.deltaDecorations(monacoCurrentLineDecorations, newDecorations);
  editor.revealLineInCenter(line);
}

export function renderBreakpointsList() {
  const container = control('debug-breakpoints-list');
  const countBadge = control('debug-bp-count');
  if (!container) return;

  const allBps = [];
  for (const [file, bps] of Object.entries(breakpointsByFile)) {
    bps.forEach(b => allBps.push({ file, ...b }));
  }

  if (countBadge) countBadge.textContent = String(allBps.length);

  if (allBps.length === 0) {
    setHtml(container, '<div class="debug-empty-state">No breakpoints</div>');
    return;
  }

  setHtml(container, '');
  allBps.forEach(bp => {
    const item = document.createElement('div');
    item.className = 'debug-bp-item';
    const fileName = bp.file.split(/[\\/]/).pop();
    setHtml(item, html`
      <input type="checkbox" class="debug-bp-check" ${bp.enabled ? 'checked' : ''} />
      <span class="debug-bp-loc">Ln ${bp.line}</span>
      <span class="debug-bp-file" title="${bp.file}">${fileName}</span>
      <span class="val-badge debug-bp-cond" role="button" title="Edit breakpoint condition" style="cursor: pointer;">${bp.condition ? `if ${bp.condition}` : '?'}</span>
      <span class="remove" role="button" aria-label="Remove breakpoint" style="cursor: pointer; opacity: 0.75;">\u00d7</span>
    `);

    item.querySelector('.debug-bp-check').addEventListener('click', (e) => {
      e.stopPropagation();
      bp.enabled = /** @type {HTMLInputElement} */ (e.target).checked;
      renderEditorBreakpoints();
      sendHost('debug_set_breakpoints', { file: bp.file, breakpoints: breakpointsByFile[bp.file] });
    });

    item.querySelector('.debug-bp-cond')?.addEventListener('click', async (e) => {
      e.stopPropagation();
      if (typeof globalThis.showInputDialog !== 'function') return;
      const cond = await globalThis.showInputDialog(`Breakpoint condition (Ln ${bp.line})`, bp.condition || '', 'Save');
      if (cond === null || cond === undefined) return;
      const target = (breakpointsByFile[bp.file] || []).find(b => b.line === bp.line);
      if (target) {
        target.condition = cond.trim() || null;
        renderEditorBreakpoints();
        renderBreakpointsList();
        sendHost('debug_set_breakpoints', { file: bp.file, breakpoints: breakpointsByFile[bp.file] });
      }
    });

    item.querySelector('.remove').addEventListener('click', (e) => {
      e.stopPropagation();
      toggleEditorBreakpoint(bp.line, { forceBreakpoint: true });
    });

    item.addEventListener('click', () => {
      sendHost('read_file', { path: bp.file });
      setTimeout(() => {
        const editor = globalThis.getMonacoEditor?.();
        if (editor) editor.revealLineInCenter(bp.line);
      }, 100);
    });

    container.appendChild(item);
  });
}

export function renderCallStack(frames) {
  currentCallFrames = frames || [];
  const container = control('debug-callstack-list');
  const countBadge = control('debug-stack-count');
  if (!container) return;

  if (countBadge) countBadge.textContent = String(currentCallFrames.length);

  if (currentCallFrames.length === 0) {
    setHtml(container, '<div class="debug-empty-state">Not running</div>');
    return;
  }

  setHtml(container, '');
  currentCallFrames.forEach((frame, idx) => {
    const item = document.createElement('div');
    item.className = 'debug-stack-item' + (idx === 0 ? ' is-active-frame' : '');
    const fileName = frame.FilePath.split(/[\\/]/).pop();
    setHtml(item, html`
      <span class="debug-stack-fn">${frame.Name || '(anonymous)'}</span>
      <span class="debug-stack-loc">${fileName}:${frame.Line}</span>
    `);

    item.addEventListener('click', () => {
      container.querySelectorAll('.debug-stack-item').forEach((el, i) => el.classList.toggle('is-active-frame', i === idx));
      currentActiveFrameId = frame.InternalFrameId;
      renderCurrentExecutionLine(frame.FilePath, frame.Line);
    });

    container.appendChild(item);
  });

  if (currentCallFrames.length > 0) {
    currentActiveFrameId = currentCallFrames[0].InternalFrameId;
    renderCurrentExecutionLine(currentCallFrames[0].FilePath, currentCallFrames[0].Line);
  }
}

function createVariableElement(v, depth = 0) {
  const wrap = document.createElement('div');
  const item = document.createElement('div');
  item.className = 'debug-var-item';
  if (depth > 0) item.style.paddingLeft = `${8 + depth * 12}px`;
  const ref = Number(v.VariablesReference ?? v.variablesReference ?? 0);
  const name = v.Name ?? v.name ?? '';
  const val = v.Value ?? v.value ?? '';
  const type = v.Type ?? v.type ?? '';
  if (ref > 0) wrap.dataset.varRef = String(ref);
  setHtml(item, html`
    ${ref > 0 ? html`<span class="debug-var-chevron" aria-hidden="true">\u25b8</span>` : ''}
    <span class="debug-var-name">${name}:</span>
    <span class="debug-var-val" title="${val}">${val}</span>
    <span class="debug-var-type">${type}</span>
  `);

  if (ref > 0) {
    item.style.cursor = 'pointer';
    item.addEventListener('click', (e) => {
      e.stopPropagation();
      const childBox = /** @type {HTMLElement | null} */ (wrap.querySelector(':scope > .debug-var-children'));
      const chev = item.querySelector('.debug-var-chevron');
      if (childBox) {
        const open = childBox.style.display !== 'none';
        childBox.style.display = open ? 'none' : 'block';
        if (chev) chev.textContent = open ? '\u25b8' : '\u25be';
        return;
      }
      if (chev) chev.textContent = '\u25be';
      sendHost('debug_get_variables', { variablesReference: ref });
    });
  }
  wrap.appendChild(item);
  wrap.dataset.varDepth = String(depth);
  return wrap;
}

export function renderVariables(vars, parentRef = 0) {
  const container = control('debug-variables-list');
  const countBadge = control('debug-var-count');
  if (!container) return;

  if (parentRef && parentRef > 0) {
    const parentWrap = /** @type {HTMLElement | null} */ (container.querySelector(`[data-var-ref="${parentRef}"]`));
    if (parentWrap) {
      let childBox = /** @type {HTMLElement | null} */ (parentWrap.querySelector(':scope > .debug-var-children'));
      if (!childBox) {
        childBox = document.createElement('div');
        childBox.className = 'debug-var-children';
        parentWrap.appendChild(childBox);
      }
      setHtml(childBox, '');
      const depth = Number(parentWrap.dataset.varDepth || 0) + 1;
      (vars || []).forEach(v => childBox.appendChild(createVariableElement(v, depth)));
      return;
    }
  }

  currentVariables = vars || [];
  if (countBadge) countBadge.textContent = String(currentVariables.length);

  if (currentVariables.length === 0) {
    setHtml(container, '<div class="debug-empty-state">No local variables</div>');
    return;
  }

  setHtml(container, '');
  currentVariables.forEach(v => {
    container.appendChild(createVariableElement(v, 0));
  });
}

export function renderWatchList() {
  const container = control('debug-watch-list');
  if (!container) return;

  if (watchExpressions.length === 0) {
    setHtml(container, '<div class="debug-empty-state">No watch expressions</div>');
    return;
  }

  setHtml(container, '');
  watchExpressions.forEach(expr => {
    const item = document.createElement('div');
    item.className = 'debug-watch-item';
    setHtml(item, html`
      <span class="debug-var-name">${expr}:</span>
      <span class="debug-var-val" id="watch-val-${expr}">evaluating\u2026</span>
      <span class="remove" role="button" aria-label="Remove watch expression" style="cursor: pointer; opacity: 0.75; margin-left: auto;">\u00d7</span>
    `);

    item.querySelector('.remove').addEventListener('click', (e) => {
      e.stopPropagation();
      watchExpressions = watchExpressions.filter(x => x !== expr);
      renderWatchList();
    });

    container.appendChild(item);
  });
}

export function evaluateWatchExpressions() {
  watchExpressions.forEach(expr => {
    sendHost('debug_evaluate', { expression: expr, frameId: currentActiveFrameId });
  });
}

export function appendDebugConsoleLine(text, category = 'stdout') {
  const container = control('debug-console-output');
  if (!container) return;

  const line = document.createElement('div');
  line.className = `debug-log-line debug-log-line--${category}`;
  line.textContent = text;
  container.appendChild(line);
  container.scrollTop = container.scrollHeight;
}

export function handleDebugStarted(payload) {
  debugSessionActive = true;
  debugSessionPaused = false;
  const toolbar = control('debug-floating-toolbar');
  if (toolbar) toolbar.style.display = 'flex';

  const btnStart = control('btn-sidebar-debug-start');
  const btnStop = control('btn-sidebar-debug-stop');
  if (btnStart) btnStart.style.display = 'none';
  if (btnStop) btnStop.style.display = 'inline-flex';

  control('btn-dbg-continue').style.display = 'none';
  control('btn-dbg-pause').style.display = 'inline-flex';

  globalThis.showToast?.(`Debugging ${payload.target || 'started'}`, 'info');
}

export function handleDebugPaused(payload) {
  debugSessionPaused = true;
  control('btn-dbg-continue').style.display = 'inline-flex';
  control('btn-dbg-pause').style.display = 'none';

  renderCallStack(payload.frames || []);
  renderVariables(payload.variables || []);
  evaluateWatchExpressions();
  globalThis.switchSidebarView?.('debug');
}

export function handleDebugResumed() {
  debugSessionPaused = false;
  control('btn-dbg-continue').style.display = 'none';
  control('btn-dbg-pause').style.display = 'inline-flex';
  renderCurrentExecutionLine(null, 0);
}

export function handleDebugStopped() {
  debugSessionActive = false;
  debugSessionPaused = false;
  const toolbar = control('debug-floating-toolbar');
  if (toolbar) toolbar.style.display = 'none';

  const btnStart = control('btn-sidebar-debug-start');
  const btnStop = control('btn-sidebar-debug-stop');
  if (btnStart) btnStart.style.display = 'inline-flex';
  if (btnStop) btnStop.style.display = 'none';

  renderCurrentExecutionLine(null, 0);
  renderCallStack([]);
  renderVariables([]);
}

// The host reports unimplemented runtimes through a single debug_unsupported event. Route it
// Here (debug.js owns the debug domain) so app.js's message switch stays untouched.
// Installed from init() - a module body runs before installBridge() publishes
// Window.VoidBridge, so subscribing at import time would silently never happen.
function subscribeHostEvents() {
  if (window.VoidBridge && typeof window.VoidBridge.subscribe === 'function') {
    window.VoidBridge.subscribe((action, payload) => {
      if (action === 'debug_unsupported') handleDebugUnsupported(payload);
      else if (action === 'capabilities_response' && payload && payload.debug) applyDebugCapabilities(payload.debug);
    });
  }
}

// The breakpoints cache is this module's state.
export function handleDebugBreakpoints(payload = {}) {
  if (payload.file) {
    breakpointsByFile[payload.file] = payload.breakpoints || [];
  } else {
    breakpointsByFile = {};
  }
  renderEditorBreakpoints();
  renderBreakpointsList();
}

// The palette's F5 command needs to know whether a session is live.
export function debugStartOrContinue() {
  if (debugSessionActive) sendHost('debug_continue');
  else startDebuggingActiveFile();
}

// Main.js calls this once; the entry points go on globalThis for the classic app.js
// (bridge switch, palette, editor gutter and boot).
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  subscribeHostEvents();
  Object.assign(globalThis, {
    setupDebugger,
    debugRuntimeForPath,
    debugSupportForPath,
    applyDebugCapabilities,
    syncDebugAvailability,
    startDebuggingActiveFile,
    handleDebugUnsupported,
    toggleEditorBreakpoint,
    clearAllBreakpoints,
    renderEditorBreakpoints,
    renderCurrentExecutionLine,
    renderBreakpointsList,
    renderCallStack,
    renderVariables,
    renderWatchList,
    evaluateWatchExpressions,
    appendDebugConsoleLine,
    handleDebugStarted,
    handleDebugPaused,
    handleDebugResumed,
    handleDebugStopped,
    handleDebugBreakpoints,
    debugStartOrContinue
  });
  return { setupDebugger, handleDebugStarted, handleDebugPaused, handleDebugResumed, handleDebugStopped, handleDebugBreakpoints, debugStartOrContinue, toggleEditorBreakpoint, clearAllBreakpoints, renderEditorBreakpoints, renderBreakpointsList, renderVariables, appendDebugConsoleLine, syncDebugAvailability, startDebuggingActiveFile };
}

