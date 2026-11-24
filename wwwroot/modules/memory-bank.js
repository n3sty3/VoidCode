// Persistent Cross-Session Agent Memory Bank modal UI.
// Lets the operator inspect, filter, create, and delete project (.voidcode/memory.json)
// and global operator memories that VoidAgent automatically receives in every turn.
import { html, setHtml } from '../core/markdown.js';

let hostRequest = (action, data, timeoutMs) => globalThis.VoidBridge.request(action, data, timeoutMs);
let modalEl = null;
let currentScopeFilter = 'all';

function ensureModal() {
  if (modalEl && document.body.contains(modalEl)) return modalEl;

  const overlay = document.createElement('div');
  overlay.id = 'memory-bank-modal';
  overlay.className = 'modal-overlay';
  overlay.style.display = 'none';

  const card = document.createElement('div');
  card.className = 'glass glass--float';
  card.setAttribute('role', 'dialog');
  card.setAttribute('aria-modal', 'true');
  card.setAttribute('aria-label', 'VoidAgent Memory Bank');
  card.style.cssText = 'width:min(780px,92vw);max-height:84vh;display:flex;flex-direction:column;border-radius:16px;padding:18px;gap:12px;';

  setHtml(card, html`
    <div style="display:flex;align-items:center;justify-content:space-between;gap:12px;">
      <div>
        <div style="font-size:16px;font-weight:700;">VoidAgent Memory Bank</div>
        <div style="font-size:12px;opacity:0.75;">Durable facts, conventions, and commands injected into VoidAgent across sessions.</div>
      </div>
      <div style="display:flex;gap:6px;align-items:center;">
        <button type="button" class="btn btn--quiet btn--xs" id="btn-mem-refresh" style="white-space:nowrap;flex-shrink:0;">Refresh</button>
        <button type="button" class="icon-btn icon-btn--sm" id="btn-mem-close" aria-label="Close Memory Bank">&times;</button>
      </div>
    </div>

    <div style="display:grid;grid-template-columns:120px 130px 1fr auto;gap:8px;align-items:center;">
      <select id="mem-new-scope" class="text-input" aria-label="Memory scope" style="height:34px;color-scheme:dark;background:var(--glass-hover);color:var(--text);border:1px solid var(--seam);border-radius:8px;padding:0 8px;">
        <option value="project">Project</option>
        <option value="global">Global</option>
      </select>
      <select id="mem-new-category" class="text-input" aria-label="Memory category" style="height:34px;color-scheme:dark;background:var(--glass-hover);color:var(--text);border:1px solid var(--seam);border-radius:8px;padding:0 8px;">
        <option value="convention">Convention</option>
        <option value="command">Command</option>
        <option value="architecture">Architecture</option>
        <option value="preference">Preference</option>
        <option value="pitfall">Pitfall</option>
        <option value="fact">Fact</option>
      </select>
      <input type="text" id="mem-new-content" class="text-input" placeholder="e.g. Always run 'dotnet test -c Debug' instead of Release..." />
      <button type="button" class="btn btn--primary btn--sm" id="btn-mem-add" style="white-space:nowrap;flex-shrink:0;">Add Memory</button>
    </div>

    <div style="display:flex;gap:6px;align-items:center;">
      <button type="button" class="btn btn--quiet btn--xs" data-mem-filter="all" style="white-space:nowrap;flex-shrink:0;">All</button>
      <button type="button" class="btn btn--quiet btn--xs" data-mem-filter="project" style="white-space:nowrap;flex-shrink:0;">Project (.voidcode/memory.json)</button>
      <button type="button" class="btn btn--quiet btn--xs" data-mem-filter="global" style="white-space:nowrap;flex-shrink:0;">Global Profile</button>
    </div>

    <div id="memory-bank-list" style="overflow-y:auto;display:flex;flex-direction:column;gap:8px;min-height:180px;max-height:48vh;"></div>
  `);

  overlay.appendChild(card);
  document.body.appendChild(overlay);
  modalEl = overlay;

  overlay.addEventListener('click', (e) => {
    if (e.target === overlay) closeMemoryBankModal();
  });
  document.getElementById('btn-mem-close')?.addEventListener('click', closeMemoryBankModal);
  document.getElementById('btn-mem-refresh')?.addEventListener('click', refreshMemories);
  document.getElementById('btn-mem-add')?.addEventListener('click', saveMemoryFromInput);
  document.getElementById('mem-new-content')?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') saveMemoryFromInput();
  });
  card.querySelectorAll('[data-mem-filter]').forEach((btn) => {
    btn.addEventListener('click', () => {
      currentScopeFilter = btn.getAttribute('data-mem-filter') || 'all';
      refreshMemories();
    });
  });

  return overlay;
}

