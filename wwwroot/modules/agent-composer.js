// Agent composer, context attachments (@ mentions & chips), Plan/Act mode,
// Continue & Plan action cards, buildAgentContext, and terminal command suggestions/history helper.
import { html, setHtml } from '../core/markdown.js';
import { get as stateGet, set as stateSet } from '../core/state.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

let attachedFiles = [];
let attachedContextItems = [];

// ==========================================
// CONTINUE AFFORDANCE
// ==========================================
export function shouldOfferContinue(finalMessage) {
  if (typeof finalMessage !== 'string') return false;
  const s = finalMessage.trim();
  return /^Reached (the (\d+-step )?safety limit|maximum autonomous turns)/i.test(s) || /^cancell?ed\.?$/i.test(s);
}

export function renderContinueAction() {
  const container = document.getElementById('agent-chat-container');
  if (!container || document.getElementById('agent-continue-card')) return;
  const card = document.createElement('div');
  card.id = 'agent-continue-card';
  card.className = 'agent-continue-card';
  const label = document.createElement('span');
  label.className = 'agent-continue-label';
  label.textContent = 'Run stopped early \u2014 continue where it left off?';
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.id = 'btn-agent-continue';
  btn.className = 'btn agent-continue-btn';
  btn.textContent = 'Continue';
  btn.addEventListener('click', () => {
    card.remove();
    continueAgentRun();
  });
  card.appendChild(label);
  card.appendChild(btn);
  container.appendChild(card);
  globalThis.scrollAgentToBottom?.();
}

export function continueAgentRun() {
  const prompt = 'Continue from where you left off. Keep going until the task is fully done, using the available tools as needed.';
  const mode = stateGet('currentAgentMode', 'act');
  stateSet('currentTurnEdits', []);
  stateSet('lastRunMode', mode);
  globalThis.appendUserMessage?.(prompt, null, prompt);
  const btnSend = document.getElementById('btn-send-agent');
  const btnCancel = document.getElementById('btn-cancel-agent');
  if (btnSend) btnSend.style.display = 'none';
  if (btnCancel) btnCancel.style.display = 'inline-flex';
  globalThis.setAgentStatus?.('Starting', true);
  globalThis.setActivityStatus?.('Running');
  sendHost('start_agent', {
    prompt,
    history: stateGet('chatHistory', []).slice(),
    context: buildAgentContext(),
    mode,
    sessionId: globalThis.getActiveSessionId?.() || ''
  });
}

// ==========================================
// ATTACHED CONTEXT & CHIPS
// ==========================================
export function getAttachedContextItems() {
  return attachedContextItems.map(item => ({
    id: item.id,
    type: item.type,
    label: item.label,
    path: item.path || '',
    content: item.content || ''
  }));
}

export function removeAttachedContextItem(id) {
  const item = attachedContextItems.find(x => x.id === id);
  attachedContextItems = attachedContextItems.filter(x => x.id !== id);
  if (item && item.type === 'file' && item.path) {
    attachedFiles = attachedFiles.filter(x => x !== item.path);
  }
  renderAttachedChips();
}

export function attachContextItem(item) {
  if (!attachedContextItems.some(x => x.id === item.id)) {
    attachedContextItems.push(item);
    renderAttachedChips();
  }
}

export function attachFileToPrompt(path) {
  if (!path) return;
  const name = path.split(/[\\/]/).pop();
  attachContextItem({
    id: 'file:' + path,
    type: 'file',
    label: name,
    path
  });
  if (!attachedFiles.includes(path)) attachedFiles.push(path);
}

export function renderAttachedChips() {
  const bar = document.getElementById('attached-context-bar');
  if (!bar) return;
  if (attachedContextItems.length === 0) {
    bar.style.display = 'none';
    setHtml(bar, '');
    globalThis.updateAgentTokenPill?.();
    return;
  }
  bar.style.display = 'flex';
  setHtml(bar, '');
  globalThis.updateAgentTokenPill?.();
  attachedContextItems.forEach(item => {
    const chip = document.createElement('div');
    chip.className = 'attached-chip';
    chip.title = item.path || item.label;
    setHtml(chip, html`<span>${item.label}</span><span class="remove" role="button" aria-label="Remove ${item.label}">\u00d7</span>`);
    chip.querySelector('.remove')?.addEventListener('click', () => {
      attachedContextItems = attachedContextItems.filter(x => x.id !== item.id);
      if (item.type === 'file' && item.path) {
        attachedFiles = attachedFiles.filter(x => x !== item.path);
      }
      renderAttachedChips();
    });
    bar.appendChild(chip);
  });
  const inspectBtn = document.createElement('button');
  inspectBtn.type = 'button';
  inspectBtn.className = 'btn btn--quiet btn--xs';
  inspectBtn.textContent = 'Inspect context';
  inspectBtn.addEventListener('click', () => globalThis.openContextInspector?.());
  bar.appendChild(inspectBtn);
}

