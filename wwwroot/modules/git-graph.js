// Visual Git Commit Graph, Stash Manager & Git Clone Repository UI.
// Provides an interactive Obsidian Glass commit graph with branch/tag pills, syntax-colored
// commit diff inspection, 1-click "Explain Commit with VoidAgent", Stash/Pop, and Git Clone.
import { escapeHtml, html, raw, setHtml } from '../core/markdown.js';

let hostRequest = (action, data, timeoutMs) => globalThis.VoidBridge.request(action, data, timeoutMs);
let graphModalEl = null;
let cloneModalEl = null;
let selectedCommit = null;
let lastCommitDiff = '';

function ensureGraphModal() {
  if (graphModalEl && document.body.contains(graphModalEl)) return graphModalEl;

  const overlay = document.createElement('div');
  overlay.id = 'git-graph-modal';
  overlay.className = 'modal-overlay';
  overlay.style.cssText = 'display:none;z-index:2600;';

  const card = document.createElement('div');
  card.className = 'glass glass--float';
  card.setAttribute('role', 'dialog');
  card.setAttribute('aria-modal', 'true');
  card.setAttribute('aria-label', 'Git Commit Graph & Stash Manager');
  card.style.cssText = 'width:min(1040px,95vw);max-height:88vh;display:flex;flex-direction:column;border-radius:16px;padding:18px;gap:10px;';

  setHtml(card, html`
    <div style="display:flex;align-items:center;justify-content:space-between;gap:12px;">
      <div>
        <div style="font-size:16px;font-weight:700;">Git Commit Graph &amp; Stash</div>
        <div style="font-size:12px;opacity:0.75;">Inspect branches, commit history diffs, and working-tree stashes visually.</div>
      </div>
      <div style="display:flex;gap:6px;align-items:center;">
        <button type="button" class="btn btn--quiet btn--xs" id="btn-gg-stash-push" style="white-space:nowrap;flex-shrink:0;">Stash Changes</button>
        <button type="button" class="btn btn--quiet btn--xs" id="btn-gg-stash-pop" style="white-space:nowrap;flex-shrink:0;">Pop Stash</button>
        <button type="button" class="btn btn--quiet btn--xs" id="btn-gg-ai-commit" style="white-space:nowrap;flex-shrink:0;">Explain Commit with AI</button>
        <button type="button" class="btn btn--quiet btn--xs" id="btn-gg-refresh" style="white-space:nowrap;flex-shrink:0;">Refresh</button>
        <button type="button" class="icon-btn icon-btn--sm" id="btn-gg-close" aria-label="Close Git Graph">&times;</button>
      </div>
    </div>

    <div id="gg-stashes-bar" style="display:none;align-items:center;gap:6px;flex-wrap:wrap;padding:6px 10px;border-radius:8px;border:1px solid var(--seam);background:rgba(255,255,255,0.03);font-size:11px;"></div>

    <div style="display:grid;grid-template-columns:minmax(320px,42%) 1fr;gap:12px;min-height:400px;max-height:66vh;">
      <div id="gg-commits-list" style="overflow-y:auto;border:1px solid var(--seam);border-radius:10px;padding:6px;background:rgba(0,0,0,0.2);display:flex;flex-direction:column;gap:4px;"></div>
      <pre id="gg-commit-diff" style="margin:0;padding:12px;border:1px solid var(--seam);border-radius:10px;background:rgba(0,0,0,0.28);font-family:var(--font-code);font-size:11px;line-height:1.45;overflow:auto;white-space:pre-wrap;word-break:break-word;">Select a commit on the left to inspect its diff.</pre>
    </div>
  `);

  overlay.appendChild(card);
  document.body.appendChild(overlay);
  graphModalEl = overlay;

  overlay.addEventListener('click', (e) => { if (e.target === overlay) closeGitGraphModal(); });
  document.getElementById('btn-gg-close')?.addEventListener('click', closeGitGraphModal);
  document.getElementById('btn-gg-refresh')?.addEventListener('click', loadCommitGraph);
  document.getElementById('btn-gg-stash-push')?.addEventListener('click', async () => {
    const msg = await globalThis.showInputDialog?.('Stash Working Changes', 'WIP snapshot', 'Stash');
    if (msg === null || msg === undefined) return;
    const res = await hostRequest('git_graph_stash_push', { message: msg }, 15000);
    globalThis.showToast?.(res?.data?.message || 'Stashed', res?.data?.ok ? 'success' : 'error');
    loadCommitGraph();
  });
  document.getElementById('btn-gg-stash-pop')?.addEventListener('click', async () => {
    const res = await hostRequest('git_graph_stash_pop', {}, 15000);
    globalThis.showToast?.(res?.data?.message || 'Stash popped', res?.data?.ok ? 'success' : 'error');
    loadCommitGraph();
  });
  document.getElementById('btn-gg-ai-commit')?.addEventListener('click', () => {
    if (!selectedCommit || !lastCommitDiff) {
      globalThis.showToast?.('Select a commit first', 'info');
      return;
    }
    globalThis.attachContextItem?.({
      id: `commit_${selectedCommit.shortHash}`,
      type: 'selection',
      label: `Commit ${selectedCommit.shortHash}`,
      content: lastCommitDiff.slice(0, 8000)
    });
    closeGitGraphModal();
    globalThis.toggleWorkspacePane?.('agent', true);
    const box = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
    if (box) {
      box.value = `Explain commit ${selectedCommit.shortHash} ("${selectedCommit.subject}") and flag any subtle bugs or regressions.`;
      box.focus();
    }
  });

  return overlay;
}

