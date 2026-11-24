// VoidAgent v2 Streaming & Run Trace UI:
// - Flicker-free block-stable Markdown streaming
// - Live Reasoning preview ticker with elapsed timer
// - Smart activity pills ("Explored 4 files · 2 searches · 1 edit") with clickable Monaco file targets
// - Diff-first Pre-Approval cards (real LCS unified diff, Approve / Approve Turn / Deny with reason)
// - Changed Files Dock pinned above the composer (file pills + Keep All / Revert All)
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

let currentStreamMsgEl = null;
let currentStreamContentEl = null;
let currentStreamReasoningEl = null;
let currentStreamBuffer = '';
let currentReasoningBuffer = '';
let reasonStartTime = null;
let activeToolGroup = null;
let streamRenderScheduled = false;
let lastCommittedBlockCount = 0;
let liveTurnCardEl = null;
let liveTurnTimer = null;
let liveTurnStartMs = 0;
const DEFAULT_COMPOSER_PLACEHOLDER = 'Ask VoidAgent to build, debug, or refactor… (@ for context, / for workflows)';

function startLiveTurnCard(initialStatus = 'Connecting to model…') {
  clearLiveTurnCard();
  const container = document.getElementById('agent-chat-container');
  if (!container) return;

  const rawModel = window.VoidState?.get?.('settings')?.Model || document.getElementById('composer-model-label')?.textContent || 'active model';
  const model = rawModel.includes('/') ? rawModel.split('/').pop() : rawModel;
  const card = document.createElement('div');
  card.className = 'agent-live-turn';
  card.setAttribute('role', 'status');
  setHtml(card, html`
    <div class="agent-live-head">
      <div class="agent-live-left">
        <span class="agent-live-pulse" aria-hidden="true"></span>
        <strong style="color:var(--text);">VoidAgent</strong>
        <span class="agent-live-model">${model}</span>
      </div>
      <span class="agent-live-timer">0.0s</span>
    </div>
    <div class="agent-live-status">${initialStatus}</div>
  `);

  container.appendChild(card);
  liveTurnCardEl = card;
  liveTurnStartMs = Date.now();
  liveTurnTimer = setInterval(() => {
    if (!liveTurnCardEl || !document.body.contains(liveTurnCardEl)) {
      clearLiveTurnCard();
      return;
    }
    const t = liveTurnCardEl.querySelector('.agent-live-timer');
    if (t) t.textContent = `${((Date.now() - liveTurnStartMs) / 1000).toFixed(1)}s`;
  }, 100);

  const input = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
  if (input) input.placeholder = 'Steer VoidAgent mid-run… (Enter to queue guidance)';
  scrollAgentToBottom();
}

function updateLiveTurnStatus(statusText) {
  if (!statusText) return;
  if (!liveTurnCardEl || !document.body.contains(liveTurnCardEl)) {
    // Only create live card if no stream message is currently open.
    if (!currentStreamMsgEl) startLiveTurnCard(statusText);
    return;
  }
  const el = liveTurnCardEl.querySelector('.agent-live-status');
  if (el) el.textContent = statusText;
}

function clearLiveTurnCard(restorePlaceholder = false) {
  if (liveTurnTimer) {
    clearInterval(liveTurnTimer);
    liveTurnTimer = null;
  }
  if (liveTurnCardEl) {
    liveTurnCardEl.remove();
    liveTurnCardEl = null;
  }
  if (restorePlaceholder) {
    const input = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
    if (input) input.placeholder = DEFAULT_COMPOSER_PLACEHOLDER;
  }
}

// Rich v2 payload cache populated via VoidBridge.subscribe before app.js invokes legacy signatures.
const toolMetaById = new Map();
const sessionChangedFiles = new Map(); // normalizedPath -> { path, name, added, removed, checkpointId, turnId }
let lastUserMsgEl = null;