export function getActiveProblemsText() {
  const m = globalThis.monaco;
  if (!m) return '';
  const markers = m.editor.getModelMarkers({});
  if (markers.length === 0) return 'No active problems detected.';
  return markers.slice(0, 30).map(mk => {
    const sev = mk.severity === 8 ? 'Error' : mk.severity === 4 ? 'Warning' : 'Info';
    const file = mk.resource ? mk.resource.path.split('/').pop() : '';
    return `[${sev}] ${file}:${mk.startLineNumber}:${mk.startColumn} - ${mk.message}`;
  }).join('\n');
}

export function getActiveSelectionContext() {
  const monacoEditor = globalThis.getMonacoEditor?.();
  const activeTab = stateGet('activeTab');
  if (!monacoEditor || !activeTab) return null;
  const sel = monacoEditor.getSelection();
  if (!sel || sel.isEmpty()) return null;
  const model = monacoEditor.getModel();
  if (!model) return null;
  const text = model.getValueInRange(sel);
  const startLine = sel.startLineNumber;
  const endLine = sel.endLineNumber;
  const fileName = activeTab.name || activeTab.path.split(/[\\/]/).pop();
  return {
    label: `${fileName}:${startLine}-${endLine}`,
    text
  };
}

export function getActiveGitSummary() {
  const gitStatus = globalThis.getGitStatus?.();
  if (!gitStatus) return 'Git working tree status unavailable.';
  const staged = (gitStatus.staged || []).map(f => `staged: ${f.path} (${f.status})`);
  const working = (gitStatus.working || []).map(f => `modified: ${f.path} (${f.status})`);
  const all = [...staged, ...working];
  return all.length > 0 ? all.join('\n') : 'Git working tree clean (no uncommitted changes).';
}

export function getActiveFolderTree() {
  const allWorkspaceFiles = stateGet('workspaceFiles', []);
  if (!allWorkspaceFiles || allWorkspaceFiles.length === 0) return 'Empty folder';
  return allWorkspaceFiles
    .slice(0, 80)
    .map(file => file.relativePath || file.path || String(file))
    .join('\n');
}

// ==========================================
// PLAN / ACT MODE & PLAN ACTIONS
// ==========================================
export function setAgentMode(mode) {
  const next = mode === 'plan' ? 'plan' : 'act';
  stateSet('currentAgentMode', next);
  const appSettings = stateGet('settings', {});
  if (appSettings) appSettings.AgentMode = next;
  try { localStorage.setItem('voidcode_agent_mode', next); } catch (err) { globalThis.logSwallowed?.(err, 'agent-composer.setAgentMode.persist'); }
  document.querySelectorAll('#agent-mode-toggle .mode-btn').forEach(b => {
    const on = b.getAttribute('data-mode') === next;
    b.classList.toggle('is-active', on);
    b.setAttribute('aria-pressed', on ? 'true' : 'false');
  });
  const input = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
  if (input) {
    input.placeholder = next === 'plan'
      ? 'Describe the task \u2014 the agent investigates read-only and proposes a plan\u2026'
      : 'Describe a task, bug, or feature to build... (Type @ to mention files, git, terminal)';
  }
}

export function renderPlanActions() {
  const container = document.getElementById('agent-chat-container');
  if (!container) return;

  const chatHistory = stateGet('chatHistory', []);
  let planText = '';
  for (let i = chatHistory.length - 1; i >= 0; i--) {
    const m = chatHistory[i];
    if (m && m.role === 'assistant' && (m.content || '').trim()) { planText = m.content; break; }
  }
  if (!planText) return;

  const card = document.createElement('div');
  card.className = 'agent-plan-actions';
  setHtml(card, `
    <div class="agent-plan-head">
      <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="M9 11l3 3L22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/></svg>
      <span>Plan ready \u2014 review before executing</span>
    </div>
    <div class="agent-plan-btn-row">
      <button class="btn btn--primary btn--xs" data-plan-act="execute" type="button">Approve &amp; execute</button>
      <button class="btn btn--quiet btn--xs" data-plan-act="edit" type="button">Edit plan</button>
    </div>
  `);

  card.querySelector('[data-plan-act="execute"]')?.addEventListener('click', () => {
    card.remove();
    setAgentMode('act');
    const text = 'Execute the plan above. Proceed step by step and verify your changes.';
    const mode = stateGet('currentAgentMode', 'act');
    stateSet('currentTurnEdits', []);
    stateSet('lastRunMode', mode);
    globalThis.appendUserMessage?.(text);
    const sendBtn = document.getElementById('btn-send-agent');
    const cancelBtn = document.getElementById('btn-cancel-agent');
    if (sendBtn) sendBtn.style.display = 'none';
    if (cancelBtn) cancelBtn.style.display = 'inline-flex';
    globalThis.setAgentStatus?.('Starting', true);
    sendHost('start_agent', {
      prompt: text,
      history: stateGet('chatHistory', []).slice(),
      context: buildAgentContext(),
      mode,
      sessionId: globalThis.getActiveSessionId?.() || ''
    });
  });

  card.querySelector('[data-plan-act="edit"]')?.addEventListener('click', () => {
    const input = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
    if (input) { input.value = planText; input.focus(); }
    card.remove();
  });

  container.appendChild(card);
  container.scrollTop = container.scrollHeight;
}

