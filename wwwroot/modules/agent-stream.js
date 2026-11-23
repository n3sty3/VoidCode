// Agent streaming & run trace: user/assistant message rendering, reasoning, streaming,
// Tool cards + run-trace grouping, turn-review card, plan actions, agent mode toggle.
//
// Main.js calls init() and the renderers are exported + published on
// GlobalThis for the app.js bridge switch. The streaming/run-trace state that used to
// Live in app.js's script scope is declared here, the markup helpers are imported, and the
// Per-message helpers come from modules/agent-chat.js as a real import rather than a global.
import { escapeHtml, html, raw, renderSafeMarkdown, setHtml } from '../core/markdown.js';
import {
  addAssistantMessageActions,
  addUserMessageActions,
  buildPatchPreview,
  scrollAgentToBottom
} from './agent-chat.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

// Streaming / run-trace state (app.js's script scope.
let currentStreamMsgEl = null;
let currentStreamContentEl = null;
let currentStreamReasoningEl = null;
let currentStreamBuffer = '';
let currentReasoningBuffer = '';
let reasonStartTime = null;
let activeToolGroup = null;
let streamRenderScheduled = false;

// Tool name -> human verb ().
const TOOL_VERBS = {
  read_file: 'Read',
  view_image: 'Viewed',
  write_file: 'Wrote',
  patch_file: 'Patched',
  delete_file: 'Deleted',
  move_file: 'Moved',
  list_dir: 'Listed',
  search_code: 'Searched',
  run_command: 'Ran',
  git_status_diff: 'Git status',
  get_diagnostics: 'Diagnostics',
  inspect_binary: 'Inspected',
  web_fetch: 'Fetched'
};

// App.js renders image attachments into the live stream bubble, so it asks for the
// Element instead of reading this module's state.
export function getStreamMessageEl() {
  return currentStreamMsgEl;
}

// Agent Streaming & Run Trace
export function appendUserMessage(text, attachments, displayText) {
  const container = document.getElementById('agent-chat-container');
  container.querySelector('.agent-welcome-card')?.remove();
  const div = document.createElement('div');
  div.className = 'chat-msg user';

  const shown = (typeof displayText === 'string' && displayText.length) ? displayText : text;
  let chips = '';
  let previews = '';
  if (Array.isArray(attachments) && attachments.length) {
    chips = '<div class="msg-attachments">' +
      attachments.map(a => `<span class="msg-attach-chip" title="${escapeHtml(a.label || '')}">${escapeHtml(a.label || a.type || 'context')}</span>`).join('') +
      '</div>';
    const imgItems = attachments.filter(a => a && a.type === 'image' && typeof a.preview === 'string' && /^data:image\/[a-zA-Z0-9.+-]+;base64,[A-Za-z0-9+/=]+$/.test(a.preview));
    if (imgItems.length) {
      previews = '<div class="msg-image-previews">' +
        imgItems.map(a => `<img class="msg-attach-thumb" src="${a.preview}" alt="${escapeHtml(a.label || 'Screenshot')}" style="max-width:180px;max-height:120px;border-radius:6px;margin-top:6px;border:1px solid var(--border-subtle);display:block;" />`).join('') +
        '</div>';
    }
  }
  setHtml(div, html`<div class="msg-content">${raw(chips)}${shown}${raw(previews)}</div>`);

  container.appendChild(div);
  addUserMessageActions(div);
  window.VoidState.set('agentAutoScroll', true);
  container.scrollTop = container.scrollHeight;
  window.VoidState.get('chatHistory').push({ role: 'user', content: text });
  globalThis.saveCurrentSessionToStorage?.();
}

