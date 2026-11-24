// Chat session persistence/history, agent timeline & checkpoints,
// Custom playbooks, live HTML/SVG/Markdown preview, and chat/preview image viewer.
import { html, raw, renderSafeMarkdown, setHtml } from '../core/markdown.js';
import { get as stateGet, set as stateSet } from '../core/state.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

function normPath(p) {
  return globalThis.normalizePath ? globalThis.normalizePath(p) : String(p || '').replace(/\//g, '\\').toLowerCase();
}

const DEFAULT_PLAYBOOKS = [
  {
    id: 'pb_re',
    title: 'Binary & PE structure audit',
    prompt: 'Scan the workspace for compiled binaries (.exe, .dll), run inspect_binary on each, and summarize their architecture, sections, hashes, and notable strings.'
  },
  {
    id: 'pb_sec',
    title: 'Security & vulnerability review',
    prompt: 'Search and read the codebase to identify memory safety bugs, injection vectors, hardcoded secrets, or race conditions. Provide exact file and line citations and patch fixes.'
  },
  {
    id: 'pb_arch',
    title: 'Codebase architecture map',
    prompt: 'Inspect the directory tree and core entry points. Produce a concise architectural breakdown of modules, data flow, and external dependencies.'
  },
  {
    id: 'pb_perf',
    title: 'Zero-allocation & performance pass',
    prompt: 'Analyze the active file or hot paths in the workspace for unnecessary allocations, lock contention, or O(N^2) bottlenecks, and patch them for maximum throughput.'
  },
  {
    id: 'pb_build',
    title: 'Build & fix all compiler errors',
    prompt: 'Run the project build command in the terminal, inspect any compiler or linker errors, patch the offending files, and re-run until the build succeeds cleanly.'
  }
];

let savedSessions = [];
let activeSessionId = 'sess_' + Date.now();
let timelineEntries = [];
let checkpointCount = 0;
let playbooks = [...DEFAULT_PLAYBOOKS];
let previewDebounceTimer = null;

// ==========================================
// PERSISTENT SESSIONS & PLAYBOOKS
// ==========================================
export function loadStoredState() {
  try {
    const rawSessions = localStorage.getItem('voidcode_sessions_v2');
    savedSessions = rawSessions ? JSON.parse(rawSessions) : [];
  } catch (err) {
    globalThis.logSwallowed?.(err, 'workspace-tools.loadSessions');
    savedSessions = [];
  }

  try {
    const rawPlaybooks = localStorage.getItem('voidcode_playbooks_v2');
    playbooks = rawPlaybooks ? JSON.parse(rawPlaybooks) : [...DEFAULT_PLAYBOOKS];
  } catch (err) {
    globalThis.logSwallowed?.(err, 'workspace-tools.loadPlaybooks');
    playbooks = [...DEFAULT_PLAYBOOKS];
  }
}

export function hydrateSessions(sessions) {
  if (!Array.isArray(sessions) || sessions.length === 0) return;
  savedSessions = sessions;
  renderHistorySessions();
}

export function hydrateCheckpoints(checkpoints) {
  if (!Array.isArray(checkpoints) || checkpoints.length === 0 || timelineEntries.length > 0) return;
  checkpoints.forEach(cp => {
    timelineEntries.push({
      id: 'tl_' + cp.checkpointId,
      verb: cp.verb || 'Edited',
      target: cp.target || 'workspace',
      time: cp.time || '',
      checkpointId: cp.checkpointId,
      reverted: false
    });
  });
  checkpointCount = checkpoints.length;
  const statusEl = document.getElementById('status-checkpoints');
  if (statusEl) {
    statusEl.textContent = `${checkpointCount} checkpoint${checkpointCount === 1 ? '' : 's'}`;
  }
  renderTimelineEntries();
}

export function saveCurrentSessionToStorage() {
  const chatHistory = stateGet('chatHistory', []);
  if (chatHistory.length === 0) return;
  const firstUser = chatHistory.find(m => m.role === 'user');
  const title = firstUser
    ? firstUser.content.replace(/\[Referenced File:.*?\]\s*/g, '').slice(0, 46)
    : 'Session';

  const existingIdx = savedSessions.findIndex(s => s.id === activeSessionId);
  const entry = {
    id: activeSessionId,
    title: title || 'Untitled session',
    updatedAt: new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
    workspace: stateGet('workspace') || '',
    messages: [...chatHistory]
  };

  if (existingIdx >= 0) savedSessions[existingIdx] = entry;
  else savedSessions.unshift(entry);

  savedSessions = savedSessions.slice(0, 40);
  try {
    localStorage.setItem('voidcode_sessions_v2', JSON.stringify(savedSessions));
  } catch (err) { globalThis.logSwallowed?.(err, 'workspace-tools.saveSessions'); }
  sendHost('save_session', { session: entry });

  const titleEl = document.getElementById('active-session-title');
  if (titleEl) titleEl.textContent = entry.title;
  globalThis.updateAgentTokenPill?.();
  renderHistorySessions();
}

export function renderHistorySessions() {
  const container = document.getElementById('history-sessions-list');
  if (!container) return;
  setHtml(container, '');

  if (savedSessions.length === 0) {
    setHtml(container, '<div class="empty-state"><p>No saved conversations yet.</p><p class="empty-hint">Send a prompt to the agent and your session will appear here automatically.</p></div>');
    return;
  }

  savedSessions.forEach(sess => {
    const isActive = sess.id === activeSessionId;
    const item = document.createElement('div');
    item.className = 'win-item' + (isActive ? ' is-active' : '');
    setHtml(item, html`
      <div class="win-item-head">
        <div class="win-item-title-group">
          <span class="win-item-title">${sess.title}</span>
          ${isActive ? raw('<span class="val-badge is-ok">Active</span>') : ''}
        </div>
        <div class="win-item-actions">
          <button class="btn ${isActive ? 'btn--quiet' : 'btn--accent-soft'} btn--xs" data-act="open" type="button">Load session</button>
          <button class="btn btn--quiet btn--xs" data-act="delete" type="button">Delete</button>
        </div>
      </div>
      <div class="win-item-meta">
        <span class="val-badge val-badge--mono">${sess.messages.length} msgs</span>
        <span>\u00b7 Updated ${sess.updatedAt}</span>
      </div>
    `);

    item.querySelector('[data-act="open"]')?.addEventListener('click', () => {
      loadSessionIntoChat(sess);
    });

    item.querySelector('[data-act="delete"]')?.addEventListener('click', () => {
      savedSessions = savedSessions.filter(s => s.id !== sess.id);
      try { localStorage.setItem('voidcode_sessions_v2', JSON.stringify(savedSessions)); } catch (err) { globalThis.logSwallowed?.(err, 'workspace-tools.deleteSession.persist'); }
      sendHost('delete_session', { sessionId: sess.id });
      renderHistorySessions();
    });

    container.appendChild(item);
  });
}

export function loadSessionIntoChat(sess) {
  activeSessionId = sess.id;
  const msgs = [...sess.messages];
  stateSet('chatHistory', msgs);
  const titleEl = document.getElementById('active-session-title');
  if (titleEl) titleEl.textContent = sess.title;

  const container = document.getElementById('agent-chat-container');
  if (container) {
    setHtml(container, '');
    msgs.forEach(m => {
      const div = document.createElement('div');
      div.className = 'chat-msg ' + (m.role === 'user' ? 'user' : 'assistant');
      if (m.role === 'user') {
        setHtml(div, html`<div class="msg-content">${m.content}</div>`);
      } else {
        const rendered = renderSafeMarkdown(m.content);
        setHtml(div, raw(`<div class="msg-content">${rendered}</div>`));
      }
      container.appendChild(div);
    });
    container.scrollTop = container.scrollHeight;
  }
  renderHistorySessions();
  globalThis.updateAgentTokenPill?.();
  globalThis.showToast?.(`Loaded "${sess.title}"`, 'info');
}

export function startNewSession() {
  activeSessionId = 'sess_' + Date.now();
  stateSet('chatHistory', []);
  globalThis.updateAgentTokenPill?.();
  const titleEl = document.getElementById('active-session-title');
  if (titleEl) titleEl.textContent = 'Agent';
  setHtml(document.getElementById('agent-chat-container'), `
    <div class="agent-welcome-card">
      <h2>What should we build?</h2>
      <p>Ask for a multi-file edit, point at context with <kbd>@</kbd>, or start from one of these.</p>
      <div class="agent-starter-grid">
        <button type="button" class="agent-starter-pill" data-quick-prompt="@folder Explain the project architecture and entry points.">
          <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.75" aria-hidden="true"><circle cx="12" cy="12" r="9"/><path d="M12 8v4l3 2"/></svg>
          <span>Explain project architecture</span>
        </button>
        <button type="button" class="agent-starter-pill" data-quick-prompt="@git Review my working tree changes for bugs or edge cases.">
          <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.75" aria-hidden="true"><line x1="6" x2="6" y1="3" y2="15"/><circle cx="18" cy="6" r="3"/><circle cx="6" cy="18" r="3"/><path d="M18 9a9 9 0 0 1-9 9"/></svg>
          <span>Review uncommitted git diff</span>
        </button>
        <button type="button" class="agent-starter-pill" data-quick-prompt="@problems Diagnose and fix the active workspace problems.">
          <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.75" aria-hidden="true"><path d="m12 3-1.9 5.8a2 2 0 0 1-1.3 1.3L3 12l5.8 1.9a2 2 0 0 1 1.3 1.3L12 21l1.9-5.8a2 2 0 0 1 1.3-1.3L21 12l-5.8-1.9a2 2 0 0 1-1.3-1.3z"/></svg>
          <span>Diagnose &amp; fix problems</span>
        </button>
        <button type="button" class="agent-starter-pill" data-quick-prompt="Write comprehensive unit tests for the active file.">
          <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.75" aria-hidden="true"><path d="m9 11 3 3L22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/></svg>
          <span>Generate unit tests</span>
        </button>
      </div>
    </div>
  `);
  renderHistorySessions();
}

// ==========================================
// TIMELINE & CHECKPOINTS WINDOW
// ==========================================
export function recordTimelineEvent(verb, target, checkpointId) {
  if (checkpointId) {
    checkpointCount += 1;
    const statusEl = document.getElementById('status-checkpoints');
    if (statusEl) {
      statusEl.textContent = `${checkpointCount} checkpoint${checkpointCount === 1 ? '' : 's'}`;
    }
  }

  timelineEntries.unshift({
    id: 'tl_' + Date.now() + '_' + Math.random().toString(36).slice(2, 6),
    verb,
    target: target || 'workspace',
    time: new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }),
    checkpointId,
    reverted: false
  });

  renderTimelineEntries();
}