// ==========================================
// AGENT CONTEXT BUILDER & COMPOSER WIRING
// ==========================================
export function buildAgentContext() {
  const activeTab = stateGet('activeTab');
  const openTabs = stateGet('openTabs', []);
  const allWorkspaceFiles = stateGet('workspaceFiles', []);
  const monacoEditor = globalThis.getMonacoEditor?.();

  const context = {
    activePath: activeTab ? activeTab.path : null,
    language: activeTab ? (activeTab.language || '') : '',
    selection: '',
    content: '',
    openFiles: openTabs.map(t => t.path),
    workspaceFiles: allWorkspaceFiles.slice(0, 400).map(f =>
      (f && (f.relativePath || f.path)) ? (f.relativePath || f.path) : String(f)
    )
  };

  if (activeTab && activeTab.model && !activeTab.model.isDisposed()) {
    const value = activeTab.model.getValue();
    context.content = value.length > 20000 ? value.slice(0, 20000) + '\n\u2026 (truncated)' : value;
  }

  if (monacoEditor && activeTab && activeTab.model) {
    const sel = monacoEditor.getSelection();
    if (sel && !sel.isEmpty()) {
      context.selection = activeTab.model.getValueInRange(sel);
    }
  }

  return context;
}

export function setupAgentComposer() {
  document.getElementById('agent-chat-container')?.addEventListener('click', (e) => {
    const pill = /** @type {Element | null} */ (e.target)?.closest?.('[data-quick-prompt]');
    if (!pill) return;
    let prompt = pill.getAttribute('data-quick-prompt') || '';
    if (prompt.startsWith('@folder ')) {
      attachContextItem({ id: 'folder', type: 'folder', label: 'Workspace Tree', content: getActiveFolderTree() });
      prompt = prompt.slice('@folder '.length);
    } else if (prompt.startsWith('@git ')) {
      attachContextItem({ id: 'git', type: 'git', label: 'Git Diff', content: getActiveGitSummary() });
      prompt = prompt.slice('@git '.length);
    } else if (prompt.startsWith('@problems ')) {
      attachContextItem({ id: 'problems', type: 'problems', label: 'Problems / Diagnostics', content: getActiveProblemsText() || 'No problems detected' });
      prompt = prompt.slice('@problems '.length);
    }
    const box = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
    if (box && prompt) {
      box.value = prompt;
      box.focus();
      box.setSelectionRange(box.value.length, box.value.length);
    }
  });

  document.getElementById('btn-attach-active-file')?.addEventListener('click', () => {
    const activeTab = stateGet('activeTab');
    if (activeTab) attachFileToPrompt(activeTab.path);
    else globalThis.showToast?.('Open a file first to attach it', 'info');
  });

  const agentInput = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
  const btnSend = document.getElementById('btn-send-agent');
  const btnCancel = document.getElementById('btn-cancel-agent');

  let initialMode = 'act';
  try { initialMode = localStorage.getItem('voidcode_agent_mode') === 'plan' ? 'plan' : 'act'; }
  catch (err) { globalThis.logSwallowed?.(err, 'agent-composer.readMode'); }
  setAgentMode(initialMode);
  document.querySelectorAll('#agent-mode-toggle .mode-btn').forEach(btn => {
    btn.addEventListener('click', () => setAgentMode(btn.getAttribute('data-mode')));
  });

  const agentChatEl = document.getElementById('agent-chat-container');
  const agentScrollBtn = document.getElementById('agent-scroll-latest');
  if (agentChatEl) {
    agentChatEl.addEventListener('scroll', () => {
      const nearBottom = (agentChatEl.scrollHeight - agentChatEl.scrollTop - agentChatEl.clientHeight) < 48;
      stateSet('agentAutoScroll', nearBottom);
      if (agentScrollBtn) agentScrollBtn.style.display = nearBottom ? 'none' : 'inline-flex';
    });
  }
  if (agentScrollBtn) {
    agentScrollBtn.addEventListener('click', () => {
      stateSet('agentAutoScroll', true);
      agentScrollBtn.style.display = 'none';
      if (agentChatEl) agentChatEl.scrollTop = agentChatEl.scrollHeight;
    });
  }

  function triggerAgent() {
    if (!agentInput) return;
    let text = agentInput.value.trim();

    // Mid-run steering: if the agent is already running, queue this guidance for the next step
    // instead of cancelling or starting a conflicting run.
    if (btnCancel && btnCancel.style.display !== 'none' && text) {
      agentInput.value = '';
      agentInput.style.height = '';
      globalThis.VoidBridge?.request?.('agent_steer', { message: text });
      const container = document.getElementById('agent-chat-container');
      if (container) {
        const note = document.createElement('div');
        note.style.cssText = 'align-self:flex-end;padding:6px 10px;border-radius:10px;border:1px dashed var(--glacier-line);background:var(--glacier-wash);font-size:11px;color:var(--glacier);max-width:86%;';
        note.textContent = `Queued guidance: ${text}`;
        container.appendChild(note);
        globalThis.scrollAgentToBottom?.();
      }
      globalThis.showToast?.('Guidance queued for next agent step', 'info');
      return;
    }

    if (!text && !attachedContextItems.some(i => i.type === 'image')) return;
    if (!text) text = 'Analyze the attached screenshot.';

    const originalText = text;
    const displayAttachments = attachedContextItems.length
      ? attachedContextItems.map(i => ({
        type: i.type,
        label: i.label,
        preview: i.type === 'image' && typeof i.content === 'string' && i.content.startsWith('data:image/') ? i.content : ''
      }))
      : attachedFiles.map(f => ({ type: 'file', label: f.split(/[\\/]/).pop() }));

    if (attachedContextItems.length > 0) {
      const blocks = [];
      for (const item of attachedContextItems) {
        if (item.type === 'file') {
          blocks.push(`[Referenced File: ${item.path}]`);
        } else if (item.type === 'image' && item.content) {
          blocks.push(`[Attached Image: ${item.content}]`);
        } else if (item.type === 'git') {
          blocks.push(`[Git Working Tree Status & Diff]:\n${item.content || getActiveGitSummary()}`);
        } else if (item.type === 'terminal') {
          blocks.push(`[Active Terminal Output]:\n${item.content || (globalThis.getActiveTerminalText?.() || '')}`);
        } else if (item.type === 'problems') {
          blocks.push(`[Active Code Problems & Diagnostics]:\n${item.content || getActiveProblemsText()}`);
        } else if (item.type === 'selection') {
          blocks.push(`[Selected Code from ${item.label}]:\n\`\`\`\n${item.content}\n\`\`\``);
        } else if (item.type === 'folder') {
          blocks.push(`[Workspace File Tree]:\n${item.content || getActiveFolderTree()}`);
        }
      }
      text = `${blocks.join('\n\n')}\n\n${text}`;
      attachedContextItems = [];
      attachedFiles = [];
      renderAttachedChips();
    } else if (attachedFiles.length > 0) {
      const refs = attachedFiles.map(f => `[Referenced File: ${f}]`).join('\n');
      text = `${refs}\n\n${text}`;
      attachedFiles = [];
      renderAttachedChips();
    }

    const mode = stateGet('currentAgentMode', 'act');
    stateSet('currentTurnEdits', []);
    stateSet('lastRunMode', mode);

    globalThis.appendUserMessage?.(text, displayAttachments, originalText);
    agentInput.value = '';
    agentInput.style.height = '';

    if (btnSend) btnSend.style.display = 'none';
    if (btnCancel) btnCancel.style.display = 'inline-flex';
    globalThis.setAgentStatus?.('Starting', true);

    sendHost('start_agent', {
      prompt: text,
      history: stateGet('chatHistory', []).slice(),
      context: buildAgentContext(),
      mode,
      sessionId: globalThis.getActiveSessionId?.() || ''
    });
  }

  if (agentInput) {
    agentInput.addEventListener('input', () => {
      agentInput.style.height = 'auto';
      agentInput.style.height = Math.min(agentInput.scrollHeight, 220) + 'px';
    });
    agentInput.addEventListener('paste', (e) => {
      const items = e.clipboardData?.items;
      if (!items) return;
      for (const item of Array.from(items)) {
        if (item.kind === 'file' && item.type.startsWith('image/')) {
          e.preventDefault();
          const blob = item.getAsFile();
          if (!blob) continue;
          const reader = new FileReader();
          reader.onload = () => {
            const dataUri = typeof reader.result === 'string' ? reader.result : '';
            if (dataUri.startsWith('data:image/')) {
              const stamp = new Date().toLocaleTimeString([], { hour12: false }).replace(/:/g, '-');
              attachContextItem({
                id: `img:${Date.now()}`,
                type: 'image',
                label: `Screenshot-${stamp}.png`,
                content: dataUri
              });
              globalThis.showToast?.('Attached screenshot from clipboard', 'success');
            }
          };
          reader.readAsDataURL(blob);
        }
      }
    });
    agentInput.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && !e.shiftKey) {
        const mentionPop = document.getElementById('agent-mention-popover');
        if (mentionPop && mentionPop.style.display !== 'none') return;
        e.preventDefault();
        triggerAgent();
      }
    });
  }

  btnSend?.addEventListener('click', triggerAgent);

  btnCancel?.addEventListener('click', () => {
    sendHost('cancel_agent');
    if (btnSend) btnSend.style.display = 'inline-flex';
    if (btnCancel) btnCancel.style.display = 'none';
    globalThis.setAgentStatus?.('Idle', false);
    globalThis.setActivityStatus?.('Ready');
  });

  document.getElementById('btn-clear-chat')?.addEventListener('click', () => {
    globalThis.startNewSession?.();
  });

  const agentPane = document.getElementById('sidebar-agent');
  if (agentPane) {
    agentPane.addEventListener('dragover', (e) => {
      if (e.dataTransfer?.types?.includes?.('Files')) {
        e.preventDefault();
        e.dataTransfer.dropEffect = 'copy';
      }
    });
    agentPane.addEventListener('drop', (e) => {
      const files = e.dataTransfer?.files;
      if (!files || files.length === 0) return;
      e.preventDefault();
      e.stopPropagation();
      const ws = stateGet('workspace') || '';
      let attached = 0;
      Array.from(files).forEach(f => {
        const p = /** @type {any} */ (f).path || (ws ? `${ws}\\${f.name}` : f.name);
        if (p) {
          attachFileToPrompt(p);
          attached++;
        }
      });
      if (attached > 0) {
        globalThis.showToast?.(`Attached ${attached} file${attached === 1 ? '' : 's'} to Agent chat`, 'success');
      }
    });
  }
}

