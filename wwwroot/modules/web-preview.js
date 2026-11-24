// Native Tabbed Live Localhost & Web Preview Workbench (Ctrl+Shift+V):
// Preview localhost dev servers (Vite/Next/React/API) or live-edited HTML/SVG/Markdown
// with Desktop/Tablet/Mobile viewport frames, auto-detected terminal dev URLs,
// and live console error forwarding straight into VoidAgent.
import { html, raw, setHtml } from '../core/markdown.js';

let viewEl = null;
/** @type {'url' | 'active-file'} */
let currentMode = 'url';
let currentUrl = 'http://localhost:3000';
/** @type {'desktop' | 'tablet' | 'mobile'} */
let currentViewport = 'desktop';
let lastSourceFileTab = null;
let consoleOpen = false;
/** @type {Array<{ level: string, text: string, time: string }>} */
let consoleEntries = [];
/** @type {string[]} */
const detectedDevUrls = [];
let fileSyncTimer = null;
let messageBound = false;

const LOCALHOST_URL_RE = /https?:\/\/(?:localhost|127\.0\.0\.1|\[::1\]):\d{2,5}(?:\/[^\s"'<>)\]]*)?/gi;

function normalizePreviewUrl(rawInput) {
  const trimmed = String(rawInput || '').trim();
  if (!trimmed) return '';
  if (/^:\d{2,5}(\/.*)?$/.test(trimmed)) return `http://localhost${trimmed}`;
  if (/^(?:localhost|127\.0\.0\.1)(?::\d{2,5})?(\/.*)?$/i.test(trimmed)) return `http://${trimmed}`;
  try {
    const parsed = new URL(trimmed);
    if (parsed.protocol === 'http:' || parsed.protocol === 'https:') return parsed.toString();
  } catch (err) {
    globalThis.logSwallowed?.(err, 'web-preview.normalizeUrl');
  }
  return '';
}

function isLocalhostUrl(url) {
  try {
    const u = new URL(url);
    return (u.protocol === 'http:' || u.protocol === 'https:')
      && (u.hostname === 'localhost' || u.hostname === '127.0.0.1' || u.hostname === '::1' || u.hostname === '[::1]');
  } catch {
    // Invalid URL string cannot be localhost.
    return false;
  }
}

function getPreviewableFileTab() {
  const active = globalThis.VoidState?.get?.('activeTab');
  if (active && !active.isVirtual && active.path && /\.(html?|svg|md|markdown)$/i.test(active.path)) {
    lastSourceFileTab = active;
    return active;
  }
  if (lastSourceFileTab && lastSourceFileTab.model && !lastSourceFileTab.model.isDisposed?.()) {
    return lastSourceFileTab;
  }
  const openTabs = globalThis.VoidState?.get?.('openTabs') || [];
  const found = openTabs.find((t) => !t.isVirtual && t.path && /\.(html?|svg|md|markdown)$/i.test(t.path));
  if (found) lastSourceFileTab = found;
  return found || null;
}

function ensureView() {
  if (viewEl) return viewEl;

  const view = document.createElement('div');
  view.id = 'web-preview-tab';
  view.className = 'tool-tab-view web-preview-view';
  view.setAttribute('aria-label', 'Live Web Preview');

  setHtml(view, html`
    <div class="web-preview-toolbar">
      <div class="web-preview-nav-group">
        <button type="button" class="icon-btn icon-btn--sm" id="btn-wp-reload" title="Reload preview" aria-label="Reload preview">&#8635;</button>
        <input type="text" id="wp-url-input" class="text-input web-preview-url" placeholder="http://localhost:5173 or pick Active File..." spellcheck="false" autocomplete="off" />
        <button type="button" class="btn btn--primary btn--xs" id="btn-wp-go">Go</button>
      </div>
      <div class="web-preview-controls">
        <button type="button" class="btn btn--quiet btn--xs" id="btn-wp-active-file" title="Live preview active HTML/SVG/Markdown file">Active File</button>
        <div class="web-preview-vp-group" role="group" aria-label="Viewport size">
          <button type="button" class="btn btn--quiet btn--xs is-active" data-wp-vp="desktop">Desktop</button>
          <button type="button" class="btn btn--quiet btn--xs" data-wp-vp="tablet">Tablet</button>
          <button type="button" class="btn btn--quiet btn--xs" data-wp-vp="mobile">Mobile</button>
        </div>
        <button type="button" class="btn btn--quiet btn--xs" id="btn-wp-console" title="Toggle preview console">Console <span id="wp-err-badge" class="wp-err-badge" style="display:none;">0</span></button>
        <button type="button" class="btn btn--accent-soft btn--xs" id="btn-wp-send-ai" title="Send preview URL and console errors to VoidAgent">Fix in AI</button>
      </div>
    </div>

    <div class="web-preview-presets" id="wp-presets-bar">
      <span class="wp-preset-label">Quick ports:</span>
      <button type="button" class="btn btn--quiet btn--xs" data-wp-port="http://localhost:3000">:3000</button>
      <button type="button" class="btn btn--quiet btn--xs" data-wp-port="http://localhost:5173">:5173</button>
      <button type="button" class="btn btn--quiet btn--xs" data-wp-port="http://localhost:8080">:8080</button>
      <button type="button" class="btn btn--quiet btn--xs" data-wp-port="http://localhost:4200">:4200</button>
      <span id="wp-detected-ports" style="display:inline-flex;gap:5px;align-items:center;"></span>
      <span id="wp-status-label" class="wp-status-label">Ready</span>
    </div>

    <div class="web-preview-stage" id="wp-stage" data-viewport="desktop">
      <div class="web-preview-frame-shell" id="wp-frame-shell">
        <iframe id="wp-iframe" class="web-preview-iframe" title="VoidCode Live Web Preview" sandbox="allow-scripts" referrerpolicy="no-referrer"></iframe>
        <div id="wp-markdown" class="preview-markdown-body web-preview-md" style="display:none;"></div>
      </div>
    </div>

    <div class="web-preview-console" id="wp-console-drawer" style="display:none;">
      <div class="wp-console-head">
        <span>Preview Console</span>
        <div style="display:flex;gap:6px;">
          <button type="button" class="btn btn--quiet btn--xs" id="btn-wp-console-clear">Clear</button>
          <button type="button" class="btn btn--quiet btn--xs" id="btn-wp-console-close">Hide</button>
        </div>
      </div>
      <div id="wp-console-list" class="wp-console-list"></div>
    </div>
  `);

  viewEl = view;

  view.querySelector('#btn-wp-go')?.addEventListener('click', () => {
    const val = /** @type {HTMLInputElement | null} */ (view.querySelector('#wp-url-input'))?.value || '';
    navigateUrl(val);
  });
  view.querySelector('#wp-url-input')?.addEventListener('keydown', (e) => {
    if (/** @type {KeyboardEvent} */ (e).key === 'Enter') {
      const val = /** @type {HTMLInputElement | null} */ (view.querySelector('#wp-url-input'))?.value || '';
      navigateUrl(val);
    }
  });
  view.querySelector('#btn-wp-reload')?.addEventListener('click', () => refreshPreview());
  view.querySelector('#btn-wp-active-file')?.addEventListener('click', () => {
    currentMode = 'active-file';
    refreshPreview();
  });
  view.querySelectorAll('[data-wp-vp]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const vp = btn.getAttribute('data-wp-vp');
      if (vp === 'desktop' || vp === 'tablet' || vp === 'mobile') setViewport(vp);
    });
  });
  view.querySelectorAll('[data-wp-port]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const url = btn.getAttribute('data-wp-port') || '';
      if (url) navigateUrl(url);
    });
  });
  view.querySelector('#btn-wp-console')?.addEventListener('click', () => {
    consoleOpen = !consoleOpen;
    const drawer = /** @type {HTMLElement | null} */ (view.querySelector('#wp-console-drawer'));
    if (drawer) drawer.style.display = consoleOpen ? 'flex' : 'none';
  });
  view.querySelector('#btn-wp-console-close')?.addEventListener('click', () => {
    consoleOpen = false;
    const drawer = /** @type {HTMLElement | null} */ (view.querySelector('#wp-console-drawer'));
    if (drawer) drawer.style.display = 'none';
  });
  view.querySelector('#btn-wp-console-clear')?.addEventListener('click', () => {
    consoleEntries = [];
    renderConsole();
  });
  view.querySelector('#btn-wp-send-ai')?.addEventListener('click', sendPreviewContextToAgent);

  bindPreviewMessageBridge();
  return view;
}