export function clearTimelineEntries() {
  timelineEntries = [];
  renderTimelineEntries();
}

export function renderTimelineEntries() {
  const container = document.getElementById('timeline-entries-list');
  if (!container) return;
  setHtml(container, '');

  if (timelineEntries.length === 0) {
    setHtml(container, '<div class="empty-state"><p>No tool actions recorded yet.</p><p class="empty-hint">Every file edit, command, and search run by the agent is logged here with instant rollback.</p></div>');
    return;
  }

  timelineEntries.forEach(entry => {
    const item = document.createElement('div');
    item.className = 'win-item';
    const fileName = entry.target.split(/[\\/]/).pop() || entry.target;
    const verbTone = entry.reverted ? 'is-muted' : /create|save/i.test(entry.verb) ? 'is-ok' : /delete/i.test(entry.verb) ? 'is-danger' : '';
    setHtml(item, html`
      <div class="win-item-head">
        <div class="win-item-title-group">
          <span class="val-badge ${verbTone}">${entry.reverted ? 'Reverted' : entry.verb}</span>
          <span class="win-item-title">${fileName}</span>
        </div>
        <span class="win-item-meta">${entry.time}</span>
      </div>
      <div class="win-item-foot">
        <span class="win-item-path" title="${entry.target}">${entry.target}</span>
      </div>
    `);

    if (entry.checkpointId) {
      const actions = document.createElement('div');
      actions.className = 'win-item-actions';

      const diffBtn = document.createElement('button');
      diffBtn.type = 'button';
      diffBtn.className = 'btn btn--quiet btn--xs btn-undo-checkpoint';
      diffBtn.textContent = 'View diff';
      diffBtn.addEventListener('click', () => {
        sendHost('get_checkpoint_diff', { checkpointId: entry.checkpointId });
      });
      actions.appendChild(diffBtn);

      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'btn btn--quiet btn--xs btn-undo-checkpoint';
      btn.textContent = entry.reverted ? 'Reverted' : 'Undo edit';
      btn.disabled = entry.reverted;
      btn.addEventListener('click', () => {
        sendHost('restore_checkpoint', { checkpointId: entry.checkpointId });
        entry.reverted = true;
        renderTimelineEntries();
      });
      actions.appendChild(btn);
      item.querySelector('.win-item-foot')?.appendChild(actions);
    }

    container.appendChild(item);
  });
}