const TOOL_VERBS = {
  read_file: 'Read',
  view_image: 'Viewed',
  write_file: 'Wrote',
  patch_file: 'Patched',
  multi_patch_file: 'Multi-patched',
  delete_file: 'Deleted',
  move_file: 'Moved',
  list_dir: 'Listed',
  glob_files: 'Found files',
  search_code: 'Searched',
  search_codebase: 'Indexed search',
  run_command: 'Ran',
  git_status_diff: 'Git status',
  get_diagnostics: 'Build check',
  get_lsp_diagnostics: 'LSP check',
  inspect_binary: 'Inspected',
  web_fetch: 'Fetched',
  save_memory: 'Remembered',
  delete_memory: 'Forgot',
  delegate_research: 'Researched',
  update_plan: 'Updated plan'
};

const FILE_TOOLS = new Set([
  'read_file', 'view_image', 'write_file', 'patch_file', 'multi_patch_file',
  'delete_file', 'get_lsp_diagnostics', 'inspect_binary'
]);

const READ_CATEGORY_TOOLS = new Set(['read_file', 'view_image', 'list_dir', 'glob_files']);
const SEARCH_CATEGORY_TOOLS = new Set(['search_code', 'search_codebase', 'delegate_research']);
const EDIT_CATEGORY_TOOLS = new Set(['write_file', 'patch_file', 'multi_patch_file', 'delete_file', 'move_file']);

function formatToolVerb(tool) {
  if (TOOL_VERBS[tool]) return TOOL_VERBS[tool];
  if (typeof tool === 'string' && tool.startsWith('mcp__')) {
    const parts = tool.split('__');
    return `MCP: ${parts[parts.length - 1] || tool}`;
  }
  return tool || 'Tool';
}

function resolveWorkspaceFullPath(rawPath) {
  if (!rawPath) return '';
  if (/^[a-z]:[\\/]/i.test(rawPath) || rawPath.startsWith('/')) return rawPath;
  const ws = window.VoidState?.get?.('workspace') || '';
  if (!ws) return rawPath;
  const sep = ws.includes('\\') ? '\\' : '/';
  return `${ws}${ws.endsWith(sep) ? '' : sep}${rawPath.replace(/[/\\]/g, sep)}`;
}

function formatColorizedDiffHtml(diffText, tailMeta = '') {
  const lines = (diffText || '').split('\n').map((line) => {
    let cls = 'diff-line';
    if (line.startsWith('+ ') || (line.startsWith('+') && !line.startsWith('+++'))) cls = 'diff-line diff-line--add';
    else if (line.startsWith('- ') || (line.startsWith('-') && !line.startsWith('---'))) cls = 'diff-line diff-line--del';
    else if (line.startsWith('@@')) cls = 'diff-line diff-line--meta';
    return `<span class="${cls}">${escapeHtml(line)}</span>`;
  }).join('\n');
  const tail = tailMeta ? `\n<span class="diff-line diff-line--meta">${escapeHtml(tailMeta)}</span>` : '';
  return lines + tail;
}

