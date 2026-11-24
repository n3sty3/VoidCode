// 1-Click MCP Server Gallery (Stdio & Streamable HTTP/SSE) backed by `.voidcode/mcp.json`.
import { html, setHtml } from '../core/markdown.js';

let hostRequest = (action, data, timeoutMs) => globalThis.VoidBridge.request(action, data, timeoutMs);

function ensureModal() {
  let modal = document.getElementById('mcp-gallery-modal');
  if (modal) return modal;

  modal = document.createElement('div');
  modal.id = 'mcp-gallery-modal';
  modal.className = 'modal-overlay';
  modal.style.cssText = 'display:none;position:fixed;inset:0;z-index:525;align-items:center;justify-content:center;background:rgba(5,9,15,0.68);backdrop-filter:blur(8px);';

  const card = document.createElement('div');
  card.className = 'glass glass--float';
  card.setAttribute('role', 'dialog');
  card.setAttribute('aria-label', 'MCP Server Gallery');
  card.style.cssText = 'width:min(820px,92vw);max-height:84vh;display:flex;flex-direction:column;border-radius:14px;border:1px solid var(--border-subtle,rgba(255,255,255,0.12));overflow:hidden;box-shadow:0 24px 64px rgba(0,0,0,0.55);';

  setHtml(card, html`
    <div style="display:flex;align-items:center;justify-content:space-between;padding:14px 18px;border-bottom:1px solid var(--border-subtle,rgba(255,255,255,0.08));">
      <div>
        <div style="font-size:15px;font-weight:600;">MCP Server Gallery (Stdio &amp; HTTP/SSE)</div>
        <div style="font-size:12px;opacity:0.72;">1-click install curated MCP servers into <code>.voidcode/mcp.json</code> or connect a custom remote HTTP/SSE endpoint.</div>
      </div>
      <button type="button" class="icon-btn icon-btn--sm" id="btn-close-mcp-gallery" aria-label="Close MCP Gallery">&times;</button>
    </div>
    <div style="padding:12px 18px;border-bottom:1px solid var(--border-subtle,rgba(255,255,255,0.08));display:grid;grid-template-columns:160px 1fr auto;gap:8px;">
      <input type="text" class="text-input" id="mcp-custom-id" placeholder="Custom ID (e.g. linear)" />
      <input type="text" class="text-input" id="mcp-custom-url" placeholder="Remote HTTP/SSE MCP URL (e.g. https://mcp.example.com/mcp)" />
      <button type="button" class="btn btn--primary btn--xs" id="btn-add-custom-mcp">Add HTTP MCP</button>
    </div>
    <div id="mcp-gallery-list" style="padding:14px 18px;overflow-y:auto;display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:10px;"></div>
  `);

  modal.appendChild(card);
  document.body.appendChild(modal);

  modal.addEventListener('click', (e) => { if (e.target === modal) closeMcpGallery(); });
  document.getElementById('btn-close-mcp-gallery')?.addEventListener('click', closeMcpGallery);
  document.getElementById('btn-add-custom-mcp')?.addEventListener('click', addCustomHttpServer);
  return modal;
}

async function addCustomHttpServer() {
  const idEl = /** @type {HTMLInputElement | null} */ (document.getElementById('mcp-custom-id'));
  const urlEl = /** @type {HTMLInputElement | null} */ (document.getElementById('mcp-custom-url'));
  const serverId = (idEl?.value || '').trim();
  const url = (urlEl?.value || '').trim();
  if (!serverId || !url) {
    globalThis.showToast?.('Enter both a server ID and an HTTP/HTTPS MCP URL.', 'warn');
    return;
  }
  const res = await hostRequest('mcp_gallery_install', { serverId, url }, 6000);
  const data = res?.ok ? res.data : null;
  globalThis.showToast?.(data?.message || res?.error || 'Could not install custom MCP server.', data?.ok ? 'info' : 'error');
  if (data?.ok) {
    if (idEl) idEl.value = '';
    if (urlEl) urlEl.value = '';
    renderItems(data.items || []);
  }
}

function renderItems(items) {
  const host = document.getElementById('mcp-gallery-list');
  if (!host) return;
  setHtml(host, '');

  if (!Array.isArray(items) || items.length === 0) {
    setHtml(host, html`<div class="empty-hint">No MCP servers available.</div>`);
    return;
  }

  items.forEach((item) => {
    const card = document.createElement('div');
    card.style.cssText = 'padding:10px 12px;border-radius:10px;border:1px solid var(--border-subtle,rgba(255,255,255,0.1));background:var(--surface-1,rgba(255,255,255,0.03));display:flex;flex-direction:column;gap:6px;';

    const top = document.createElement('div');
    top.style.cssText = 'display:flex;align-items:center;justify-content:space-between;gap:8px;';

    const title = document.createElement('strong');
    title.style.fontSize = '13px';
    title.textContent = item.name || item.id;

    const badge = document.createElement('span');
    badge.style.cssText = 'font-size:10px;padding:2px 6px;border-radius:999px;border:1px solid var(--border-subtle,rgba(255,255,255,0.18));opacity:0.8;';
    badge.textContent = `${item.transport || 'stdio'} · ${item.category || 'MCP'}`;
    top.append(title, badge);

    const desc = document.createElement('div');
    desc.style.cssText = 'font-size:12px;opacity:0.78;flex:1;';
    desc.textContent = item.description || '';

    const foot = document.createElement('div');
    foot.style.cssText = 'display:flex;align-items:center;justify-content:space-between;gap:8px;';

    const envEl = document.createElement('span');
    envEl.style.cssText = 'font-size:11px;opacity:0.65;';
    envEl.textContent = item.envHint ? `Env: ${item.envHint}` : (item.installed ? 'Configured in .voidcode/mcp.json' : '');

    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = item.installed ? 'btn btn--danger btn--xs' : 'btn btn--primary btn--xs';
    btn.textContent = item.installed ? 'Remove' : 'Install';
    btn.addEventListener('click', async () => {
      btn.disabled = true;
      try {
        const action = item.installed ? 'mcp_gallery_remove' : 'mcp_gallery_install';
        const res = await hostRequest(action, { serverId: item.id }, 6000);
        const data = res?.ok ? res.data : null;
        globalThis.showToast?.(data?.message || res?.error || 'MCP update failed', data?.ok ? 'info' : 'error');
        if (data?.ok) renderItems(data.items || []);
      } finally {
        btn.disabled = false;
      }
    });

    foot.append(envEl, btn);
    card.append(top, desc, foot);
    host.appendChild(card);
  });
}

export async function openMcpGallery() {
  const modal = ensureModal();
  modal.style.display = 'flex';
  const host = document.getElementById('mcp-gallery-list');
  if (host) setHtml(host, html`<div class="empty-hint">Loading MCP server catalog...</div>`);
  try {
    const res = await hostRequest('mcp_gallery_list', {}, 5000);
    renderItems(res?.ok && Array.isArray(res.data) ? res.data : []);
  } catch (err) {
    globalThis.logSwallowed?.(err, 'mcpGallery.openMcpGallery');
  }
}

export function closeMcpGallery() {
  const modal = document.getElementById('mcp-gallery-modal');
  if (modal) modal.style.display = 'none';
}

export function init(ctx = {}) {
  if (typeof ctx.requestHost === 'function') hostRequest = ctx.requestHost;
  Object.assign(globalThis, { openMcpGallery, closeMcpGallery });
  return { openMcpGallery, closeMcpGallery };
}
