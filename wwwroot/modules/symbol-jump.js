// Ctrl+T Workspace Symbol Jump (backed by SQLite .voidcode/symbols.db via SymbolGraphService).
import { html, setHtml } from '../core/markdown.js';

let modalEl = null;
let inputEl = null;
let listEl = null;
let statusEl = null;
let results = [];
let selectedIdx = 0;
let debounceTimer = null;

function resolveWorkspaceFullPath(relPath) {
  const ws = window.VoidState?.get?.('workspace') || '';
  if (!relPath) return ws;
  if (/^[a-zA-Z]:[\\/]/.test(relPath) || relPath.startsWith('/')) return relPath;
  const clean = String(relPath).replace(/^\.[\\/]/, '');
  const sep = ws.includes('\\') ? '\\' : '/';
  return ws ? `${ws.replace(/[\\/]+$/, '')}${sep}${clean.replace(/[\\/]/g, sep)}` : clean;
}

function kindBadgeColor(kind) {
  switch (String(kind || '').toLowerCase()) {
    case 'class':
    case 'struct':
    case 'record': return '#7dd3fc';
    case 'interface':
    case 'trait':
    case 'type': return '#a78bfa';
    case 'function':
    case 'fn':
    case 'method': return '#6be2b3';
    case 'enum': return '#f5b963';
    default: return 'var(--text-muted)';
  }
}

function ensureModal() {
  if (modalEl) return modalEl;
  modalEl = document.createElement('div');
  modalEl.id = 'symbol-jump-modal';
  modalEl.className = 'modal-overlay';
  modalEl.setAttribute('role', 'dialog');
  modalEl.setAttribute('aria-modal', 'true');
  modalEl.setAttribute('aria-label', 'Go to Symbol in Workspace');
  modalEl.style.cssText = 'display:none;z-index:2600;align-items:flex-start;padding-top:10vh;';

  setHtml(modalEl, html`
    <div class="modal-card glass glass--float" style="width:min(680px,92vw);max-height:72vh;display:flex;flex-direction:column;gap:8px;padding:12px;">
      <div style="display:flex;align-items:center;gap:8px;">
        <span style="font-size:11px;font-weight:600;letter-spacing:0.06em;text-transform:uppercase;color:var(--glacier);padding:3px 7px;border-radius:6px;background:var(--glacier-wash);border:1px solid var(--glacier-line);">Ctrl+Alt+T</span>
        <input id="symbol-jump-input" type="text" placeholder="Go to symbol in workspace (class, interface, function, method)..." autocomplete="off" spellcheck="false" style="flex:1;height:34px;padding:0 10px;border-radius:8px;border:1px solid var(--seam);background:rgba(0,0,0,0.28);color:var(--text);font-size:13px;" />
        <button id="btn-symbol-jump-close" type="button" class="btn btn--quiet btn--xs">Esc</button>
      </div>
      <div id="symbol-jump-status" style="font-size:11px;color:var(--text-muted);padding:0 4px;">Type a symbol name to jump across the workspace.</div>
      <div id="symbol-jump-list" role="listbox" style="flex:1;overflow-y:auto;display:flex;flex-direction:column;gap:2px;max-height:48vh;"></div>
    </div>
  `);

  document.body.appendChild(modalEl);
  inputEl = /** @type {HTMLInputElement | null} */ (document.getElementById('symbol-jump-input'));
  listEl = document.getElementById('symbol-jump-list');
  statusEl = document.getElementById('symbol-jump-status');

  document.getElementById('btn-symbol-jump-close')?.addEventListener('click', closeSymbolJump);
  modalEl.addEventListener('click', (e) => {
    if (e.target === modalEl) closeSymbolJump();
  });

  inputEl?.addEventListener('input', () => {
    if (debounceTimer) clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => runQuery(inputEl?.value || ''), 90);
  });

  inputEl?.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      e.preventDefault();
      closeSymbolJump();
    } else if (e.key === 'ArrowDown') {
      e.preventDefault();
      if (results.length > 0) {
        selectedIdx = (selectedIdx + 1) % results.length;
        renderResults();
      }
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      if (results.length > 0) {
        selectedIdx = (selectedIdx - 1 + results.length) % results.length;
        renderResults();
      }
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (results[selectedIdx]) jumpTo(results[selectedIdx]);
    }
  });

  return modalEl;
}

