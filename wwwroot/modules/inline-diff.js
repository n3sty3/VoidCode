// Inline Monaco AI Review Bar + Real In-Editor Red/Green Diff ViewZones:
// Renders green added-line highlights AND red deleted-line Monaco ViewZones inline directly
// inside the buffer (with Alt+Y Accept / Alt+N Revert and floating review bar).
let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

/** @type {any} */
let latestSummary = null;
/** @type {Set<string>} */
const acceptedKeys = new Set();
/** @type {Map<string, Array<{ anchorLine: number, addedLines: number[], removedLines: string[] }>>} */
const diffHunksByFile = new Map();
/** @type {string[]} */
let activeDecorations = [];
/** @type {string[]} */
let activeViewZoneIds = [];

function parseDiffHunks(diffText) {
  /** @type {Array<{ anchorLine: number, addedLines: number[], removedLines: string[] }>} */
  const hunks = [];
  if (!diffText) return hunks;
  let curNewLine = 1;
  /** @type {{ anchorLine: number, addedLines: number[], removedLines: string[] } | null} */
  let current = null;

  for (const line of String(diffText).split('\n')) {
    const hdr = /^@@\s+-\d+(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@/.exec(line);
    if (hdr) {
      if (current && (current.addedLines.length > 0 || current.removedLines.length > 0)) {
        hunks.push(current);
      }
      curNewLine = parseInt(hdr[1], 10) || 1;
      current = { anchorLine: Math.max(0, curNewLine - 1), addedLines: [], removedLines: [] };
      continue;
    }
    if (!current) continue;

    if (line.startsWith('+') && !line.startsWith('+++')) {
      if (current.addedLines.length === 0 && current.removedLines.length === 0) {
        current.anchorLine = Math.max(0, curNewLine - 1);
      }
      current.addedLines.push(curNewLine);
      curNewLine++;
    } else if (line.startsWith('-') && !line.startsWith('---')) {
      if (current.removedLines.length === 0 && current.addedLines.length === 0) {
        current.anchorLine = Math.max(0, curNewLine - 1);
      }
      const clean = line.startsWith('- ') ? line.slice(2) : line.slice(1);
      current.removedLines.push(clean);
    } else if (line.startsWith(' ')) {
      curNewLine++;
    }
  }
  if (current && (current.addedLines.length > 0 || current.removedLines.length > 0)) {
    hunks.push(current);
  }
  return hunks;
}

function clearMonacoAiVisuals() {
  const editor = globalThis.getMonacoEditor?.();
  if (!editor) return;
  if (activeDecorations.length > 0) {
    activeDecorations = editor.deltaDecorations(activeDecorations, []);
  }
  if (activeViewZoneIds.length > 0 && typeof editor.changeViewZones === 'function') {
    const idsToRemove = activeViewZoneIds.slice();
    activeViewZoneIds = [];
    editor.changeViewZones((accessor) => {
      for (const id of idsToRemove) accessor.removeZone(id);
    });
  }
}

function applyMonacoAiVisuals(filePath) {
  const editor = globalThis.getMonacoEditor?.();
  const monaco = globalThis.monaco;
  if (!editor || !monaco) return;

  clearMonacoAiVisuals();
  const hunks = diffHunksByFile.get(normalizePathKey(filePath)) || [];
  if (hunks.length === 0) return;

  const model = editor.getModel?.();
  const maxLine = model?.getLineCount?.() || 99999;
  const addedDecos = [];

  for (const h of hunks) {
    for (const ln of h.addedLines) {
      if (ln >= 1 && ln <= maxLine && addedDecos.length < 180) {
        addedDecos.push({
          range: new monaco.Range(ln, 1, ln, 1),
          options: {
            isWholeLine: true,
            className: 'voidcode-ai-added-line',
            linesDecorationsClassName: 'voidcode-ai-added-gutter'
          }
        });
      }
    }
  }

  activeDecorations = editor.deltaDecorations([], addedDecos);

  // Render red deleted-line blocks inline directly above each changed hunk via Monaco ViewZones.
  if (typeof editor.changeViewZones === 'function') {
    const createdIds = [];
    editor.changeViewZones((accessor) => {
      for (const h of hunks.slice(0, 16)) {
        if (!h.removedLines || h.removedLines.length === 0) continue;
        const shownLines = h.removedLines.slice(0, 12);
        const hiddenExtra = h.removedLines.length - shownLines.length;
        const domNode = document.createElement('div');
        domNode.className = 'vc-ai-removed-zone';
        domNode.style.cssText =
          'background:rgba(248,113,113,0.11);border-left:3px solid #f87171;' +
          'font-family:var(--font-code);font-size:12px;line-height:19px;padding:2px 10px;' +
          'color:rgba(252,165,165,0.92);overflow:hidden;white-space:pre;pointer-events:none;';

        const textLines = shownLines.map((l) => `- ${l}`);
        if (hiddenExtra > 0) textLines.push(`… (${hiddenExtra} more removed line${hiddenExtra === 1 ? '' : 's'})`);
        domNode.textContent = textLines.join('\n');

        const zoneId = accessor.addZone({
          afterLineNumber: Math.max(0, Math.min(maxLine, h.anchorLine)),
          heightInLines: textLines.length,
          domNode
        });
        if (zoneId) createdIds.push(zoneId);
      }
    });
    activeViewZoneIds = createdIds;
  }
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
  btnDiff.textContent = 'Side-by-Side Diff';
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
    clearMonacoAiVisuals();
    return;
  }
  const label = document.getElementById('inline-ai-review-label');
  if (label) {
    label.textContent = `VoidAgent edited ${fileBasename(pending.file.path)}`;
  }
  bar.style.display = 'inline-flex';
  applyMonacoAiVisuals(pending.file.path);
}

export function acceptActiveFileEdit() {
  const pending = findActivePendingEntry();
  if (!pending) return;
  acceptedKeys.add(pending.key);
  diffHunksByFile.delete(normalizePathKey(pending.file.path));
  clearMonacoAiVisuals();
  refreshInlineDiffBar();
  globalThis.showToast?.(`Accepted AI edit to ${fileBasename(pending.file.path)}`, 'info');
}

export function revertActiveFileEdit() {
  const pending = findActivePendingEntry();
  if (!pending) return;
  diffHunksByFile.delete(normalizePathKey(pending.file.path));
  clearMonacoAiVisuals();
  hostSend('revert_turn_file', { turnId: pending.turnId, path: pending.file.path });
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  if (window.VoidBridge && typeof window.VoidBridge.subscribe === 'function') {
    window.VoidBridge.subscribe((action, payload) => {
      if (action === 'agent_tool_finish' && payload?.diffPreview && payload?.modifiedFilePath) {
        const hunks = parseDiffHunks(payload.diffPreview);
        if (hunks.length > 0) {
          diffHunksByFile.set(normalizePathKey(payload.modifiedFilePath), hunks);
          const tab = window.VoidState?.get?.('activeTab');
          if (tab?.path && normalizePathKey(tab.path) === normalizePathKey(payload.modifiedFilePath)) {
            applyMonacoAiVisuals(payload.modifiedFilePath);
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
