// Bottom-panel view switcher, Problems & Diagnostics list, Tests Runner panel,
// And roving keyboard accessibility layer for list/tree/tab rows.
import { escapeHtml, html, raw, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

function getActiveTab() {
  return globalThis.VoidState?.get?.('activeTab') || null;
}

function getWorkspace() {
  return globalThis.VoidState?.get?.('workspace') || '';
}

function normPath(p) {
  return globalThis.normalizePath ? globalThis.normalizePath(p) : String(p || '').replace(/\//g, '\\').toLowerCase();
}

// ==========================================
// PROBLEMS & BOTTOM PANEL SWITCHER
// ==========================================
let activeBottomView = 'terminal';
let diagnosticsDebounceTimer = null;

export function getActiveBottomView() {
  return activeBottomView;
}

export function setupProblemsPanel() {
  const btnTerm = document.getElementById('btn-view-terminal');
  const btnProb = document.getElementById('btn-view-problems');
  const btnTests = document.getElementById('btn-view-tests');
  const btnDebug = document.getElementById('btn-view-debug');
  const btnRefresh = document.getElementById('btn-refresh-problems');

  btnTerm?.addEventListener('click', () => switchBottomView('terminal'));
  btnProb?.addEventListener('click', () => switchBottomView('problems'));
  btnTests?.addEventListener('click', () => switchBottomView('tests'));
  btnDebug?.addEventListener('click', () => switchBottomView('debug'));
  btnRefresh?.addEventListener('click', () => refreshProblems());
}

export function switchBottomView(view) {
  activeBottomView = view;

  const panelEl = document.getElementById('terminal-panel');
  if (panelEl && panelEl.classList.contains('collapsed')) {
    globalThis.setTerminalCollapsed?.(false, false);
  }

  const btnTerm = document.getElementById('btn-view-terminal');
  const btnProb = document.getElementById('btn-view-problems');
  const btnTests = document.getElementById('btn-view-tests');
  const btnDebug = document.getElementById('btn-view-debug');
  const termHost = document.getElementById('terminal-host');
  const termControls = /** @type {HTMLElement | null} */ (document.querySelector('.terminal-controls-stage'));
  const termTabs = document.getElementById('terminal-tab-strip');
  const probView = document.getElementById('problems-panel-view');
  const testsView = document.getElementById('tests-panel-view');
  const debugView = document.getElementById('debug-panel-view');

  btnTerm?.classList.toggle('is-active', view === 'terminal');
  btnTerm?.setAttribute('aria-selected', view === 'terminal' ? 'true' : 'false');
  btnProb?.classList.toggle('is-active', view === 'problems');
  btnProb?.setAttribute('aria-selected', view === 'problems' ? 'true' : 'false');
  btnTests?.classList.toggle('is-active', view === 'tests');
  btnTests?.setAttribute('aria-selected', view === 'tests' ? 'true' : 'false');
  btnDebug?.classList.toggle('is-active', view === 'debug');
  btnDebug?.setAttribute('aria-selected', view === 'debug' ? 'true' : 'false');

  panelEl?.setAttribute('data-active-view', view);

  const consoleEl = document.getElementById('void-console');
  const isTerminalView = view !== 'problems' && view !== 'tests' && view !== 'debug';

  if (consoleEl) consoleEl.style.display = isTerminalView ? 'flex' : 'none';
  if (termHost) termHost.style.display = isTerminalView ? 'block' : 'none';
  if (termControls) termControls.style.display = isTerminalView ? 'flex' : 'none';
  if (termTabs) termTabs.style.display = isTerminalView ? 'flex' : 'none';

  if (probView) probView.style.display = view === 'problems' ? 'flex' : 'none';
  if (testsView) testsView.style.display = view === 'tests' ? 'flex' : 'none';
  if (debugView) debugView.style.display = view === 'debug' ? 'flex' : 'none';

  if (view === 'problems') {
    refreshProblems();
  } else if (view === 'debug') {
    const dbgInput = document.getElementById('debug-console-input');
    if (dbgInput) setTimeout(() => dbgInput.focus(), 60);
  } else if (isTerminalView) {
    globalThis.fitActiveTerminal?.();
    globalThis.focusActiveTerminal?.();
    if (globalThis.VoidConsole) globalThis.VoidConsole.focus();
  }
}

// ==========================================
// TESTS RUNNER PANEL
// ==========================================
let currentTestResults = null;
let isTestRunning = false;
let failedTestNames = [];
let testOutputUpdateTimer = null;
let latestTestOutputLine = '';
let activeTestRunId = '';

export function getLastTestResults() {
  return currentTestResults;
}

export function setupTestsPanel() {
  const btnRunAll = document.getElementById('btn-run-all-tests');
  const btnRunFailed = document.getElementById('btn-run-failed-tests');
  const btnCancel = document.getElementById('btn-cancel-tests');

  btnRunAll?.addEventListener('click', () => {
    if (isTestRunning) return;
    sendHost('run_tests');
  });

  btnRunFailed?.addEventListener('click', () => {
    if (isTestRunning || failedTestNames.length === 0) return;
    sendHost('run_tests', { filter: failedTestNames.join('|') });
  });

  btnCancel?.addEventListener('click', () => {
    sendHost('cancel_tests');
  });
}

export function handleTestRunnerDetected(payload) {
  const badge = document.getElementById('tests-runner-badge');
  if (badge) {
    badge.textContent = payload.hasRunner ? payload.runner : 'No Runner';
  }
}

export function handleTestRunStarted(payload = {}) {
  isTestRunning = true;
  activeTestRunId = payload.runId || '';
  const summaryEl = document.getElementById('tests-summary-text');
  const btnRunAll = document.getElementById('btn-run-all-tests');
  const btnCancel = document.getElementById('btn-cancel-tests');

  if (summaryEl) summaryEl.textContent = 'Running tests...';
  if (btnRunAll) btnRunAll.style.display = 'none';
  if (btnCancel) btnCancel.style.display = 'inline-flex';
}

export function handleTestOutputLine(payload = {}) {
  if (payload.runId && payload.runId !== activeTestRunId) return;
  latestTestOutputLine = String(payload.line || '').trim();
  if (!latestTestOutputLine || testOutputUpdateTimer) return;
  testOutputUpdateTimer = setTimeout(() => {
    testOutputUpdateTimer = null;
    if (!isTestRunning) return;
    const summary = document.getElementById('tests-summary-text');
    if (summary) {
      const compact = latestTestOutputLine.length > 120
        ? `${latestTestOutputLine.slice(0, 117)}\u2026`
        : latestTestOutputLine;
      summary.textContent = `Running \u00b7 ${compact}`;
      summary.title = latestTestOutputLine;
    }
  }, 100);
}

export function handleTestRunCancelled(payload = {}) {
  if (payload.runId && payload.runId !== activeTestRunId) return;
  isTestRunning = false;
  activeTestRunId = '';
  clearTimeout(testOutputUpdateTimer);
  testOutputUpdateTimer = null;
  const summary = document.getElementById('tests-summary-text');
  const runAll = document.getElementById('btn-run-all-tests');
  const cancel = document.getElementById('btn-cancel-tests');
  if (summary) {
    summary.textContent = 'Test run cancelled.';
    summary.removeAttribute('title');
  }
  if (runAll) runAll.style.display = 'inline-flex';
  if (cancel) cancel.style.display = 'none';
}

export function handleTestRunCompleted(payload) {
  if (payload.runId && payload.runId !== activeTestRunId) return;
  isTestRunning = false;
  activeTestRunId = '';
  clearTimeout(testOutputUpdateTimer);
  testOutputUpdateTimer = null;
  currentTestResults = payload;

  const btnRunAll = document.getElementById('btn-run-all-tests');
  const btnCancel = document.getElementById('btn-cancel-tests');
  const btnRunFailed = document.getElementById('btn-run-failed-tests');
  const summaryEl = document.getElementById('tests-summary-text');
  const badgeEl = document.getElementById('tests-count-badge');
  const runnerBadge = document.getElementById('tests-runner-badge');
  const listEl = document.getElementById('tests-list');

  if (btnRunAll) btnRunAll.style.display = 'inline-flex';
  if (btnCancel) btnCancel.style.display = 'none';

  failedTestNames = [];

  const total = payload.total || 0;
  const passed = payload.passed || 0;
  const failed = payload.failed || 0;
  const dur = payload.durationMs ? `${(payload.durationMs / 1000).toFixed(2)}s` : '';

  if (summaryEl) {
    summaryEl.textContent = total > 0
      ? `${passed}/${total} passed (${failed} failed) in ${dur}`
      : 'No tests executed.';
  }

  if (badgeEl) {
    badgeEl.textContent = failed > 0 ? `${failed}` : `${total}`;
    badgeEl.style.display = total > 0 ? 'inline-flex' : 'none';
  }

  if (btnRunFailed) {
    btnRunFailed.style.display = failed > 0 ? 'inline-flex' : 'none';
  }
  if (runnerBadge) {
    const format = payload.resultFormat && payload.resultFormat !== 'console'
      ? ` \u00b7 ${payload.resultFormat.toUpperCase()}`
      : ' \u00b7 console fallback';
    runnerBadge.textContent = `${payload.runner || 'Tests'}${format}`;
    runnerBadge.title = payload.command || '';
  }

  if (!listEl) return;
  setHtml(listEl, '');

  const tests = Array.isArray(payload.tests) ? payload.tests : [];
  if (tests.length === 0) {
    setHtml(listEl, html`
      <div class="empty-state">
        <p>${payload.rawOutput || 'No test output received.'}</p>
      </div>
    `);
    return;
  }

  tests.forEach(t => {
    if (t.status === 'failed') {
      failedTestNames.push(t.name);
    }

    const item = document.createElement('div');
    item.className = `test-item ${t.status}`;
    const statusIcon = t.status === 'passed' ? '\u2713' : (t.status === 'failed' ? '\u2715' : '\u2022');
    setHtml(item, html`
      <div class="test-item-left">
        <span class="test-status-glyph" aria-hidden="true">${statusIcon}</span>
        ${t.suite ? raw(`<span class="test-suite-tag">${escapeHtml(t.suite)}</span>`) : ''}
        <span class="test-name">${t.name}</span>
        ${t.error ? raw(`<span class="test-error-hint">(${escapeHtml(t.error)})</span>`) : ''}
      </div>
      <div class="test-item-right">
        ${t.duration ? raw(`<span class="test-duration">${escapeHtml(t.duration)}</span>`) : ''}
        ${t.status === 'failed' ? raw('<button type="button" class="btn btn--accent-soft btn--xs test-fix-btn" title="Send failing test to Agent">Fix with AI</button>') : ''}
      </div>
    `);

    item.querySelector('.test-fix-btn')?.addEventListener('click', (e) => {
      e.stopPropagation();
      const box = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
      if (box) {
        const suiteInfo = t.suite ? `[Suite: ${t.suite}] ` : '';
        const errInfo = t.error ? `\nFailure output: ${t.error}` : '';
        box.value = `Diagnose and fix the failing test ${suiteInfo}"${t.name}".${errInfo}`;
        globalThis.toggleWorkspacePane?.('agent', true);
        box.focus();
      }
    });

    item.addEventListener('click', () => {
      if (t.filePath) {
        globalThis.navigateToFileLocation?.(t.filePath, t.line || 1, 1);
      }
    });

    listEl.appendChild(item);
  });
}

// ==========================================
// DIAGNOSTICS AGGREGATION & JUMP
// ==========================================
export function scheduleDiagnosticsUpdate() {
  clearTimeout(diagnosticsDebounceTimer);
  diagnosticsDebounceTimer = setTimeout(() => {
    refreshProblems();
  }, 350);
}

export function refreshProblems() {
  const listEl = document.getElementById('problems-list');
  const badgeEl = document.getElementById('problems-count-badge');
  const summaryEl = document.getElementById('problems-summary-text');
  if (!listEl) return;

  const problems = [];
  const activeTab = getActiveTab();
  const currentWorkspace = getWorkspace();
  const m = globalThis.monaco;

  if (m && m.editor && m.editor.getModelMarkers) {
    const markers = m.editor.getModelMarkers({});
    markers.forEach(marker => {
      const severity = marker.severity === 8 ? 'error' : marker.severity === 4 ? 'warn' : 'info';
      let filePath = marker.resource?.path || (activeTab ? activeTab.path : 'Active Document');
      if (currentWorkspace && filePath.startsWith(currentWorkspace)) {
        filePath = filePath.substring(currentWorkspace.length).replace(/^[\\/]/, '');
      }
      problems.push({
        severity,
        message: marker.message,
        file: filePath,
        fullPath: marker.resource?.fsPath || marker.resource?.path || (activeTab ? activeTab.path : ''),
        line: marker.startLineNumber,
        col: marker.startColumn
      });
    });
  }

  const terminalErrorSnippet = globalThis.getLastTerminalErrorSnippet?.() || '';
  if (terminalErrorSnippet) {
    const errLines = terminalErrorSnippet.split('\n');
    errLines.forEach(l => {
      const match = l.match(/(?:^|\s)([\w\-./\\]+\.(?:cs|js|ts|py|go|rs))\s*\(([0-9]+),([0-9]+)\)\s*:\s*(error|warning)\s+([A-Za-z0-9]+:\s*.+)/i);
      if (match) {
        problems.push({
          severity: match[4].toLowerCase() === 'error' ? 'error' : 'warn',
          file: match[1],
          fullPath: match[1],
          line: parseInt(match[2], 10),
          col: parseInt(match[3], 10),
          message: match[5].trim()
        });
      }
    });
  }

  if (badgeEl) badgeEl.textContent = `${problems.length}`;
  if (summaryEl) {
    const errCount = problems.filter(p => p.severity === 'error').length;
    const warnCount = problems.filter(p => p.severity === 'warn').length;
    summaryEl.textContent = problems.length === 0
      ? 'No problems detected in workspace'
      : `${errCount} error${errCount === 1 ? '' : 's'}, ${warnCount} warning${warnCount === 1 ? '' : 's'}`;
  }

  renderProblemsList(problems);
}

export function renderProblemsList(problems) {
  const listEl = document.getElementById('problems-list');
  if (!listEl) return;
  setHtml(listEl, '');

  if (problems.length === 0) {
    setHtml(listEl, '<div class="empty-state"><p>No problems have been detected in the current workspace.</p></div>');
    return;
  }

  problems.forEach(p => {
    const div = document.createElement('div');
    div.className = 'problem-item';
    setHtml(div, html`
      <span class="problem-badge badge-${p.severity}">${p.severity === 'error' ? 'Error' : p.severity === 'warn' ? 'Warn' : 'Info'}</span>
      <span class="problem-msg">${p.message}</span>
      <span class="problem-location">${p.file}:${p.line}:${p.col}</span>
      <button type="button" class="btn btn--quiet btn--xs problem-fix-btn" title="Ask VoidAgent to fix this problem">Fix with AI</button>
    `);

    div.querySelector('.problem-fix-btn')?.addEventListener('click', (e) => {
      e.stopPropagation();
      const box = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
      if (box) {
        box.value = `Fix the ${p.severity} in ${p.file} at line ${p.line}, column ${p.col}: "${p.message}"`;
        globalThis.toggleWorkspacePane?.('agent', true);
        box.focus();
      }
    });

    div.addEventListener('click', () => {
      jumpToProblem(p.fullPath, p.line, p.col);
    });

    listEl.appendChild(div);
  });
}

export function jumpToProblem(fullPath, line, col) {
  const activeTab = getActiveTab();
  if (fullPath && (!activeTab || normPath(activeTab.path) !== normPath(fullPath))) {
    // Fixed undeclared `pendingLocation` bug: route through navigateToFileLocation
    // So opening an unloaded file applies the pending cursor position on read_file_response.
    globalThis.navigateToFileLocation?.(fullPath, line, col);
  } else {
    globalThis.jumpToSymbol?.(line, col);
  }
}

// ==========================================
// KEYBOARD & SCREEN READER ACCESSIBILITY LAYER
// ==========================================
const A11Y_ROW_SELECTOR = '.tree-node, .tab-item, .outline-item, .problem-item, .symbol-result-item, .quick-open-item, .ctx-item, .sidebar-section-header, .tab-close-btn';

export function enhanceA11yRows(root) {
  if (!root || root.nodeType !== 1) return;
  const elRoot = /** @type {Element} */ (root);
  const nodes = elRoot.matches?.(A11Y_ROW_SELECTOR) ? [elRoot] : [];
  elRoot.querySelectorAll?.(A11Y_ROW_SELECTOR).forEach(n => nodes.push(n));
  nodes.forEach(el => {
    if (!el.hasAttribute('tabindex')) el.setAttribute('tabindex', el.classList.contains('tab-close-btn') ? '0' : '-1');
    if (!el.hasAttribute('role')) {
      if (el.classList.contains('ctx-item')) el.setAttribute('role', 'menuitem');
      else if (el.classList.contains('tab-item')) el.setAttribute('role', 'tab');
      else el.setAttribute('role', 'button');
    }
  });
  ['file-tree-container', 'tabs-list', 'sidebar-outline-container', 'problems-list'].forEach(id => {
    const group = document.getElementById(id);
    if (!group) return;
    const rows = group.querySelectorAll(A11Y_ROW_SELECTOR);
    if (rows.length && !group.querySelector(`${A11Y_ROW_SELECTOR}[tabindex="0"]:not(.tab-close-btn)`)) {
      for (const r of rows) {
        if (!r.classList.contains('tab-close-btn')) { r.setAttribute('tabindex', '0'); break; }
      }
    }
  });
}

function visibleRows(container, selector) {
  return /** @type {HTMLElement[]} */ (Array.from(container.querySelectorAll(selector)))
    .filter(el => el.offsetParent !== null);
}

export function setupKeyboardA11y() {
  enhanceA11yRows(document.body);
  new MutationObserver((mutations) => {
    for (const m of mutations) m.addedNodes.forEach(enhanceA11yRows);
  }).observe(document.body, { childList: true, subtree: true });

  document.addEventListener('keydown', (e) => {
    const el = e.target;
    if (!(el instanceof HTMLElement) || !el.matches(A11Y_ROW_SELECTOR)) return;

    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      el.click();
      return;
    }

    if (el.classList.contains('tab-item') && (e.key === 'Delete' || (e.ctrlKey && e.key.toLowerCase() === 'w'))) {
      e.preventDefault();
      /** @type {HTMLElement | null} */ (el.querySelector('.tab-close-btn'))?.click();
      return;
    }

    const groupEl = el.closest('#tree-context-menu, #file-tree-container, #tabs-list, #sidebar-outline-container, #problems-list, #symbol-results-list');
    if (!groupEl) return;
    const horizontal = groupEl.id === 'tabs-list';
    const next = horizontal ? 'ArrowRight' : 'ArrowDown';
    const prev = horizontal ? 'ArrowLeft' : 'ArrowUp';
    if (e.key !== next && e.key !== prev && e.key !== 'Home' && e.key !== 'End') return;

    const rowSel = horizontal ? '.tab-item' : A11Y_ROW_SELECTOR.replace(', .tab-close-btn', '');
    const rows = visibleRows(groupEl, rowSel);
    const idx = rows.indexOf(/** @type {HTMLElement} */ (el.closest(rowSel) || el));
    if (idx === -1) return;
    e.preventDefault();
    let target = idx;
    if (e.key === next) target = Math.min(rows.length - 1, idx + 1);
    else if (e.key === prev) target = Math.max(0, idx - 1);
    else if (e.key === 'Home') target = 0;
    else target = rows.length - 1;
    rows.forEach(r => r.setAttribute('tabindex', r === rows[target] ? '0' : '-1'));
    rows[target].focus();
  });

  const badge = document.getElementById('problems-count-badge');
  if (badge) {
    badge.setAttribute('role', 'status');
    badge.setAttribute('aria-live', 'polite');
    badge.setAttribute('aria-label', 'Problems count');
  }
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  globalThis.VoidHostEvents?.register?.({
    test_runner_detected: (payload) => handleTestRunnerDetected(payload),
    test_run_started: (payload) => handleTestRunStarted(payload),
    test_output_line: (payload) => handleTestOutputLine(payload),
    test_run_completed: (payload) => handleTestRunCompleted(payload),
    test_run_cancelled: (payload) => handleTestRunCancelled(payload)
  });
  const api = {
    getActiveBottomView,
    getLastTestResults,
    setupProblemsPanel,
    switchBottomView,
    setupTestsPanel,
    handleTestRunnerDetected,
    handleTestRunStarted,
    handleTestOutputLine,
    handleTestRunCancelled,
    handleTestRunCompleted,
    scheduleDiagnosticsUpdate,
    refreshProblems,
    renderProblemsList,
    jumpToProblem,
    enhanceA11yRows,
    setupKeyboardA11y
  };
  Object.assign(globalThis, api);
  return api;
}
