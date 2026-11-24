// 1-Click "Import from VS Code / Cursor" module.
// Mounts an unobtrusive import strip inside Settings -> Editor and exposes a standalone
// modal via Command Palette so switchers bring over their font, tabs, wrap, minimap,
// and recent folders in a single click.
import { html, setHtml } from '../core/markdown.js';

let hostRequest = (action, data, timeoutMs) => globalThis.VoidBridge.request(action, data, timeoutMs);
let modalEl = null;

function ensureModal() {
  if (modalEl && document.body.contains(modalEl)) return modalEl;

  const overlay = document.createElement('div');
  overlay.id = 'ide-import-modal';
  overlay.className = 'modal-overlay';
  overlay.style.cssText = 'display:none;z-index:2600;';

  const card = document.createElement('div');
  card.className = 'glass glass--float';
  card.setAttribute('role', 'dialog');
  card.setAttribute('aria-modal', 'true');
  card.setAttribute('aria-label', 'Import Settings from VS Code or Cursor');
  card.style.cssText = 'width:min(620px,92vw);max-height:80vh;display:flex;flex-direction:column;border-radius:16px;padding:18px;gap:12px;';

  setHtml(card, html`
    <div style="display:flex;align-items:center;justify-content:space-between;gap:12px;">
      <div>
        <div style="font-size:16px;font-weight:700;">Import from VS Code / Cursor</div>
        <div style="font-size:12px;opacity:0.75;">Imports your editor font, tab size, wrap, minimap, line numbers, and recent folders.</div>
      </div>
      <button type="button" class="icon-btn icon-btn--sm" id="btn-ide-import-close" aria-label="Close Importer">&times;</button>
    </div>

    <div id="ide-import-sources" style="display:flex;flex-direction:column;gap:8px;min-height:120px;"></div>
  `);

  overlay.appendChild(card);
  document.body.appendChild(overlay);
  modalEl = overlay;

  overlay.addEventListener('click', (e) => {
    if (e.target === overlay) closeIdeImportModal();
  });
  document.getElementById('btn-ide-import-close')?.addEventListener('click', closeIdeImportModal);
  return overlay;
}

async function applyImport(sourceId, btnEl) {
  if (btnEl) btnEl.disabled = true;
  try {
    const res = await hostRequest('ide_import_apply', { sourceId }, 8000);
    const data = res?.ok ? res.data : null;
    if (data?.ok) {
      globalThis.showToast?.(data.message || 'Settings imported', 'success');
      if (data.settings) {
        globalThis.VoidState?.set?.('settings', data.settings);
        globalThis.applySettingsToUI?.(data.settings);
      }
      closeIdeImportModal();
    } else {
      globalThis.showToast?.(data?.message || res?.error || 'Import failed', 'error');
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'ide-import.apply');
  } finally {
    if (btnEl) btnEl.disabled = false;
  }
}

async function loadSources() {
  const host = document.getElementById('ide-import-sources');
  if (!host) return;
  setHtml(host, html`<div style="padding:18px;text-align:center;opacity:0.7;font-size:12px;">Scanning for VS Code and Cursor profiles...</div>`);

  try {
    const res = await hostRequest('ide_import_detect', {}, 6000);
    const sources = res?.ok && Array.isArray(res.data) ? res.data : [];
    setHtml(host, '');

    if (sources.length === 0) {
      setHtml(host, html`<div style="padding:18px;text-align:center;opacity:0.7;font-size:12px;">No editor profiles detected on this machine.</div>`);
      return;
    }

    for (const src of sources) {
      const row = document.createElement('div');
      row.style.cssText = 'display:flex;align-items:center;justify-content:space-between;gap:12px;padding:12px;border-radius:10px;border:1px solid var(--border-subtle,rgba(255,255,255,0.1));background:var(--surface-1,rgba(255,255,255,0.03));';

      const info = document.createElement('div');
      info.style.cssText = 'min-width:0;flex:1;';
      setHtml(info, html`
        <div style="font-size:13px;font-weight:600;">${src.name}</div>
        <div style="font-size:11px;opacity:0.72;">
          ${src.found
            ? `${src.previewSettingsCount} compatible settings • ${src.recentWorkspacesCount} recent folders`
            : 'Not detected on this machine'}
        </div>
      `);
      row.appendChild(info);

      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = src.found ? 'btn btn--primary btn--sm' : 'btn btn--quiet btn--sm';
      btn.style.cssText = 'white-space:nowrap;flex-shrink:0;';
      btn.disabled = !src.found;
      btn.textContent = src.found ? 'Import Now' : 'Not Found';
      btn.addEventListener('click', () => applyImport(src.id, btn));
      row.appendChild(btn);

      host.appendChild(row);
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'ide-import.loadSources');
  }
}

function mountSettingsShortcut() {
  const editorTab = document.getElementById('tab-editor');
  if (!editorTab || document.getElementById('voidcode-ide-import-banner')) return;

  const wrap = document.createElement('div');
  wrap.id = 'voidcode-ide-import-banner';
  wrap.style.marginTop = '6px';
  setHtml(wrap, html`
    <div style="font-size:10px;font-weight:700;letter-spacing:0.08em;text-transform:uppercase;color:var(--text-disabled);padding:10px 2px 6px;border-top:1px solid var(--seam);">Migrate</div>
    <div class="control-well">
      <div style="display:flex;align-items:center;justify-content:space-between;gap:14px;width:100%;">
        <div style="min-width:0;flex:1;">
          <div style="font-size:13px;font-weight:600;margin-bottom:2px;">Switching from VS Code or Cursor?</div>
          <div style="font-size:11px;opacity:0.72;line-height:1.4;">Import your editor font, tabs, minimap, wrap, line numbers, and recent folders in one click.</div>
        </div>
        <button type="button" class="btn btn--quiet btn--sm" id="btn-open-ide-importer" style="white-space:nowrap;flex-shrink:0;">Import Settings…</button>
      </div>
    </div>
  `);
  editorTab.appendChild(wrap);
  document.getElementById('btn-open-ide-importer')?.addEventListener('click', openIdeImportModal);
}

export function openIdeImportModal() {
  const el = ensureModal();
  el.style.display = 'flex';
  loadSources();
}

export function closeIdeImportModal() {
  if (modalEl) modalEl.style.display = 'none';
}

export function init(ctx = {}) {
  if (typeof ctx.requestHost === 'function') hostRequest = ctx.requestHost;
  const boot = () => mountSettingsShortcut();
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot, { once: true });
  } else {
    boot();
  }

  Object.assign(globalThis, {
    openIdeImportModal,
    closeIdeImportModal
  });
  return {
    openIdeImportModal,
    closeIdeImportModal
  };
}
