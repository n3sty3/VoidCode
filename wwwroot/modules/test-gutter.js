// Clickable In-Editor Gutter Test Runner for Monaco:
// Detects C# ([Fact]/[Theory]/[Test]/[TestMethod]), JS/TS (test/it), Python (def test_),
// Rust (#[test]), and Go (func Test) cases, renders clickable play/pass/fail margin glyphs,
// runs individual tests via run_tests { filter }, and shows inline failure popovers with AI fix.
import { html, raw, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);
let testGutterDecorations = [];
/** @type {Array<{ line: number, name: string, suite: string, filter: string, kind: string, key: string }>} */
let detectedTests = [];
/** @type {Map<string, { status: string, duration?: string, error?: string, rawOutput?: string }>} */
const testResultsByKey = new Map();
let activeRunningKey = '';
let activeRunningFilePath = '';
let scanTimer = null;
let popoverEl = null;
let modelSub = null;
let boundModelUri = '';

function getEditor() {
  return globalThis.getMonacoEditor?.() || null;
}

function normPath(p) {
  return globalThis.normalizePath ? globalThis.normalizePath(p) : String(p || '').replace(/\//g, '\\').toLowerCase();
}

function makeTestKey(filePath, name) {
  return `${normPath(filePath)}::${String(name || '').toLowerCase()}`;
}

/**
 * Pure scanner exported for unit tests as well as runtime Monaco scanning.
 * @param {string} filePath
 * @param {string} sourceText
 */
export function detectTestsInSource(filePath, sourceText) {
  const pathStr = String(filePath || '');
  const text = String(sourceText || '');
  if (!pathStr || !text) return [];

  const ext = (pathStr.split('.').pop() || '').toLowerCase();
  const base = pathStr.split(/[\\/]/).pop() || '';
  const lines = text.split(/\r?\n/);
  /** @type {Array<{ line: number, name: string, suite: string, filter: string, kind: string, key: string }>} */
  const found = [];

  if (ext === 'cs') {
    let currentClass = '';
    for (let i = 0; i < lines.length; i++) {
      const ln = lines[i];
      const clsMatch = ln.match(/\b(?:class|record|struct)\s+([A-Za-z_]\w*)/);
      if (clsMatch) currentClass = clsMatch[1];

      if (/^\s*\[(?:Fact|Theory|Test|TestMethod|SkippableFact|StaFact)\b/.test(ln)) {
        for (let j = i + 1; j < Math.min(lines.length, i + 8); j++) {
          const cand = lines[j].trim();
          if (!cand || cand.startsWith('[') || cand.startsWith('//')) continue;
          const fnMatch = cand.match(/\b([A-Za-z_]\w*)\s*(?:<[^>]+>)?\s*\(/);
          if (fnMatch && !['if', 'for', 'while', 'switch', 'catch', 'using'].includes(fnMatch[1])) {
            const name = fnMatch[1];
            const filter = currentClass ? `${currentClass}.${name}` : name;
            found.push({ line: j + 1, name, suite: currentClass, filter, kind: 'csharp', key: makeTestKey(pathStr, name) });
          }
          break;
        }
      }
    }
  } else if (['js', 'ts', 'jsx', 'tsx', 'mjs', 'cjs'].includes(ext)) {
    const isTestFile = /\.(test|spec)\.[mc]?[jt]sx?$/i.test(base) || /[\\/]tests?[\\/]/i.test(pathStr) || /\b(?:test|it)\s*\(\s*['"`]/.test(text);
    if (isTestFile) {
      for (let i = 0; i < lines.length; i++) {
        const m = lines[i].match(/^\s*(?:test|it)(?:\.(?:only|skip|concurrent|todo))?\s*\(\s*(['"`])([^'"`]{1,160})\1/);
        if (m) {
          const name = m[2].trim();
          if (name) {
            found.push({ line: i + 1, name, suite: base, filter: name, kind: 'js', key: makeTestKey(pathStr, name) });
          }
        }
      }
    }
  } else if (ext === 'py') {
    let currentClass = '';
    for (let i = 0; i < lines.length; i++) {
      const ln = lines[i];
      const clsMatch = ln.match(/^class\s+(Test\w*)\b/);
      if (clsMatch) currentClass = clsMatch[1];
      else if (/^\S/.test(ln) && !ln.startsWith('#') && !ln.startsWith('@')) currentClass = '';

      const fnMatch = ln.match(/^\s*(?:async\s+)?def\s+(test_\w+)\s*\(/);
      if (fnMatch) {
        const name = fnMatch[1];
        found.push({ line: i + 1, name, suite: currentClass, filter: name, kind: 'python', key: makeTestKey(pathStr, name) });
      }
    }
  } else if (ext === 'rs') {
    for (let i = 0; i < lines.length; i++) {
      if (/^\s*#\[(?:(?:tokio|async_std|actix_rt)::)?test(?:\([^)]*\))?\]/.test(lines[i])) {
        for (let j = i + 1; j < Math.min(lines.length, i + 6); j++) {
          const cand = lines[j].trim();
          if (!cand || cand.startsWith('#[') || cand.startsWith('//')) continue;
          const fnMatch = cand.match(/\bfn\s+([A-Za-z_]\w*)\s*\(/);
          if (fnMatch) {
            const name = fnMatch[1];
            found.push({ line: j + 1, name, suite: base, filter: name, kind: 'rust', key: makeTestKey(pathStr, name) });
          }
          break;
        }
      }
    }
  } else if (ext === 'go' && /_test\.go$/i.test(base)) {
    for (let i = 0; i < lines.length; i++) {
      const m = lines[i].match(/^func\s+(Test[A-Za-z0-9_]+)\s*\(/);
      if (m) {
        const name = m[1];
        found.push({ line: i + 1, name, suite: base, filter: `^${name}$`, kind: 'go', key: makeTestKey(pathStr, name) });
      }
    }
  }

  return found;
}

function closePopover() {
  if (popoverEl) popoverEl.style.display = 'none';
}

function ensurePopover() {
  if (popoverEl && document.body.contains(popoverEl)) return popoverEl;
  popoverEl = document.createElement('div');
  popoverEl.id = 'test-gutter-popover';
  popoverEl.className = 'glass glass--float test-gutter-popover';
  popoverEl.style.display = 'none';
  document.body.appendChild(popoverEl);

  document.addEventListener('mousedown', (e) => {
    if (!popoverEl || popoverEl.style.display === 'none') return;
    if (!popoverEl.contains(/** @type {Node} */ (e.target))) closePopover();
  }, true);
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && popoverEl && popoverEl.style.display !== 'none') closePopover();
  });
  return popoverEl;
}

function showResultPopover(testItem, res) {
  const pop = ensurePopover();
  const status = res?.status || 'idle';
  const badgeText = status === 'passed' ? 'PASSED' : (status === 'failed' ? 'FAILED' : 'READY');
  const badgeCls = status === 'passed' ? 'is-pass' : (status === 'failed' ? 'is-fail' : '');
  const details = res?.error || (status === 'passed' ? `Test passed${res?.duration ? ` in ${res.duration}` : ''}.` : 'Click Re-run to execute this test.');

  setHtml(pop, html`
    <div class="tg-pop-head">
      <div class="tg-pop-title">
        <span class="tg-pop-badge ${badgeCls}">${badgeText}</span>
        <strong>${testItem.name}</strong>
      </div>
      <div class="tg-pop-actions">
        <button type="button" class="btn btn--primary btn--xs" data-tg-act="rerun">Re-run</button>
        ${status === 'failed' ? raw('<button type="button" class="btn btn--accent-soft btn--xs" data-tg-act="fix-ai">Fix with AI</button>') : ''}
        <button type="button" class="btn btn--quiet btn--xs" data-tg-act="close">&times;</button>
      </div>
    </div>
    <pre class="tg-pop-body">${details}</pre>
  `);

  pop.querySelector('[data-tg-act="rerun"]')?.addEventListener('click', () => {
    closePopover();
    runSingleTest(testItem);
  });
  pop.querySelector('[data-tg-act="fix-ai"]')?.addEventListener('click', () => {
    closePopover();
    const tab = globalThis.VoidState?.get?.('activeTab');
    const box = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
    if (box) {
      const loc = tab?.path ? `${tab.path}:${testItem.line}` : testItem.name;
      box.value = `Diagnose and fix the failing test "${testItem.name}" at ${loc}.\nFailure output:\n${res?.error || res?.rawOutput || '(see test panel)'}`;
      globalThis.toggleWorkspacePane?.('agent', true);
      box.focus();
    }
  });
  pop.querySelector('[data-tg-act="close"]')?.addEventListener('click', closePopover);

  const ed = getEditor();
  const rect = ed?.getDomNode?.()?.getBoundingClientRect?.();
  const topPx = ed?.getTopForLineNumber ? (rect?.top || 90) + Math.max(12, Math.min(260, ed.getTopForLineNumber(testItem.line) - (ed.getScrollTop?.() || 0))) : 140;
  pop.style.left = `${Math.max(16, Math.min((rect?.left || 240) + 64, window.innerWidth - 520))}px`;
  pop.style.top = `${Math.max(52, Math.min(topPx, window.innerHeight - 280))}px`;
  pop.style.display = 'flex';
}

export function runSingleTest(testItem) {
  if (!testItem) return;
  const tab = globalThis.VoidState?.get?.('activeTab');
  activeRunningKey = testItem.key;
  activeRunningFilePath = tab?.path || '';
  testResultsByKey.set(testItem.key, { status: 'running' });
  applyDecorations();
  globalThis.showToast?.(`Running test: ${testItem.name}`, 'info');
  hostSend('run_tests', { filter: testItem.filter });
}

export function runActiveFileTests() {
  if (!detectedTests.length) {
    globalThis.showToast?.('No runnable tests detected in this file', 'info');
    return;
  }
  const tab = globalThis.VoidState?.get?.('activeTab');
  const firstSuite = detectedTests.find((t) => t.suite && t.kind === 'csharp')?.suite;
  const filter = firstSuite || detectedTests.slice(0, 12).map((t) => t.filter).join('|');
  activeRunningKey = '';
  activeRunningFilePath = tab?.path || '';
  detectedTests.forEach((t) => testResultsByKey.set(t.key, { status: 'running' }));
  applyDecorations();
  globalThis.showToast?.(`Running ${detectedTests.length} test${detectedTests.length === 1 ? '' : 's'} in ${tab?.name || 'file'}`, 'info');
  hostSend('run_tests', { filter });
}

export function handleTestGutterLineClick(lineNumber) {
  const match = detectedTests.find((t) => t.line === lineNumber);
  if (!match) return false;
  const existing = testResultsByKey.get(match.key);
  if (existing && (existing.status === 'failed' || existing.status === 'passed')) {
    showResultPopover(match, existing);
  } else {
    runSingleTest(match);
  }
  return true;
}

export function renderTestContextAction(container, tab) {
  if (!container || !tab || tab.isVirtual || !detectedTests.length) return;
  const existing = container.querySelector('#btn-run-file-tests');
  if (existing) existing.remove();
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.id = 'btn-run-file-tests';
  btn.className = 'btn btn--quiet btn--xs';
  btn.textContent = `\u25b6 Run ${detectedTests.length} test${detectedTests.length === 1 ? '' : 's'}`;
  btn.title = 'Run all detected tests in this file';
  btn.addEventListener('click', runActiveFileTests);
  container.appendChild(btn);
}

function applyDecorations() {
  const ed = getEditor();
  const monaco = globalThis.monaco;
  const model = ed?.getModel?.();
  if (!ed || !monaco || !model) return;

  const maxLine = model.getLineCount();
  const decos = detectedTests
    .filter((t) => t.line >= 1 && t.line <= maxLine)
    .map((t) => {
      const res = testResultsByKey.get(t.key);
      const st = res?.status || 'idle';
      const cls = st === 'running'
        ? 'vc-test-gutter vc-test-gutter--running'
        : (st === 'passed'
          ? 'vc-test-gutter vc-test-gutter--pass'
          : (st === 'failed' ? 'vc-test-gutter vc-test-gutter--fail' : 'vc-test-gutter vc-test-gutter--run'));
      const hover = st === 'running'
        ? `Running "${t.name}"...`
        : (st === 'passed'
          ? `Passed "${t.name}"${res?.duration ? ` (${res.duration})` : ''} — click for details / re-run`
          : (st === 'failed'
            ? `Failed "${t.name}" — click to inspect failure or Fix with AI`
            : `Run test "${t.name}"`));
      return {
        range: new monaco.Range(t.line, 1, t.line, 1),
        options: {
          isWholeLine: false,
          glyphMarginClassName: cls,
          glyphMarginHoverMessage: { value: hover }
        }
      };
    });

  testGutterDecorations = ed.deltaDecorations(testGutterDecorations, decos);
}

export function scanActiveTabTests() {
  const tab = globalThis.VoidState?.get?.('activeTab');
  const ed = getEditor();
  const model = ed?.getModel?.();
  if (!tab || tab.isVirtual || !tab.path || !model || model.isDisposed?.()) {
    detectedTests = [];
    applyDecorations();
    return;
  }

  const uriStr = model.uri?.toString?.() || tab.path;
  if (uriStr !== boundModelUri) {
    modelSub?.dispose?.();
    boundModelUri = uriStr;
    modelSub = model.onDidChangeContent?.(() => scheduleScan(400));
  }

  detectedTests = detectTestsInSource(tab.path, model.getValue());
  applyDecorations();

  const ctxBar = document.getElementById('editor-context-actions');
  if (ctxBar) {
    const oldBtn = ctxBar.querySelector('#btn-run-file-tests');
    if (oldBtn) oldBtn.remove();
    if (detectedTests.length > 0) renderTestContextAction(ctxBar, tab);
  }
}

function scheduleScan(delayMs = 180) {
  clearTimeout(scanTimer);
  scanTimer = setTimeout(() => {
    scanTimer = null;
    scanActiveTabTests();
  }, delayMs);
}

function reconcileTestRunCompleted(payload) {
  const tests = Array.isArray(payload?.tests) ? payload.tests : [];
  const activeTab = globalThis.VoidState?.get?.('activeTab');
  const filePath = activeRunningFilePath || activeTab?.path || '';
  let matchedAny = false;

  for (const item of tests) {
    const rawName = String(item?.name || '').trim();
    if (!rawName) continue;
    const shortName = rawName.split('.').pop()?.replace(/\(.*\)$/, '') || rawName;
    const match = detectedTests.find((dt) => {
      const dl = dt.name.toLowerCase();
      const rl = rawName.toLowerCase();
      const sl = shortName.toLowerCase();
      return dl === rl || dl === sl || rl.endsWith(`.${dl}`) || rl.includes(dl);
    });
    if (match) {
      matchedAny = true;
      testResultsByKey.set(match.key, {
        status: item.status === 'failed' ? 'failed' : (item.status === 'passed' ? 'passed' : 'idle'),
        duration: item.duration || '',
        error: item.error || '',
        rawOutput: payload?.rawOutput || ''
      });
    }
  }

  // Fallback when running a single test and runner reports only aggregate counts
  if (!matchedAny && activeRunningKey && filePath) {
    const total = Number(payload?.total || 0);
    const failed = Number(payload?.failed || 0);
    const passed = Number(payload?.passed || 0);
    if (failed > 0) {
      testResultsByKey.set(activeRunningKey, {
        status: 'failed',
        error: String(payload?.rawOutput || 'Test failed.').slice(-1500),
        rawOutput: payload?.rawOutput || ''
      });
    } else if (passed > 0 || total > 0) {
      testResultsByKey.set(activeRunningKey, {
        status: 'passed',
        duration: payload?.durationMs ? `${(payload.durationMs / 1000).toFixed(2)}s` : ''
      });
    } else {
      testResultsByKey.delete(activeRunningKey);
    }
  }

  // Clear any remaining 'running' spinners
  for (const [k, v] of testResultsByKey.entries()) {
    if (v?.status === 'running') testResultsByKey.delete(k);
  }
  activeRunningKey = '';
  applyDecorations();
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  window.VoidState?.subscribe?.('activeTab', () => scheduleScan(120));
  window.VoidBridge?.subscribe?.((action, payload) => {
    if (action === 'test_run_completed') {
      reconcileTestRunCompleted(payload);
    } else if (action === 'test_run_cancelled') {
      for (const [k, v] of testResultsByKey.entries()) {
        if (v?.status === 'running') testResultsByKey.delete(k);
      }
      activeRunningKey = '';
      applyDecorations();
    }
  });

  const api = {
    detectTestsInSource,
    scanActiveTabTests,
    handleTestGutterLineClick,
    renderTestContextAction,
    runSingleTest,
    runActiveFileTests
  };
  Object.assign(globalThis, api);
  return api;
}