async function loadCommitGraph() {
  const listEl = document.getElementById('gg-commits-list');
  const stashesBar = document.getElementById('gg-stashes-bar');
  if (!listEl) return;
  setHtml(listEl, html`<div style="padding:18px;text-align:center;opacity:0.7;font-size:12px;">Loading commit graph...</div>`);

  try {
    const [graphRes, stashRes] = await Promise.all([
      hostRequest('git_commit_graph', { maxCount: 60 }, 15000),
      hostRequest('git_graph_stash_list', {}, 8000)
    ]);

    const stashes = stashRes?.ok && Array.isArray(stashRes.data) ? stashRes.data : [];
    if (stashesBar) {
      if (stashes.length === 0) {
        stashesBar.style.display = 'none';
      } else {
        stashesBar.style.display = 'flex';
        setHtml(stashesBar, html`<strong>Stashes (${stashes.length}):</strong>`);
        stashes.slice(0, 4).forEach((st) => {
          const b = document.createElement('button');
          b.type = 'button';
          b.className = 'btn btn--quiet btn--xs';
          b.style.cssText = 'white-space:nowrap;flex-shrink:0;font-family:var(--font-code);';
          b.textContent = `${st.ref}: ${st.message.slice(0, 32)}`;
          b.title = `Click to pop ${st.ref}`;
          b.addEventListener('click', async () => {
            const pop = await hostRequest('git_graph_stash_pop', { stashRef: st.ref }, 15000);
            globalThis.showToast?.(pop?.data?.message || 'Stash popped', pop?.data?.ok ? 'success' : 'error');
            loadCommitGraph();
          });
          stashesBar.appendChild(b);
        });
      }
    }

    const commits = graphRes?.ok && Array.isArray(graphRes.data) ? graphRes.data : [];
    setHtml(listEl, '');
    if (commits.length === 0) {
      setHtml(listEl, html`<div style="padding:18px;text-align:center;opacity:0.7;font-size:12px;">No Git commits found in this workspace.</div>`);
      return;
    }

    for (const c of commits) {
      const row = document.createElement('button');
      row.type = 'button';
      row.className = 'ctx-item';
      row.style.cssText = 'height:auto;min-height:42px;padding:7px 10px;align-items:flex-start;flex-direction:column;gap:3px;border:1px solid transparent;';

      const refBadges = (c.refs || []).slice(0, 3).map((r) =>
        `<span style="padding:1px 5px;border-radius:4px;font-size:10px;background:var(--glacier-wash);color:var(--glacier);border:1px solid var(--glacier-line);white-space:nowrap;">${escapeHtml(r)}</span>`
      ).join(' ');

      setHtml(row, html`
        <div style="display:flex;align-items:center;gap:6px;width:100%;min-width:0;">
          <span style="font-family:var(--font-code);color:var(--glacier);font-size:11px;white-space:pre;flex-shrink:0;">${c.graph || '*'}</span>
          <span style="font-family:var(--font-code);font-size:10px;opacity:0.7;flex-shrink:0;">${c.shortHash}</span>
          ${raw(refBadges)}
          <span style="flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:12px;color:var(--text);">${c.subject}</span>
        </div>
        <div style="font-size:10px;opacity:0.6;padding-left:14px;">${c.author} · ${c.date}</div>
      `);

      row.addEventListener('click', () => inspectCommit(c));
      listEl.appendChild(row);
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'git-graph.load');
  }
}

async function inspectCommit(commit) {
  selectedCommit = commit;
  const diffEl = document.getElementById('gg-commit-diff');
  if (!diffEl) return;
  diffEl.textContent = `Loading commit ${commit.shortHash}...`;

  try {
    const res = await hostRequest('git_commit_show', { hash: commit.hash }, 15000);
    const out = res?.ok && res.data?.ok ? (res.data.output || '') : (res?.data?.message || 'Could not load commit diff.');
    lastCommitDiff = out;
    const htmlLines = out.split('\n').map((line) => {
      let cls = 'diff-line';
      if (line.startsWith('+') && !line.startsWith('+++')) cls = 'diff-line diff-line--add';
      else if (line.startsWith('-') && !line.startsWith('---')) cls = 'diff-line diff-line--del';
      else if (line.startsWith('@@') || line.startsWith('diff --git')) cls = 'diff-line diff-line--meta';
      return `<span class="${cls}">${escapeHtml(line)}</span>`;
    }).join('\n');
    setHtml(diffEl, raw(htmlLines));
  } catch (err) {
    globalThis.logSwallowed?.(err, 'git-graph.inspectCommit');
  }
}

