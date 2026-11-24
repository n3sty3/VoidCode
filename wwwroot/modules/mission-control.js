// Mission Control UI: Parallel Git-Worktree Background Agents.
// Launch multiple isolated VoidAgent tasks concurrently on separate Git worktree branches,
// Monitor progress/tokens/cost live, inspect unified diffs, and 1-click Merge or Discard.
import { html, setHtml } from '../core/markdown.js';

let hostRequest = (action, data, timeoutMs) => globalThis.VoidBridge.request(action, data, timeoutMs);

let pollTimer = null;
let isOpen = false;
let latestMissions = [];

function statusColor(status) {
  switch (String(status || '').toLowerCase()) {
    case 'running':
    case 'queued':
      return '#38bdf8';
    case 'completed':
    case 'merged':
      return '#10b981';
    case 'failed':
      return '#f43f5e';
    default:
      return '#94a3b8';
  }
}

function updateStatusChip() {
  const chip = document.getElementById('status-missions');
  if (!chip) return;
  const running = latestMissions.filter((m) => m.status === 'running' || m.status === 'queued').length;
  chip.textContent = running > 0 ? `Missions (${running})` : 'Missions';
  chip.title = running > 0
    ? `${running} parallel Git-worktree agent mission(s) running — click to open Mission Control`
    : 'Mission Control — launch parallel Git-worktree background agents';
}

function mountStatusChip() {
  const bar = document.getElementById('statusbar');
  if (!bar || document.getElementById('status-missions')) return;

  const chip = document.createElement('button');
  chip.id = 'status-missions';
  chip.type = 'button';
  chip.className = 'status-item';
  chip.style.cssText = 'background:none;border:none;color:inherit;cursor:pointer;font:inherit;padding:0 6px;';
  chip.textContent = 'Missions';
  chip.addEventListener('click', openMissionControl);

  const tabAi = document.getElementById('status-tab-ai');
  if (tabAi && tabAi.parentNode) {
    tabAi.parentNode.insertBefore(chip, tabAi.nextSibling);
  } else {
    bar.appendChild(chip);
  }
  updateStatusChip();
}

function ensureModal() {
  let modal = document.getElementById('mission-control-modal');
  if (modal) return modal;

  modal = document.createElement('div');
  modal.id = 'mission-control-modal';
  modal.className = 'modal-overlay';
  modal.style.cssText = 'display:none;position:fixed;inset:0;z-index:520;align-items:center;justify-content:center;background:rgba(5,9,15,0.68);backdrop-filter:blur(8px);';

  const card = document.createElement('div');
  card.className = 'glass glass--float';
  card.setAttribute('role', 'dialog');
  card.setAttribute('aria-label', 'Mission Control — Parallel Background Agents');
  card.style.cssText = 'width:min(860px,92vw);max-height:84vh;display:flex;flex-direction:column;border-radius:14px;border:1px solid var(--border-subtle,rgba(255,255,255,0.12));overflow:hidden;box-shadow:0 24px 64px rgba(0,0,0,0.55);';

  setHtml(card, html`
    <div style="display:flex;align-items:center;justify-content:space-between;padding:14px 18px;border-bottom:1px solid var(--border-subtle,rgba(255,255,255,0.08));">
      <div>
        <div style="font-size:15px;font-weight:600;">Mission Control &mdash; Parallel Git-Worktree Agents</div>
        <div style="font-size:12px;opacity:0.72;">Launch autonomous VoidAgent tasks concurrently in isolated Git worktrees. Inspect diffs and 1-click Merge or Discard.</div>
      </div>
      <button type="button" class="icon-btn icon-btn--sm" id="btn-close-missions" aria-label="Close Mission Control">&times;</button>
    </div>
    <div style="padding:14px 18px;border-bottom:1px solid var(--border-subtle,rgba(255,255,255,0.08));display:flex;flex-direction:column;gap:8px;">
      <input type="text" class="text-input" id="mission-title-input" placeholder="Short mission title (e.g. Add unit tests for GitService)" />
      <textarea class="text-input" id="mission-prompt-input" rows="2" placeholder="Describe the autonomous task for VoidAgent to execute inside its isolated Git worktree branch..."></textarea>
      <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;">
        <span style="font-size:11px;opacity:0.7;">Each mission runs on its own <code>voidcode/mission-*</code> branch without touching your active edits.</span>
        <div style="display:flex;gap:8px;">
          <button type="button" class="btn btn--quiet btn--xs" id="btn-refresh-missions">Refresh</button>
          <button type="button" class="btn btn--primary btn--xs" id="btn-launch-mission">Launch Mission in Worktree</button>
        </div>
      </div>
    </div>
    <div id="mission-list-container" style="padding:14px 18px;overflow-y:auto;display:flex;flex-direction:column;gap:10px;flex:1;"></div>
    <pre id="mission-diff-preview" style="display:none;margin:0 18px 14px;padding:10px;max-height:220px;overflow:auto;font-size:11px;border-radius:8px;background:rgba(0,0,0,0.35);border:1px solid var(--border-subtle,rgba(255,255,255,0.08));white-space:pre-wrap;"></pre>
  `);

  modal.appendChild(card);
  document.body.appendChild(modal);

  modal.addEventListener('click', (e) => {
    if (e.target === modal) closeMissionControl();
  });
  document.getElementById('btn-close-missions')?.addEventListener('click', closeMissionControl);
  document.getElementById('btn-refresh-missions')?.addEventListener('click', refreshMissions);
  document.getElementById('btn-launch-mission')?.addEventListener('click', launchMission);

  return modal;
}