// ==========================================
// AGENT MENTIONS (@ references)
// ==========================================
let mentionSelectedIndex = 0;
let currentMentionFilteredItems = [];

export function setupAgentMentions() {
  const input = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
  const popover = document.getElementById('agent-mention-popover');
  const list = document.getElementById('agent-mention-list');
  if (!input || !popover || !list) return;

  function closeMentionPopover() {
    popover.style.display = 'none';
  }

  function getBuiltinMentionItems() {
    return [
      { id: 'git', type: 'git', badge: '@git', label: 'Git Diff', desc: 'Working & staged changes' },
      { id: 'terminal', type: 'terminal', badge: '@terminal', label: 'Active Terminal', desc: 'Terminal screen buffer' },
      { id: 'problems', type: 'problems', badge: '@problems', label: 'Problems', desc: 'Current LSP errors & warnings' },
      { id: 'selection', type: 'selection', badge: '@selection', label: 'Selection', desc: 'Active editor selection' },
      { id: 'folder', type: 'folder', badge: '@folder', label: 'Workspace Tree', desc: 'Directory structure overview' }
    ];
  }

  function getBuiltinSlashItems() {
    return [
      { id: 'slash:fix', type: 'slash', badge: '/fix', label: 'Fix Problems & Failures', desc: 'Diagnose & patch active diagnostics', prompt: 'Diagnose and fix all active workspace diagnostics and failing tests. Verify your patch.' },
      { id: 'slash:review', type: 'slash', badge: '/review', label: 'Review Working Diff', desc: 'Audit uncommitted changes for bugs', prompt: 'Review all uncommitted changes in the working tree for bugs, edge cases, and regressions.' },
      { id: 'slash:test', type: 'slash', badge: '/test', label: 'Generate Unit Tests', desc: 'Write tests for the active file', prompt: 'Write comprehensive unit tests covering normal paths, edge cases, and failure modes for the active file.' },
      { id: 'slash:explain', type: 'slash', badge: '/explain', label: 'Explain Architecture', desc: 'Map control flow & invariants', prompt: 'Explain the architecture, entry points, data flow, and core invariants of this module.' },
      { id: 'slash:security', type: 'slash', badge: '/security', label: 'Security & Trust Audit', desc: 'Find injection or boundary bugs', prompt: 'Audit the active file and workspace for injection vectors, path traversal, auth bypasses, and unsafe patterns with exact file:line citations.' },
      { id: 'slash:init', type: 'slash', badge: '/init', label: 'Init AGENTS.md Project Rules', desc: 'Scan repo & generate AGENTS.md', prompt: 'Inspect the workspace directory tree, project files, build/test scripts, and architecture, then create or update root AGENTS.md with concise build/test commands, module boundaries, and coding conventions.' },
      { id: 'slash:todo', type: 'slash', badge: '/todo', label: 'Audit TODO & FIXME Comments', desc: 'Find & resolve open TODO/FIXME items', prompt: 'Search the workspace for all TODO, FIXME, HACK, and BUG comments, summarize them with file:line citations, and propose or apply fixes.' },
      { id: 'slash:commit', type: 'slash', badge: '/commit', label: 'Draft Commit & Stage Plan', desc: 'Summarize @git diff into a commit', prompt: 'Inspect the attached git diff and status, draft a concise Conventional Commit message with bullet points, and verify there are no debug leftovers or secrets.' },
      { id: 'slash:doc', type: 'slash', badge: '/doc', label: 'Generate Documentation', desc: 'Add docstrings / JSDoc / XML docs', prompt: 'Add clear, accurate documentation comments (JSDoc, XML doc comments, or docstrings matching the language) for the public functions, classes, and types in the active file.' },
      { id: 'slash:compact', type: 'slash-action', badge: '/compact', label: 'Inspect & Compact Context', desc: 'Open Agent Context Inspector' }
    ];
  }

  function renderSlashOptions(query) {
    const q = (query || '').toLowerCase().trim();
    const header = popover.querySelector('.mention-header');
    if (header) header.textContent = 'VoidAgent Slash Workflows (/)';
    const matches = getBuiltinSlashItems().filter(s => !q || s.badge.toLowerCase().includes(q) || s.label.toLowerCase().includes(q));
    currentMentionFilteredItems = matches;
    mentionSelectedIndex = Math.min(mentionSelectedIndex, Math.max(0, matches.length - 1));
    if (matches.length === 0) {
      closeMentionPopover();
      return;
    }
    setHtml(list, '');
    matches.forEach((item, idx) => {
      const el = document.createElement('div');
      el.className = 'mention-item' + (idx === mentionSelectedIndex ? ' is-selected' : '');
      el.setAttribute('role', 'option');
      setHtml(el, html`
        <span class="mention-badge">${item.badge}</span>
        <span class="mention-title">${item.label}</span>
        <span class="mention-desc">${item.desc || ''}</span>
      `);
      el.addEventListener('click', () => selectMentionItem(item));
      list.appendChild(el);
    });
    popover.style.display = 'flex';
  }

  function renderMentionOptions(query) {
    const header = popover.querySelector('.mention-header');
    if (header) header.textContent = 'Mention Context (@)';
    const q = (query || '').toLowerCase().trim();
    const builtins = getBuiltinMentionItems();
    const allWorkspaceFiles = stateGet('workspaceFiles', []);
    let matches = [];

    if (!q) {
      matches = [...builtins];
      const topFiles = allWorkspaceFiles.slice(0, 8).map(file => {
        const fullPath = file.path || file.fullPath || String(file);
        const relativePath = file.relativePath || fullPath;
        return {
          id: 'file:' + fullPath,
          type: 'file',
          badge: '@file',
          label: file.name || relativePath.split(/[\\/]/).pop(),
          desc: relativePath,
          path: fullPath
        };
      });
      matches = matches.concat(topFiles);
    } else {
      const filteredBuiltins = builtins.filter(b => b.badge.toLowerCase().includes(q) || b.label.toLowerCase().includes(q));
      const fileMatches = allWorkspaceFiles
        .filter(file => {
          const searchable = `${file.name || ''} ${file.relativePath || ''} ${file.path || ''}`.toLowerCase();
          return searchable.includes(q);
        })
        .slice(0, 10)
        .map(file => {
          const fullPath = file.path || file.fullPath || String(file);
          const relativePath = file.relativePath || fullPath;
          return {
            id: 'file:' + fullPath,
            type: 'file',
            badge: '@file',
            label: file.name || relativePath.split(/[\\/]/).pop(),
            desc: relativePath,
            path: fullPath
          };
        });
      matches = [...filteredBuiltins, ...fileMatches];
    }

    currentMentionFilteredItems = matches;
    mentionSelectedIndex = Math.min(mentionSelectedIndex, Math.max(0, matches.length - 1));

    if (matches.length === 0) {
      setHtml(list, '<div style="padding: 4px 8px; color: var(--text-dim); font-size: var(--fs-xs);">No matching context</div>');
      popover.style.display = 'flex';
      return;
    }

    setHtml(list, '');
    matches.forEach((item, idx) => {
      const el = document.createElement('div');
      el.className = 'mention-item' + (idx === mentionSelectedIndex ? ' is-selected' : '');
      el.setAttribute('role', 'option');
      setHtml(el, html`
        <span class="mention-badge">${item.badge}</span>
        <span class="mention-title">${item.label}</span>
        <span class="mention-desc">${item.desc || ''}</span>
      `);
      el.addEventListener('click', () => selectMentionItem(item));
      list.appendChild(el);
    });

    popover.style.display = 'flex';
  }

  function selectMentionItem(item) {
    if (item.type === 'slash' || item.type === 'slash-action') {
      closeMentionPopover();
      if (item.type === 'slash-action') {
        input.value = '';
        globalThis.openContextInspector?.();
        return;
      }
      if (item.id === 'slash:fix') {
        attachContextItem({ id: 'problems', type: 'problems', label: 'Problems / Diagnostics', content: getActiveProblemsText() || 'No problems detected' });
      } else if (item.id === 'slash:review' || item.id === 'slash:commit') {
        attachContextItem({ id: 'git', type: 'git', label: 'Git Diff', content: getActiveGitSummary() });
      } else if (item.id === 'slash:init') {
        attachContextItem({ id: 'folder', type: 'folder', label: 'Workspace Tree', content: getActiveFolderTree() });
      }
      input.value = item.prompt || '';
      input.focus();
      input.setSelectionRange(input.value.length, input.value.length);
      return;
    }

    const pos = input.selectionStart || 0;
    const text = input.value;
    const lastAt = text.lastIndexOf('@', pos);
    if (lastAt !== -1) {
      input.value = text.substring(0, lastAt) + text.substring(pos);
      input.selectionStart = input.selectionEnd = lastAt;
    }

    if (item.type === 'file') {
      attachContextItem({
        id: item.id,
        type: 'file',
        label: item.label,
        path: item.path
      });
    } else if (item.type === 'git') {
      attachContextItem({
        id: 'git',
        type: 'git',
        label: 'Git Diff',
        content: getActiveGitSummary()
      });
    } else if (item.type === 'terminal') {
      const termContent = globalThis.getActiveTerminalText?.() || '';
      attachContextItem({
        id: 'terminal',
        type: 'terminal',
        label: 'Terminal Buffer',
        content: termContent || '(Empty terminal buffer)'
      });
    } else if (item.type === 'problems') {
      const problemsContent = getActiveProblemsText();
      attachContextItem({
        id: 'problems',
        type: 'problems',
        label: 'Problems / Diagnostics',
        content: problemsContent || 'No problems detected'
      });
    } else if (item.type === 'selection') {
      const sel = getActiveSelectionContext();
      if (sel) {
        attachContextItem({
          id: 'selection',
          type: 'selection',
          label: `Selection (${sel.label})`,
          content: sel.text
        });
      } else {
        globalThis.showToast?.('No text selected in active editor', 'info');
      }
    } else if (item.type === 'folder') {
      attachContextItem({
        id: 'folder',
        type: 'folder',
        label: 'Workspace Tree',
        content: getActiveFolderTree()
      });
    }

    closeMentionPopover();
    input.focus();
  }

  input.addEventListener('input', () => {
    const pos = input.selectionStart || 0;
    const text = input.value.substring(0, pos);
    if (text.startsWith('/') && !/\s/.test(text.slice(1))) {
      mentionSelectedIndex = 0;
      renderSlashOptions(text.slice(1));
      return;
    }
    const lastAt = text.lastIndexOf('@');
    if (lastAt !== -1 && (lastAt === 0 || /\s/.test(text[lastAt - 1]))) {
      const query = text.substring(lastAt + 1);
      if (!/\s/.test(query)) {
        mentionSelectedIndex = 0;
        renderMentionOptions(query);
        return;
      }
    }
    closeMentionPopover();
  });

  input.addEventListener('keydown', (e) => {
    if (popover.style.display !== 'none') {
      const items = list.querySelectorAll('.mention-item');
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        if (items.length > 0) {
          mentionSelectedIndex = (mentionSelectedIndex + 1) % items.length;
          items.forEach((it, i) => it.classList.toggle('is-selected', i === mentionSelectedIndex));
          items[mentionSelectedIndex]?.scrollIntoView({ block: 'nearest' });
        }
        return;
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault();
        if (items.length > 0) {
          mentionSelectedIndex = (mentionSelectedIndex - 1 + items.length) % items.length;
          items.forEach((it, i) => it.classList.toggle('is-selected', i === mentionSelectedIndex));
          items[mentionSelectedIndex]?.scrollIntoView({ block: 'nearest' });
        }
        return;
      }
      if (e.key === 'Enter' || e.key === 'Tab') {
        if (currentMentionFilteredItems.length > 0 && currentMentionFilteredItems[mentionSelectedIndex]) {
          e.preventDefault();
          selectMentionItem(currentMentionFilteredItems[mentionSelectedIndex]);
          return;
        }
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        closeMentionPopover();
        return;
      }
    }
  });

  window.addEventListener('click', (e) => {
    if (!/** @type {Element | null} */ (e.target)?.closest?.('#agent-input-container')) {
      closeMentionPopover();
    }
  });
}