function ensureCloneModal() {
  if (cloneModalEl && document.body.contains(cloneModalEl)) return cloneModalEl;

  const overlay = document.createElement('div');
  overlay.id = 'git-clone-modal';
  overlay.className = 'modal-overlay';
  overlay.style.cssText = 'display:none;z-index:2650;';

  const card = document.createElement('div');
  card.className = 'glass glass--float';
  card.setAttribute('role', 'dialog');
  card.setAttribute('aria-modal', 'true');
  card.setAttribute('aria-label', 'Clone Git Repository');
  card.style.cssText = 'width:min(560px,92vw);display:flex;flex-direction:column;border-radius:16px;padding:18px;gap:12px;';

  setHtml(card, html`
    <div style="display:flex;align-items:center;justify-content:space-between;">
      <div>
        <div style="font-size:16px;font-weight:700;">Clone Git Repository</div>
        <div style="font-size:12px;opacity:0.75;">Clone any HTTPS or SSH repository and open it immediately in VoidCode.</div>
      </div>
      <button type="button" class="icon-btn icon-btn--sm" id="btn-gc-close" aria-label="Close Clone Dialog">&times;</button>
    </div>
    <div class="form-group">
      <label for="gc-url">Repository URL</label>
      <input type="text" id="gc-url" placeholder="https://github.com/owner/repo.git" autocomplete="off" spellcheck="false" />
    </div>
    <div class="form-group">
      <label for="gc-dest">Parent destination folder (optional — defaults to ~/Projects)</label>
      <input type="text" id="gc-dest" placeholder="Leave blank for ~/Projects" autocomplete="off" spellcheck="false" />
    </div>
    <div id="gc-status" style="font-size:12px;opacity:0.8;"></div>
    <div style="display:flex;justify-content:flex-end;gap:8px;">
      <button type="button" class="btn btn--quiet" id="btn-gc-cancel">Cancel</button>
      <button type="button" class="btn btn--primary" id="btn-gc-submit" style="white-space:nowrap;">Clone &amp; Open</button>
    </div>
  `);

  overlay.appendChild(card);
  document.body.appendChild(overlay);
  cloneModalEl = overlay;

  const close = () => { overlay.style.display = 'none'; };
  overlay.addEventListener('click', (e) => { if (e.target === overlay) close(); });
  document.getElementById('btn-gc-close')?.addEventListener('click', close);
  document.getElementById('btn-gc-cancel')?.addEventListener('click', close);
  document.getElementById('btn-gc-submit')?.addEventListener('click', runClone);
  document.getElementById('gc-url')?.addEventListener('keydown', (e) => { if (e.key === 'Enter') runClone(); });
  return overlay;
}

async function runClone() {
  const url = (/** @type {HTMLInputElement | null} */ (document.getElementById('gc-url'))?.value || '').trim();
  const dest = (/** @type {HTMLInputElement | null} */ (document.getElementById('gc-dest'))?.value || '').trim();
  const statusEl = document.getElementById('gc-status');
  const submitBtn = /** @type {HTMLButtonElement | null} */ (document.getElementById('btn-gc-submit'));
  if (!url) {
    globalThis.showToast?.('Enter a Git repository URL', 'info');
    return;
  }

  if (submitBtn) submitBtn.disabled = true;
  if (statusEl) statusEl.textContent = 'Cloning repository...';
  try {
    const res = await hostRequest('git_clone_repo', { url, destinationDir: dest }, 120000);
    if (res?.ok && res.data?.ok) {
      if (cloneModalEl) cloneModalEl.style.display = 'none';
      globalThis.showToast?.(res.data.message || 'Repository cloned', 'success');
    } else if (statusEl) {
      statusEl.textContent = res?.data?.message || res?.error || 'Clone failed.';
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'git-graph.clone');
    if (statusEl) statusEl.textContent = 'Clone failed.';
  } finally {
    if (submitBtn) submitBtn.disabled = false;
  }
}

export function openGitGraphModal() {
  const el = ensureGraphModal();
  el.style.display = 'flex';
  loadCommitGraph();
}

export function closeGitGraphModal() {
  if (graphModalEl) graphModalEl.style.display = 'none';
}

export function openGitCloneModal() {
  const el = ensureCloneModal();
  el.style.display = 'flex';
  const statusEl = document.getElementById('gc-status');
  if (statusEl) statusEl.textContent = '';
  document.getElementById('gc-url')?.focus();
}

export function init(ctx = {}) {
  if (typeof ctx.requestHost === 'function') hostRequest = ctx.requestHost;
  Object.assign(globalThis, {
    openGitGraphModal,
    closeGitGraphModal,
    openGitCloneModal
  });
  return {
    openGitGraphModal,
    closeGitGraphModal,
    openGitCloneModal
  };
}
