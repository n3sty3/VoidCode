// Live Git Diff Gutter Bars in Monaco Editor:
// Paints green (added), cyan (modified), and red (deleted) bars in the left margin vs HEAD,
// with click-to-peek original HEAD text and 1-click hunk revert directly in Monaco.
import { html, setHtml } from '../core/markdown.js';

let gutterDecorations = [];
/** @type {any[]} */
let currentHunks = [];
let currentFilePath = '';
let refreshTimer = null;
let peekEl = null;
let attachedEditor = null;

function getEditor() {
  return globalThis.getMonacoEditor?.() || null;
}

function closePeek() {
  if (peekEl) peekEl.style.display = 'none';
}

function ensurePeekPopover() {
  if (peekEl && document.body.contains(peekEl)) return peekEl;
  peekEl = document.createElement('div');
  peekEl.id = 'git-gutter-peek';
  peekEl.className = 'glass glass--float';
  peekEl.style.cssText =
    'display:none;position:fixed;z-index:2550;width:min(480px,86vw);max-height:280px;' +
    'flex-direction:column;gap:8px;padding:10px 12px;border-radius:11px;border:1px solid var(--seam);' +
    'box-shadow:0 14px 36px rgba(0,0,0,0.48);';
  document.body.appendChild(peekEl);

  document.addEventListener('mousedown', (e) => {
    if (!peekEl || peekEl.style.display === 'none') return;
    if (!peekEl.contains(/** @type {Node} */ (e.target))) closePeek();
  }, true);
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && peekEl && peekEl.style.display !== 'none') closePeek();
  });
  return peekEl;
}

function revertHunkInEditor(hunk) {
  const ed = getEditor();
  const monaco = globalThis.monaco;
  const model = ed?.getModel?.();
  if (!ed || !monaco || !model || !hunk) return;

  const lineCount = model.getLineCount();
  let range;
  let text = hunk.originalText || '';

  if (hunk.kind === 'added') {
    const s = Math.max(1, Math.min(lineCount, hunk.startLine));
    const e = Math.max(s, Math.min(lineCount, hunk.endLine));
    if (e < lineCount) {
      range = new monaco.Range(s, 1, e + 1, 1);
    } else if (s > 1) {
      range = new monaco.Range(s - 1, model.getLineMaxColumn(s - 1), e, model.getLineMaxColumn(e));
    } else {
      range = new monaco.Range(1, 1, e, model.getLineMaxColumn(e));
    }
    text = '';
  } else if (hunk.kind === 'deleted') {
    const ln = Math.max(1, Math.min(lineCount, hunk.startLine));
    const endCol = model.getLineMaxColumn(ln);
    range = new monaco.Range(ln, endCol, ln, endCol);
    text = text ? `\n${text}` : '';
  } else {
    const s = Math.max(1, Math.min(lineCount, hunk.startLine));
    const e = Math.max(s, Math.min(lineCount, hunk.endLine));
    range = new monaco.Range(s, 1, e, model.getLineMaxColumn(e));
  }

  ed.executeEdits('git-gutter-revert', [{ range, text }]);
  closePeek();
  globalThis.showToast?.('Reverted Git hunk in editor (Ctrl+S to save)', 'info');
  scheduleGitGutterRefresh(250);
}