export function startAssistantStreamMessage() {
  const container = document.getElementById('agent-chat-container');
  currentStreamBuffer = '';
  currentReasoningBuffer = '';
  reasonStartTime = null;
  finalizeToolGroup();

  const div = document.createElement('div');
  div.className = 'chat-msg assistant';
  setHtml(div, `
    <div class="msg-content is-streaming">
      <div class="reasoning-box" style="display:none;">
        <div class="reasoning-header" role="button" tabindex="0" aria-expanded="false">
          <span class="reasoning-label">Thinking…</span>
          <span class="reasoning-chevron" aria-hidden="true">▸</span>
        </div>
        <div class="reasoning-content" style="display:none;"></div>
      </div>
      <div class="stream-markdown-body"></div>
    </div>
  `);

  const rHeader = div.querySelector('.reasoning-header');
  const rContent = div.querySelector('.reasoning-content');
  const rChevron = div.querySelector('.reasoning-chevron');
  const toggleReasoning = () => {
    const open = rContent.style.display !== 'none';
    rContent.style.display = open ? 'none' : 'block';
    rHeader.setAttribute('aria-expanded', open ? 'false' : 'true');
    if (rChevron) rChevron.textContent = open ? '▸' : '▾';
  };
  rHeader.addEventListener('click', toggleReasoning);
  rHeader.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleReasoning(); }
  });

  container.appendChild(div);
  currentStreamMsgEl = div;
  currentStreamContentEl = div.querySelector('.stream-markdown-body');
  currentStreamReasoningEl = div.querySelector('.reasoning-box');
}

export function appendStreamReasoningDelta(delta) {
  if (!currentStreamMsgEl) startAssistantStreamMessage();
  if (reasonStartTime == null) reasonStartTime = Date.now();
  currentReasoningBuffer += delta;
  currentStreamReasoningEl.style.display = 'block';
  const rc = currentStreamReasoningEl.querySelector('.reasoning-content');
  rc.textContent = currentReasoningBuffer;
}

export function appendStreamContentDelta(delta) {
  if (!currentStreamMsgEl) startAssistantStreamMessage();
  currentStreamBuffer += delta;
  // Coalesce bursts of deltas into one render per frame instead of re-parsing on every token.
  if (streamRenderScheduled) return;
  streamRenderScheduled = true;
  requestAnimationFrame(() => {
    streamRenderScheduled = false;
    if (!currentStreamContentEl) return;
    setHtml(currentStreamContentEl, raw(renderSafeMarkdown(currentStreamBuffer)));
    scrollAgentToBottom();
  });
}

export function finalizeAssistantStreamMessage(fullContent) {
  if (currentStreamReasoningEl && currentReasoningBuffer.trim()) {
    const label = currentStreamReasoningEl.querySelector('.reasoning-label');
    if (label) {
      if (reasonStartTime) {
        const secs = Math.max(0.1, (Date.now() - reasonStartTime) / 1000);
        label.textContent = `Thought for ${secs.toFixed(1)}s`;
      } else {
        label.textContent = 'Thought';
      }
    }
  }
  if (currentStreamContentEl && fullContent) {
    setHtml(currentStreamContentEl, raw(renderSafeMarkdown(fullContent)));
    enhanceChatCodeBlocks(currentStreamContentEl);
    window.VoidState.get('chatHistory').push({ role: 'assistant', content: fullContent });
    globalThis.saveCurrentSessionToStorage?.();
  }
  if (currentStreamMsgEl) {
    const msgContent = currentStreamMsgEl.querySelector('.msg-content');
    if (msgContent) msgContent.classList.remove('is-streaming');
    addAssistantMessageActions(currentStreamMsgEl);
  }
  currentStreamMsgEl = null;
  currentStreamContentEl = null;
  currentStreamReasoningEl = null;
  reasonStartTime = null;
}

export function appendStaticAssistantMessage(markdownText) {
  const container = document.getElementById('agent-chat-container');
  const div = document.createElement('div');
  div.className = 'chat-msg assistant';
  const html = renderSafeMarkdown(markdownText);
  setHtml(div, raw(`<div class="msg-content">${html}</div>`));
  enhanceChatCodeBlocks(div);
  container.appendChild(div);
  addAssistantMessageActions(div);
  scrollAgentToBottom();
}

// Per-message action + footer helpers moved to modules/agent-chat.js:
// AddAssistantMessageActions, addUserMessageActions, regenerateLastAnswer,
// RenderRunFooter, buildPatchPreview, scrollAgentToBottom.