// ==========================================
// TERMINAL COMMAND SUGGESTIONS & HISTORY HELPER
// ==========================================
const TERMINAL_COMMAND_SUGGESTIONS = [
  { cmd: 'git status --short', desc: 'Working tree status and branch info', category: 'git' },
  { cmd: 'git diff', desc: 'Show changes in unstaged modified files', category: 'git' },
  { cmd: 'git log --oneline -n 10', desc: 'Show last 10 commits concisely', category: 'git' },
  { cmd: 'git add .', desc: 'Stage all modified and untracked files', category: 'git' },
  { cmd: 'git commit -m "update"', desc: 'Commit staged changes with message', category: 'git' },
  { cmd: 'git pull', desc: 'Fetch and integrate remote changes', category: 'git' },
  { cmd: 'git push', desc: 'Push local commits to remote branch', category: 'git' },
  { cmd: 'git checkout -b feature-branch', desc: 'Create and switch to new branch', category: 'git' },
  { cmd: 'dotnet build', desc: 'Build C# project and dependencies', category: 'dotnet' },
  { cmd: 'dotnet run', desc: 'Compile and run .NET application', category: 'dotnet' },
  { cmd: 'dotnet test', desc: 'Execute unit test suite', category: 'dotnet' },
  { cmd: 'dotnet clean', desc: 'Clean build outputs and intermediate files', category: 'dotnet' },
  { cmd: 'npm run dev', desc: 'Start local development server', category: 'node' },
  { cmd: 'npm test', desc: 'Run JavaScript and TypeScript tests', category: 'node' },
  { cmd: 'npm run build', desc: 'Compile production bundle', category: 'node' },
  { cmd: 'npm install', desc: 'Install packages from package.json', category: 'node' },
  { cmd: 'python main.py', desc: 'Run Python entrypoint script', category: 'python' },
  { cmd: 'pip install -r requirements.txt', desc: 'Install Python dependencies', category: 'python' },
  { cmd: 'cargo check', desc: 'Fast compiler syntax and type check', category: 'cargo' },
  { cmd: 'cargo build --release', desc: 'Compile optimized release binary', category: 'cargo' },
  { cmd: 'cargo test', desc: 'Execute Rust test suite', category: 'cargo' },
  { cmd: 'Get-Process | Sort-Object CPU -Descending | Select -First 10', desc: 'Top 10 CPU-consuming processes', category: 'ps' },
  { cmd: 'Get-ChildItem -Recurse | Measure-Object', desc: 'Count total files in active workspace', category: 'ps' },
  { cmd: 'Test-NetConnection -ComputerName 127.0.0.1 -Port 8080', desc: 'Check if localhost port is listening', category: 'ps' },
  { cmd: 'Clear-Host', desc: 'Clear the terminal output screen', category: 'ps' },
  { cmd: 'ipconfig', desc: 'Display network adapter configuration', category: 'ps' }
];

