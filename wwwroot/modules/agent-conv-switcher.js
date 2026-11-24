// Inline Agent Conversation Switcher anchored directly under the VoidAgent header title.
// Lets the operator search, switch, create, or delete chats without opening a floating modal.
import { html, setHtml } from '../core/markdown.js';

let popEl = null;

function closeSwitcher() {
  if (popEl) popEl.style.display = 'none';
}

function renderList(filterText = '') {
  const listEl = document.getElementById('conv-switcher-list');
  if (!listEl) return;
  const sessions = globalThis.getSavedSessions?.() || [];
  const activeId = globalThis.getActiveSessionId?.() || '';
  const q = filterText.trim().toLowerCase();

  const filtered = sessions.filter((s) => {
    if (!q) return true;
    return `${s.title || ''} ${s.workspace || ''}`.toLowerCase().includes(q);
  });

  setHtml(listEl, '');
  if (filtered.length === 0) {
    setHtml(listEl, html`<div class="empty-hint" style="padding:14px;text-align:center;">No saved conversations yet.</div>`);
    return;
  }

  filtered.forEach((sess) => {
    const isAct = sess.id === activeId;
    const row = document.createElement('div');
    row.style.cssText =
      'display:flex;align-items:center;gap:6px;padding:6px 8px;border-radius:8px;cursor:pointer;' +
      `border:1px solid ${isAct ? 'var(--glacier-line)' : 'transparent'};` +
      `background:${isAct ? 'var(--glacier-wash)' : 'transparent'};`;

    const mainBtn = document.createElement('button');
    mainBtn.type = 'button';
    mainBtn.style.cssText = 'flex:1;min-width:0;text-align:left;background:none;border:none;color:var(--text);cursor:pointer;padding:0;display:flex;flex-direction:column;gap:2px;';
    const msgCount = Array.isArray(sess.messages) ? sess.messages.length : 0;
    setHtml(mainBtn, html`
      <span style="font-size:12px;font-weight:${isAct ? '700' : '500'};color:${isAct ? 'var(--glacier)' : 'var(--text)'};white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${isAct ? '● ' : ''}${sess.title || 'Untitled session'}</span>
      <span style="font-size:10.5px;color:var(--text-muted);">${msgCount} msg${msgCount === 1 ? '' : 's'} · ${sess.updatedAt || ''}</span>
    `);
    mainBtn.addEventListener('click', () => {
      globalThis.loadSessionIntoChat?.(sess);
      closeSwitcher();
    });

    const delBtn = document.createElement('button');
    delBtn.type = 'button';
    delBtn.className = 'btn btn--quiet btn--xs';
    delBtn.textContent = '✕';
    delBtn.title = 'Delete conversation';
    delBtn.style.cssText = 'height:24px;min-width:24px;padding:0 5px;opacity:0.65;flex-shrink:0;';
    delBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      globalThis.deleteSessionById?.(sess.id);
      renderList(/** @type {HTMLInputElement | null} */ (document.getElementById('conv-switcher-filter'))?.value || '');
    });

    row.append(mainBtn, delBtn);
    listEl.appendChild(row);
  });
}

function ensurePopover() {
  if (popEl && document.body.contains(popEl)) return popEl;
  popEl = document.createElement('div');
  popEl.id = 'agent-conv-switcher';
  popEl.className = 'glass glass--float';
  popEl.setAttribute('role', 'dialog');
  popEl.setAttribute('aria-label', 'Switch VoidAgent Conversation');
  popEl.style.cssText =
    'display:none;position:fixed;z-index:2620;width:320px;max-height:66vh;' +
    'flex-direction:column;gap:8px;padding:10px;border-radius:12px;border:1px solid var(--seam);' +
    'box-shadow:0 14px 36px rgba(0,0,0,0.48);';

  setHtml(popEl, html`
    <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;">
      <span style="font-size:12px;font-weight:700;">Conversations</span>
      <button type="button" class="btn btn--primary btn--xs" id="btn-conv-switcher-new">+ New Chat</button>
    </div>
    <input id="conv-switcher-filter" type="text" placeholder="Search conversations…" autocomplete="off" spellcheck="false" style="width:100%;height:30px;padding:0 9px;border-radius:8px;border:1px solid var(--seam);background:rgba(0,0,0,0.24);color:var(--text);font-size:12px;" />
    <div id="conv-switcher-list" style="overflow-y:auto;display:flex;flex-direction:column;gap:3px;max-height:44vh;"></div>
  `);

  document.body.appendChild(popEl);
  document.getElementById('conv-switcher-filter')?.addEventListener('input', (e) => {
    renderList(/** @type {HTMLInputElement} */ (e.target).value || '');
  });
  document.getElementById('btn-conv-switcher-new')?.addEventListener('click', () => {
    globalThis.startNewSession?.();
    closeSwitcher();
  });

  document.addEventListener('mousedown', (e) => {
    if (!popEl || popEl.style.display === 'none') return;
    if (!popEl.contains(/** @type {Node} */ (e.target))) closeSwitcher();
  }, true);
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && popEl && popEl.style.display !== 'none') closeSwitcher();
  });

  return popEl;
}

export function openConversationSwitcher(anchorEl = null) {
  const pop = ensurePopover();
  const anchor = anchorEl || document.getElementById('active-session-title');
  pop.style.display = 'flex';
  const inp = /** @type {HTMLInputElement | null} */ (document.getElementById('conv-switcher-filter'));
  if (inp) inp.value = '';
  renderList('');

  if (anchor) {
    const r = anchor.getBoundingClientRect();
    const x = Math.max(12, Math.min(r.left, window.innerWidth - 332));
    const y = Math.min(r.bottom + 6, window.innerHeight - 280);
    pop.style.left = `${x}px`;
    pop.style.top = `${y}px`;
  }
  setTimeout(() => inp?.focus(), 20);
}

export function init() {
  const wire = () => {
    const titleEl = document.getElementById('active-session-title');
    if (titleEl) {
      titleEl.style.cursor = 'pointer';
      titleEl.title = 'Click to switch or search VoidAgent conversations';
      titleEl.addEventListener('click', (e) => {
        e.stopPropagation();
        openConversationSwitcher(titleEl);
      });
    }
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', wire, { once: true });
  else wire();

  const api = { openConversationSwitcher };
  Object.assign(globalThis, api);
  return api;
}
