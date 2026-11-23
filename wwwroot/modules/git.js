// Git Source Control panel + Monaco diff viewer.
//
// Main.js calls init(), the 16 entry points are exported + published on
// GlobalThis for the app.js (bridge switch, palette, boot) and modules/terminal.js,
// Which read the git status through getGitStatus() instead of a script-scoped `let`.
import { html, raw, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

// Git Source Control & Monaco Diff Viewer
let monacoGitDiffEditor = null;
let currentDiffInfo = null;
let currentGitBranches = [];
let currentActiveBranch = '';
let branchSelectedIndex = 0;
let currentCommitHistory = [];
let currentGitStatus = null;
let currentGitHunkReview = null;

// One typed lookup instead of a cast per call site - the controls this module reads
// By id are inputs, and tsc only knows getElementById returns a plain HTMLElement.
/**
 * @param {string} id
 * @returns {HTMLInputElement | null}
 */
function control(id) {
  return /** @type {HTMLInputElement | null} */ (document.getElementById(id));
}

export function setupGitPanel() {
  control('btn-refresh-git')?.addEventListener('click', refreshGitStatus);
  control('btn-git-stage-all')?.addEventListener('click', () => sendHost('git_stage_all'));
  control('btn-git-unstage-all')?.addEventListener('click', () => sendHost('git_unstage_all'));
  control('btn-git-pull')?.addEventListener('click', () => sendHost('git_pull'));
  control('btn-git-push')?.addEventListener('click', () => sendHost('git_push'));
  control('btn-git-ai-commit')?.addEventListener('click', () => {
    const btn = control('btn-git-ai-commit');
    if (btn) btn.disabled = true;
    sendHost('generate_commit_message');
  });
  control('btn-git-stash')?.addEventListener('click', async () => {
    const msg = await globalThis.showInputDialog?.('Git Stash', '', 'Stash');
    if (msg !== null) sendHost('git_stash', { message: msg });
  });
  control('btn-git-history')?.addEventListener('click', () => sendHost('git_get_history'));
  control('btn-close-git-hunks')?.addEventListener('click', closeGitHunkReview);
  control('git-branch-banner')?.addEventListener('click', openBranchPicker);
  control('status-git-branch')?.addEventListener('click', openBranchPicker);

  // Branch picker input
  const branchInput = control('branch-input');
  branchInput?.addEventListener('input', (e) => {
    branchSelectedIndex = 0;
    renderBranchResults(/** @type {HTMLInputElement} */ (e.target).value);
  });
  branchInput?.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      closeBranchPicker();
      return;
    }
    const items = document.querySelectorAll('#branch-results-list .symbol-item');
    if (items.length === 0) return;
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      branchSelectedIndex = (branchSelectedIndex + 1) % items.length;
      updateBranchSelection(items);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      branchSelectedIndex = (branchSelectedIndex - 1 + items.length) % items.length;
      updateBranchSelection(items);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (items[branchSelectedIndex]) {
        items[branchSelectedIndex].click();
      }
    }
  });

  // History filter input
  const histFilter = control('git-history-filter');
  histFilter?.addEventListener('input', (e) => {
    renderGitHistoryResults(/** @type {HTMLInputElement} */ (e.target).value);
  });
  histFilter?.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') closeGitHistoryModal();
  });

  // Click outside to close modals
  control('branch-modal')?.addEventListener('click', (e) => {
    if (/** @type {HTMLInputElement} */ (e.target).id === 'branch-modal') closeBranchPicker();
  });
  control('git-history-modal')?.addEventListener('click', (e) => {
    if (/** @type {HTMLInputElement} */ (e.target).id === 'git-history-modal') closeGitHistoryModal();
  });

  const msgInput = control('git-commit-message');
  const btnCommit = control('btn-git-commit');

  function triggerCommit() {
    if (!msgInput) return;
    const msg = msgInput.value.trim();
    if (!msg) {
      globalThis.showToast?.('Enter a commit message first', 'error');
      return;
    }
    sendHost('git_commit', { message: msg });
    msgInput.value = '';
  }

  btnCommit?.addEventListener('click', triggerCommit);
  msgInput?.addEventListener('keydown', (e) => {
    if (e.ctrlKey && e.key === 'Enter') {
      e.preventDefault();
      triggerCommit();
    }
  });
}