async function launchMission() {
  const titleEl = /** @type {HTMLInputElement | null} */ (document.getElementById('mission-title-input'));
  const promptEl = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('mission-prompt-input'));
  const prompt = (promptEl?.value || '').trim();
  const title = (titleEl?.value || '').trim();
  if (!prompt) {
    globalThis.showToast?.('Enter a mission prompt to launch a worktree agent.', 'warn');
    return;
  }

  const btn = /** @type {HTMLButtonElement | null} */ (document.getElementById('btn-launch-mission'));
  if (btn) btn.disabled = true;
  try {
    const res = await hostRequest('mission_start', { title, prompt }, 12000);
    const data = res?.ok ? res.data : null;
    if (!data?.ok) {
      globalThis.showToast?.(data?.message || res?.error || 'Failed to launch mission.', 'error');
      return;
    }
    if (titleEl) titleEl.value = '';
    if (promptEl) promptEl.value = '';
    globalThis.showToast?.(data.message || 'Mission launched!', 'info');
    await refreshMissions();
  } catch (err) {
    globalThis.logSwallowed?.(err, 'missionControl.launchMission');
  } finally {
    if (btn) btn.disabled = false;
  }
}

async function showMissionDiff(missionId) {
  const pre = document.getElementById('mission-diff-preview');
  if (!pre) return;
  pre.style.display = 'block';
  pre.textContent = 'Loading worktree diff...';
  try {
    const res = await hostRequest('mission_diff', { missionId }, 10000);
    const data = res?.ok ? res.data : null;
    if (!data?.ok) {
      pre.textContent = data?.error || res?.error || 'Could not compute diff.';
      return;
    }
    const header = `Branch: ${data.branch}\n${data.diffStat || '(no file changes yet)'}\n\n`;
    pre.textContent = header + (data.unifiedDiff || '(empty diff)');
  } catch (err) {
    globalThis.logSwallowed?.(err, 'missionControl.showMissionDiff');
  }
}

async function runMissionAction(action, missionId) {
  try {
    const res = await hostRequest(action, { missionId }, 15000);
    const data = res?.ok ? res.data : null;
    globalThis.showToast?.(data?.message || (res?.ok ? 'Done' : res?.error || 'Action failed'), data?.ok ? 'info' : 'error');
    const pre = document.getElementById('mission-diff-preview');
    if (pre && action === 'mission_discard') pre.style.display = 'none';
    await refreshMissions();
  } catch (err) {
    globalThis.logSwallowed?.(err, `missionControl.${action}`);
  }
}

