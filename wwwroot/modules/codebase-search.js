// Codebase search modal (keyword & symbol index): state, setup, open/close, render results.
//
// Main.js calls init(), and the four entry points are published on
// GlobalThis for the app.js (bridge result handler, command palette, boot).
// Markup is built with the core/markdown.js helpers, which are imported rather than globals.
import { html, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

// Codebase Search Modal (keyword and symbol index)
let codebaseSelectedIndex = 0;
let currentCodebaseResults = [];
let codebaseSearchSequence = 0;
let activeCodebaseSearchId = '';

// One typed lookup instead of a cast per call site - the controls this module reads
// By id are inputs, and tsc only knows getElementById returns a plain HTMLElement.
/**
 * @param {string} id
 * @returns {HTMLInputElement | null}
 */
function control(id) {
  return /** @type {HTMLInputElement | null} */ (document.getElementById(id));
}

export function setupCodebaseSearch() {
  const modal = control('codebase-modal');
  const input = control('codebase-search-input');
  if (!modal || !input) return;

  let debounceTimer = null;
  input.addEventListener('input', (e) => {
    clearTimeout(debounceTimer);
    const query = /** @type {HTMLInputElement} */ (e.target).value.trim();
    activeCodebaseSearchId = '';
    if (!query) {
      renderCodebaseSearchResults([]);
      return;
    }
    debounceTimer = setTimeout(() => {
      const searchId = `codebase_${++codebaseSearchSequence}`;
      activeCodebaseSearchId = searchId;
      const stateActiveTab = window.VoidState.get('activeTab');
      const language = stateActiveTab && typeof globalThis.getLanguageInfo === 'function'
        ? globalThis.getLanguageInfo(stateActiveTab.path).id
        : '';
      const lspLanguages = ['python', 'csharp', 'rust', 'go', 'cpp', 'typescript', 'javascript'];
      sendHost('search_codebase', {
        searchId,
        query,
        path: stateActiveTab && lspLanguages.includes(language) ? stateActiveTab.path : ''
      });
    }, 200);
  });

  input.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      closeCodebaseSearch();
      return;
    }
    const items = document.querySelectorAll('#codebase-results-list .symbol-item');
    if (items.length === 0) return;
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      codebaseSelectedIndex = (codebaseSelectedIndex + 1) % items.length;
      updateCodebaseSelection(items);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      codebaseSelectedIndex = (codebaseSelectedIndex - 1 + items.length) % items.length;
      updateCodebaseSelection(items);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (items[codebaseSelectedIndex]) {
        items[codebaseSelectedIndex].click();
      }
    }
  });

  modal.addEventListener('click', (e) => {
    if (/** @type {HTMLInputElement} */ (e.target).id === 'codebase-modal') closeCodebaseSearch();
  });
}

function updateCodebaseSelection(items) {
  items.forEach((item, idx) => {
    item.classList.toggle('is-selected', idx === codebaseSelectedIndex);
    if (idx === codebaseSelectedIndex) item.scrollIntoView({ block: 'nearest' });
  });
}

export function openCodebaseSearch() {
  const modal = control('codebase-modal');
  const input = control('codebase-search-input');
  if (!modal || !input) return;
  modal.style.display = 'flex';
  input.value = '';
  input.focus();
  codebaseSelectedIndex = 0;
  activeCodebaseSearchId = '';
  renderCodebaseSearchResults([]);
}

export function closeCodebaseSearch() {
  const modal = control('codebase-modal');
  if (modal) modal.style.display = 'none';
  activeCodebaseSearchId = '';
}

export function renderCodebaseSearchResults(results) {
  const list = control('codebase-results-list');
  if (!list) return;
  setHtml(list, '');
  currentCodebaseResults = results || [];
  codebaseSelectedIndex = 0;

  if (currentCodebaseResults.length === 0) {
    setHtml(list, '<div style="padding: 8px 12px; color: var(--text-dim); font-size: var(--fs-xs);">No matching symbols or chunks</div>');
    return;
  }

  currentCodebaseResults.forEach((res, idx) => {
    const item = document.createElement('div');
    item.className = 'symbol-item' + (idx === 0 ? ' is-selected' : '');
    item.role = 'option';
    const fileName = (res.file || '').split(/[\\/]/).pop();
    setHtml(item, html`
      <span class="symbol-kind-tag">${res.kind || 'chunk'}</span>
      <span class="symbol-name">${res.symbol || fileName}</span>
      <span class="symbol-file" title="${res.detail || ''}">${res.file || ''}:${res.startLine || 1}</span>
    `);
    item.addEventListener('click', () => {
      closeCodebaseSearch();
      globalThis.navigateToFileLocation?.(res.file, res.startLine || 1, res.startColumn || 1, 0);
    });
    list.appendChild(item);
  });
}

export function handleCodebaseSearchResponse(payload) {
  if (!payload) return;
  if (payload.searchId && payload.searchId !== activeCodebaseSearchId) return;
  renderCodebaseSearchResults(payload.results || []);
}

// Main.js calls this once; the exported entry points go on globalThis for the
// Classic app.js.
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  globalThis.VoidHostEvents?.register?.({
    search_codebase_response: (payload) => handleCodebaseSearchResponse(payload)
  });
  Object.assign(globalThis, {
    openCodebaseSearch,
    closeCodebaseSearch,
    setupCodebaseSearch,
    renderCodebaseSearchResults,
    handleCodebaseSearchResponse
  });
  return { openCodebaseSearch, closeCodebaseSearch, setupCodebaseSearch, renderCodebaseSearchResults, handleCodebaseSearchResponse };
}