export function updateBranchSelection(items) {
  items.forEach((item, idx) => {
    item.classList.toggle('is-selected', idx === branchSelectedIndex);
    if (idx === branchSelectedIndex) item.scrollIntoView({ block: 'nearest' });
  });
}

export function openBranchPicker() {
  const modal = control('branch-modal');
  const input = control('branch-input');
  if (!modal || !input) return;

  sendHost('git_get_branches');
  input.value = '';
  modal.style.display = 'flex';
  input.focus();
  branchSelectedIndex = 0;
  renderBranchResults('');
}

export function closeBranchPicker() {
  const modal = control('branch-modal');
  if (modal) modal.style.display = 'none';
}

export function handleGitBranchesResponse(payload) {
  currentActiveBranch = payload.current || '';
  currentGitBranches = payload.branches || [];
  const input = control('branch-input');
  renderBranchResults(input ? input.value : '');
}

export function renderBranchResults(query) {
  const listEl = control('branch-results-list');
  if (!listEl) return;
  setHtml(listEl, '');

  const q = (query || '').trim().toLowerCase();
  const filtered = currentGitBranches.filter(b => !q || b.toLowerCase().includes(q));
  const items = [];

  const exactMatch = currentGitBranches.some(b => b.toLowerCase() === q);
  if (q && !exactMatch) {
    items.push({
      type: 'create',
      name: query.trim(),
      label: `+ Create branch "${query.trim()}"`
    });
  }

  filtered.forEach(b => {
    items.push({
      type: 'checkout',
      name: b,
      label: b,
      isCurrent: b === currentActiveBranch
    });
  });

  if (items.length === 0) {
    const emptyMsg = !window.VoidState.get('workspace')
      ? 'Open a Git workspace folder to view branches, or type a branch name above.'
      : 'No branches found — type a branch name above to create one.';
    setHtml(listEl, html`<div class="outline-empty">${emptyMsg}</div>`);
    return;
  }

  items.forEach((item, idx) => {
    const el = document.createElement('div');
    el.className = 'symbol-item' + (idx === branchSelectedIndex ? ' is-selected' : '');
    el.setAttribute('role', 'option');

    if (item.type === 'create') {
      setHtml(el, html`
        <span class="symbol-kind symbol-kind--action">
          <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="M12 5v14M5 12h14"/></svg>
        </span>
        <span class="symbol-name" style="color: var(--glacier); font-weight: 500;">${item.label}</span>
      `);
    } else {
      const iconSvg = item.isCurrent
        ? '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><polyline points="20 6 9 17 4 12"/></svg>'
        : '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><line x1="6" x2="6" y1="3" y2="15"/><circle cx="18" cy="6" r="3"/><circle cx="6" cy="18" r="3"/><path d="M18 9a9 9 0 0 1-9 9"/></svg>';
      setHtml(el, html`
        <span class="symbol-kind">${raw(iconSvg)}</span>
        <span class="symbol-name">${item.name}</span>
        ${item.isCurrent ? raw('<span class="symbol-container">current</span>') : ''}
      `);
    }

    el.addEventListener('click', () => {
      closeBranchPicker();
      if (item.type === 'create') {
        sendHost('git_create_branch', { branch: item.name });
      } else {
        if (item.name === currentActiveBranch) return;
        sendHost('git_checkout_branch', { branch: item.name });
      }
    });

    listEl.appendChild(el);
  });
}

export function openGitHistoryModal(commits) {
  currentCommitHistory = commits || [];
  const modal = control('git-history-modal');
  const filterInput = control('git-history-filter');
  if (!modal) return;

  if (filterInput) filterInput.value = '';
  modal.style.display = 'flex';
  filterInput?.focus();
  renderGitHistoryResults('');
}