async function saveMemoryFromInput() {
  const scopeEl = /** @type {HTMLSelectElement | null} */ (document.getElementById('mem-new-scope'));
  const catEl = /** @type {HTMLSelectElement | null} */ (document.getElementById('mem-new-category'));
  const contentEl = /** @type {HTMLInputElement | null} */ (document.getElementById('mem-new-content'));
  const content = (contentEl?.value || '').trim();
  if (!content) {
    globalThis.showToast?.('Enter a memory note first', 'info');
    return;
  }

  try {
    const res = await hostRequest('memory_save', {
      scope: scopeEl?.value || 'project',
      category: catEl?.value || 'fact',
      content
    }, 5000);
    if (res?.ok && res.data?.ok) {
      if (contentEl) contentEl.value = '';
      globalThis.showToast?.(res.data.message || 'Memory saved', 'success');
      await refreshMemories();
    } else {
      globalThis.showToast?.(res?.data?.message || res?.error || 'Could not save memory', 'error');
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'memory-bank.save');
  }
}

async function deleteMemory(id) {
  try {
    const res = await hostRequest('memory_delete', { id }, 5000);
    if (res?.ok && res.data?.ok) {
      globalThis.showToast?.('Memory deleted', 'info');
      await refreshMemories();
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'memory-bank.delete');
  }
}

async function refreshMemories() {
  const listEl = document.getElementById('memory-bank-list');
  if (!listEl) return;

  try {
    const res = await hostRequest('memory_list', {}, 5000);
    const items = res?.ok && Array.isArray(res.data) ? res.data : [];
    const filtered = currentScopeFilter === 'all'
      ? items
      : items.filter((i) => i.scope === currentScopeFilter);

    setHtml(listEl, '');
    if (filtered.length === 0) {
      setHtml(listEl, html`
        <div style="padding:28px;text-align:center;opacity:0.7;font-size:12px;">
          No memories stored yet. Add one above or ask VoidAgent to remember your project's build commands and conventions.
        </div>
      `);
      return;
    }

    for (const item of filtered) {
      const row = document.createElement('div');
      row.style.cssText = 'display:flex;align-items:flex-start;justify-content:space-between;gap:10px;padding:10px 12px;border-radius:10px;border:1px solid var(--border-subtle,rgba(255,255,255,0.1));background:var(--surface-1,rgba(255,255,255,0.03));';

      const body = document.createElement('div');
      body.style.cssText = 'flex:1;min-width:0;';
      setHtml(body, html`
        <div style="display:flex;gap:6px;align-items:center;margin-bottom:4px;font-size:11px;">
          <span class="val-badge">${item.scope}</span>
          <span class="val-badge is-muted">${item.category}</span>
          <span style="opacity:0.5;font-family:monospace;">${item.id}</span>
        </div>
        <div style="font-size:13px;line-height:1.45;word-break:break-word;">${item.content}</div>
      `);
      row.appendChild(body);

      const delBtn = document.createElement('button');
      delBtn.type = 'button';
      delBtn.className = 'btn btn--quiet btn--xs';
      delBtn.style.cssText = 'white-space:nowrap;flex-shrink:0;';
      delBtn.textContent = 'Delete';
      delBtn.addEventListener('click', () => deleteMemory(item.id));
      row.appendChild(delBtn);

      listEl.appendChild(row);
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'memory-bank.refresh');
  }
}

export function openMemoryBankModal() {
  const el = ensureModal();
  el.style.display = 'flex';
  refreshMemories();
}

export function closeMemoryBankModal() {
  if (modalEl) modalEl.style.display = 'none';
}

export function init(ctx = {}) {
  if (typeof ctx.requestHost === 'function') hostRequest = ctx.requestHost;
  Object.assign(globalThis, {
    openMemoryBankModal,
    closeMemoryBankModal
  });
  return {
    openMemoryBankModal,
    closeMemoryBankModal
  };
}
