// Global workspace search & replace: state, setupSearchAndReplace, result + replace handlers.
//
// Main.js calls init(); setupSearchAndReplace, handleSearchResponse,
// HandleReplaceResponse and handleSearchCancelled are exported and published on globalThis for the
// App.js. The search ids belong to this module, so the search_workspace_cancelled
// Branch rather than reaching into a script-scoped `let`.
import { html, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

// Global Workspace Search & Replace
let searchModifiers = {
  matchCase: false,
  matchWholeWord: false,
  isRegex: false
};
let workspaceSearchSequence = 0;
let activeWorkspaceSearchId = '';
let workspaceSearchDebounceTimer = null;

// One typed lookup instead of a cast per call site - the controls this module reads
// By id are inputs, and tsc only knows getElementById returns a plain HTMLElement.
/**
 * @param {string} id
 * @returns {HTMLInputElement | null}
 */
function control(id) {
  return /** @type {HTMLInputElement | null} */ (document.getElementById(id));
}

export function setupSearchAndReplace() {
  const searchInput = control('global-search-input');
  const replaceInput = control('global-replace-input');
  const btnRefresh = control('btn-refresh-search');
  const btnClear = control('btn-clear-search');
  const btnCase = control('btn-mod-case');
  const btnWord = control('btn-mod-word');
  const btnRegex = control('btn-mod-regex');
  const btnToggleReplace = control('btn-toggle-replace');
  const replaceRow = control('replace-box-row');
  const btnToggleFilters = control('btn-toggle-filters');
  const filtersBox = control('search-filters-container');
  const btnReplaceAll = control('btn-action-replace-all');

  function triggerSearch() {
    if (!searchInput) return;
    const query = searchInput.value.trim();
    if (!query) {
      sendHost('cancel_search');
      activeWorkspaceSearchId = '';
      control('search-status-bar').textContent = 'No search executed.';
      setHtml(control('search-results-tree'), '');
      return;
    }
    const inc = control('search-filter-include')?.value.trim() || null;
    const exc = control('search-filter-exclude')?.value.trim() || null;

    control('search-status-bar').textContent = 'Searching workspace...';
    const searchId = `search_${++workspaceSearchSequence}`;
    activeWorkspaceSearchId = searchId;
    sendHost('search_workspace', {
      searchId,
      query,
      isRegex: searchModifiers.isRegex,
      matchCase: searchModifiers.matchCase,
      matchWholeWord: searchModifiers.matchWholeWord,
      fileFilter: inc,
      excludeFilter: exc
    });
  }

  if (searchInput) {
    searchInput.addEventListener('input', () => {
      clearTimeout(workspaceSearchDebounceTimer);
      if (activeWorkspaceSearchId) sendHost('cancel_search');
      activeWorkspaceSearchId = '';
      if (!searchInput.value.trim()) {
        triggerSearch();
        return;
      }
      control('search-status-bar').textContent = 'Waiting to search...';
      workspaceSearchDebounceTimer = setTimeout(triggerSearch, 280);
    });
    searchInput.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        clearTimeout(workspaceSearchDebounceTimer);
        triggerSearch();
      } else if (e.key === 'Escape') {
        e.preventDefault();
        clearTimeout(workspaceSearchDebounceTimer);
        sendHost('cancel_search');
        activeWorkspaceSearchId = '';
        control('search-status-bar').textContent = 'Search cancelled.';
      }
    });
  }

  if (btnRefresh) btnRefresh.addEventListener('click', triggerSearch);
  if (btnClear) {
    btnClear.addEventListener('click', () => {
      clearTimeout(workspaceSearchDebounceTimer);
      sendHost('cancel_search');
      activeWorkspaceSearchId = '';
      if (searchInput) searchInput.value = '';
      control('search-status-bar').textContent = 'No search executed.';
      setHtml(control('search-results-tree'), '');
    });
  }

  function bindMod(btn, key) {
    if (!btn) return;
    btn.addEventListener('click', () => {
      searchModifiers[key] = !searchModifiers[key];
      btn.classList.toggle('is-active', searchModifiers[key]);
      triggerSearch();
    });
  }
  bindMod(btnCase, 'matchCase');
  bindMod(btnWord, 'matchWholeWord');
  bindMod(btnRegex, 'isRegex');

  if (btnToggleReplace && replaceRow) {
    btnToggleReplace.addEventListener('click', () => {
      const isHidden = replaceRow.style.display === 'none';
      replaceRow.style.display = isHidden ? 'flex' : 'none';
      btnToggleReplace.classList.toggle('expanded', isHidden);
      if (isHidden && replaceInput) replaceInput.focus();
    });
  }

  if (btnToggleFilters && filtersBox) {
    btnToggleFilters.addEventListener('click', () => {
      const isHidden = filtersBox.style.display === 'none';
      filtersBox.style.display = isHidden ? 'flex' : 'none';
    });
  }

  if (btnReplaceAll) {
    btnReplaceAll.addEventListener('click', async () => {
      const query = searchInput?.value.trim() || '';
      const replaceText = replaceInput?.value || '';
      if (!query) {
        globalThis.showToast?.('Enter a search term first', 'error');
        return;
      }
      const confirmed = await globalThis.showConfirmDialog?.(
        'Confirm Global Replace',
        `Replace occurrences of "${query}" across workspace with "${replaceText}"? Checkpoints are saved automatically.`
      );
      if (confirmed) {
        const inc = control('search-filter-include')?.value.trim() || null;
        const exc = control('search-filter-exclude')?.value.trim() || null;
        sendHost('replace_workspace', {
          query,
          replaceText,
          isRegex: searchModifiers.isRegex,
          matchCase: searchModifiers.matchCase,
          matchWholeWord: searchModifiers.matchWholeWord,
          fileFilter: inc,
          excludeFilter: exc
        });
      }
    });
  }
}