// ==========================================
// PLAYBOOKS WINDOW
// ==========================================
export function addCustomPlaybook(title, promptText) {
  playbooks.unshift({
    id: 'pb_' + Date.now(),
    title,
    prompt: promptText,
    custom: true
  });
  try { localStorage.setItem('voidcode_playbooks_v2', JSON.stringify(playbooks)); } catch (err) { globalThis.logSwallowed?.(err, 'workspace-tools.savePlaybook.persist'); }
  renderPlaybooks();
}

export function renderPlaybooks() {
  const container = document.getElementById('playbooks-list');
  if (!container) return;
  setHtml(container, '');

  playbooks.forEach(pb => {
    const item = document.createElement('div');
    item.className = 'win-item';
    setHtml(item, html`
      <div class="win-item-head">
        <span class="win-item-title">${pb.title}</span>
        <div class="win-item-actions">
          <button class="btn btn--quiet btn--xs" data-act="insert" title="Insert prompt into composer">Insert</button>
          <button class="btn btn--accent-soft btn--xs" data-act="run" title="Run playbook now">
            <svg viewBox="0 0 24 24" width="10" height="10" fill="currentColor" aria-hidden="true"><path d="M6 4l14 8-14 8z"/></svg>
            <span>Run</span>
          </button>
          ${pb.custom ? raw('<button class="btn btn--quiet btn--xs" data-act="del">Remove</button>') : ''}
        </div>
      </div>
      <div class="win-item-meta playbook-desc">${pb.prompt}</div>
    `);

    item.querySelector('[data-act="run"]')?.addEventListener('click', () => {
      const box = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
      if (box) box.value = pb.prompt;
      globalThis.toggleMiniWindow?.('win-prompts', false);
      globalThis.toggleWorkspacePane?.('agent', true);
      document.getElementById('btn-send-agent')?.click();
    });

    item.querySelector('[data-act="insert"]')?.addEventListener('click', () => {
      const box = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
      if (box) box.value = pb.prompt;
      globalThis.toggleWorkspacePane?.('agent', true);
      box?.focus();
      globalThis.toggleMiniWindow?.('win-prompts', false);
    });

    const delBtn = item.querySelector('[data-act="del"]');
    if (delBtn) {
      delBtn.addEventListener('click', () => {
        playbooks = playbooks.filter(x => x.id !== pb.id);
        try { localStorage.setItem('voidcode_playbooks_v2', JSON.stringify(playbooks)); } catch (err) { globalThis.logSwallowed?.(err, 'workspace-tools.deletePlaybook.persist'); }
        renderPlaybooks();
      });
    }

    container.appendChild(item);
  });
}