export function closeGitHistoryModal() {
  const modal = control('git-history-modal');
  if (modal) modal.style.display = 'none';
}

export function renderGitHistoryResults(filterText) {
  const listEl = control('git-history-results-list');
  if (!listEl) return;
  setHtml(listEl, '');

  const q = (filterText || '').toLowerCase().trim();
  const filtered = currentCommitHistory.filter(c => {
    if (!q) return true;
    return (c.hash || '').toLowerCase().includes(q) ||
           (c.author || '').toLowerCase().includes(q) ||
           (c.subject || '').toLowerCase().includes(q);
  });

  if (filtered.length === 0) {
    setHtml(listEl, '<div class="outline-empty">No commits match filter.</div>');
    return;
  }

  filtered.forEach(c => {
    const row = document.createElement('div');
    row.className = 'git-commit-row';
    setHtml(row, html`
      <div class="git-commit-left">
        <span class="git-commit-hash">${c.hash}</span>
        <span class="git-commit-subject" title="${c.subject}">${c.subject}</span>
      </div>
      <div class="git-commit-meta">
        <span>${c.author}</span>
        <span>•</span>
        <span>${c.timeAgo}</span>
      </div>
    `);
    listEl.appendChild(row);
  });
}

export function refreshGitStatus() {
  sendHost('git_get_status');
}

