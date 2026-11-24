// Built-In API / HTTP Client Studio modal UI (Ctrl+Shift+R).
// Native Postman / Thunder Client inside VoidCode with request history, headers/JSON body,
// pretty response viewer, latency/status metrics, and 1-click "Generate Types / Tests with VoidAgent".
import { html, setHtml } from '../core/markdown.js';

let hostRequest = (action, data, timeoutMs) => globalThis.VoidBridge.request(action, data, timeoutMs);
let modalEl = null;
let lastResponse = null;
let activeRespTab = 'body';
const HISTORY_KEY = 'voidcode_http_client_history_v1';

function loadHistory() {
  try {
    const raw = localStorage.getItem(HISTORY_KEY);
    const parsed = raw ? JSON.parse(raw) : [];
    return Array.isArray(parsed) ? parsed.slice(0, 15) : [];
  } catch (err) {
    globalThis.logSwallowed?.(err, 'api-client.loadHistory');
    return [];
  }
}

function pushHistory(entry) {
  try {
    const list = loadHistory().filter((h) => !(h.method === entry.method && h.url === entry.url));
    list.unshift(entry);
    localStorage.setItem(HISTORY_KEY, JSON.stringify(list.slice(0, 15)));
  } catch (err) {
    globalThis.logSwallowed?.(err, 'api-client.pushHistory');
  }
}

function ensureModal() {
  if (modalEl && document.body.contains(modalEl)) return modalEl;

  const overlay = document.createElement('div');
  overlay.id = 'api-client-modal';
  overlay.className = 'modal-overlay';
  overlay.style.cssText = 'display:none;z-index:2600;';

  const card = document.createElement('div');
  card.className = 'glass glass--float';
  card.setAttribute('role', 'dialog');
  card.setAttribute('aria-modal', 'true');
  card.setAttribute('aria-label', 'API & HTTP Client');
  card.style.cssText = 'width:min(940px,95vw);max-height:88vh;display:flex;flex-direction:column;border-radius:16px;padding:18px;gap:10px;';

  setHtml(card, html`
    <div style="display:flex;align-items:center;justify-content:space-between;gap:12px;">
      <div>
        <div style="font-size:16px;font-weight:700;">API &amp; HTTP Client</div>
        <div style="font-size:12px;opacity:0.75;">Test REST/JSON APIs natively inside VoidCode and send responses straight to VoidAgent.</div>
      </div>
      <div style="display:flex;gap:6px;align-items:center;">
        <button type="button" class="btn btn--quiet btn--xs" id="btn-api-ai-types" style="white-space:nowrap;flex-shrink:0;">Generate Types / Tests with AI</button>
        <button type="button" class="icon-btn icon-btn--sm" id="btn-api-close" aria-label="Close API Client">&times;</button>
      </div>
    </div>

    <div style="display:flex;gap:8px;align-items:center;">
      <select id="api-method" class="text-input" aria-label="HTTP Method" style="width:110px;height:34px;font-weight:600;color-scheme:dark;background:var(--glass-hover);color:var(--text);border:1px solid var(--seam);border-radius:8px;padding:0 8px;">
        <option value="GET">GET</option>
        <option value="POST">POST</option>
        <option value="PUT">PUT</option>
        <option value="PATCH">PATCH</option>
        <option value="DELETE">DELETE</option>
        <option value="HEAD">HEAD</option>
      </select>
      <input type="text" id="api-url" class="text-input" placeholder="https://api.github.com/repos/n3sty3/VoidCode" style="flex:1;" />
      <button type="button" class="btn btn--primary btn--sm" id="btn-api-send" style="white-space:nowrap;flex-shrink:0;">Send</button>
    </div>

    <div id="api-history-bar" style="display:flex;gap:6px;flex-wrap:wrap;align-items:center;"></div>

    <div style="display:grid;grid-template-columns:1fr 1fr;gap:10px;">
      <div style="display:flex;flex-direction:column;gap:4px;">
        <label for="api-headers" style="font-size:11px;opacity:0.75;">Headers (one per line: Key: Value)</label>
        <textarea id="api-headers" rows="4" class="text-input" spellcheck="false" placeholder="Accept: application/json&#10;Authorization: Bearer ..." style="font-family:monospace;font-size:12px;resize:vertical;"></textarea>
      </div>
      <div style="display:flex;flex-direction:column;gap:4px;">
        <label for="api-body" style="font-size:11px;opacity:0.75;">Request Body (JSON / text)</label>
        <textarea id="api-body" rows="4" class="text-input" spellcheck="false" placeholder='{"hello": "voidcode"}' style="font-family:monospace;font-size:12px;resize:vertical;"></textarea>
      </div>
    </div>

    <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;padding-top:4px;border-top:1px solid var(--border-subtle,rgba(255,255,255,0.1));">
      <div style="display:flex;gap:6px;align-items:center;">
        <button type="button" class="btn btn--quiet btn--xs" data-api-resp-tab="body" style="white-space:nowrap;flex-shrink:0;">Response Body</button>
        <button type="button" class="btn btn--quiet btn--xs" data-api-resp-tab="headers" style="white-space:nowrap;flex-shrink:0;">Headers</button>
      </div>
      <div id="api-resp-meta" style="font-size:12px;font-family:monospace;opacity:0.85;">Ready</div>
    </div>

    <pre id="api-resp-output" style="margin:0;padding:12px;border-radius:10px;border:1px solid var(--border-subtle,rgba(255,255,255,0.1));background:rgba(0,0,0,0.28);font-family:monospace;font-size:12px;line-height:1.45;overflow:auto;min-height:190px;max-height:38vh;white-space:pre-wrap;word-break:break-word;">Enter a URL and click Send.</pre>
  `);

  overlay.appendChild(card);
  document.body.appendChild(overlay);
  modalEl = overlay;
  globalThis.setupCustomSelects?.();

  overlay.addEventListener('click', (e) => {
    if (e.target === overlay) closeApiClientModal();
  });
  document.getElementById('btn-api-close')?.addEventListener('click', closeApiClientModal);
  document.getElementById('btn-api-send')?.addEventListener('click', sendRequest);
  document.getElementById('btn-api-ai-types')?.addEventListener('click', sendResponseToAgent);
  document.getElementById('api-url')?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') sendRequest();
  });
  card.querySelectorAll('[data-api-resp-tab]').forEach((btn) => {
    btn.addEventListener('click', () => {
      activeRespTab = btn.getAttribute('data-api-resp-tab') || 'body';
      renderResponse();
    });
  });

  renderHistory();
  return overlay;
}

