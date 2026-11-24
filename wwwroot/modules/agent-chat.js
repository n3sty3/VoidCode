// Agent chat UI helpers: per-message action bars, regenerate, run footer, patch preview,
// And auto-scroll.
//
// Main.js calls init() and the six helpers are exported + published
// On globalThis for the app.js and modules/agent-stream.js. The agent state comes from
// The store; `currentTurnEdits`/`lastRunMode` moved into it too, because regenerating an answer has
// To reset them and a module cannot assign another script's `let`.
let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

// Small per-message action bar. Add is idempotent per message.
// One typed lookup instead of a cast per call site - the controls this module reads
// By id are inputs, and tsc only knows getElementById returns a plain HTMLElement.
/**
 * @param {string} id
 * @returns {HTMLInputElement | null}
 */
function control(id) {
  return /** @type {HTMLInputElement | null} */ (document.getElementById(id));
}

// An agent failure used to be a wall of red text and nothing to do about it. Every
// Failure now ends in exactly one primary action, chosen from the message the host sent:
//   * configuration problems (missing/rejected key, unknown model, bad base URL) -> open the section that
//     Holds the fix;
//   * everything else (network, rate limit, host error) -> retry, which is what the user was about to do.
// The card reuses the existing .empty-state vocabulary, so it needs no new CSS.
const CONFIG_FAILURE = /(api key|unauthor|forbidden|401|403|model name|base url|not configured|invalid model)/i;
const RATE_LIMIT_FAILURE = /(429|too\s*many\s*requests|rate\s*limit|quota|credits)/i;

function parseAgentErrorMessage(rawMessage) {
  const raw = String(rawMessage || 'The agent run failed.').trim();
  const statusMatch = /\((\d{3})[^)]*\)/.exec(raw);
  const statusCode = statusMatch ? parseInt(statusMatch[1], 10) : null;

  let summary = raw;
  let hasDetails = false;

  const jsonStart = raw.indexOf('{');
  if (jsonStart >= 0) {
    try {
      const parsed = JSON.parse(raw.slice(jsonStart));
      const extracted = parsed?.error?.message || parsed?.message || parsed?.detail;
      if (typeof extracted === 'string' && extracted.trim()) {
        summary = extracted.trim();
        hasDetails = true;
      }
    } catch {
      // Malformed/truncated JSON: fall back to regex extraction of "message":"..."
      const msgMatch = /"message"\s*:\s*"((?:\\.|[^"\\])+)"/.exec(raw);
      if (msgMatch) {
        summary = msgMatch[1].replace(/\\"/g, '"').replace(/\\n/g, ' ').trim();
        hasDetails = true;
      }
    }
  }

  let badge = '';
  if (statusCode === 429 || RATE_LIMIT_FAILURE.test(raw)) badge = '429 Rate limit';
  else if (statusCode === 401 || statusCode === 403) badge = `${statusCode} Auth error`;
  else if (statusCode === 404) badge = '404 Not found';
  else if (statusCode && statusCode >= 500) badge = `${statusCode} Server error`;

  return { badge, summary, raw, hasDetails, isRateLimit: statusCode === 429 || RATE_LIMIT_FAILURE.test(raw) };
}