function renderMissions(missions) {
  const list = document.getElementById('mission-list-container');
  if (!list) return;
  setHtml(list, '');

  if (!Array.isArray(missions) || missions.length === 0) {
    setHtml(list, html`<div class="empty-hint">No active or recent worktree missions. Launch one above to run VoidAgent in parallel on its own Git branch.</div>`);
    return;
  }

  missions.forEach((m) => {
    const card = document.createElement('div');
    card.style.cssText = 'padding:10px 12px;border-radius:10px;border:1px solid var(--border-subtle,rgba(255,255,255,0.1));background:var(--surface-1,rgba(255,255,255,0.03));display:flex;flex-direction:column;gap:6px;';

    const top = document.createElement('div');
    top.style.cssText = 'display:flex;align-items:center;justify-content:space-between;gap:8px;';

    const titleWrap = document.createElement('div');
    titleWrap.style.cssText = 'display:flex;align-items:center;gap:8px;min-width:0;';

    const badge = document.createElement('span');
    badge.style.cssText = `font-size:10px;font-weight:700;text-transform:uppercase;padding:2px 6px;border-radius:999px;border:1px solid ${statusColor(m.status)};color:${statusColor(m.status)};`;
    badge.textContent = m.status || 'queued';

    const title = document.createElement('strong');
    title.style.cssText = 'font-size:13px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
    title.textContent = m.title || m.id;

    const branch = document.createElement('code');
    branch.style.cssText = 'font-size:11px;opacity:0.7;';
    branch.textContent = m.branch || '';

    titleWrap.append(badge, title, branch);

    const meta = document.createElement('span');
    meta.style.cssText = 'font-size:11px;opacity:0.75;white-space:nowrap;';
    const secs = ((m.elapsedMs || 0) / 1000).toFixed(1);
    const filesCount = Array.isArray(m.changedFiles) ? m.changedFiles.length : 0;
    const costPart = m.costLabel ? ` · ${m.costLabel}` : '';
    meta.textContent = `${filesCount} file${filesCount === 1 ? '' : 's'} · ${m.toolCalls || 0} tools${costPart} · ${secs}s`;

    top.append(titleWrap, meta);

    const statusLine = document.createElement('div');
    statusLine.style.cssText = 'font-size:12px;opacity:0.82;';
    statusLine.textContent = m.error || m.lastStatus || '';

    const actions = document.createElement('div');
    actions.style.cssText = 'display:flex;align-items:center;gap:6px;flex-wrap:wrap;';

    const btnDiff = document.createElement('button');
    btnDiff.type = 'button';
    btnDiff.className = 'btn btn--quiet btn--xs';
    btnDiff.textContent = 'View Diff';
    btnDiff.addEventListener('click', () => showMissionDiff(m.id));
    actions.appendChild(btnDiff);

    if (m.status === 'running' || m.status === 'queued') {
      const btnCancel = document.createElement('button');
      btnCancel.type = 'button';
      btnCancel.className = 'btn btn--danger btn--xs';
      btnCancel.textContent = 'Cancel';
      btnCancel.addEventListener('click', () => runMissionAction('mission_cancel', m.id));
      actions.appendChild(btnCancel);
    } else if (!m.merged && m.status !== 'failed') {
      const btnMerge = document.createElement('button');
      btnMerge.type = 'button';
      btnMerge.className = 'btn btn--primary btn--xs';
      btnMerge.textContent = 'Merge into Workspace';
      btnMerge.addEventListener('click', () => runMissionAction('mission_merge', m.id));
      actions.appendChild(btnMerge);
    }

    const btnDiscard = document.createElement('button');
    btnDiscard.type = 'button';
    btnDiscard.className = 'btn btn--quiet btn--xs';
    btnDiscard.textContent = 'Discard Worktree';
    btnDiscard.addEventListener('click', () => runMissionAction('mission_discard', m.id));
    actions.appendChild(btnDiscard);

    card.append(top, statusLine, actions);
    list.appendChild(card);
  });
}

export async function refreshMissions() {
  try {
    const res = await hostRequest('mission_list', {}, 4000);
    if (res?.ok && Array.isArray(res.data)) {
      latestMissions = res.data;
      updateStatusChip();
      if (isOpen) renderMissions(latestMissions);
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'missionControl.refreshMissions');
  }
}

export function openMissionControl() {
  const modal = ensureModal();
  modal.style.display = 'flex';
  isOpen = true;
  refreshMissions();
  if (!pollTimer) {
    pollTimer = setInterval(() => {
      if (isOpen || latestMissions.some((m) => m.status === 'running' || m.status === 'queued')) {
        refreshMissions();
      }
    }, 2200);
  }
}

export function closeMissionControl() {
  const modal = document.getElementById('mission-control-modal');
  if (modal) modal.style.display = 'none';
  isOpen = false;
}

export function init(ctx = {}) {
  if (typeof ctx.requestHost === 'function') hostRequest = ctx.requestHost;

  const boot = () => mountStatusChip();
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot, { once: true });
  } else {
    boot();
  }

  Object.assign(globalThis, {
    openMissionControl,
    closeMissionControl,
    refreshMissions
  });

  return {
    openMissionControl,
    closeMissionControl,
    refreshMissions
  };
}