// ==========================================
// LIVE PREVIEW & IMAGE VIEWER
// ==========================================
export function scheduleLivePreviewSync() {
  const win = document.getElementById('win-preview');
  if (!win || win.style.display === 'none') return;
  clearTimeout(previewDebounceTimer);
  previewDebounceTimer = setTimeout(() => {
    renderLivePreviewFromActiveTab();
  }, 240);
}

export function renderLivePreviewFromActiveTab() {
  const activeTab = stateGet('activeTab');
  const monacoEditor = globalThis.getMonacoEditor?.();
  if (!activeTab || !monacoEditor) return;
  const ext = activeTab.path.split('.').pop().toLowerCase();
  const content = monacoEditor.getValue();
  const iframe = /** @type {HTMLIFrameElement | null} */ (document.getElementById('preview-iframe'));
  const mdBox = document.getElementById('preview-markdown');
  const imgStage = document.getElementById('preview-image-stage');
  const emptyBox = document.getElementById('preview-empty');
  const titleEl = document.getElementById('preview-win-title');
  const badgeEl = document.getElementById('preview-mode-badge');

  const win = document.getElementById('win-preview');
  const btnViewport = document.getElementById('btn-preview-viewport');
  const btnRefresh = document.getElementById('btn-preview-refresh');

  if (titleEl) titleEl.textContent = activeTab.name;
  const currentWorkspace = stateGet('workspace') || '';

  if (['html', 'htm', 'svg'].includes(ext) && iframe) {
    if (win) win.dataset.previewMode = 'web';
    if (btnViewport) btnViewport.style.display = '';
    if (btnRefresh) btnRefresh.style.display = '';
    iframe.style.display = 'block';
    if (mdBox) mdBox.style.display = 'none';
    if (imgStage) imgStage.style.display = 'none';
    if (emptyBox) emptyBox.style.display = 'none';
    if (badgeEl) badgeEl.textContent = ext.toUpperCase();

    let relDir = '';
    if (currentWorkspace && normPath(activeTab.path).startsWith(normPath(currentWorkspace))) {
      const sub = activeTab.path.slice(currentWorkspace.length).replace(/^[\\/]+/, '');
      const parts = sub.split(/[\\/]/);
      parts.pop();
      relDir = parts.length > 0 ? parts.join('/') + '/' : '';
    }
    const isLoopbackHost = window.location.hostname === '127.0.0.1' || window.location.hostname === 'localhost';
    const previewOrigin = isLoopbackHost ? window.location.origin : 'http://workspace.local';
    const previewBasePrefix = isLoopbackHost ? `${window.location.origin}/__workspace__/` : 'http://workspace.local/';
    const previewPolicy = `<meta http-equiv="Content-Security-Policy" content="default-src ${previewOrigin} data: blob:; script-src 'unsafe-inline' 'unsafe-eval' ${previewOrigin}; style-src 'unsafe-inline' ${previewOrigin}; img-src data: blob: ${previewOrigin}; font-src data: ${previewOrigin}; connect-src ${previewOrigin}; object-src 'none'; form-action 'none'">`;
    const baseTag = `<base href="${previewBasePrefix}${relDir}">`;
    const previewHead = `${previewPolicy}${baseTag}`;
    let docHtml;
    if (ext === 'svg') {
      docHtml = `<!DOCTYPE html><html><head>${previewHead}</head><body style="margin:0;display:grid;place-items:center;min-height:100vh;background:#0d151d;">${content}</body></html>`;
    } else if (/<head(?:\s[^>]*)?>/i.test(content)) {
      docHtml = content.replace(/<head(\s[^>]*)?>/i, (match) => `${match}${previewHead}`);
    } else if (/<html(?:\s[^>]*)?>/i.test(content)) {
      docHtml = content.replace(/<html(\s[^>]*)?>/i, (match) => `${match}<head>${previewHead}</head>`);
    } else {
      docHtml = `<!DOCTYPE html><html><head>${previewHead}</head><body>${content}</body></html>`;
    }

    iframe.srcdoc = docHtml;
  } else if (['md', 'markdown'].includes(ext) && mdBox) {
    if (win) win.dataset.previewMode = 'markdown';
    if (btnViewport) btnViewport.style.display = 'none';
    if (btnRefresh) btnRefresh.style.display = '';
    if (iframe) iframe.style.display = 'none';
    mdBox.style.display = 'block';
    if (imgStage) imgStage.style.display = 'none';
    if (emptyBox) emptyBox.style.display = 'none';
    if (badgeEl) badgeEl.textContent = 'Markdown';
    setHtml(mdBox, raw(renderSafeMarkdown(content)));
    renderMermaidBlocksInPreview(mdBox);
  }
}