function renderChangedFilesDock() {
  const inputContainer = document.getElementById('agent-input-container');
  if (!inputContainer) return;

  let dock = document.getElementById('agent-changed-files-dock');
  if (sessionChangedFiles.size === 0) {
    if (dock) dock.style.display = 'none';
    return;
  }

  if (!dock) {
    dock = document.createElement('div');
    dock.id = 'agent-changed-files-dock';
    dock.style.cssText = 'display:flex;flex-direction:column;gap:6px;padding:8px 10px;border-radius:10px;border:1px solid var(--seam);background:rgba(16,22,33,0.92);font-size:11px;';
    inputContainer.prepend(dock);
  }

  let totalAdded = 0;
  let totalRemoved = 0;
  const files = Array.from(sessionChangedFiles.values());
  files.forEach((f) => {
    totalAdded += f.added || 0;
    totalRemoved += f.removed || 0;
  });

  dock.style.display = 'flex';
  setHtml(dock, '');

  const topRow = document.createElement('div');
  topRow.style.cssText = 'display:flex;align-items:center;justify-content:space-between;gap:8px;';
  setHtml(topRow, html`
    <div style="display:flex;align-items:center;gap:6px;min-width:0;">
      <strong style="color:var(--text);white-space:nowrap;">${files.length} file${files.length === 1 ? '' : 's'} changed</strong>
      ${totalAdded > 0 ? raw(`<span style="color:#6be2b3;font-family:var(--font-code);">+${totalAdded}</span>`) : ''}
      ${totalRemoved > 0 ? raw(`<span style="color:var(--coral);font-family:var(--font-code);">-${totalRemoved}</span>`) : ''}
    </div>
    <div style="display:flex;gap:4px;flex-shrink:0;">
      <button type="button" class="btn btn--quiet btn--xs" data-dock-act="keep" style="white-space:nowrap;">Keep All</button>
      <button type="button" class="btn btn--quiet btn--xs" data-dock-act="revert" style="white-space:nowrap;color:var(--coral);">Revert All</button>
    </div>
  `);
  topRow.querySelector('[data-dock-act="keep"]')?.addEventListener('click', () => {
    sessionChangedFiles.clear();
    renderChangedFilesDock();
    globalThis.showToast?.('Kept all modified files', 'success');
  });
  topRow.querySelector('[data-dock-act="revert"]')?.addEventListener('click', () => {
    const rev = files.slice().reverse();
    rev.forEach((f) => {
      if (f.checkpointId) sendHost('restore_checkpoint', { checkpointId: f.checkpointId });
    });
    sessionChangedFiles.clear();
    renderChangedFilesDock();
    globalThis.showToast?.(`Reverted ${rev.length} modified file${rev.length === 1 ? '' : 's'}`, 'info');
  });
  dock.appendChild(topRow);

  const pills = document.createElement('div');
  pills.style.cssText = 'display:flex;flex-wrap:wrap;gap:4px;';
  files.slice(0, 8).forEach((f) => {
    const pill = document.createElement('button');
    pill.type = 'button';
    pill.className = 'btn btn--quiet btn--xs';
    pill.style.cssText = 'max-width:190px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-family:var(--font-code);font-size:10px;';
    pill.title = `${f.path} (+${f.added || 0} / -${f.removed || 0}) — click to inspect diff`;
    const stat = (f.added || f.removed) ? ` +${f.added || 0}/-${f.removed || 0}` : '';
    pill.textContent = `${f.name}${stat}`;
    pill.addEventListener('click', () => {
      if (f.checkpointId) sendHost('get_checkpoint_diff', { checkpointId: f.checkpointId });
      else globalThis.navigateToFileLocation?.(resolveWorkspaceFullPath(f.path), 1, 1);
    });
    pills.appendChild(pill);
  });
  dock.appendChild(pills);
}

export function clearSessionChangedFiles() {
  sessionChangedFiles.clear();
  renderChangedFilesDock();
  document.getElementById('agent-live-plan-widget')?.remove();
}

function renderLivePlanWidget(steps) {
  const sidebar = document.getElementById('sidebar-agent');
  const chatEl = document.getElementById('agent-chat-container');
  if (!sidebar || !chatEl || !Array.isArray(steps) || steps.length === 0) return;

  let widget = document.getElementById('agent-live-plan-widget');
  if (!widget) {
    widget = document.createElement('div');
    widget.id = 'agent-live-plan-widget';
    widget.className = 'agent-plan-widget';
    sidebar.insertBefore(widget, chatEl);
  }

  const doneCount = steps.filter((s) => s.status === 'done').length;
  const isCollapsed = widget.classList.contains('is-collapsed');
  setHtml(widget, '');

  const head = document.createElement('div');
  head.className = 'agent-plan-widget-head';
  setHtml(head, html`
    <span>Plan · ${doneCount}/${steps.length} done</span>
    <span style="opacity:0.65;font-size:10px;">${isCollapsed ? '▸' : '▾'}</span>
  `);
  head.addEventListener('click', () => {
    const next = widget.classList.toggle('is-collapsed');
    const arrow = head.lastElementChild;
    if (arrow) arrow.textContent = next ? '▸' : '▾';
  });
  widget.appendChild(head);

  const list = document.createElement('div');
  list.className = 'agent-plan-widget-list';
  steps.forEach((s) => {
    const row = document.createElement('div');
    row.className = 'agent-plan-step' + (s.status === 'done' ? ' is-done' : (s.status === 'in_progress' ? ' is-progress' : ''));
    const icon = s.status === 'done' ? '✓' : (s.status === 'in_progress' ? '●' : '○');
    setHtml(row, html`<span style="flex-shrink:0;">${icon}</span><span>${s.title}</span>`);
    list.appendChild(row);
  });
  widget.appendChild(list);
}