let terminalHistory = [];
let terminalSuggestIndex = -1;
let currentSuggestions = [];

export function setupTerminalHelper() {
  try {
    const rawTerm = localStorage.getItem('voidcode_term_history_v1');
    terminalHistory = rawTerm ? JSON.parse(rawTerm) : [];
  } catch (err) {
    globalThis.logSwallowed?.(err, 'agent-composer.loadTerminalHistory');
    terminalHistory = [];
  }

  const popover = document.getElementById('terminal-suggest-popover');
  const listEl = document.getElementById('terminal-suggest-list');
  const btnToggle = document.getElementById('btn-term-suggest-toggle');

  if (!popover || !listEl) return;

  function renderSuggestions(query = '') {
    const q = (query || '').toLowerCase().trim();
    currentSuggestions = TERMINAL_COMMAND_SUGGESTIONS.filter(item => {
      if (!q) return true;
      return item.cmd.toLowerCase().includes(q) || item.desc.toLowerCase().includes(q) || item.category.toLowerCase().includes(q);
    });

    setHtml(listEl, '');
    if (currentSuggestions.length === 0) {
      setHtml(listEl, '<div class="empty-state" style="padding: 8px;">No matching commands.</div>');
      return;
    }

    terminalSuggestIndex = Math.min(terminalSuggestIndex, currentSuggestions.length - 1);
    if (terminalSuggestIndex < 0) terminalSuggestIndex = 0;

    currentSuggestions.forEach((item, idx) => {
      const el = document.createElement('div');
      el.className = 'suggest-item' + (idx === terminalSuggestIndex ? ' is-selected' : '');
      el.setAttribute('role', 'option');
      setHtml(el, html`
        <div class="suggest-left">
          <span class="suggest-badge">${item.category}</span>
          <span class="suggest-cmd">${item.cmd}</span>
          <span class="suggest-desc">${item.desc}</span>
        </div>
      `);
      el.addEventListener('click', () => {
        applySuggestion(item.cmd, true);
      });
      listEl.appendChild(el);
    });
  }

  function showSuggestions() {
    renderSuggestions('');
    popover.style.display = 'flex';
  }

  function hideSuggestions() {
    popover.style.display = 'none';
    terminalSuggestIndex = -1;
  }

  function applySuggestion(cmd, autoRun = false) {
    hideSuggestions();
    if (autoRun && !cmd.includes('<') && !cmd.includes('"update"')) {
      executeTerminalCommand(cmd);
    } else {
      globalThis.sendToActiveTerminal?.(cmd);
    }
  }

  function executeTerminalCommand(cmd) {
    const trimmed = (cmd || '').trim();
    if (!trimmed) return;
    terminalHistory.unshift(trimmed);
    if (terminalHistory.length > 60) terminalHistory = terminalHistory.slice(0, 60);
    try { localStorage.setItem('voidcode_term_history_v1', JSON.stringify(terminalHistory)); } catch (err) { globalThis.logSwallowed?.(err, 'agent-composer.saveTerminalHistory'); }
    hideSuggestions();

    if (!globalThis.sendToActiveTerminal?.(trimmed, true) && globalThis.VoidConsole) {
      globalThis.VoidConsole.print('void \u276f ' + trimmed, 'cmd');
      globalThis.VoidConsole.execute(trimmed);
      globalThis.VoidConsole.focus();
    }
  }

  globalThis.executeTerminalCommand = executeTerminalCommand;

  if (btnToggle) {
    btnToggle.addEventListener('click', (e) => {
      e.stopPropagation();
      if (popover.style.display === 'none') showSuggestions();
      else hideSuggestions();
    });
  }

  document.querySelectorAll('.term-chip[data-term-cmd]').forEach(chip => {
    chip.addEventListener('click', () => {
      const cmd = chip.getAttribute('data-term-cmd');
      if (cmd) executeTerminalCommand(cmd);
    });
  });

  window.addEventListener('click', (e) => {
    if (!/** @type {Element | null} */ (e.target)?.closest?.('#terminal-panel')) {
      hideSuggestions();
    }
  });
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  const api = {
    shouldOfferContinue,
    renderContinueAction,
    continueAgentRun,
    getAttachedContextItems,
    removeAttachedContextItem,
    attachContextItem,
    attachFileToPrompt,
    renderAttachedChips,
    getActiveProblemsText,
    getActiveSelectionContext,
    getActiveGitSummary,
    getActiveFolderTree,
    setAgentMode,
    renderPlanActions,
    buildAgentContext,
    setupAgentComposer,
    setupAgentMentions,
    setupTerminalHelper
  };
  Object.assign(globalThis, api);
  return api;
}