function renderMermaidBlocksInPreview(root) {
  if (!root) return;
  root.querySelectorAll('pre > code.language-mermaid').forEach((codeEl) => {
    const src = (codeEl.textContent || '').trim();
    const pre = codeEl.parentElement;
    if (!pre || !src) return;
    const edges = [];
    const nodes = new Map();
    const edgeRe = /([A-Za-z0-9_]+)(?:\["?([^"\]]+)"?\])?\s*(?:-->|---|\.-+>)\s*(?:\|([^|]+)\|\s*)?([A-Za-z0-9_]+)(?:\["?([^"\]]+)"?\])?/g;
    let m;
    while ((m = edgeRe.exec(src)) !== null) {
      const a = m[1];
      const b = m[4];
      if (!nodes.has(a)) nodes.set(a, m[2] || a);
      else if (m[2]) nodes.set(a, m[2]);
      if (!nodes.has(b)) nodes.set(b, m[5] || b);
      else if (m[5]) nodes.set(b, m[5]);
      edges.push({ from: a, to: b, label: (m[3] || '').trim() });
    }
    if (nodes.size === 0 || edges.length === 0) return;

    const keys = Array.from(nodes.keys());
    const colW = 190;
    const rowH = 74;
    const cols = Math.min(3, Math.max(1, keys.length));
    const coords = new Map();
    keys.forEach((k, i) => {
      const col = i % cols;
      const row = Math.floor(i / cols);
      coords.set(k, { x: 24 + col * colW, y: 24 + row * rowH });
    });
    const width = Math.max(360, cols * colW + 40);
    const height = Math.max(120, (Math.ceil(keys.length / cols)) * rowH + 36);

    const svgNS = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(svgNS, 'svg');
    svg.setAttribute('viewBox', `0 0 ${width} ${height}`);
    svg.style.cssText = 'width:100%;height:auto;border-radius:10px;border:1px solid var(--seam);background:rgba(10,16,24,0.75);margin:8px 0;display:block;';

    for (const e of edges) {
      const p1 = coords.get(e.from);
      const p2 = coords.get(e.to);
      if (!p1 || !p2) continue;
      const line = document.createElementNS(svgNS, 'line');
      line.setAttribute('x1', String(p1.x + 76));
      line.setAttribute('y1', String(p1.y + 18));
      line.setAttribute('x2', String(p2.x + 76));
      line.setAttribute('y2', String(p2.y + 18));
      line.setAttribute('stroke', '#8fd3c1');
      line.setAttribute('stroke-width', '1.6');
      line.setAttribute('stroke-opacity', '0.7');
      svg.appendChild(line);
    }

    for (const k of keys) {
      const pos = coords.get(k);
      const rect = document.createElementNS(svgNS, 'rect');
      rect.setAttribute('x', String(pos.x));
      rect.setAttribute('y', String(pos.y));
      rect.setAttribute('width', '152');
      rect.setAttribute('height', '36');
      rect.setAttribute('rx', '8');
      rect.setAttribute('fill', '#162230');
      rect.setAttribute('stroke', '#8fd3c1');
      rect.setAttribute('stroke-opacity', '0.45');
      svg.appendChild(rect);

      const text = document.createElementNS(svgNS, 'text');
      text.setAttribute('x', String(pos.x + 76));
      text.setAttribute('y', String(pos.y + 22));
      text.setAttribute('text-anchor', 'middle');
      text.setAttribute('fill', '#e6edf3');
      text.setAttribute('font-size', '11');
      const lbl = String(nodes.get(k) || k);
      text.textContent = lbl.length > 20 ? `${lbl.slice(0, 20)}…` : lbl;
      svg.appendChild(text);
    }

    pre.replaceWith(svg);
  });
}