export function handleGitStatusResponse(payload) {
  const badgeCountEl = control('git-badge-count');
  const branchLabelEl = control('git-branch-label');
  const noticeBannerEl = control('git-notice-banner');
  const sectionsEl = control('git-changes-sections');
  const stagedCountEl = control('git-staged-count');
  const workingCountEl = control('git-working-count');
  const stagedListEl = control('git-staged-list');
  const workingListEl = control('git-working-list');

  if (!badgeCountEl) return;

  if (!payload.isAvailable) {
    badgeCountEl.style.display = 'none';
    const statusBranchBtn = control('status-git-branch');
    if (statusBranchBtn) statusBranchBtn.style.display = 'none';

    // Git is a hard dependency: show one error card with a way out and hide the
    // Branch selector, commit composer and action icons instead of faking them.
    const gitView = control('view-git');
    if (gitView) gitView.classList.add('is-unavailable');

    if (noticeBannerEl) {
      setHtml(noticeBannerEl, html`
        <div class="git-blocked">
          <h4>Git is not available</h4>
          <p>${payload.message || 'Git was not found on this system, so source control is disabled.'}</p>
          <p>Install Git for Windows, or add <code>git.exe</code> to your <code>PATH</code>, then retry detection.</p>
          <div class="git-blocked-actions">
            <button type="button" class="btn btn--primary btn--xs" id="btn-git-install">Install Git</button>
            <button type="button" class="btn btn--quiet btn--xs" id="btn-git-retry">Retry detection</button>
          </div>
        </div>
      `);
      noticeBannerEl.style.display = 'block';

      control('btn-git-install')?.addEventListener('click', () => {
        sendHost('open_external_url', { url: 'https://git-scm.com/download/win' });
      });
      control('btn-git-retry')?.addEventListener('click', () => {
        refreshGitStatus();
      });
    }

    if (sectionsEl) sectionsEl.style.display = 'none';
    return;
  }

  control('view-git')?.classList.remove('is-unavailable');

  if (!payload.isRepo) {
    badgeCountEl.style.display = 'none';
    const statusBranchBtn = control('status-git-branch');
    if (statusBranchBtn) statusBranchBtn.style.display = 'none';
    if (noticeBannerEl) {
      setHtml(noticeBannerEl, html`
        <p>${payload.message || 'Workspace is not a Git repository.'}</p>
        <button type="button" class="btn btn--primary btn--sm" id="btn-git-init-repo" style="margin-top: 8px;">Initialize Git Repo</button>
      `);
      noticeBannerEl.style.display = 'block';
      control('btn-git-init-repo')?.addEventListener('click', () => {
        // Run git through VoidShell; the host's PowerShell fallback must never be reached
        if (window.VoidConsole) {
          window.VoidConsole.print('void \u276f git init', 'cmd');
          window.VoidConsole.execute('git init');
        }
        setTimeout(refreshGitStatus, 1500);
      });
    }
    if (sectionsEl) sectionsEl.style.display = 'none';
    return;
  }

  // Active Git Repo
  if (noticeBannerEl) noticeBannerEl.style.display = 'none';
  if (sectionsEl) sectionsEl.style.display = 'flex';

  if (branchLabelEl) branchLabelEl.textContent = payload.branch || 'HEAD';
  const statusBranchBtn = control('status-git-branch');
  const statusBranchName = control('status-branch-name');
  if (statusBranchBtn && statusBranchName) {
    statusBranchName.textContent = payload.branch || 'HEAD';
    statusBranchBtn.style.display = 'inline-flex';
  }

  const staged = payload.stagedChanges || [];
  const working = payload.workingChanges || [];
  const totalCount = staged.length + working.length;
  currentGitStatus = { branch: payload.branch || 'HEAD', staged, working };
  globalThis.updateConsoleStatus?.();

  badgeCountEl.textContent = totalCount;
  badgeCountEl.style.display = totalCount > 0 ? 'inline-flex' : 'none';

  if (stagedCountEl) stagedCountEl.textContent = staged.length;
  if (workingCountEl) workingCountEl.textContent = working.length;

  function renderFileList(listEl, items, isStaged) {
    if (!listEl) return;
    setHtml(listEl, '');
    if (items.length === 0) {
      setHtml(listEl, '<div style="font-size: var(--fs-xs); color: var(--text-faint); padding: 4px 8px;">No changes</div>');
      return;
    }

    items.forEach(item => {
      const row = document.createElement('div');
      row.className = 'git-file-row';

      const tagClass = 'git-status-tag--' + item.status.toLowerCase().charAt(0);
      const tagLetter = item.status.charAt(0).toUpperCase();

      setHtml(row, html`
        <div class="git-file-info" title="${item.path} (${item.statusDesc})">
          <span class="git-status-tag ${tagClass}">${tagLetter}</span>
          <span class="git-file-name">${item.path.split(/[\\/]/).pop()}</span>
        </div>
        <div class="git-file-actions">
          ${isStaged ? raw(`
            <button type="button" class="icon-btn icon-btn--sm btn-unstage-file" title="Unstage file" aria-label="Unstage file">
              <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="M5 12h14"/></svg>
            </button>
          `) : raw(`
            <button type="button" class="icon-btn icon-btn--sm btn-discard-file" title="Discard changes" aria-label="Discard changes">
              <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"/><path d="M3 3v5h5"/></svg>
            </button>
            <button type="button" class="icon-btn icon-btn--sm btn-stage-file" title="Stage file" aria-label="Stage file">
              <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="M12 5v14M5 12h14"/></svg>
            </button>
          `)}
        </div>
      `);

      row.addEventListener('click', () => {
        sendHost('git_get_file_diff', { path: item.path, staged: isStaged });
        sendHost('git_get_hunks', { path: item.path, staged: isStaged });
      });

      row.querySelector('.btn-stage-file')?.addEventListener('click', (e) => {
        e.stopPropagation();
        sendHost('git_stage_file', { path: item.path });
      });

      row.querySelector('.btn-unstage-file')?.addEventListener('click', (e) => {
        e.stopPropagation();
        sendHost('git_unstage_file', { path: item.path });
      });

      row.querySelector('.btn-discard-file')?.addEventListener('click', async (e) => {
        e.stopPropagation();
        const confirmed = await globalThis.showConfirmDialog?.(
          'Discard Changes',
          `Are you sure you want to discard changes in "${item.path}"?`
        );
        if (confirmed) {
          sendHost('git_discard_file', { path: item.path });
        }
      });

      listEl.appendChild(row);
    });
  }

  renderFileList(stagedListEl, staged, true);
  renderFileList(workingListEl, working, false);
}

export function closeGitHunkReview() {
  currentGitHunkReview = null;
  const review = control('git-hunk-review');
  if (review) review.style.display = 'none';
}

