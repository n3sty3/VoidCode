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

export function renderAgentError(payload) {
  const message = String(payload && payload.message ? payload.message : 'The agent run failed.');
  const container = control('agent-chat-container');
  if (!container) return false;

  const card = document.createElement('div');
  card.className = 'empty-state agent-error-state';
  card.setAttribute('role', 'alert');
  const title = document.createElement('p');
  title.textContent = 'Agent run failed';
  const detail = document.createElement('p');
  detail.className = 'empty-hint';
  detail.textContent = message;
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
  if (wantsSettings) {
    const row = document.createElement('div');
    row.style.display = 'flex';
    row.style.gap = '8px';
    row.style.justifyContent = 'center';
    row.style.flexWrap = 'wrap';
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
    row.append(action, localBtn);
    card.append(title, detail, row);
  } else {
    card.append(title, detail, action);
  }
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
    clone.querySelectorAll('.msg-attachments, .msg-actions').forEach(n => n.remove());
    const plain = (clone.textContent || '').trim();
    const input = control('agent-input-text');
    if (input) { input.value = plain; input.focus(); input.scrollIntoView({ block: 'nearest' }); }
  });
  bar.appendChild(edit);
  content.appendChild(bar);
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
  globalThis.setAgentStatus?.('Starting', true);
  sendHost('start_agent', {
    prompt: userText,
    history: history.slice(),
    context: globalThis.buildAgentContext?.(),
    mode: agentMode
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
    regenerateLastAnswer,
    renderRunFooter,
    buildPatchPreview,
    scrollAgentToBottom,
    renderAgentError
  });
  return { addAssistantMessageActions, addUserMessageActions, regenerateLastAnswer, renderRunFooter, buildPatchPreview, scrollAgentToBottom, renderAgentError };
}