function bindPreviewMessageBridge() {
  if (messageBound) return;
  messageBound = true;
  window.addEventListener('message', (ev) => {
    const iframe = /** @type {HTMLIFrameElement | null} */ (viewEl?.querySelector('#wp-iframe'));
    if (!iframe || ev.source !== iframe.contentWindow) return;
    const msg = ev.data;
    if (!msg || msg.__voidPreviewLog !== true) return;
    const level = msg.level === 'error' ? 'error' : (msg.level === 'warn' ? 'warn' : 'info');
    const text = String(msg.text || '').slice(0, 1200);
    if (!text) return;
    const now = new Date();
    const time = `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}:${String(now.getSeconds()).padStart(2, '0')}`;
    consoleEntries.push({ level, text, time });
    if (consoleEntries.length > 80) consoleEntries.shift();
    renderConsole();
  });
}

function renderConsole() {
  if (!viewEl) return;
  const list = viewEl.querySelector('#wp-console-list');
  const badge = /** @type {HTMLElement | null} */ (viewEl.querySelector('#wp-err-badge'));
  const errCount = consoleEntries.filter((e) => e.level === 'error').length;
  if (badge) {
    badge.textContent = String(errCount);
    badge.style.display = errCount > 0 ? 'inline-flex' : 'none';
  }
  if (!list) return;
  if (consoleEntries.length === 0) {
    setHtml(list, html`<div class="wp-console-empty">No console output captured yet (live error capture runs automatically on local HTML/SVG previews).</div>`);
    return;
  }
  setHtml(list, '');
  consoleEntries.forEach((entry) => {
    const row = document.createElement('div');
    row.className = `wp-console-row wp-console-row--${entry.level}`;
    setHtml(row, html`<span class="wp-console-time">${entry.time}</span><span class="wp-console-lvl">${entry.level.toUpperCase()}</span><span class="wp-console-msg">${entry.text}</span>`);
    list.appendChild(row);
  });
  list.scrollTop = list.scrollHeight;
}