function renderHistory() {
  const bar = document.getElementById('api-history-bar');
  if (!bar) return;
  const list = loadHistory();
  setHtml(bar, '');
  if (list.length === 0) return;

  const lbl = document.createElement('span');
  lbl.style.cssText = 'font-size:11px;opacity:0.65;';
  lbl.textContent = 'Recent:';
  bar.appendChild(lbl);

  list.slice(0, 5).forEach((item) => {
    const chip = document.createElement('button');
    chip.type = 'button';
    chip.className = 'btn btn--quiet btn--xs';
    chip.style.cssText = 'white-space:nowrap;flex-shrink:0;max-width:220px;overflow:hidden;text-overflow:ellipsis;font-family:monospace;';
    chip.textContent = `${item.method} ${item.url.replace(/^https?:\/\//, '')}`;
    chip.title = `${item.method} ${item.url}`;
    chip.addEventListener('click', () => {
      const mEl = /** @type {HTMLSelectElement | null} */ (document.getElementById('api-method'));
      const uEl = /** @type {HTMLInputElement | null} */ (document.getElementById('api-url'));
      if (mEl) mEl.value = item.method;
      if (uEl) uEl.value = item.url;
    });
    bar.appendChild(chip);
  });
}

function parseHeadersText(text) {
  const headers = {};
  for (const line of (text || '').split('\n')) {
    const idx = line.indexOf(':');
    if (idx > 0) {
      const k = line.slice(0, idx).trim();
      const v = line.slice(idx + 1).trim();
      if (k) headers[k] = v;
    }
  }
  return headers;
}