export function handleGitHunksResponse(payload) {
  const review = control('git-hunk-review');
  const pathElement = control('git-hunk-review-path');
  const list = control('git-hunk-list');
  if (!review || !pathElement || !list) return;

  currentGitHunkReview = payload;
  review.style.display = 'flex';
  pathElement.textContent = `${payload.staged ? 'Staged' : 'Working'} · ${payload.path || ''}`;
  pathElement.title = payload.path || '';
  list.replaceChildren();

  const hunks = Array.isArray(payload.hunks) ? payload.hunks : [];
  if (!payload.success || hunks.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'outline-empty';
    empty.textContent = payload.message || 'No text hunks are available.';
    list.appendChild(empty);
    return;
  }

  hunks.forEach(hunk => {
    const card = document.createElement('article');
    card.className = 'git-hunk-card';

    const head = document.createElement('div');
    head.className = 'git-hunk-card-head';
    const jump = document.createElement('button');
    jump.type = 'button';
    jump.className = 'git-hunk-jump';
    jump.textContent = hunk.section || hunk.header || `Lines ${hunk.newStart || hunk.oldStart}`;
    jump.title = hunk.header || '';
    jump.addEventListener('click', () => {
      const targetLine = Math.max(1, hunk.newStart || hunk.oldStart || 1);
      monacoGitDiffEditor?.getModifiedEditor().revealLineInCenter(targetLine);
      monacoGitDiffEditor?.getModifiedEditor().setPosition({ lineNumber: targetLine, column: 1 });
      monacoGitDiffEditor?.getModifiedEditor().focus();
    });

    const actions = document.createElement('div');
    actions.className = 'git-hunk-card-actions';
    const primary = document.createElement('button');
    primary.type = 'button';
    primary.className = 'btn btn--primary btn--xs';
    primary.textContent = payload.staged ? 'Unstage' : 'Stage';
    primary.addEventListener('click', () => applyGitHunk(payload.staged ? 'unstage' : 'stage', hunk, primary));
    actions.appendChild(primary);

    if (!payload.staged) {
      const discard = document.createElement('button');
      discard.type = 'button';
      discard.className = 'btn btn--quiet btn--xs';
      discard.textContent = 'Discard';
      discard.addEventListener('click', async () => {
        const approved = await globalThis.showConfirmDialog?.(
          'Discard selected hunk',
          `Discard ${hunk.additions || 0} added and ${hunk.deletions || 0} removed lines from “${hunk.section || payload.path}”? This changes the working file.`,
          'Discard hunk'
        );
        if (approved) applyGitHunk('discard', hunk, discard);
      });
      actions.appendChild(discard);
    }
    head.append(jump, actions);

    const stats = document.createElement('div');
    stats.className = 'git-hunk-stats';
    setHtml(stats, html`<span class="git-hunk-additions">+${hunk.additions || 0}</span><span class="git-hunk-deletions">−${hunk.deletions || 0}</span><span>${hunk.header || ''}</span>`);

    const changedLine = (hunk.lines || []).find(line => line.type === 'addition' || line.type === 'deletion');
    card.append(head, stats);
    if (changedLine) {
      const preview = document.createElement('div');
      preview.className = 'git-hunk-preview';
      preview.textContent = `${changedLine.type === 'addition' ? '+' : '−'} ${changedLine.text || ''}`;
      card.appendChild(preview);
    }
    list.appendChild(card);
  });
}

export function applyGitHunk(action, hunk, button) {
  if (!currentGitHunkReview?.path || !hunk?.id) return;
  button.disabled = true;
  sendHost('git_apply_hunk', {
    path: currentGitHunkReview.path,
    hunkId: hunk.id,
    hunkAction: action
  });
}