// Enhance <pre><code> blocks in Agent Chat with Copy & Insert into Editor buttons
export function enhanceChatCodeBlocks(rootEl) {
  if (!rootEl) return;
  rootEl.querySelectorAll('pre').forEach(pre => {
    if (pre.querySelector('.code-block-head')) return;
    const codeEl = pre.querySelector('code');
    const rawCode = codeEl ? codeEl.textContent : pre.textContent;
    const langMatch = codeEl && codeEl.className ? codeEl.className.match(/language-([\w-]+)/) : null;
    const langLabel = langMatch ? langMatch[1] : 'code';

    const linesCount = (rawCode || '').split('\n').length;
    const head = document.createElement('div');
    head.className = 'code-block-head';
    setHtml(head, html`
      <div class="code-block-meta">
        <span class="code-lang-badge">${langLabel}</span>
        <span class="code-line-count">${linesCount} line${linesCount === 1 ? '' : 's'}</span>
      </div>
      <div class="code-block-actions">
        <button type="button" class="btn btn--quiet btn--xs" data-code-act="copy">Copy</button>
        <button type="button" class="btn btn--quiet btn--xs" data-code-act="save">Save to file</button>
        <button type="button" class="btn btn--accent-soft btn--xs" data-code-act="insert">Apply to editor</button>
      </div>
    `);

    head.querySelector('[data-code-act="copy"]').addEventListener('click', () => {
      if (navigator.clipboard) {
        navigator.clipboard.writeText(rawCode);
        globalThis.showToast?.('Copied code to clipboard', 'success');
      }
    });

    head.querySelector('[data-code-act="save"]')?.addEventListener('click', async () => {
      const extMap = { javascript: 'js', typescript: 'ts', python: 'py', csharp: 'cs', rust: 'rs', html: 'html', css: 'css', json: 'json', powershell: 'ps1', bash: 'sh', markdown: 'md' };
      const ext = extMap[langLabel.toLowerCase()] || (langLabel === 'code' ? 'txt' : langLabel);
      const rel = await globalThis.showInputDialog?.('Save snippet to file', `snippet.${ext}`, 'Save');
      if (!rel || !rel.trim()) return;
      const ws = window.VoidState?.get?.('workspace') || '';
      const fullPath = /^[a-z]:[\\/]/i.test(rel.trim()) ? rel.trim() : (ws ? `${ws}\\${rel.trim().replace(/\//g, '\\')}` : rel.trim());
      sendHost('save_file', { path: fullPath, content: rawCode || '' });
      globalThis.openFileInEditor?.(fullPath, rawCode || '');
    });

    head.querySelector('[data-code-act="insert"]').addEventListener('click', () => {
      const stateActiveTab = window.VoidState.get('activeTab');
      const editor = globalThis.getMonacoEditor?.();
      if (editor && stateActiveTab) {
        const selection = editor.getSelection();
        editor.executeEdits('agent-insert', [{
          range: selection,
          text: rawCode,
          forceMoveMarkers: true
        }]);
        globalThis.showToast?.(`Inserted into ${stateActiveTab.name}`, 'success');
      } else {
        globalThis.openFileInEditor?.((window.VoidState.get('workspace') || 'C:\\workspace') + '\\snippet.' + (langLabel === 'code' ? 'txt' : langLabel), rawCode);
        globalThis.showToast?.('Opened snippet in editor', 'info');
      }
    });

    pre.prepend(head);
  });

  const citeRegex = /^([A-Za-z0-9_./\\-]+\.[A-Za-z0-9]{1,8})(?::(\d+))?(?::(\d+))?$/;
  rootEl.querySelectorAll(':not(pre) > code').forEach(code => {
    if (code.classList.contains('chat-file-link')) return;
    const text = (code.textContent || '').trim();
    const m = citeRegex.exec(text);
    if (!m) return;
    const rawPath = m[1];
    const line = m[2] ? parseInt(m[2], 10) : 1;
    const col = m[3] ? parseInt(m[3], 10) : 1;
    code.classList.add('chat-file-link');
    code.title = `Open ${rawPath} at line ${line}`;
    code.tabIndex = 0;
    code.setAttribute('role', 'link');
    const openCitedFile = () => {
      const ws = window.VoidState?.get?.('workspace') || '';
      const files = window.VoidState?.get?.('workspaceFiles') || [];
      const normCite = rawPath.replace(/\//g, '\\').toLowerCase();
      const found = files.find(f => {
        const fp = String(f.path || f.fullPath || f).replace(/\//g, '\\').toLowerCase();
        return fp === normCite || fp.endsWith('\\' + normCite);
      });
      const fullPath = found
        ? (found.path || found.fullPath || String(found))
        : (/^[a-z]:\\/i.test(rawPath) ? rawPath : (ws ? `${ws}\\${rawPath.replace(/\//g, '\\')}` : rawPath));
      globalThis.navigateToFileLocation?.(fullPath, line, col);
    };
    code.addEventListener('click', openCitedFile);
    code.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openCitedFile(); }
    });
  });
}