export function handleSearchResponse(payload) {
  const statusEl = control('search-status-bar');
  const treeEl = control('search-results-tree');
  if (!statusEl || !treeEl) return;
  if (payload.searchId && payload.searchId !== activeWorkspaceSearchId) return;

  if (payload.error) {
    statusEl.textContent = payload.error;
    setHtml(treeEl, '');
    return;
  }

  const { totalMatches, fileCount, files, query } = payload;
  if (totalMatches === 0) {
    statusEl.textContent = `No matches found for "${query}".`;
    setHtml(treeEl, '<div class="empty-state" style="padding: 8px;">No matching results in workspace.</div>');
    return;
  }

  statusEl.textContent = `Found ${totalMatches} match${totalMatches === 1 ? '' : 'es'} across ${fileCount} file${fileCount === 1 ? '' : 's'}`;
  setHtml(treeEl, '');

  files.forEach(fileResult => {
    const groupEl = document.createElement('div');
    groupEl.className = 'search-file-group';

    const fileName = fileResult.relativePath.split(/[\\/]/).pop();
    const lastSlash = Math.max(fileResult.relativePath.lastIndexOf('/'), fileResult.relativePath.lastIndexOf('\\'));
    const dirPath = lastSlash > 0 ? fileResult.relativePath.substring(0, lastSlash) : '';

    const headerEl = document.createElement('div');
    headerEl.className = 'search-file-header';
    setHtml(headerEl, html`
      <div class="search-file-name" title="${fileResult.relativePath}">
        <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true" style="transform: rotate(90deg); transition: transform var(--dur-micro) var(--ease);"><path d="m9 18 6-6-6-6"/></svg>
        <span>${fileName}</span>
        <span style="color: var(--text-faint); font-size: var(--fs-xs);">${dirPath}</span>
      </div>
      <span class="search-file-count">${fileResult.matches.length}</span>
    `);

    const matchesContainer = document.createElement('div');
    matchesContainer.className = 'search-file-matches';

    fileResult.matches.forEach(match => {
      const matchRow = document.createElement('div');
      matchRow.className = 'search-match-row';
      matchRow.setAttribute('role', 'treeitem');

      setHtml(matchRow, html`
        <span class="search-match-line">${match.line}</span>
        <span class="search-match-preview" title="${match.preview}">${match.preview}</span>
      `);

      matchRow.addEventListener('click', (e) => {
        e.stopPropagation();
        globalThis.closeDiffEditor?.();
        globalThis.navigateToFileLocation?.(fileResult.fullPath, match.line, match.column, match.length);
      });

      matchesContainer.appendChild(matchRow);
    });

    headerEl.addEventListener('click', () => {
      const isCollapsed = matchesContainer.style.display === 'none';
      matchesContainer.style.display = isCollapsed ? 'flex' : 'none';
      const chevron = headerEl.querySelector('svg');
      if (chevron) chevron.style.transform = isCollapsed ? 'rotate(90deg)' : '';
    });

    groupEl.appendChild(headerEl);
    groupEl.appendChild(matchesContainer);
    treeEl.appendChild(groupEl);
  });
}

export function handleReplaceResponse(payload) {
  if (payload.error) {
    globalThis.showToast?.(payload.error, 'error');
  } else {
    globalThis.showToast?.(payload.message || 'Replace complete', 'success');
    const searchInput = control('global-search-input');
    if (searchInput && searchInput.value.trim()) {
      searchInput.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    }
  }
}

// The check needs this module's own id, and
// The old bare-global read stopped working the moment the file became a module.
export function handleSearchCancelled(payload = {}) {
  if (payload.searchId !== activeWorkspaceSearchId) return;
  const statusEl = control('search-status-bar');
  if (statusEl) statusEl.textContent = 'Search cancelled.';
}

// Main.js calls this once; the entry points go on globalThis for the classic app.js
//.
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  Object.assign(globalThis, {
    setupSearchAndReplace,
    handleSearchResponse,
    handleReplaceResponse,
    handleSearchCancelled
  });
  return { setupSearchAndReplace, handleSearchResponse, handleReplaceResponse, handleSearchCancelled };
}
