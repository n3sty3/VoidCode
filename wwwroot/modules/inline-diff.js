// Inline Monaco AI Review Bar:
// Surfaces a floating Accept / Diff / Revert bar directly inside the editor whenever the
// Active file has unreviewed edits from the latest VoidAgent turn (with Alt+Y / Alt+N shortcuts).
let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

/** @type {any} */
let latestSummary = null;
/** @type {Set<string>} */
const acceptedKeys = new Set();
/** @type {Map<string, number[]>} */
const addedLinesByFile = new Map();
/** @type {string[]} */
let activeDecorations = [];

function extractAddedLineNumbers(diffText) {
  const nums = [];
  if (!diffText) return nums;
  let curNewLine = 1;
  for (const line of String(diffText).split('\n')) {
    const hdr = /^@@\s+-\d+(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@/.exec(line);
    if (hdr) {
      curNewLine = parseInt(hdr[1], 10) || 1;
      continue;
    }
    if (line.startsWith('+ ') || (line.startsWith('+') && !line.startsWith('+++'))) {
      nums.push(curNewLine);
      curNewLine++;
    } else if (line.startsWith('- ') || (line.startsWith('-') && !line.startsWith('---'))) {
      // Removed line does not advance new line counter.
    } else if (line.startsWith(' ')) {
      curNewLine++;
    }
  }
  return nums;
}

function clearMonacoAiDecorations() {
  const editor = globalThis.getMonacoEditor?.();
  if (editor && activeDecorations.length > 0) {
    activeDecorations = editor.deltaDecorations(activeDecorations, []);
  }
}

function applyMonacoAiDecorations(filePath) {
  const editor = globalThis.getMonacoEditor?.();
  const monaco = globalThis.monaco;
  if (!editor || !monaco) return;
  const lines = addedLinesByFile.get(normalizePathKey(filePath)) || [];
  if (lines.length === 0) {
    clearMonacoAiDecorations();
    return;
  }
  const model = editor.getModel?.();
  const maxLine = model?.getLineCount?.() || 99999;
  const decos = lines
    .filter((ln) => ln >= 1 && ln <= maxLine)
    .slice(0, 120)
    .map((ln) => ({
      range: new monaco.Range(ln, 1, ln, 1),
      options: {
        isWholeLine: true,
        className: 'voidcode-ai-added-line',
        linesDecorationsClassName: 'voidcode-ai-added-gutter'
      }
    }));
  activeDecorations = editor.deltaDecorations(activeDecorations, decos);
}

function normalizePathKey(p) {
  return String(p || '').replace(/\//g, '\\').toLowerCase();
}

function fileBasename(p) {
  return String(p || '').split(/[\\/]/).pop() || 'file';
}

function findActivePendingEntry() {
  const tab = window.VoidState?.get?.('activeTab');
  if (!tab?.path || !latestSummary?.turnId || !Array.isArray(latestSummary.files)) return null;
  const tabKey = normalizePathKey(tab.path);
  const match = latestSummary.files.find(
    (f) => f && !f.reverted && normalizePathKey(f.path) === tabKey
  );
  if (!match) return null;
  const key = `${latestSummary.turnId}::${tabKey}`;
  if (acceptedKeys.has(key)) return null;
  return { turnId: latestSummary.turnId, file: match, key };
}

function ensureBannerElement() {
  let bar = document.getElementById('inline-ai-review-bar');
  if (bar) return bar;
  const editorHost = document.getElementById('editor-container')?.parentElement || document.getElementById('editor-container');
  if (!editorHost) return null;

  if (getComputedStyle(editorHost).position === 'static') {
    editorHost.style.position = 'relative';
  }

  bar = document.createElement('div');
  bar.id = 'inline-ai-review-bar';
  bar.className = 'glass glass--float';
  bar.style.cssText =
    'display:none;position:absolute;top:10px;right:18px;z-index:35;align-items:center;gap:8px;' +
    'padding:6px 10px;border-radius:9px;border:1px solid var(--border-subtle,rgba(56,189,248,0.35));' +
    'font-size:12px;box-shadow:0 8px 24px rgba(0,0,0,0.35);';

  const label = document.createElement('span');
  label.id = 'inline-ai-review-label';
  label.style.cssText = 'font-weight:500;margin-right:2px;';

  const btnAccept = document.createElement('button');
  btnAccept.type = 'button';
  btnAccept.className = 'btn btn--primary btn--xs';
  btnAccept.textContent = 'Accept (Alt+Y)';
  btnAccept.addEventListener('click', acceptActiveFileEdit);

  const btnDiff = document.createElement('button');
  btnDiff.type = 'button';
  btnDiff.className = 'btn btn--quiet btn--xs';
  btnDiff.textContent = 'Diff';
  btnDiff.addEventListener('click', () => {
    const pending = findActivePendingEntry();
    if (!pending) return;
    hostSend('get_turn_file_diff', { turnId: pending.turnId, path: pending.file.path });
  });

  const btnRevert = document.createElement('button');
  btnRevert.type = 'button';
  btnRevert.className = 'btn btn--danger btn--xs';
  btnRevert.textContent = 'Revert (Alt+N)';
  btnRevert.addEventListener('click', revertActiveFileEdit);

  bar.append(label, btnAccept, btnDiff, btnRevert);
  editorHost.appendChild(bar);
  return bar;
}

export function refreshInlineDiffBar() {
  const bar = ensureBannerElement();
  if (!bar) return;
  const pending = findActivePendingEntry();
  if (!pending) {
    bar.style.display = 'none';
    clearMonacoAiDecorations();
    return;
  }
  const label = document.getElementById('inline-ai-review-label');
  if (label) {
    label.textContent = `VoidAgent edited ${fileBasename(pending.file.path)}`;
  }
  bar.style.display = 'inline-flex';
  applyMonacoAiDecorations(pending.file.path);
}

export function acceptActiveFileEdit() {
  const pending = findActivePendingEntry();
  if (!pending) return;
  acceptedKeys.add(pending.key);
  addedLinesByFile.delete(normalizePathKey(pending.file.path));
  clearMonacoAiDecorations();
  refreshInlineDiffBar();
  globalThis.showToast?.(`Accepted AI edits in ${fileBasename(pending.file.path)}`, 'info');
}

export function revertActiveFileEdit() {
  const pending = findActivePendingEntry();
  if (!pending) return;
  addedLinesByFile.delete(normalizePathKey(pending.file.path));
  clearMonacoAiDecorations();
  hostSend('revert_turn_file', { turnId: pending.turnId, path: pending.file.path });
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  if (window.VoidBridge && typeof window.VoidBridge.subscribe === 'function') {
    window.VoidBridge.subscribe((action, payload) => {
      if (action === 'agent_tool_finish' && payload?.diffPreview && payload?.modifiedFilePath) {
        const added = extractAddedLineNumbers(payload.diffPreview);
        if (added.length > 0) {
          addedLinesByFile.set(normalizePathKey(payload.modifiedFilePath), added);
          const tab = window.VoidState?.get?.('activeTab');
          if (tab?.path && normalizePathKey(tab.path) === normalizePathKey(payload.modifiedFilePath)) {
            applyMonacoAiDecorations(payload.modifiedFilePath);
          }
        }
      }
    });
  }

  window.addEventListener('voidcode:turn-review', (e) => {
    const detail = /** @type {CustomEvent} */ (e).detail;
    latestSummary = detail || null;
    refreshInlineDiffBar();
  });

  window.VoidState?.subscribe?.('activeTab', () => refreshInlineDiffBar());

  window.addEventListener('keydown', (e) => {
    if (!e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
    const pending = findActivePendingEntry();
    if (!pending) return;
    const k = e.key.toLowerCase();
    if (k === 'y') {
      e.preventDefault();
      acceptActiveFileEdit();
    } else if (k === 'n') {
      e.preventDefault();
      revertActiveFileEdit();
    }
  });

  return {
    refreshInlineDiffBar,
    acceptActiveFileEdit,
    revertActiveFileEdit
  };
}