export function handleGitDiffResponse(payload) {
  if (!payload.success) {
    globalThis.showToast?.('Could not load HEAD diff: ' + payload.message, 'error');
    return;
  }

  const diffMount = control('monaco-diff-mount');
  const monacoMount = control('monaco-mount');
  const noFileState = control('no-file-state');
  if (!diffMount) return;

  if (!monacoGitDiffEditor && window.monaco) {
    monacoGitDiffEditor = monaco.editor.createDiffEditor(diffMount, {
      theme: 'harbor-glass',
      automaticLayout: true,
      readOnly: true,
      originalEditable: false,
      renderSideBySide: true,
      minimap: { enabled: false }
    });
  }

  if (currentDiffInfo) {
    if (currentDiffInfo.originalModel) currentDiffInfo.originalModel.dispose();
    if (currentDiffInfo.modifiedModel) currentDiffInfo.modifiedModel.dispose();
  }

  const langInfo = globalThis.getLanguageInfo?.(payload.path) || { id: 'plaintext' };
  const originalModel = monaco.editor.createModel(payload.headContent || '', langInfo.id);
  const modifiedModel = monaco.editor.createModel(payload.currentContent || '', langInfo.id);

  if (monacoGitDiffEditor) {
    monacoGitDiffEditor.setModel({
      original: originalModel,
      modified: modifiedModel
    });
  }

  currentDiffInfo = {
    path: payload.path,
    staged: !!payload.staged,
    originalModel,
    modifiedModel
  };

  noFileState.style.display = 'none';
  monacoMount.style.display = 'none';
  diffMount.style.display = 'block';

  globalThis.updateBreadcrumbs?.(`(${payload.staged ? 'HEAD ↔ Index' : 'Index ↔ Working'}) ${payload.path}`);
  globalThis.showToast?.(`Reviewing ${payload.staged ? 'staged' : 'working'} changes in ${payload.path.split(/[\\/]/).pop()}`, 'info');
}

export function closeDiffEditor() {
  const diffMount = control('monaco-diff-mount');
  const monacoMount = control('monaco-mount');
  if (diffMount) diffMount.style.display = 'none';
  if (currentDiffInfo) {
    // Detach the models before disposing them. Disposing while the diff editor still
    // Holds them made monaco throw from its own change tracking ("Cannot read properties of null
    // (reading 'coordinatesConverter')") the next time it recomputed.
    if (monacoGitDiffEditor) monacoGitDiffEditor.setModel(null);
    if (currentDiffInfo.originalModel) currentDiffInfo.originalModel.dispose();
    if (currentDiffInfo.modifiedModel) currentDiffInfo.modifiedModel.dispose();
    currentDiffInfo = null;
  }
  closeGitHunkReview();

  const stateActiveTab = window.VoidState.get('activeTab');
  if (stateActiveTab) {
    if (monacoMount) monacoMount.style.display = 'block';
    globalThis.setActiveTab?.(stateActiveTab);
  } else {
    control('no-file-state').style.display = 'flex';
    globalThis.updateBreadcrumbs?.(null);
  }
}

// The workspace git status is read by app.js's agent context and by the console
// Prompt in modules/terminal.js, so they ask for it instead of reaching into this module's state.
export function getGitStatus() {
  return currentGitStatus;
}

// Main.js calls this once; the entry points go on globalThis for the classic app.js
// (bridge switch, palette, boot) and modules/terminal.js.js up.
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  Object.assign(globalThis, {
    setupGitPanel,
    updateBranchSelection,
    openBranchPicker,
    closeBranchPicker,
    handleGitBranchesResponse,
    renderBranchResults,
    openGitHistoryModal,
    closeGitHistoryModal,
    renderGitHistoryResults,
    refreshGitStatus,
    handleGitStatusResponse,
    closeGitHunkReview,
    handleGitHunksResponse,
    applyGitHunk,
    handleGitDiffResponse,
    closeDiffEditor,
    getGitStatus
  });
  return { setupGitPanel, openBranchPicker, closeBranchPicker, handleGitBranchesResponse, openGitHistoryModal, closeGitHistoryModal, refreshGitStatus, handleGitStatusResponse, closeGitHunkReview, handleGitHunksResponse, applyGitHunk, handleGitDiffResponse, closeDiffEditor, getGitStatus };
}