export function renderAgentError(payload) {
  const message = String(payload && payload.message ? payload.message : 'The agent run failed.');
  const container = control('agent-chat-container');
  if (!container) return false;

  const parsed = parseAgentErrorMessage(message);

  const card = document.createElement('div');
  card.className = 'empty-state agent-error-state';
  card.setAttribute('role', 'alert');
  card.style.cssText = 'text-align:left;align-items:stretch;gap:8px;padding:12px 14px;overflow:hidden;border-color:rgba(248,113,113,0.32);background:rgba(248,113,113,0.06);';

  const headRow = document.createElement('div');
  headRow.style.cssText = 'display:flex;align-items:center;justify-content:space-between;gap:8px;flex-wrap:wrap;';

  const title = document.createElement('p');
  title.style.cssText = 'margin:0;font-weight:600;font-size:13px;color:var(--coral,#f87171);';
  title.textContent = 'Agent run failed';
  headRow.appendChild(title);

  if (parsed.badge) {
    const pill = document.createElement('span');
    pill.style.cssText = 'font-family:var(--font-code);font-size:10.5px;padding:2px 7px;border-radius:999px;background:rgba(248,113,113,0.16);border:1px solid rgba(248,113,113,0.35);color:var(--coral,#f87171);';
    pill.textContent = parsed.badge;
    headRow.appendChild(pill);
  }

  const detail = document.createElement('p');
  detail.className = 'empty-hint';
  detail.style.cssText = 'margin:0;font-size:12px;line-height:1.45;color:var(--text);overflow-wrap:anywhere;word-break:break-word;white-space:pre-wrap;max-width:100%;';
  detail.textContent = parsed.summary;

  const action = document.createElement('button');
  action.type = 'button';
  action.className = 'btn btn--primary btn--xs';
  const wantsSettings = CONFIG_FAILURE.test(message);
  action.textContent = wantsSettings ? 'Open settings' : 'Retry';
  action.addEventListener('click', () => {
    card.remove();
    if (wantsSettings) globalThis.openSettingsToTab?.('tab-provider');
    else regenerateLastAnswer();
  });

  const row = document.createElement('div');
  row.style.cssText = 'display:flex;gap:6px;flex-wrap:wrap;align-items:center;margin-top:2px;';
  row.appendChild(action);

  if (wantsSettings) {
    const localBtn = document.createElement('button');
    localBtn.type = 'button';
    localBtn.className = 'btn btn--quiet btn--xs';
    localBtn.textContent = 'Use Local Ollama';
    localBtn.addEventListener('click', () => {
      card.remove();
      globalThis.openSettingsToTab?.('tab-provider');
      const prov = control('settings-provider');
      if (prov) {
        prov.value = 'ollama';
        prov.dispatchEvent(new Event('change'));
        globalThis.syncAllCustomSelects?.();
      }
    });
    row.appendChild(localBtn);
  } else if (parsed.isRateLimit && typeof globalThis.openQuickModelSwitcher === 'function') {
    const switchBtn = document.createElement('button');
    switchBtn.type = 'button';
    switchBtn.className = 'btn btn--quiet btn--xs';
    switchBtn.textContent = 'Switch model';
    switchBtn.addEventListener('click', () => globalThis.openQuickModelSwitcher?.());
    row.appendChild(switchBtn);
  }

  card.append(headRow, detail);

  if (parsed.hasDetails) {
    const detailsEl = document.createElement('details');
    detailsEl.style.cssText = 'margin-top:2px;font-size:11px;color:var(--text-muted);';
    const summaryEl = document.createElement('summary');
    summaryEl.textContent = 'Technical details';
    summaryEl.style.cssText = 'cursor:pointer;user-select:none;opacity:0.8;';
    const pre = document.createElement('pre');
    pre.style.cssText = 'margin:6px 0 0;padding:8px;border-radius:7px;background:rgba(0,0,0,0.32);border:1px solid var(--seam);font-family:var(--font-code);font-size:10.5px;white-space:pre-wrap;overflow-wrap:anywhere;max-height:140px;overflow-y:auto;color:var(--text-muted);';
    pre.textContent = parsed.raw;
    detailsEl.append(summaryEl, pre);
    card.appendChild(detailsEl);
  }

  card.appendChild(row);
  container.appendChild(card);
  scrollAgentToBottom();
  return true;
}

export function addAssistantMessageActions(msgEl) {
  const content = msgEl.querySelector('.msg-content');
  if (!content || content.querySelector('.msg-actions')) return;
  const source = msgEl.querySelector('.stream-markdown-body') || content;
  const fullText = source.innerText || '';
  if (!fullText.trim()) return;

  const bar = document.createElement('div');
  bar.className = 'msg-actions';

  const copy = document.createElement('button');
  copy.type = 'button';
  copy.className = 'msg-action-btn';
  copy.textContent = 'Copy';
  copy.addEventListener('click', () => {
    if (navigator.clipboard) navigator.clipboard.writeText(fullText);
    copy.textContent = 'Copied';
    setTimeout(() => { copy.textContent = 'Copy'; }, 1200);
  });
  bar.appendChild(copy);

  const retry = document.createElement('button');
  retry.type = 'button';
  retry.className = 'msg-action-btn';
  retry.textContent = 'Regenerate';
  retry.addEventListener('click', () => regenerateLastAnswer());
  bar.appendChild(retry);

  content.appendChild(bar);
}