export function renderChatImage(path, dataUri, sizeBytes) {
  const container = document.getElementById('agent-chat-container');
  if (!container || !dataUri) return;

  const name = (path || '').split(/[\\/]/).pop() || 'image';
  const fig = document.createElement('figure');
  fig.className = 'chat-image';
  setHtml(fig, html`<img alt="${name}"><figcaption>${name}${sizeBytes ? ' \u00b7 ' + (sizeBytes / 1024).toFixed(0) + ' KB' : ''}</figcaption>`);

  const img = fig.querySelector('img');
  if (img) {
    img.src = dataUri;
    img.addEventListener('click', () => openImagePreview(path, dataUri, sizeBytes));
  }

  const streamBody = globalThis.getStreamMessageEl?.()?.querySelector('.msg-content') || null;
  (streamBody || container).appendChild(fig);
  container.scrollTop = container.scrollHeight;
}

export function openImagePreview(path, dataUri, sizeBytes) {
  const fileName = path.split(/[\\/]/).pop() || path;
  const iframe = document.getElementById('preview-iframe');
  const mdBox = document.getElementById('preview-markdown');
  const imgStage = document.getElementById('preview-image-stage');
  const emptyBox = document.getElementById('preview-empty');
  const holder = document.getElementById('preview-image-holder');
  const meta = document.getElementById('preview-image-meta');

  const win = document.getElementById('win-preview');
  const btnViewport = document.getElementById('btn-preview-viewport');
  const btnRefresh = document.getElementById('btn-preview-refresh');
  if (win) win.dataset.previewMode = 'image';
  if (btnViewport) btnViewport.style.display = 'none';
  if (btnRefresh) btnRefresh.style.display = 'none';

  if (iframe) iframe.style.display = 'none';
  if (mdBox) mdBox.style.display = 'none';
  if (imgStage) imgStage.style.display = 'flex';
  if (emptyBox) emptyBox.style.display = 'none';

  const titleEl = document.getElementById('preview-win-title');
  const badgeEl = document.getElementById('preview-mode-badge');
  if (titleEl) titleEl.textContent = fileName;
  if (badgeEl) badgeEl.textContent = 'Image';

  if (holder) {
    setHtml(holder, '');
    const img = document.createElement('img');
    img.src = dataUri;
    img.alt = fileName;
    img.onload = () => {
      const kb = (sizeBytes / 1024).toFixed(1);
      if (meta) meta.textContent = `${img.naturalWidth} \u00d7 ${img.naturalHeight} px \u00b7 ${kb} KB \u00b7 ${path}`;
    };
    holder.appendChild(img);
  }
  globalThis.toggleMiniWindow?.('win-preview', true);
}