function setViewport(vp) {
  currentViewport = vp;
  if (!viewEl) return;
  const stage = viewEl.querySelector('#wp-stage');
  if (stage) stage.setAttribute('data-viewport', vp);
  viewEl.querySelectorAll('[data-wp-vp]').forEach((b) => {
    b.classList.toggle('is-active', b.getAttribute('data-wp-vp') === vp);
  });
}

function setStatus(text) {
  const el = viewEl?.querySelector('#wp-status-label');
  if (el) el.textContent = text;
}

function renderDetectedPorts() {
  const host = viewEl?.querySelector('#wp-detected-ports');
  if (!host) return;
  setHtml(host, '');
  detectedDevUrls.forEach((url) => {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn--accent-soft btn--xs';
    btn.textContent = `Live ${url.replace(/^https?:\/\//, '')}`;
    btn.title = `Detected dev server: ${url}`;
    btn.addEventListener('click', () => navigateUrl(url));
    host.appendChild(btn);
  });
}

function buildConsoleCaptureScript() {
  return `<script src="${window.location.origin}/preview-bridge.js"></script>`;
}

function rewriteInlineScriptsForSandbox(markup) {
  return String(markup || '').replace(
    /<script(?![^>]*\bsrc\s*=)(?![^>]*\btype\s*=\s*["']?(?:application\/(?:ld\+)?json|importmap|text\/void-preview-inline))([^>]*)>/gi,
    '<script type="text/void-preview-inline"$1>'
  );
}

function renderActiveFileInPreview() {
  const tab = getPreviewableFileTab();
  const iframe = /** @type {HTMLIFrameElement | null} */ (viewEl?.querySelector('#wp-iframe'));
  const mdBox = /** @type {HTMLElement | null} */ (viewEl?.querySelector('#wp-markdown'));
  const inp = /** @type {HTMLInputElement | null} */ (viewEl?.querySelector('#wp-url-input'));
  if (!iframe || !mdBox) return;

  if (!tab || !tab.model || tab.model.isDisposed?.()) {
    setStatus('Open an HTML, SVG, or Markdown file, or enter a localhost URL');
    iframe.style.display = 'none';
    mdBox.style.display = 'block';
    setHtml(mdBox, html`<div class="empty-state"><p>No previewable HTML, SVG, or Markdown file is open.</p><p class="empty-hint">Enter a local dev server URL like <code>http://localhost:5173</code> above or open an <code>.html</code> / <code>.md</code> file.</p></div>`);
    return;
  }

  const content = tab.model.getValue();
  const ext = (tab.path.split('.').pop() || '').toLowerCase();
  if (inp) inp.value = `file://${tab.name}`;

  if (ext === 'md' || ext === 'markdown') {
    iframe.style.display = 'none';
    mdBox.style.display = 'block';
    const rendered = globalThis.renderSafeMarkdown ? globalThis.renderSafeMarkdown(content) : content;
    setHtml(mdBox, raw(rendered));
    setStatus(`Markdown · ${tab.name}`);
    return;
  }

  consoleEntries = [];
  renderConsole();
  mdBox.style.display = 'none';
  iframe.style.display = 'block';
  iframe.removeAttribute('src');
  iframe.setAttribute('sandbox', 'allow-scripts');

  const currentWorkspace = globalThis.VoidState?.get?.('workspace') || '';
  const normPath = (p) => (globalThis.normalizePath ? globalThis.normalizePath(p) : String(p || '').replace(/\//g, '\\').toLowerCase());
  let relDir = '';
  if (currentWorkspace && normPath(tab.path).startsWith(normPath(currentWorkspace))) {
    const sub = tab.path.slice(currentWorkspace.length).replace(/^[\\/]+/, '');
    const parts = sub.split(/[\\/]/);
    parts.pop();
    relDir = parts.length > 0 ? parts.join('/') + '/' : '';
  }
  const isLoopbackHost = window.location.hostname === '127.0.0.1' || window.location.hostname === 'localhost';
  const previewBasePrefix = isLoopbackHost ? `${window.location.origin}/__workspace__/` : 'http://workspace.local/';
  const headInject = `<base href="${previewBasePrefix}${relDir}">${buildConsoleCaptureScript()}`;
  const preparedContent = ext === 'svg' ? content : rewriteInlineScriptsForSandbox(content);

  let docHtml;
  if (ext === 'svg') {
    docHtml = `<!DOCTYPE html><html><head>${headInject}</head><body style="margin:0;display:grid;place-items:center;min-height:100vh;background:#0d151d;">${preparedContent}</body></html>`;
  } else if (/<head(?:\s[^>]*)?>/i.test(preparedContent)) {
    docHtml = preparedContent.replace(/<head(\s[^>]*)?>/i, (m) => `${m}${headInject}`);
  } else if (/<html(?:\s[^>]*)?>/i.test(preparedContent)) {
    docHtml = preparedContent.replace(/<html(\s[^>]*)?>/i, (m) => `${m}<head>${headInject}</head>`);
  } else {
    docHtml = `<!DOCTYPE html><html><head>${headInject}</head><body>${preparedContent}</body></html>`;
  }

  iframe.srcdoc = docHtml;
  setStatus(`Live file · ${tab.name}`);
}

function navigateUrl(rawInput) {
  const url = normalizePreviewUrl(rawInput);
  if (!url) {
    globalThis.showToast?.('Enter a valid http:// or https:// URL (e.g. localhost:5173)', 'info');
    return;
  }
  currentMode = 'url';
  currentUrl = url;
  const view = ensureView();
  const inp = /** @type {HTMLInputElement | null} */ (view.querySelector('#wp-url-input'));
  if (inp) inp.value = url;

  const iframe = /** @type {HTMLIFrameElement | null} */ (view.querySelector('#wp-iframe'));
  const mdBox = /** @type {HTMLElement | null} */ (view.querySelector('#wp-markdown'));
  if (!iframe) return;
  if (mdBox) mdBox.style.display = 'none';
  iframe.style.display = 'block';
  iframe.removeAttribute('srcdoc');
  iframe.setAttribute(
    'sandbox',
    isLocalhostUrl(url)
      ? 'allow-scripts allow-same-origin allow-forms allow-popups allow-modals'
      : 'allow-scripts allow-forms allow-popups'
  );
  setStatus(`Loading ${url}...`);
  const startedAt = Date.now();
  iframe.onload = () => setStatus(`Loaded ${url} (${Date.now() - startedAt}ms)`);
  iframe.src = url;
}

function refreshPreview() {
  if (currentMode === 'active-file') {
    renderActiveFileInPreview();
  } else {
    navigateUrl(currentUrl);
  }
}

function sendPreviewContextToAgent() {
  const targetLabel = currentMode === 'active-file'
    ? (lastSourceFileTab?.name || 'Active HTML/SVG preview')
    : currentUrl;
  const errLines = consoleEntries
    .filter((e) => e.level === 'error' || e.level === 'warn')
    .slice(-15)
    .map((e) => `[${e.time} ${e.level.toUpperCase()}] ${e.text}`)
    .join('\n');

  globalThis.attachContextItem?.({
    id: `web_preview_${Date.now()}`,
    type: 'selection',
    label: `Preview (${targetLabel})`,
    content: `Preview target: ${targetLabel}\nViewport: ${currentViewport}\n\nCaptured console output:\n${errLines || '(No console errors captured)'}`
  });
  globalThis.toggleWorkspacePane?.('agent', true);
  const box = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
  if (box) {
    box.value = errLines
      ? `Diagnose and fix the runtime errors captured in Live Preview (${targetLabel}).`
      : `Inspect the UI rendered at ${targetLabel} (${currentViewport} viewport) and help improve layout/styling.`;
    box.focus();
  }
}

export function openWebPreviewTab(opts = {}) {
  const view = ensureView();
  if (opts?.url) {
    currentMode = 'url';
    currentUrl = normalizePreviewUrl(opts.url) || currentUrl;
  } else if (opts?.mode === 'active-file') {
    currentMode = 'active-file';
    getPreviewableFileTab();
  } else if (getPreviewableFileTab() && !opts?.url) {
    currentMode = 'active-file';
  }

  const tabName = currentMode === 'active-file' && lastSourceFileTab?.name
    ? `Preview · ${lastSourceFileTab.name}`
    : 'Live Preview';

  if (globalThis.openToolTab) {
    globalThis.openToolTab({
      id: 'web-preview',
      name: tabName,
      icon: '\u25ce',
      toolKind: 'Preview',
      mountEl: view,
      onActivate: () => renderDetectedPorts()
    });
  } else {
    if (!view.isConnected) document.body.appendChild(view);
    view.style.display = 'flex';
  }

  renderDetectedPorts();
  renderConsole();
  refreshPreview();
}

export function closeWebPreviewTab() {
  globalThis.closeToolTab?.('web-preview');
}

function scanTerminalOutputForDevUrl(text) {
  if (!text || typeof text !== 'string') return;
  const matches = text.match(LOCALHOST_URL_RE);
  if (!matches) return;
  let added = false;
  for (const m of matches) {
    const clean = m.replace(/[.,;]+$/, '');
    if (!detectedDevUrls.includes(clean)) {
      detectedDevUrls.unshift(clean);
      if (detectedDevUrls.length > 4) detectedDevUrls.pop();
      added = true;
    }
  }
  if (added) renderDetectedPorts();
}

export function init() {
  window.VoidState?.subscribe?.('activeTab', (tab) => {
    if (tab && !tab.isVirtual && tab.path && /\.(html?|svg|md|markdown)$/i.test(tab.path)) {
      lastSourceFileTab = tab;
    }
  });

  window.VoidBridge?.subscribe?.((action, payload) => {
    if (action === 'terminal_output' || action === 'pty_output') {
      scanTerminalOutputForDevUrl(payload?.data || payload?.text || '');
    } else if (action === 'save_file_response' && currentMode === 'active-file' && viewEl?.isConnected) {
      clearTimeout(fileSyncTimer);
      fileSyncTimer = setTimeout(() => {
        const active = globalThis.VoidState?.get?.('activeTab');
        if (active?.toolId === 'web-preview') renderActiveFileInPreview();
      }, 150);
    }
  });

  const api = {
    openWebPreviewTab,
    closeWebPreviewTab
  };
  Object.assign(globalThis, api);
  return api;
}