// ---- Run-trace grouping: collapse consecutive tool cards under one header ----
function ensureToolGroup() {
  const container = document.getElementById('agent-chat-container');
  if (!container) { activeToolGroup = null; return; }
  if (activeToolGroup && container.contains(activeToolGroup.el)) return;
  const el = document.createElement('div');
  el.className = 'tool-run-group is-running';
  setHtml(el, `
    <div class="tool-run-head" role="button" tabindex="0" aria-expanded="true">
      <span class="tool-run-dot" aria-hidden="true"></span>
      <span class="tool-run-label">Working…</span>
      <span class="tool-run-meta"></span>
      <span class="tool-run-chevron" aria-hidden="true">▾</span>
    </div>
    <div class="tool-run-body"></div>`);
  const head = el.querySelector('.tool-run-head');
  const toggle = () => {
    const collapsed = el.classList.toggle('is-collapsed');
    head.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
    el.querySelector('.tool-run-chevron').textContent = collapsed ? '▸' : '▾';
  };
  head.addEventListener('click', toggle);
  head.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });
  container.appendChild(el);
  activeToolGroup = { el, body: el.querySelector('.tool-run-body'), head, count: 0, startTs: Date.now() };
}

function updateToolGroupHeader() {
  const g = activeToolGroup;
  if (!g) return;
  const label = g.head.querySelector('.tool-run-label');
  const meta = g.head.querySelector('.tool-run-meta');
  if (label) label.textContent = g.count === 0 ? 'Working…' : `Ran ${g.count} tool${g.count === 1 ? '' : 's'}`;
  if (meta) meta.textContent = `${((Date.now() - g.startTs) / 1000).toFixed(1)}s`;
}

export function finalizeToolGroup() {
  if (!activeToolGroup) return;
  activeToolGroup.el.classList.remove('is-running');
  activeToolGroup.el.classList.add('is-done');
  updateToolGroupHeader();
  activeToolGroup = null;
}

export function renderToolCardStart(id, tool, argsRaw) {
  const container = document.getElementById('agent-chat-container');
  const card = document.createElement('div');
  card.id = `tool-card-${id}`;
  card.className = 'tool-card';

  const verb = TOOL_VERBS[tool] || tool;
  let target = '';
  try {
    const parsed = JSON.parse(argsRaw);
    target = parsed.path || parsed.command || parsed.pattern || parsed.url || '';
  } catch (err) { logSwallowed(err, "agentStream.toolCardTarget"); }

  if (tool === 'patch_file') {
    try {
      const pp = JSON.parse(argsRaw);
      card._patch = { search: pp.search || '', replace: pp.replace || '' };
    } catch (err) { logSwallowed(err, "agentStream.toolCardPatch"); }
  }

  card.dataset.verb = verb;
  card.dataset.target = target;
  card.dataset.startTs = String(Date.now());

  setHtml(card, html`
    <div class="tool-card-header">
      <div class="tool-name-wrap">
        <span class="tool-verb">${verb}</span>
        <span class="tool-target">${target}</span>
      </div>
      <div class="tool-actions-right">
        <span class="tool-duration"></span>
        <span class="tool-badge-pill">Running</span>
      </div>
    </div>
    <pre class="tool-card-details">${argsRaw}</pre>
  `);

  card.querySelector('.tool-card-header').addEventListener('click', (e) => {
    if (/** @type {HTMLInputElement} */ (e.target).classList.contains('btn-undo-checkpoint')) return;
    card.classList.toggle('expanded');
  });

  ensureToolGroup();
  if (activeToolGroup) {
    activeToolGroup.body.appendChild(card);
    activeToolGroup.count++;
    updateToolGroupHeader();
  } else {
    container.appendChild(card);
  }
  scrollAgentToBottom();
}

