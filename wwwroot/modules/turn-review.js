// Per-file and per-hunk agent turn review UI + Monaco checkpoint diff viewer.
// Extracted from app.js (`openCheckpointDiff`, `renderTurnReviewCard`) into an ES module.
import { html, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

/** @type {any} */
let monacoDiffEditor = null;
/** @type {any} */
let activeTurnSummary = null;
/** @type {any} */
let activeReviewDiff = null;
/** @type {Map<string, HTMLElement>} */
const turnCardMap = new Map();

function byId(id) {
  return document.getElementById(id);
}

function fileBasename(p) {
  return String(p || '').split(/[\\/]/).pop() || 'Diff';
}

function normalizePathKey(p) {
  return String(p || '').replace(/\//g, '\\').toLowerCase();
}

function actionLabel(action) {
  switch (String(action || '').toLowerCase()) {
    case 'create': return 'Created';
    case 'delete': return 'Deleted';
    case 'move_from': return 'Moved from';
    case 'move_to': return 'Moved to';
    default: return 'Edited';
  }
}

function syncOpenEditorTab(filePath, exists, content) {
  const tabs = window.VoidState?.get?.('openTabs') || [];
  const norm = normalizePathKey(filePath);
  const tab = tabs.find(t => normalizePathKey(t.path) === norm);
  if (!tab) return;
  if (exists && typeof content === 'string' && tab.model && !tab.model.isDisposed()) {
    tab.model.setValue(content);
    tab.dirty = false;
    globalThis.renderTabs?.();
  } else if (!exists) {
    globalThis.closeTabByPath?.(filePath);
  }
}

function computeLineDelta(beforeText, afterText) {
  const bLines = String(beforeText || '').split('\n');
  const aLines = String(afterText || '').split('\n');
  const bSet = new Set(bLines);
  const aSet = new Set(aLines);
  let added = 0;
  let removed = 0;
  aLines.forEach(l => { if (!bSet.has(l)) added++; });
  bLines.forEach(l => { if (!aSet.has(l)) removed++; });
  return { added, removed };
}

function renderDiffEditor(path, beforeContent, afterContent) {
  const statsEl = byId('diff-win-stats');
  if (statsEl) {
    const { added, removed } = computeLineDelta(beforeContent, afterContent);
    statsEl.textContent = `+${added} / -${removed} lines`;
  }
  const mount = byId('diff-editor-mount');
  if (!mount || !window.monaco) return;
  const langInfo = globalThis.getLanguageInfo ? globalThis.getLanguageInfo(path || 'file.txt') : { id: 'plaintext' };

  if (!monacoDiffEditor) {
    monacoDiffEditor = window.monaco.editor.createDiffEditor(mount, {
      theme: 'harbor-glass',
      automaticLayout: true,
      readOnly: true,
      renderSideBySide: true,
      fontSize: 13,
      fontFamily: "'JetBrains Mono', 'Cascadia Code', Consolas, monospace"
    });
  }

  const prevModel = monacoDiffEditor.getModel();
  const origModel = window.monaco.editor.createModel(beforeContent || '', langInfo.id);
  const modModel = window.monaco.editor.createModel(afterContent || '', langInfo.id);
  monacoDiffEditor.setModel({ original: origModel, modified: modModel });
  if (prevModel) {
    prevModel.original?.dispose?.();
    prevModel.modified?.dispose?.();
  }
  setTimeout(() => monacoDiffEditor?.layout?.(), 40);
}

export function openCheckpointDiff(path, beforeContent, afterContent) {
  activeReviewDiff = null;
  const titleEl = byId('diff-win-title');
  if (titleEl) titleEl.textContent = `Diff · ${fileBasename(path)}`;
  const toolbar = byId('diff-review-toolbar');
  if (toolbar) {
    toolbar.style.display = 'none';
    setHtml(toolbar, '');
  }
  const hunksBar = byId('diff-hunks-bar');
  if (hunksBar) {
    hunksBar.style.display = 'none';
    setHtml(hunksBar, '');
  }
  globalThis.toggleMiniWindow?.('win-diff', true);
  renderDiffEditor(path, beforeContent, afterContent);
}

function renderReviewToolbarAndHunks(diff) {
  activeReviewDiff = diff;
  const titleEl = byId('diff-win-title');
  if (titleEl) titleEl.textContent = `Turn review · ${diff.relativePath || fileBasename(diff.path)}`;

  const toolbar = byId('diff-review-toolbar');
  if (toolbar) {
    toolbar.style.display = 'flex';
    setHtml(toolbar, '');

    const files = activeTurnSummary?.turnId === diff.turnId ? (activeTurnSummary.files || []) : [];
    const fileStrip = document.createElement('div');
    fileStrip.className = 'diff-file-strip';
    files.forEach(f => {
      const btn = document.createElement('button');
      btn.type = 'button';
      const isCurrent = normalizePathKey(f.path) === normalizePathKey(diff.path);
      btn.className = isCurrent ? 'btn btn--accent-soft btn--xs' : 'btn btn--quiet btn--xs';
      btn.textContent = f.relativePath || fileBasename(f.path);
      btn.addEventListener('click', () => {
        sendHost('get_turn_file_diff', { turnId: diff.turnId, path: f.path });
      });
      fileStrip.appendChild(btn);
    });

    const actions = document.createElement('div');
    actions.className = 'diff-toolbar-actions';

    if (files.length > 1) {
      const curIdx = files.findIndex(f => normalizePathKey(f.path) === normalizePathKey(diff.path));
      const prevBtn = document.createElement('button');
      prevBtn.type = 'button';
      prevBtn.className = 'btn btn--quiet btn--xs';
      prevBtn.textContent = '← Prev';
      prevBtn.disabled = curIdx <= 0;
      prevBtn.addEventListener('click', () => {
        if (curIdx > 0) sendHost('get_turn_file_diff', { turnId: diff.turnId, path: files[curIdx - 1].path });
      });

      const nextBtn = document.createElement('button');
      nextBtn.type = 'button';
      nextBtn.className = 'btn btn--quiet btn--xs';
      nextBtn.textContent = 'Next →';
      nextBtn.disabled = curIdx < 0 || curIdx >= files.length - 1;
      nextBtn.addEventListener('click', () => {
        if (curIdx >= 0 && curIdx < files.length - 1) sendHost('get_turn_file_diff', { turnId: diff.turnId, path: files[curIdx + 1].path });
      });
      actions.append(prevBtn, nextBtn);
    }

    const modeBtn = document.createElement('button');
    modeBtn.type = 'button';
    modeBtn.className = 'btn btn--quiet btn--xs';
    let sideBySide = true;
    modeBtn.textContent = 'Inline';
    modeBtn.title = 'Toggle Side-by-Side / Inline diff view';
    modeBtn.addEventListener('click', () => {
      sideBySide = !sideBySide;
      monacoDiffEditor?.updateOptions?.({ renderSideBySide: sideBySide });
      modeBtn.textContent = sideBySide ? 'Inline' : 'Side-by-Side';
    });

    const keepBtn = document.createElement('button');
    keepBtn.type = 'button';
    keepBtn.className = 'btn btn--accent-soft btn--xs';
    keepBtn.dataset.reviewAct = 'keep-file';
    keepBtn.textContent = 'Accept file';
    keepBtn.addEventListener('click', () => {
      markFileStatusInCard(diff.turnId, diff.path, 'Accepted');
      globalThis.showToast?.(`Accepted ${fileBasename(diff.path)}`, 'info');
    });

    const rejectBtn = document.createElement('button');
    rejectBtn.type = 'button';
    rejectBtn.className = 'btn btn--danger btn--xs';
    rejectBtn.dataset.reviewAct = 'reject-file';
    rejectBtn.textContent = 'Reject file';
    rejectBtn.addEventListener('click', () => {
      sendHost('revert_turn_file', { turnId: diff.turnId, path: diff.path });
    });

    actions.append(modeBtn, keepBtn, rejectBtn);
    toolbar.append(fileStrip, actions);
  }

  const hunksBar = byId('diff-hunks-bar');
  if (hunksBar) {
    const hunks = Array.isArray(diff.hunks) ? diff.hunks : [];
    hunksBar.style.display = 'flex';
    setHtml(hunksBar, '');

    if (diff.isBinary) {
      const note = document.createElement('span');
      note.className = 'empty-hint';
      note.textContent = 'Binary file change — use Accept file or Reject file above.';
      hunksBar.appendChild(note);
    } else if (hunks.length === 0) {
      const note = document.createElement('span');
      note.className = 'empty-hint';
      note.textContent = 'No remaining differences for this file.';
      hunksBar.appendChild(note);
    } else {
      hunks.forEach(h => {
        const chip = document.createElement('div');
        chip.className = 'attached-chip diff-hunk-chip';
        chip.title = h.preview || h.header;

        const lbl = document.createElement('span');
        lbl.textContent = `Hunk ${h.index + 1} (${h.header || `L${h.newStart}`})`;

        const rejectHunkBtn = document.createElement('button');
        rejectHunkBtn.type = 'button';
        rejectHunkBtn.className = 'btn btn--danger btn--xs';
        rejectHunkBtn.dataset.rejectHunk = String(h.index);
        rejectHunkBtn.textContent = 'Reject hunk';
        rejectHunkBtn.addEventListener('click', () => {
          rejectHunkBtn.disabled = true;
          sendHost('reject_turn_hunk', { turnId: diff.turnId, path: diff.path, hunkIndex: h.index });
        });

        chip.append(lbl, rejectHunkBtn);
        hunksBar.appendChild(chip);
      });
    }
  }
}

function toneForState(stateText) {
  switch (String(stateText || '').toLowerCase()) {
    case 'created':
    case 'accepted':
      return 'is-ok';
    case 'deleted':
      return 'is-danger';
    case 'reverted':
      return 'is-muted';
    default:
      return '';
  }
}

function applyBadgeTone(badge, statusText) {
  if (!badge) return;
  badge.textContent = statusText;
  badge.classList.remove('is-ok', 'is-warn', 'is-danger', 'is-muted');
  const tone = toneForState(statusText);
  if (tone) badge.classList.add(tone);
}

function markFileStatusInCard(turnId, filePath, statusText) {
  const card = turnCardMap.get(turnId);
  if (!card) return;
  const norm = normalizePathKey(filePath);
  card.querySelectorAll('[data-turn-file-path]').forEach(row => {
    if (normalizePathKey(row.getAttribute('data-turn-file-path')) !== norm) return;
    const badge = row.querySelector('[data-turn-file-state]');
    applyBadgeTone(badge, statusText);
    const rejectBtn = /** @type {HTMLButtonElement | null} */ (row.querySelector('[data-file-act="reject"]'));
    if (rejectBtn && statusText === 'Reverted') rejectBtn.disabled = true;
  });
}

function buildFallbackSummary(turnEdits) {
  const seen = new Set();
  const files = [];
  (turnEdits || []).forEach(edit => {
    const p = edit.path || '';
    const key = normalizePathKey(p || edit.checkpointId);
    if (seen.has(key)) return;
    seen.add(key);
    files.push({
      path: p,
      relativePath: fileBasename(p),
      action: edit.tool === 'delete_file' ? 'delete' : 'edit',
      reversible: true,
      reverted: false,
      checkpointId: edit.checkpointId
    });
  });
  return { turnId: null, files, irreversibleActions: [] };
}

export function renderTurnReviewCard(turnEdits, turnSummary = null) {
  const container = byId('agent-chat-container');
  if (!container) return;

  const summary = (turnSummary && Array.isArray(turnSummary.files) && (turnSummary.files.length > 0 || (turnSummary.irreversibleActions && turnSummary.irreversibleActions.length > 0)))
    ? turnSummary
    : buildFallbackSummary(turnEdits);

  const files = summary.files || [];
  const irreversible = summary.irreversibleActions || [];
  if (files.length === 0 && irreversible.length === 0) return;

  if (summary.turnId) activeTurnSummary = summary;

  const card = document.createElement('div');
  card.className = 'agent-turn-review-bar';
  if (summary.turnId) {
    card.dataset.turnId = summary.turnId;
    turnCardMap.set(summary.turnId, card);
  }
  window.dispatchEvent(new CustomEvent('voidcode:turn-review', { detail: activeTurnSummary }));

  const head = document.createElement('div');
  head.className = 'turn-review-head';
  const fileLabel = `${files.length} file change${files.length === 1 ? '' : 's'}`;
  const irrevNote = irreversible.length > 0 ? ` · ${irreversible.length} non-reversible` : '';
  setHtml(head, html`
    <div class="agent-turn-review-info">
      <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2.2" aria-hidden="true"><polyline points="20 6 9 17 4 12"/></svg>
      <span>Turn review: ${fileLabel}${irrevNote}</span>
    </div>
  `);

  const topActions = document.createElement('div');
  topActions.className = 'agent-turn-review-actions';

  if (files.length > 0) {
    const btnRevertAll = document.createElement('button');
    btnRevertAll.type = 'button';
    btnRevertAll.className = 'btn btn--danger btn--xs';
    btnRevertAll.dataset.act = 'undo-turn';
    btnRevertAll.textContent = `Revert whole turn (${files.length})`;
    btnRevertAll.addEventListener('click', () => {
      btnRevertAll.disabled = true;
      btnRevertAll.textContent = 'Reverting...';
      if (summary.turnId) {
        sendHost('revert_whole_turn', { turnId: summary.turnId });
      } else {
        for (let i = (turnEdits || []).length - 1; i >= 0; i--) {
          sendHost('restore_checkpoint', { checkpointId: turnEdits[i].checkpointId });
        }
        globalThis.showToast?.(`Undid ${files.length} agent edit${files.length === 1 ? '' : 's'}`, 'info');
        card.remove();
      }
    });
    topActions.appendChild(btnRevertAll);
  }
  head.appendChild(topActions);
  card.appendChild(head);

  if (files.length > 0) {
    const list = document.createElement('div');
    list.className = 'turn-review-list';
    files.forEach(f => {
      const row = document.createElement('div');
      row.className = 'turn-review-file-row';
      row.setAttribute('data-turn-file-path', f.path || '');

      const stateText = f.reverted ? 'Reverted' : actionLabel(f.action);
      const tone = toneForState(stateText);
      const info = document.createElement('div');
      info.className = 'turn-review-file-info';
      setHtml(info, html`
        <span class="val-badge ${tone}" data-turn-file-state>${stateText}</span>
        <span class="turn-review-file-name" title="${f.path || ''}">${f.relativePath || fileBasename(f.path)}</span>
      `);

      const rowBtns = document.createElement('div');
      rowBtns.className = 'turn-review-file-actions';

      const diffBtn = document.createElement('button');
      diffBtn.type = 'button';
      diffBtn.className = 'btn btn--quiet btn--xs';
      diffBtn.dataset.fileAct = 'diff';
      diffBtn.textContent = 'Diff';
      diffBtn.addEventListener('click', () => {
        if (summary.turnId) {
          sendHost('get_turn_file_diff', { turnId: summary.turnId, path: f.path });
        } else if (f.checkpointId) {
          sendHost('get_checkpoint_diff', { checkpointId: f.checkpointId });
        }
      });

      const acceptBtn = document.createElement('button');
      acceptBtn.type = 'button';
      acceptBtn.className = 'btn btn--quiet btn--xs';
      acceptBtn.dataset.fileAct = 'accept';
      acceptBtn.textContent = 'Accept';
      acceptBtn.addEventListener('click', () => {
        applyBadgeTone(row.querySelector('[data-turn-file-state]'), 'Accepted');
      });

      const rejectBtn = document.createElement('button');
      rejectBtn.type = 'button';
      rejectBtn.className = 'btn btn--danger btn--xs';
      rejectBtn.dataset.fileAct = 'reject';
      rejectBtn.textContent = 'Reject';
      rejectBtn.disabled = !!f.reverted;
      rejectBtn.addEventListener('click', () => {
        rejectBtn.disabled = true;
        if (summary.turnId) {
          sendHost('revert_turn_file', { turnId: summary.turnId, path: f.path });
        } else if (f.checkpointId) {
          sendHost('restore_checkpoint', { checkpointId: f.checkpointId });
          applyBadgeTone(row.querySelector('[data-turn-file-state]'), 'Reverted');
        }
      });

      rowBtns.append(diffBtn, acceptBtn, rejectBtn);
      row.append(info, rowBtns);
      list.appendChild(row);
    });
    card.appendChild(list);
  }

  if (irreversible.length > 0) {
    const warn = document.createElement('div');
    warn.className = 'turn-review-irrev';
    const summaryText = irreversible.map(a => `${a.tool}: ${a.summary}`).slice(0, 3).join('; ');
    warn.textContent = `Not reversible (${irreversible.length}): ${summaryText}`;
    card.appendChild(warn);
  }

  container.appendChild(card);
  container.scrollTop = container.scrollHeight;
}

export function openLastTurnReview() {
  if (!activeTurnSummary || !activeTurnSummary.turnId || !activeTurnSummary.files?.length) {
    globalThis.showToast?.('No recent agent turn changes to review.', 'info');
    return;
  }
  const target = activeTurnSummary.files.find(f => !f.reverted) || activeTurnSummary.files[0];
  sendHost('get_turn_file_diff', { turnId: activeTurnSummary.turnId, path: target.path });
}

function handleTurnFileDiffResponse(payload) {
  if (!payload || !payload.path) return;
  globalThis.toggleMiniWindow?.('win-diff', true);
  renderReviewToolbarAndHunks(payload);
  renderDiffEditor(payload.path, payload.beforeContent || '', payload.afterContent || '');
}

function handleTurnHunkRejected(payload) {
  if (!payload || !payload.path) return;
  renderReviewToolbarAndHunks(payload);
  renderDiffEditor(payload.path, payload.beforeContent || '', payload.afterContent || '');
  syncOpenEditorTab(payload.path, !!payload.existsNow, payload.afterContent || '');
  if (Array.isArray(payload.hunks) && payload.hunks.length === 0) {
    markFileStatusInCard(payload.turnId, payload.path, 'Reverted');
  }
  globalThis.showToast?.(`Rejected hunk in ${fileBasename(payload.path)}`, 'info');
}

function handleTurnFileReverted(payload) {
  if (!payload || !payload.path) return;
  markFileStatusInCard(payload.turnId, payload.path, 'Reverted');
  syncOpenEditorTab(payload.path, !!payload.exists, payload.content);
  if (activeTurnSummary && activeTurnSummary.turnId === payload.turnId) {
    const f = activeTurnSummary.files?.find(x => normalizePathKey(x.path) === normalizePathKey(payload.path));
    if (f) f.reverted = true;
    window.dispatchEvent(new CustomEvent('voidcode:turn-review', { detail: activeTurnSummary }));
  }
  if (activeReviewDiff && activeReviewDiff.turnId === payload.turnId && normalizePathKey(activeReviewDiff.path) === normalizePathKey(payload.path)) {
    renderReviewToolbarAndHunks({ ...activeReviewDiff, afterContent: payload.content || '', hunks: [] });
    renderDiffEditor(payload.path, activeReviewDiff.beforeContent || '', payload.content || '');
  }
  globalThis.showToast?.(`Reverted ${fileBasename(payload.path)}`, 'info');
}

function handleTurnReverted(payload) {
  if (!payload) return;
  const reverted = payload.files || [];
  const revertedPaths = Array.isArray(payload.revertedFiles) ? payload.revertedFiles : [];
  const failedPaths = Array.isArray(payload.failedFiles) ? payload.failedFiles : [];
  reverted.forEach(f => {
    markFileStatusInCard(payload.turnId, f.path, 'Reverted');
    syncOpenEditorTab(f.path, !!f.exists, f.content);
  });
  if (activeTurnSummary && activeTurnSummary.turnId === payload.turnId && Array.isArray(activeTurnSummary.files)) {
    activeTurnSummary.files.forEach(f => { f.reverted = true; });
    window.dispatchEvent(new CustomEvent('voidcode:turn-review', { detail: activeTurnSummary }));
  }
  const card = turnCardMap.get(payload.turnId);
  if (card) {
    const btn = /** @type {HTMLButtonElement | null} */ (card.querySelector('[data-act="undo-turn"]'));
    if (btn && failedPaths.length === 0) {
      btn.disabled = true;
      btn.textContent = 'Turn reverted';
    }
  }
  const count = revertedPaths.length || reverted.length;
  const irrevCount = Array.isArray(payload.irreversibleActions) ? payload.irreversibleActions.length : 0;
  const note = irrevCount > 0 ? ` (${irrevCount} shell/web action${irrevCount === 1 ? '' : 's'} not reversible)` : '';
  if (failedPaths.length > 0) {
    globalThis.showToast?.(`Reverted ${count} file(s); failed to revert: ${failedPaths.map(fileBasename).join(', ')}`, 'error');
  } else {
    globalThis.showToast?.(`Reverted ${count} file${count === 1 ? '' : 's'}${note}`, 'info');
  }
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  const wireDiffNav = () => {
    byId('btn-diff-prev-change')?.addEventListener('click', () => monacoDiffEditor?.goToDiff?.('previous'));
    byId('btn-diff-next-change')?.addEventListener('click', () => monacoDiffEditor?.goToDiff?.('next'));
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', wireDiffNav, { once: true });
  else wireDiffNav();

  window.VoidHostEvents?.register?.({
    turn_summary_response: (payload) => { if (payload?.ok && payload.summary) activeTurnSummary = payload.summary; },
    turn_file_diff_response: handleTurnFileDiffResponse,
    turn_hunk_rejected: handleTurnHunkRejected,
    turn_file_reverted: handleTurnFileReverted,
    turn_reverted: handleTurnReverted
  });

  Object.assign(globalThis, {
    openCheckpointDiff,
    renderTurnReviewCard,
    openLastTurnReview
  });

  return {
    openCheckpointDiff,
    renderTurnReviewCard,
    openLastTurnReview
  };
}