function renderResponse() {
  const out = document.getElementById('api-resp-output');
  const meta = document.getElementById('api-resp-meta');
  if (!out || !meta || !lastResponse) return;

  if (!lastResponse.ok) {
    meta.textContent = `Error (${lastResponse.elapsedMs || 0}ms)`;
    out.textContent = lastResponse.error || 'Request failed.';
    return;
  }

  const sizeKb = lastResponse.sizeBytes >= 1024
    ? `${(lastResponse.sizeBytes / 1024).toFixed(1)} KB`
    : `${lastResponse.sizeBytes} B`;
  meta.textContent = `${lastResponse.statusCode} ${lastResponse.statusText} • ${lastResponse.elapsedMs}ms • ${sizeKb}`;

  if (activeRespTab === 'headers') {
    const lines = Object.entries(lastResponse.headers || {}).map(([k, v]) => `${k}: ${v}`);
    out.textContent = lines.length ? lines.join('\n') : '(no headers)';
  } else {
    out.textContent = lastResponse.body || '(empty response body)';
  }
}

async function sendRequest() {
  const method = /** @type {HTMLSelectElement | null} */ (document.getElementById('api-method'))?.value || 'GET';
  const url = (/** @type {HTMLInputElement | null} */ (document.getElementById('api-url'))?.value || '').trim();
  const headersText = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('api-headers'))?.value || '';
  const body = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('api-body'))?.value || '';
  const meta = document.getElementById('api-resp-meta');
  const out = document.getElementById('api-resp-output');

  if (!url) {
    globalThis.showToast?.('Enter a request URL first', 'info');
    return;
  }

  if (meta) meta.textContent = `Sending ${method} ${url}...`;
  if (out) out.textContent = 'Waiting for response...';

  try {
    const res = await hostRequest('http_client_send', {
      method,
      url,
      headers: parseHeadersText(headersText),
      body,
      timeoutMs: 20000
    }, 25000);
    lastResponse = res?.ok ? res.data : { ok: false, error: res?.error || 'Request failed.' };
    pushHistory({ method, url });
    renderHistory();
    renderResponse();
  } catch (err) {
    globalThis.logSwallowed?.(err, 'api-client.send');
    lastResponse = { ok: false, error: String(err?.message || err) };
    renderResponse();
  }
}

function sendResponseToAgent() {
  const url = (/** @type {HTMLInputElement | null} */ (document.getElementById('api-url'))?.value || '').trim();
  const method = /** @type {HTMLSelectElement | null} */ (document.getElementById('api-method'))?.value || 'GET';
  if (!lastResponse || !lastResponse.ok) {
    globalThis.showToast?.('Run an API request first so VoidAgent has a response to inspect', 'info');
    return;
  }
  const snippet = (lastResponse.body || '').slice(0, 6000);
  globalThis.attachContextItem?.({
    id: `http_${Date.now()}`,
    type: 'selection',
    label: `HTTP ${method} (${lastResponse.statusCode})`,
    content: `${method} ${url}\nStatus: ${lastResponse.statusCode} ${lastResponse.statusText} (${lastResponse.elapsedMs}ms)\n\n${snippet}`
  });
  closeApiClientModal();
  globalThis.toggleWorkspacePane?.('agent', true);
  const box = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
  if (box) {
    box.value = `Generate typed client models and integration test cases for this ${method} ${url} API response.`;
    box.focus();
  }
}

export function openApiClientModal(prefill = null) {
  const el = ensureModal();
  el.style.display = 'flex';
  if (prefill?.url) {
    const u = /** @type {HTMLInputElement | null} */ (document.getElementById('api-url'));
    const m = /** @type {HTMLSelectElement | null} */ (document.getElementById('api-method'));
    if (u) u.value = prefill.url;
    if (m && prefill.method) m.value = prefill.method;
  }
}

export function closeApiClientModal() {
  if (modalEl) modalEl.style.display = 'none';
}

export function init(ctx = {}) {
  if (typeof ctx.requestHost === 'function') hostRequest = ctx.requestHost;
  Object.assign(globalThis, {
    openApiClientModal,
    closeApiClientModal
  });
  return {
    openApiClientModal,
    closeApiClientModal
  };
}