export function addUserMessageActions(msgEl) {
  const content = msgEl.querySelector('.msg-content');
  if (!content || content.querySelector('.msg-actions')) return;
  const bar = document.createElement('div');
  bar.className = 'msg-actions msg-actions--user';

  const edit = document.createElement('button');
  edit.type = 'button';
  edit.className = 'msg-action-btn';
  edit.textContent = 'Edit';
  edit.addEventListener('click', () => {
    const clone = content.cloneNode(true);
    clone.querySelectorAll('.msg-attachments, .msg-actions, .user-prompt-eyebrow').forEach(n => n.remove());
    const plain = (clone.textContent || '').trim();
    const input = control('agent-input-text');
    if (input) { input.value = plain; input.focus(); input.scrollIntoView({ block: 'nearest' }); }
  });
  bar.appendChild(edit);
  content.appendChild(bar);
}

export function refreshUserMessageRewindButton(msgEl) {
  if (!msgEl) return;
  const bar = msgEl.querySelector('.msg-actions--user');
  if (!bar || bar.querySelector('.btn-rewind-turn')) return;
  const checkpoints = /** @type {any} */ (msgEl)._turnCheckpoints;
  if (!Array.isArray(checkpoints) || checkpoints.length === 0) return;

  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'msg-action-btn btn-rewind-turn';
  btn.title = 'Restore modified workspace files and rewind chat back to before this prompt';
  btn.textContent = '↩ Restore';
  btn.addEventListener('click', async () => {
    const container = control('agent-chat-container');
    if (!container) return;

    // Collect all checkpoints from this user message and every subsequent user message.
    const allNodes = Array.from(container.children);
    const startIdx = allNodes.indexOf(msgEl);
    if (startIdx < 0) return;

    const toRestore = [];
    const seenFiles = new Set();
    for (let i = startIdx; i < allNodes.length; i++) {
      const cps = /** @type {any} */ (allNodes[i])._turnCheckpoints;
      if (Array.isArray(cps)) {
        for (const cp of cps) {
          toRestore.push(cp);
          if (cp.path) seenFiles.add(cp.path.split(/[\\/]/).pop());
        }
      }
    }

    const filePreview = Array.from(seenFiles).slice(0, 6).join(', ');
    const ok = typeof globalThis.showConfirmDialog === 'function'
      ? await globalThis.showConfirmDialog(
        'Restore Workspace Checkpoint',
        `Revert ${toRestore.length} edit checkpoint${toRestore.length === 1 ? '' : 's'} (${filePreview || 'modified files'}) and rewind chat to before this prompt?`,
        'Restore'
      )
      : true;
    if (!ok) return;

    // Restore in reverse chronological order so earliest snapshot wins cleanly.
    for (let i = toRestore.length - 1; i >= 0; i--) {
      if (toRestore[i].checkpointId) {
        sendHost('restore_checkpoint', { checkpointId: toRestore[i].checkpointId });
      }
    }

    // Rewind DOM and chatHistory back to just before this user message.
    const histIdx = parseInt(msgEl.dataset.historyIndex || '-1', 10);
    const history = window.VoidState.get('chatHistory');
    if (Array.isArray(history) && histIdx >= 0) {
      history.splice(histIdx);
    }
    for (let i = allNodes.length - 1; i >= startIdx; i--) {
      allNodes[i].remove();
    }
    globalThis.clearSessionChangedFiles?.();
    globalThis.VoidBridge?.request?.('agent_clear_trace', { sessionId: globalThis.getActiveSessionId?.() || '' });
    globalThis.saveCurrentSessionToStorage?.();
    globalThis.showToast?.(`Restored workspace to checkpoint (${toRestore.length} edit${toRestore.length === 1 ? '' : 's'} reverted)`, 'success');
  });

  bar.appendChild(btn);
}

