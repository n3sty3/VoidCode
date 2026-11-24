// Agent Context Inspector modal.
// Lists everything that will be sent on the next agent turn (system prompt, project rules, active
// Editor/workspace context, attached chips, conversation history, prompt draft) with token
// Estimates and one-click remove buttons.
import { html, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

function byId(id) {
  return document.getElementById(id);
}

function fmtTokens(n) {
  const v = Number(n) || 0;
  return v >= 1000 ? `${(v / 1000).toFixed(1)}k` : String(v);
}

export function refreshContextInspector() {
  const inputEl = /** @type {HTMLTextAreaElement | null} */ (byId('agent-input-text'));
  const attachments = globalThis.getAttachedContextItems ? globalThis.getAttachedContextItems() : [];
  sendHost('inspect_agent_context', {
    context: globalThis.buildAgentContext ? globalThis.buildAgentContext() : null,
    history: window.VoidState?.get?.('chatHistory') || [],
    attachments,
    promptDraft: inputEl ? inputEl.value : '',
    mode: window.VoidState?.get?.('currentAgentMode') || 'act'
  });
}

export function openContextInspector() {
  const modal = byId('context-inspector-modal');
  if (!modal) return;
  modal.style.display = 'flex';
  const body = byId('context-inspector-list');
  if (body) setHtml(body, html`<div class="empty-hint">Calculating token breakdown...</div>`);
  refreshContextInspector();
}

export function closeContextInspector() {
  const modal = byId('context-inspector-modal');
  if (modal) modal.style.display = 'none';
}

export function updateAgentTokenPill(info) {
  const pill = byId('btn-agent-token-pill');
  if (!pill) return;
  if (info && typeof info.totalTokens === 'number' && info.totalTokens > 0) {
    const pct = typeof info.percentUsed === 'number' && info.percentUsed > 0 ? ` \u00b7 ${Math.round(info.percentUsed)}%` : '';
    const cost = info.costLabel ? ` \u00b7 ${info.costLabel}` : '';
    pill.textContent = `${fmtTokens(info.totalTokens)} tok${pct}${cost}`;
    if (info.cachedTokens > 0 || info.savingsLabel) {
      pill.title = `Prompt cache: ${fmtTokens(info.cachedTokens || 0)} cached tokens${info.savingsLabel ? ` (saved ${info.savingsLabel})` : ''}`;
    }
    pill.style.display = 'inline-flex';
    return;
  }
  const hist = window.VoidState?.get?.('chatHistory') || [];
  const attached = globalThis.getAttachedContextItems ? globalThis.getAttachedContextItems() : [];
  if (hist.length === 0 && attached.length === 0) {
    pill.style.display = 'none';
    return;
  }
  const chars = hist.reduce((acc, m) => acc + String(m?.content || '').length, 0) +
    attached.reduce((acc, a) => acc + String(a?.content || a?.label || '').length, 0);
  const est = Math.max(32, Math.round(chars / 4));
  pill.textContent = `~${fmtTokens(est)} tok`;
  pill.style.display = 'inline-flex';
}

function renderContextSnapshot(payload) {
  if (!payload) return;
  updateAgentTokenPill(payload);
  const summaryEl = byId('context-inspector-summary');
  const listEl = byId('context-inspector-list');
  if (!listEl) return;

  if (summaryEl) {
    const pctNum = typeof payload.percentUsed === 'number' ? payload.percentUsed : 0;
    const pct = pctNum.toFixed(1);
    const warn = payload.willCompact ? ' \u00b7 Auto-compaction will run before next send' : '';
    summaryEl.textContent = `${payload.model || 'Model'} \u2014 ${fmtTokens(payload.totalTokens)} / ${fmtTokens(payload.inputBudget)} budget tokens (${pct}% of ${fmtTokens(payload.contextWindow)} window)${warn}`;
    const fill = byId('context-budget-fill');
    if (fill) {
      fill.style.width = `${Math.max(2, Math.min(100, pctNum))}%`;
      fill.className = 'budget-meter-fill' + (payload.willCompact || pctNum >= 85 ? ' is-danger' : pctNum >= 65 ? ' is-warn' : '');
    }
  }

  setHtml(listEl, '');
  const items = Array.isArray(payload.items) ? payload.items : [];
  if (items.length === 0) {
    setHtml(listEl, html`<div class="empty-hint">No context items active.</div>`);
    return;
  }

  items.forEach(item => {
    const row = document.createElement('div');
    row.className = 'inspector-row';
    row.dataset.contextItemId = item.id || '';

    const left = document.createElement('div');
    left.className = 'inspector-row-main';
    setHtml(left, html`
      <div class="inspector-row-title">${item.label || item.category}</div>
      <div class="inspector-row-detail" title="${item.detail || ''}">${item.detail || ''}</div>
    `);

    const right = document.createElement('div');
    right.className = 'inspector-row-actions';

    const tokBadge = document.createElement('span');
    tokBadge.className = 'val-badge val-badge--mono';
    tokBadge.textContent = `~${fmtTokens(item.tokens)} tok`;
    right.appendChild(tokBadge);

    if (item.removable) {
      const rmBtn = document.createElement('button');
      rmBtn.type = 'button';
      rmBtn.className = 'btn btn--quiet btn--xs';
      rmBtn.dataset.contextRemove = item.id || '';
      rmBtn.textContent = item.category === 'history' ? 'Clear' : 'Remove';
      rmBtn.addEventListener('click', () => {
        if (item.category === 'history') {
          const hist = window.VoidState?.get?.('chatHistory');
          if (Array.isArray(hist)) hist.length = 0;
          globalThis.startNewSession?.();
        } else if (item.id) {
          globalThis.removeAttachedContextItem?.(item.id);
        }
        refreshContextInspector();
      });
      right.appendChild(rmBtn);
    }

    row.append(left, right);
    listEl.appendChild(row);
  });
}

function setupModalEvents() {
  byId('btn-agent-token-pill')?.addEventListener('click', openContextInspector);
  const modal = byId('context-inspector-modal');
  if (!modal) return;
  byId('btn-close-context-inspector')?.addEventListener('click', closeContextInspector);
  byId('btn-refresh-context-inspector')?.addEventListener('click', refreshContextInspector);
  modal.addEventListener('click', (e) => {
    if (e.target === modal) closeContextInspector();
  });
  modal.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      e.stopPropagation();
      closeContextInspector();
    }
  });
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  window.VoidHostEvents?.register?.({
    agent_context_response: renderContextSnapshot
  });

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', setupModalEvents, { once: true });
  } else {
    setupModalEvents();
  }

  Object.assign(globalThis, {
    openContextInspector,
    closeContextInspector,
    refreshContextInspector,
    updateAgentTokenPill
  });

  return {
    openContextInspector,
    closeContextInspector,
    refreshContextInspector,
    updateAgentTokenPill
  };
}