function appendLiveToolOutputDelta(id, line) {
  const card = document.getElementById(`tool-card-${id}`);
  if (!card) return;
  const details = card.querySelector('.tool-card-details');
  if (!details) return;
  card.classList.add('expanded');
  if (!/** @type {any} */ (card)._streamedLiveOutput) {
    /** @type {any} */ (card)._streamedLiveOutput = true;
    details.textContent = `${details.textContent ? details.textContent + '\n' : ''}${line}`;
  } else {
    details.textContent += `\n${line}`;
  }
  details.scrollTop = details.scrollHeight;
}

export function getStreamMessageEl() {
  return currentStreamMsgEl;
}

export function appendUserMessage(text, attachments, displayText) {
  const container = document.getElementById('agent-chat-container');
  container.querySelector('.agent-welcome-card')?.remove();
  const div = document.createElement('div');
  div.className = 'chat-msg user';

  const historyIdx = window.VoidState.get('chatHistory').length;
  div.dataset.historyIndex = String(historyIdx);
  /** @type {any} */ (div)._turnCheckpoints = [];
  lastUserMsgEl = div;

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
  setHtml(div, html`<div class="msg-content"><div class="user-prompt-eyebrow"><span>You</span></div>${raw(chips)}${shown}${raw(previews)}</div>`);

  container.appendChild(div);
  addUserMessageActions(div);
  startLiveTurnCard('Sending prompt to model…');
  window.VoidState.set('agentAutoScroll', true);
  container.scrollTop = container.scrollHeight;
  window.VoidState.get('chatHistory').push({ role: 'user', content: text });
  globalThis.saveCurrentSessionToStorage?.();
}

export function startAssistantStreamMessage() {
  clearLiveTurnCard(false);
  const container = document.getElementById('agent-chat-container');
  currentStreamBuffer = '';
  currentReasoningBuffer = '';
  reasonStartTime = null;
  lastCommittedBlockCount = 0;
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
      <div class="stream-markdown-body">
        <div class="stream-committed"></div>
        <div class="stream-active-tail"></div>
      </div>
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
  if (rc) rc.textContent = currentReasoningBuffer;

  // Live single-line reasoning ticker so the operator sees current thought progress without clutter.
  const label = currentStreamReasoningEl.querySelector('.reasoning-label');
  if (label) {
    const lines = currentReasoningBuffer.trim().split('\n').map(l => l.trim()).filter(Boolean);
    const tail = lines.length ? lines[lines.length - 1] : '';
    const snippet = tail.length > 52 ? `${tail.slice(0, 52)}…` : tail;
    const secs = ((Date.now() - reasonStartTime) / 1000).toFixed(1);
    label.textContent = snippet ? `Thinking · ${snippet} (${secs}s)` : `Thinking… (${secs}s)`;
  }
}