function showPeekForHunk(hunk, clientX, clientY) {
  const pop = ensurePeekPopover();
  const labelMap = { added: 'Added vs HEAD', modified: 'Modified vs HEAD', deleted: 'Deleted vs HEAD' };
  const colorMap = { added: '#6be2b3', modified: 'var(--glacier)', deleted: 'var(--coral)' };

  setHtml(pop, html`
    <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;">
      <span style="font-size:11.5px;font-weight:700;color:${colorMap[hunk.kind] || 'var(--text)'};">${labelMap[hunk.kind] || 'Changed vs HEAD'} · L${hunk.startLine}${hunk.endLine > hunk.startLine ? `–${hunk.endLine}` : ''}</span>
      <div style="display:flex;gap:6px;align-items:center;">
        <button type="button" class="btn btn--danger btn--xs" data-act="revert-hunk">Revert Hunk</button>
        <button type="button" class="btn btn--quiet btn--xs" data-act="close-peek">✕</button>
      </div>
    </div>
    <pre style="margin:0;padding:8px 10px;border-radius:8px;background:rgba(0,0,0,0.34);border:1px solid var(--seam);font-family:var(--font-code);font-size:11.5px;overflow:auto;max-height:190px;color:var(--text-muted);white-space:pre;">${hunk.originalText || '(Newly added lines — not present in HEAD)'}</pre>
  `);

  pop.querySelector('[data-act="revert-hunk"]')?.addEventListener('click', () => revertHunkInEditor(hunk));
  pop.querySelector('[data-act="close-peek"]')?.addEventListener('click', closePeek);

  pop.style.display = 'flex';
  const x = Math.max(12, Math.min(clientX + 14, window.innerWidth - 500));
  const y = Math.max(12, Math.min(clientY + 12, window.innerHeight - 290));
  pop.style.left = `${x}px`;
  pop.style.top = `${y}px`;
}

function applyGutterDecorations() {
  const ed = getEditor();
  const monaco = globalThis.monaco;
  const model = ed?.getModel?.();
  if (!ed || !monaco || !model) return;

  const maxLine = model.getLineCount();
  const decos = currentHunks
    .filter((h) => h && h.startLine >= 1 && h.startLine <= maxLine)
    .map((h) => {
      const end = Math.max(h.startLine, Math.min(maxLine, h.endLine || h.startLine));
      const cls = h.kind === 'added'
        ? 'vc-git-gutter-added'
        : (h.kind === 'deleted' ? 'vc-git-gutter-deleted' : 'vc-git-gutter-modified');
      return {
        range: new monaco.Range(h.startLine, 1, end, 1),
        options: {
          isWholeLine: true,
          linesDecorationsClassName: cls,
          linesDecorationsTooltip: `${h.kind} vs HEAD (click gutter to peek/revert)`
        }
      };
    });

  gutterDecorations = ed.deltaDecorations(gutterDecorations, decos);
}

function wireEditorClick() {
  const ed = getEditor();
  if (!ed || ed === attachedEditor) return;
  attachedEditor = ed;
  ed.onMouseDown?.((e) => {
    const monaco = globalThis.monaco;
    if (!monaco || !currentHunks.length) return;
    if (e.target?.type !== monaco.editor.MouseTargetType.GUTTER_LINE_DECORATIONS) return;
    const ln = e.target?.position?.lineNumber;
    if (!ln) return;
    const match = currentHunks.find((h) => ln >= h.startLine && ln <= (h.endLine || h.startLine));
    if (match) {
      showPeekForHunk(match, e.event?.posx || 120, e.event?.posy || 120);
    }
  });
}

export async function refreshGitGutter() {
  const tab = window.VoidState?.get?.('activeTab');
  const ws = window.VoidState?.get?.('workspace');
  if (!tab?.path || !ws) {
    currentHunks = [];
    applyGutterDecorations();
    return;
  }

  wireEditorClick();
  currentFilePath = tab.path;
  try {
    const res = await globalThis.VoidBridge?.request?.('git_file_gutter', { path: tab.path }, 5000);
    if (tab.path !== currentFilePath) return;
    const data = res?.data || res;
    currentHunks = Array.isArray(data?.hunks) ? data.hunks : [];
    applyGutterDecorations();
  } catch (err) {
    globalThis.logSwallowed?.(err, 'git-gutter.refresh');
  }
}

export function scheduleGitGutterRefresh(delayMs = 350) {
  if (refreshTimer) clearTimeout(refreshTimer);
  refreshTimer = setTimeout(() => {
    refreshTimer = null;
    refreshGitGutter();
  }, delayMs);
}

export function init() {
  window.VoidState?.subscribe?.('activeTab', () => scheduleGitGutterRefresh(120));
  window.VoidBridge?.subscribe?.((action) => {
    if (action === 'file_saved' || action === 'git_status_response' || action === 'agent_done' || action === 'file_changed') {
      scheduleGitGutterRefresh(250);
    }
  });

  const api = { refreshGitGutter, scheduleGitGutterRefresh };
  Object.assign(globalThis, api);
  return api;
}