export function setupWorkspaceTools() {
  document.getElementById('btn-new-session')?.addEventListener('click', () => {
    startNewSession();
    globalThis.clearSessionChangedFiles?.();
    globalThis.VoidBridge?.request?.('agent_clear_trace', {});
    globalThis.showToast?.('Started new conversation', 'info');
  });

  document.getElementById('btn-clear-timeline')?.addEventListener('click', () => {
    clearTimelineEntries();
  });

  document.getElementById('btn-add-playbook')?.addEventListener('click', async () => {
    const title = await globalThis.showInputDialog?.('Playbook title', '', 'Next');
    if (!title) return;
    const promptText = await globalThis.showInputDialog?.('Prompt template', '', 'Save playbook');
    if (!promptText) return;
    addCustomPlaybook(title, promptText);
    globalThis.showToast?.('Saved playbook', 'success');
  });
}

export function exportCurrentSessionAsMarkdown() {
  const hist = stateGet('chatHistory', []);
  if (!Array.isArray(hist) || hist.length === 0) {
    globalThis.showToast?.('No conversation messages to export yet', 'info');
    return;
  }
  const title = document.getElementById('active-session-title')?.textContent || 'VoidAgent Session';
  const lines = [`# ${title}`, `_Exported from VoidCode IDE_`, ''];
  hist.forEach(m => {
    const roleLabel = m.role === 'user' ? 'User' : 'VoidAgent';
    lines.push(`## ${roleLabel}`, '', String(m.content || '').trim(), '');
  });
  const md = lines.join('\n');
  navigator.clipboard?.writeText?.(md);
  const ws = stateGet('workspace') || 'C:\\workspace';
  globalThis.openFileInEditor?.(`${ws}\\voidagent-session.md`, md);
  globalThis.showToast?.('Conversation exported to editor & copied to clipboard', 'success');
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  const api = {
    loadStoredState,
    hydrateSessions,
    hydrateCheckpoints,
    saveCurrentSessionToStorage,
    renderHistorySessions,
    loadSessionIntoChat,
    startNewSession,
    exportCurrentSessionAsMarkdown,
    recordTimelineEvent,
    clearTimelineEntries,
    renderTimelineEntries,
    addCustomPlaybook,
    renderPlaybooks,
    scheduleLivePreviewSync,
    renderLivePreviewFromActiveTab,
    renderChatImage,
    openImagePreview,
    setupWorkspaceTools
  };
  Object.assign(globalThis, api);
  return api;
}