function jumpTo(item) {
  closeSymbolJump();
  const fullPath = resolveWorkspaceFullPath(item.path);
  globalThis.navigateToFileLocation?.(fullPath, item.line || 1, 1);
}

function renderResults() {
  if (!listEl) return;
  setHtml(listEl, '');
  if (!results.length) {
    setHtml(listEl, html`<div class="empty-hint" style="padding:18px;text-align:center;">No matching workspace symbols found.</div>`);
    return;
  }

  results.forEach((item, idx) => {
    const row = document.createElement('button');
    row.type = 'button';
    row.setAttribute('role', 'option');
    row.setAttribute('aria-selected', idx === selectedIdx ? 'true' : 'false');
    row.style.cssText =
      'display:flex;align-items:center;justify-content:space-between;gap:10px;width:100%;text-align:left;' +
      'padding:7px 10px;border-radius:7px;border:1px solid ' +
      (idx === selectedIdx ? 'var(--glacier-line)' : 'transparent') +
      ';background:' +
      (idx === selectedIdx ? 'var(--glacier-wash)' : 'transparent') +
      ';color:var(--text);cursor:pointer;font-size:12.5px;';

    const color = kindBadgeColor(item.kind);
    setHtml(row, html`
      <div style="display:flex;align-items:center;gap:8px;min-width:0;flex:1;">
        <span style="font-family:var(--font-code);font-size:10px;text-transform:uppercase;color:${color};border:1px solid rgba(255,255,255,0.12);border-radius:4px;padding:1px 5px;flex-shrink:0;">${item.kind || 'sym'}</span>
        <span style="font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${item.name}</span>
        <span style="font-family:var(--font-code);font-size:11px;color:var(--text-muted);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${item.signature || ''}</span>
      </div>
      <span style="font-family:var(--font-code);font-size:11px;color:var(--text-muted);flex-shrink:0;">${item.path}:${item.line}</span>
    `);

    row.addEventListener('click', () => jumpTo(item));
    row.addEventListener('mouseenter', () => {
      selectedIdx = idx;
      renderResults();
    });
    listEl.appendChild(row);
  });

  const active = listEl.children[selectedIdx];
  if (active && typeof active.scrollIntoView === 'function') {
    active.scrollIntoView({ block: 'nearest' });
  }
}

async function runQuery(query) {
  if (statusEl) statusEl.textContent = 'Searching SQLite symbol index…';
  try {
    const res = await globalThis.VoidBridge?.request?.('symbol_index_query', { query, limit: 60 }, 8000);
    results = Array.isArray(res?.data) ? res.data : (Array.isArray(res) ? res : []);
    selectedIdx = 0;
    if (statusEl) {
      statusEl.textContent = `${results.length} symbol${results.length === 1 ? '' : 's'} matched`;
    }
    renderResults();
  } catch (err) {
    globalThis.logSwallowed?.(err, 'symbol-jump.runQuery');
    results = [];
    if (statusEl) statusEl.textContent = 'Symbol index unavailable (open a workspace folder first).';
    renderResults();
  }
}

export function openSymbolJump() {
  ensureModal();
  if (!modalEl) return;
  modalEl.style.display = 'flex';
  if (inputEl) {
    inputEl.value = '';
    setTimeout(() => inputEl?.focus(), 20);
  }
  runQuery('');
}

export function closeSymbolJump() {
  if (modalEl) modalEl.style.display = 'none';
}

export function init() {
  window.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.altKey && !e.shiftKey && e.key.toLowerCase() === 't') {
      if (window.VoidState?.get?.('workspace')) {
        e.preventDefault();
        openSymbolJump();
      }
    }
  });

  const api = { openSymbolJump, closeSymbolJump };
  Object.assign(globalThis, api);
  return api;
}