export function renderToolApprovalPrompt(id) {
  const card = document.getElementById(`tool-card-${id}`);
  if (!card) return;
  card.classList.add('expanded', 'is-awaiting-approval');
  const actionsRight = card.querySelector('.tool-actions-right');
  const pill = card.querySelector('.tool-badge-pill');
  if (pill) pill.textContent = 'Awaiting approval';

  const approveBtn = document.createElement('button');
  approveBtn.type = 'button';
  approveBtn.className = 'btn btn--primary btn--xs';
  approveBtn.textContent = 'Approve';

  const denyBtn = document.createElement('button');
  denyBtn.type = 'button';
  denyBtn.className = 'btn btn--quiet btn--xs';
  denyBtn.textContent = 'Deny';

  function resolve(approved) {
    card.classList.remove('is-awaiting-approval');
    approveBtn.remove();
    denyBtn.remove();
    if (pill) pill.textContent = approved ? 'Running' : 'Denied';
    sendHost('resolve_tool_approval', { id, approved });
  }

  approveBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    resolve(true);
  });
  denyBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    resolve(false);
  });

  if (actionsRight) {
    actionsRight.prepend(denyBtn);
    actionsRight.prepend(approveBtn);
  }
}

export function renderToolCardFinish(id, tool, output, checkpointId) {
  const card = document.getElementById(`tool-card-${id}`);
  const verb = card ? card.dataset.verb : (TOOL_VERBS[tool] || tool);
  const target = card ? card.dataset.target : '';

  globalThis.recordTimelineEvent?.(verb, target, checkpointId);

  if (!card) return;
  const failed = /^(error|tool execution error|blocked)\b/i.test((output || '').trim());
  card.classList.add(failed ? 'is-error' : 'is-done');
  const actionsRight = card.querySelector('.tool-actions-right');
  const pill = card.querySelector('.tool-badge-pill');
  if (pill) pill.textContent = failed ? 'Failed' : 'Done';

  const durationEl = card.querySelector('.tool-duration');
  if (durationEl && card.dataset.startTs) {
    const secs = Math.max(0, (Date.now() - Number(card.dataset.startTs)) / 1000);
    durationEl.textContent = secs < 1 ? `${Math.round(secs * 1000)}ms` : `${secs.toFixed(1)}s`;
  }

  if (checkpointId && actionsRight) {
    const undoBtn = document.createElement('button');
    undoBtn.className = 'btn-undo-checkpoint';
    undoBtn.textContent = 'Undo edit';
    undoBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      sendHost('restore_checkpoint', { checkpointId });
      card.classList.add('is-reverted');
      undoBtn.textContent = 'Reverted';
      undoBtn.disabled = true;
    });
    actionsRight.prepend(undoBtn);

    const diffBtn = document.createElement('button');
    diffBtn.className = 'btn-undo-checkpoint';
    diffBtn.textContent = 'View diff';
    diffBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      sendHost('get_checkpoint_diff', { checkpointId });
    });
    actionsRight.prepend(diffBtn);
  }

  const details = card.querySelector('.tool-card-details');
  if (details) {
    const out = output || '';
    if (card._patch && (card._patch.search || card._patch.replace)) {
      const diffText = buildPatchPreview(card._patch);
      const coloredLines = diffText.split('\n').map(line => {
        const cls = line.startsWith('+ ') ? 'diff-line diff-line--add' : (line.startsWith('- ') ? 'diff-line diff-line--del' : 'diff-line');
        return `<span class="${cls}">${escapeHtml(line)}</span>`;
      }).join('\n');
      const outTail = out ? `\n<span class="diff-line diff-line--meta">${escapeHtml(out)}</span>` : '';
      setHtml(details, raw(coloredLines + outTail));
    } else {
      const preview = out.substring(0, 2000);
      details.textContent += `\n\n${preview}${out.length > 2000 ? '\n…[truncated]' : ''}`;
    }
  }
}

// Main.js calls this once; the renderers app.js drives from its bridge switch go on
// GlobalThis.
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  Object.assign(globalThis, {
    appendUserMessage,
    startAssistantStreamMessage,
    appendStreamReasoningDelta,
    appendStreamContentDelta,
    finalizeAssistantStreamMessage,
    appendStaticAssistantMessage,
    enhanceChatCodeBlocks,
    finalizeToolGroup,
    renderToolCardStart,
    renderToolApprovalPrompt,
    renderToolCardFinish,
    getStreamMessageEl
  });
  return { appendUserMessage, startAssistantStreamMessage, appendStreamReasoningDelta, appendStreamContentDelta, finalizeAssistantStreamMessage, appendStaticAssistantMessage, enhanceChatCodeBlocks, finalizeToolGroup, renderToolCardStart, renderToolApprovalPrompt, renderToolCardFinish, getStreamMessageEl };
}