// Re-run the most recent user prompt, replacing the last answer.
export function regenerateLastAnswer() {
  const container = control('agent-chat-container');
  if (!container) return;
  const assistants = container.querySelectorAll(':scope > .chat-msg.assistant');
  if (assistants.length) assistants[assistants.length - 1].remove();
  // The agent slice comes from the store (the history array is held by reference).
  const history = window.VoidState.get('chatHistory');
  const agentMode = window.VoidState.get('currentAgentMode');
  for (let i = history.length - 1; i >= 0; i--) {
    if (history[i].role === 'assistant') { history.splice(i, 1); break; }
    if (history[i].role === 'user') break;
  }
  let userText = '';
  for (let i = history.length - 1; i >= 0; i--) {
    if (history[i].role === 'user') { userText = history[i].content; break; }
  }
  if (!userText) return;

  window.VoidState.set('currentTurnEdits', []);
  window.VoidState.set('lastRunMode', agentMode);
  const sendBtn = control('btn-send-agent');
  const cancelBtn = control('btn-cancel-agent');
  if (sendBtn) sendBtn.style.display = 'none';
  if (cancelBtn) cancelBtn.style.display = 'inline-flex';
  globalThis.VoidBridge?.request?.('agent_clear_trace', { sessionId: globalThis.getActiveSessionId?.() || '' });
  globalThis.setAgentStatus?.('Starting', true);
  sendHost('start_agent', {
    prompt: userText,
    history: history.slice(),
    context: globalThis.buildAgentContext?.(),
    mode: agentMode,
    sessionId: globalThis.getActiveSessionId?.() || ''
  });
}

// Compact per-run telemetry footer under the transcript.
export function renderRunFooter(p) {
  const container = control('agent-chat-container');
  if (!container) return;
  const approx = p.estimated ? '≈' : '';
  const fmt = n => (n == null ? '0' : (n >= 1000 ? (n / 1000).toFixed(1) + 'k' : String(n)));
  const parts = [];
  if (p.steps) parts.push(`${p.steps} step${p.steps === 1 ? '' : 's'}`);
  if (p.toolCalls) parts.push(`${p.toolCalls} tool call${p.toolCalls === 1 ? '' : 's'}`);
  if (typeof p.totalTokens === 'number') parts.push(`${approx}${fmt(p.totalTokens)} tok`);
  if (p.cachedTokens > 0) parts.push(`⚡ ${fmt(p.cachedTokens)} cached`);
  if (p.costLabel) {
    const saved = p.savingsLabel ? ` (saved ${p.savingsLabel})` : '';
    parts.push(`${p.costLabel}${saved}`);
  }
  if (p.elapsedMs) parts.push(`${(p.elapsedMs / 1000).toFixed(1)}s`);
  if (!parts.length) return;

  if (typeof p.totalTokens === 'number' && globalThis.updateAgentTokenPill) {
    globalThis.updateAgentTokenPill({
      totalTokens: p.totalTokens,
      costLabel: p.costLabel || '',
      savingsLabel: p.savingsLabel || '',
      cachedTokens: p.cachedTokens || 0
    });
  }

  const el = document.createElement('div');
  el.className = 'agent-run-footer';
  el.textContent = parts.join(' · ');
  container.appendChild(el);
  scrollAgentToBottom();
}

// Build a compact -/+ preview from a patch_file's search/replace.
export function buildPatchPreview(patch) {
  const clip = (s, n) => { s = s || ''; return s.length > n ? s.slice(0, n) + '…' : s; };
  const removed = clip(patch.search, 1200).split('\n').map(l => '- ' + l).join('\n');
  const added = clip(patch.replace, 1200).split('\n').map(l => '+ ' + l).join('\n');
  return `${removed}\n${added}`;
}

// Only auto-scroll the transcript when the user is already near the bottom,
// So reading older messages is never yanked away by streaming.
export function scrollAgentToBottom() {
  if (!window.VoidState.get('agentAutoScroll')) return;
  const container = control('agent-chat-container');
  if (container) container.scrollTop = container.scrollHeight;
}

// Main.js calls this once; the helpers go on globalThis for the classic app.js and
// Modules/agent-stream.js until they are converted.
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  Object.assign(globalThis, {
    addAssistantMessageActions,
    addUserMessageActions,
    refreshUserMessageRewindButton,
    regenerateLastAnswer,
    renderRunFooter,
    buildPatchPreview,
    scrollAgentToBottom,
    renderAgentError
  });
  return { addAssistantMessageActions, addUserMessageActions, refreshUserMessageRewindButton, regenerateLastAnswer, renderRunFooter, buildPatchPreview, scrollAgentToBottom, renderAgentError };
}