function splitStableMarkdownBlocks(src) {
  // Split at double-newline boundaries OUTSIDE fenced code blocks so finished blocks never flicker.
  const lines = src.split('\n');
  const blocks = [];
  let cur = [];
  let inFence = false;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (/^\s*```/.test(line)) inFence = !inFence;
    if (!inFence && line.trim() === '' && cur.length > 0) {
      blocks.push(cur.join('\n'));
      cur = [];
    } else {
      cur.push(line);
    }
  }
  const activeTail = cur.join('\n');
  return { blocks, activeTail, inFence };
}

export function appendStreamContentDelta(delta) {
  if (!currentStreamMsgEl) startAssistantStreamMessage();
  currentStreamBuffer += delta;
  if (streamRenderScheduled) return;
  streamRenderScheduled = true;
  requestAnimationFrame(() => {
    streamRenderScheduled = false;
    if (!currentStreamContentEl) return;
    const committedEl = currentStreamContentEl.querySelector('.stream-committed');
    const tailEl = currentStreamContentEl.querySelector('.stream-active-tail');
    if (committedEl && tailEl) {
      const { blocks, activeTail } = splitStableMarkdownBlocks(currentStreamBuffer);
      if (blocks.length > lastCommittedBlockCount) {
        for (let i = lastCommittedBlockCount; i < blocks.length; i++) {
          const wrapper = document.createElement('div');
          setHtml(wrapper, raw(renderSafeMarkdown(blocks[i])));
          enhanceChatCodeBlocks(wrapper);
          committedEl.appendChild(wrapper);
        }
        lastCommittedBlockCount = blocks.length;
      }
      setHtml(tailEl, activeTail ? raw(renderSafeMarkdown(activeTail)) : '');
    } else {
      setHtml(currentStreamContentEl, raw(renderSafeMarkdown(currentStreamBuffer)));
    }
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
  lastCommittedBlockCount = 0;
}

export function appendStaticAssistantMessage(markdownText) {
  const container = document.getElementById('agent-chat-container');
  const div = document.createElement('div');
  div.className = 'chat-msg assistant';
  const rendered = renderSafeMarkdown(markdownText);
  setHtml(div, raw(`<div class="msg-content">${rendered}</div>`));
  enhanceChatCodeBlocks(div);
  container.appendChild(div);
  addAssistantMessageActions(div);
  scrollAgentToBottom();
}

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
      const fullPath = resolveWorkspaceFullPath(rel.trim());
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
        globalThis.openFileInEditor?.(resolveWorkspaceFullPath(`snippet.${langLabel === 'code' ? 'txt' : langLabel}`), rawCode);
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
      const files = window.VoidState?.get?.('workspaceFiles') || [];
      const normCite = rawPath.replace(/\//g, '\\').toLowerCase();
      const found = files.find(f => {
        const fp = String(f.path || f.fullPath || f).replace(/\//g, '\\').toLowerCase();
        return fp === normCite || fp.endsWith('\\' + normCite);
      });
      const fullPath = found ? (found.path || found.fullPath || String(found)) : resolveWorkspaceFullPath(rawPath);
      globalThis.navigateToFileLocation?.(fullPath, line, col);
    };
    code.addEventListener('click', openCitedFile);
    code.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openCitedFile(); }
    });
  });
}

// ---- Smart Activity Group Pill ----
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
  activeToolGroup = {
    el,
    body: el.querySelector('.tool-run-body'),
    head,
    count: 0,
    finished: 0,
    reads: 0,
    searches: 0,
    edits: 0,
    cmds: 0,
    startTs: Date.now(),
    timer: null
  };
  activeToolGroup.timer = setInterval(() => {
    if (!activeToolGroup || !document.body.contains(activeToolGroup.el)) {
      if (activeToolGroup?.timer) clearInterval(activeToolGroup.timer);
      return;
    }
    updateToolGroupHeader();
  }, 250);
}

function updateToolGroupHeader() {
  const g = activeToolGroup;
  if (!g) return;
  const label = g.head.querySelector('.tool-run-label');
  const meta = g.head.querySelector('.tool-run-meta');
  if (label) {
    const parts = [];
    if (g.reads > 0) parts.push(`read ${g.reads}`);
    if (g.searches > 0) parts.push(`${g.searches} search${g.searches === 1 ? '' : 'es'}`);
    if (g.edits > 0) parts.push(`${g.edits} edit${g.edits === 1 ? '' : 's'}`);
    if (g.cmds > 0) parts.push(`${g.cmds} cmd${g.cmds === 1 ? '' : 's'}`);
    label.textContent = parts.length > 0
      ? `Activity · ${parts.join(', ')}`
      : (g.count === 0 ? 'Working…' : `Ran ${g.count} tool${g.count === 1 ? '' : 's'}`);
  }
  if (meta) meta.textContent = `${((Date.now() - g.startTs) / 1000).toFixed(1)}s`;
}

export function finalizeToolGroup() {
  if (!activeToolGroup) return;
  if (activeToolGroup.timer) {
    clearInterval(activeToolGroup.timer);
    activeToolGroup.timer = null;
  }
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

  const verb = formatToolVerb(tool);
  const meta = toolMetaById.get(id) || {};
  let parsedArgs = null;
  let target = '';
  try {
    parsedArgs = JSON.parse(argsRaw);
    target = parsedArgs.path || parsedArgs.command || parsedArgs.pattern || parsedArgs.question || parsedArgs.url || parsedArgs.content || '';
    if (target.length > 80) target = `${target.slice(0, 80)}…`;
  } catch (err) { logSwallowed(err, 'agentStream.toolCardTarget'); }

  if (tool === 'patch_file' && parsedArgs) {
    /** @type {any} */ (card)._patch = { search: parsedArgs.search || '', replace: parsedArgs.replace || '' };
  }

  card.dataset.tool = tool;
  card.dataset.verb = verb;
  card.dataset.target = target;
  card.dataset.startTs = String(Date.now());

  const diffStatBadge = (meta.addedLines || meta.removedLines)
    ? `<span style="font-family:var(--font-code);font-size:10px;display:inline-flex;gap:4px;">${meta.addedLines ? `<span style="color:#6be2b3;">+${meta.addedLines}</span>` : ''}${meta.removedLines ? `<span style="color:var(--coral);">-${meta.removedLines}</span>` : ''}</span>`
    : '';

  const isClickableFile = FILE_TOOLS.has(tool) && parsedArgs?.path;
  setHtml(card, html`
    <div class="tool-card-header">
      <div class="tool-name-wrap">
        <span class="tool-status-dot" aria-hidden="true"></span>
        <span class="tool-verb">${verb}</span>
        <span class="tool-target ${isClickableFile ? 'chat-file-link' : ''}" data-open-file="${isClickableFile ? parsedArgs.path : ''}" title="${isClickableFile ? `Open ${parsedArgs.path} in editor` : target}">${target}</span>
      </div>
      <div class="tool-actions-right">
        ${raw(diffStatBadge)}
        <span class="tool-duration"></span>
        <span class="tool-badge-pill">Running</span>
      </div>
    </div>
    <pre class="tool-card-details"></pre>
  `);

  const detailsEl = card.querySelector('.tool-card-details');
  if (detailsEl) {
    if (meta.diffPreview) {
      setHtml(detailsEl, raw(formatColorizedDiffHtml(meta.diffPreview)));
    } else if (tool === 'run_command' && parsedArgs?.command) {
      detailsEl.textContent = `$ ${parsedArgs.command}`;
    } else if (parsedArgs && typeof parsedArgs === 'object') {
      const compactLines = Object.entries(parsedArgs)
        .filter(([, v]) => v != null && String(v).length > 0)
        .map(([k, v]) => `${k}: ${String(v).length > 260 ? String(v).slice(0, 260) + '…' : v}`);
      detailsEl.textContent = compactLines.join('\n') || argsRaw;
    } else {
      detailsEl.textContent = argsRaw;
    }
  }

  const targetSpan = card.querySelector('[data-open-file]');
  if (targetSpan && targetSpan.getAttribute('data-open-file')) {
    targetSpan.addEventListener('click', (e) => {
      e.stopPropagation();
      globalThis.navigateToFileLocation?.(resolveWorkspaceFullPath(targetSpan.getAttribute('data-open-file')), 1, 1);
    });
  }

  card.querySelector('.tool-card-header').addEventListener('click', (e) => {
    if (/** @type {HTMLElement} */ (e.target).closest('button, .chat-file-link')) return;
    card.classList.toggle('expanded');
  });

  ensureToolGroup();
  if (activeToolGroup) {
    activeToolGroup.body.appendChild(card);
    activeToolGroup.count++;
    if (READ_CATEGORY_TOOLS.has(tool)) activeToolGroup.reads++;
    else if (SEARCH_CATEGORY_TOOLS.has(tool)) activeToolGroup.searches++;
    else if (EDIT_CATEGORY_TOOLS.has(tool)) activeToolGroup.edits++;
    else if (tool === 'run_command' || tool === 'get_diagnostics') activeToolGroup.cmds++;
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
  const tool = card.dataset.tool || '';
  const isFileEdit = EDIT_CATEGORY_TOOLS.has(tool);
  const isCommand = tool === 'run_command';
  const pill = card.querySelector('.tool-badge-pill');
  if (pill) {
    pill.textContent = isCommand ? 'Run command?' : (tool === 'web_fetch' ? 'Fetch URL?' : 'Review edit');
  }
  const verbEl = card.querySelector('.tool-verb');
  if (verbEl && isCommand) verbEl.textContent = 'Run';

  const meta = toolMetaById.get(id) || {};
  const detailsEl = card.querySelector('.tool-card-details');
  if (detailsEl && meta.diffPreview) {
    setHtml(detailsEl, raw(formatColorizedDiffHtml(meta.diffPreview)));
  }

  card.querySelector('.approval-v2-bar')?.remove();

  const bar = document.createElement('div');
  bar.className = 'approval-v2-bar';
  bar.style.cssText = 'display:flex;flex-direction:column;gap:6px;margin-top:8px;padding-top:8px;border-top:1px solid rgba(245,185,99,0.22);';

  const btnRow = document.createElement('div');
  btnRow.style.cssText = 'display:flex;align-items:center;gap:6px;flex-wrap:wrap;';

  const approveBtn = document.createElement('button');
  approveBtn.type = 'button';
  approveBtn.className = 'btn btn--primary btn--xs';
  approveBtn.style.cssText = 'white-space:nowrap;flex-shrink:0;';
  approveBtn.textContent = isCommand ? 'Allow' : 'Approve';

  const denyBtn = document.createElement('button');
  denyBtn.type = 'button';
  denyBtn.className = 'btn btn--quiet btn--xs';
  denyBtn.style.cssText = 'white-space:nowrap;flex-shrink:0;color:var(--coral);';
  denyBtn.textContent = 'Deny';

  const reasonInput = document.createElement('input');
  reasonInput.type = 'text';
  reasonInput.placeholder = 'Optional note if denying (Enter to send)...';
  reasonInput.style.cssText = 'width:100%;height:26px;padding:0 9px;border-radius:7px;border:1px solid var(--seam);background:rgba(0,0,0,0.28);color:var(--text);font-size:11px;';

  function resolve(approved, approveTurn = false, reason = '') {
    card.classList.remove('is-awaiting-approval');
    bar.remove();
    if (pill) pill.textContent = approved ? 'Running' : 'Denied';
    if (verbEl && isCommand && approved) verbEl.textContent = 'Ran';
    sendHost('resolve_tool_approval', { id, approved, approveTurn, reason });
  }

  approveBtn.addEventListener('click', (e) => { e.stopPropagation(); resolve(true, false); });
  denyBtn.addEventListener('click', (e) => { e.stopPropagation(); resolve(false, false, reasonInput.value.trim()); });
  reasonInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      e.stopPropagation();
      resolve(false, false, reasonInput.value.trim());
    }
  });

  btnRow.append(approveBtn);
  if (isFileEdit) {
    const approveTurnBtn = document.createElement('button');
    approveTurnBtn.type = 'button';
    approveTurnBtn.className = 'btn btn--quiet btn--xs';
    approveTurnBtn.style.cssText = 'white-space:nowrap;flex-shrink:0;';
    approveTurnBtn.title = 'Approve this edit and auto-approve remaining in-workspace file edits for this turn';
    approveTurnBtn.textContent = 'Approve Turn';
    approveTurnBtn.addEventListener('click', (e) => { e.stopPropagation(); resolve(true, true); });
    btnRow.append(approveTurnBtn);
  }
  btnRow.append(denyBtn);
  bar.append(btnRow, reasonInput);
  card.appendChild(bar);
  scrollAgentToBottom();
}

export function renderToolCardFinish(id, tool, output, checkpointId) {
  const card = document.getElementById(`tool-card-${id}`);
  const verb = card ? card.dataset.verb : formatToolVerb(tool);
  const target = card ? card.dataset.target : '';
  const meta = toolMetaById.get(id) || {};

  globalThis.recordTimelineEvent?.(verb, target, checkpointId);

  if (checkpointId && lastUserMsgEl) {
    const arr = /** @type {any} */ (lastUserMsgEl)._turnCheckpoints;
    if (Array.isArray(arr)) {
      arr.push({ checkpointId, path: meta.modifiedFilePath || meta.targetPath || target, turnId: meta.turnId || '' });
      globalThis.refreshUserMessageRewindButton?.(lastUserMsgEl);
    }
  }

  if (checkpointId && EDIT_CATEGORY_TOOLS.has(tool)) {
    const path = meta.modifiedFilePath || meta.targetPath || target;
    if (path) {
      const key = path.replace(/\\/g, '/').toLowerCase();
      const prev = sessionChangedFiles.get(key);
      sessionChangedFiles.set(key, {
        path,
        name: path.split(/[\\/]/).pop() || path,
        added: (prev?.added || 0) + (meta.addedLines || 0),
        removed: (prev?.removed || 0) + (meta.removedLines || 0),
        checkpointId: prev?.checkpointId || checkpointId,
        turnId: meta.turnId || ''
      });
      renderChangedFilesDock();
    }
  }

  if (!card) return;
  const failed = /^(error|tool execution error|blocked)\b/i.test((output || '').trim());
  card.classList.add(failed ? 'is-error' : 'is-done');
  if (activeToolGroup) {
    activeToolGroup.finished++;
    updateToolGroupHeader();
    if (activeToolGroup.finished >= activeToolGroup.count) {
      activeToolGroup.el.classList.remove('is-running');
      activeToolGroup.el.classList.add('is-done');
    }
  }
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
    undoBtn.textContent = 'Undo';
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
    diffBtn.textContent = 'Diff';
    diffBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      sendHost('get_checkpoint_diff', { checkpointId });
    });
    actionsRight.prepend(diffBtn);
  }

  const details = card.querySelector('.tool-card-details');
  if (details) {
    const out = output || '';
    if (meta.diffPreview) {
      setHtml(details, raw(formatColorizedDiffHtml(meta.diffPreview, out)));
    } else if (/** @type {any} */ (card)._patch && (/** @type {any} */ (card)._patch.search || /** @type {any} */ (card)._patch.replace)) {
      const diffText = buildPatchPreview(/** @type {any} */ (card)._patch);
      setHtml(details, raw(formatColorizedDiffHtml(diffText, out)));
    } else if (/** @type {any} */ (card)._streamedLiveOutput) {
      const exitMatch = /^(Exit code:\s*-?\d+)/m.exec(out);
      if (exitMatch) details.textContent += `\n\n${exitMatch[1]}`;
    } else {
      const preview = out.substring(0, 2200);
      details.textContent = `${details.textContent ? details.textContent + '\n\n' : ''}${preview}${out.length > 2200 ? '\n…[truncated]' : ''}`;
    }
  }
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  // Capture rich v2 metadata directly from VoidBridge before legacy app.js handlers run.
  if (window.VoidBridge && typeof window.VoidBridge.subscribe === 'function') {
    window.VoidBridge.subscribe((action, payload) => {
      if (action === 'agent_status' && payload?.status) {
        updateLiveTurnStatus(payload.status);
        return;
      }
      if (action === 'agent_plan_update' && Array.isArray(payload?.steps)) {
        renderLivePlanWidget(payload.steps);
        return;
      }
      if (action === 'agent_tool_output_delta' && payload?.id && typeof payload.line === 'string') {
        appendLiveToolOutputDelta(payload.id, payload.line);
        return;
      }
      if (action === 'agent_done' || action === 'agent_error') {
        clearLiveTurnCard(true);
        finalizeToolGroup();
        return;
      }
      if (!payload) return;
      if (action === 'agent_tool_start' || action === 'agent_tool_approval_needed' || action === 'agent_tool_finish') {
        if (action === 'agent_tool_start') clearLiveTurnCard(false);
        if (payload.id) {
          const prev = toolMetaById.get(payload.id) || {};
          toolMetaById.set(payload.id, { ...prev, ...payload });
          if (toolMetaById.size > 200) {
            const firstKey = toolMetaById.keys().next().value;
            if (firstKey) toolMetaById.delete(firstKey);
          }
        }
      }
    });
  }

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
    getStreamMessageEl,
    clearSessionChangedFiles
  });

  return {
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
    getStreamMessageEl,
    clearSessionChangedFiles
  };
}
