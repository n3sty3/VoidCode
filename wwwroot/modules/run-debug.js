// One-click smart run & debug (F5): run-config detection, smart run, run/debug entry, and the
// Terminal "fix with AI" banner that reads the run state.
//
// Main.js calls init() and the entry points are exported + published
// On globalThis for the app.js. The terminal-AI banner cluster moved here from
// App.js because it reads and writes this module's lastTerminal* state - those bare-global reads
// Would have thrown the moment the file became a module.
import { html, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

// ==========================================
// ONE-CLICK SMART RUN & DEBUG (F5)
// ==========================================
let activeRunConfig = { Command: '', Label: 'Run', RuntimeType: 'general', Alternatives: [] };
let isSmartRunning = false;
let lastTerminalRunCommand = '';
let lastTerminalErrorSnippet = '';
let lastTerminalExitCode = 0;

// One typed lookup instead of a cast per call site - the controls this module reads
// By id are inputs, and tsc only knows getElementById returns a plain HTMLElement.
/**
 * @param {string} id
 * @returns {HTMLInputElement | null}
 */
function control(id) {
  return /** @type {HTMLInputElement | null} */ (document.getElementById(id));
}

export function setupSmartRun() {
  const btnRun = control('btn-smart-run');
  const btnOptions = control('btn-smart-run-options');
  const dropdown = control('smart-run-dropdown');

  btnRun?.addEventListener('click', () => toggleSmartRun());
  btnOptions?.addEventListener('click', (e) => {
    e.stopPropagation();
    if (dropdown) {
      dropdown.style.display = dropdown.style.display === 'none' ? 'block' : 'none';
    }
  });

  window.addEventListener('click', (e) => {
    if (dropdown && !/** @type {HTMLInputElement} */ (e.target).closest('#smart-run-group')) {
      dropdown.style.display = 'none';
    }
  });

  setupTerminalAiBanner();
}

export function refreshRunConfig() {
  const stateActiveTab = window.VoidState.get('activeTab');
  sendHost('detect_run_config', { activePath: stateActiveTab ? stateActiveTab.path : null });
}

export function syncRunAvailability() {
  // The Debug entry point shares this tab-change hook; keep its enabled state honest.
  if (typeof globalThis.syncDebugAvailability === 'function') globalThis.syncDebugAvailability();

  const btnRun = control('btn-smart-run');
  const btnOptions = control('btn-smart-run-options');
  const labelEl = control('smart-run-label');
  if (!btnRun) return;

  const hasCommand = !!(activeRunConfig && (activeRunConfig.Command ||
    (activeRunConfig.Alternatives && activeRunConfig.Alternatives.length > 0)));
  // A project-level command is not a reason to offer Run while the editor is
  // Empty: the no-file state must be the only call to action on screen
  const hasTarget = !!window.VoidState.get('activeTab');
  const enabled = hasCommand && hasTarget;

  btnRun.disabled = !enabled;
  btnRun.setAttribute('aria-disabled', enabled ? 'false' : 'true');
  btnRun.title = !hasTarget
    ? 'Open a file to run it'
    : (hasCommand ? `Run: ${activeRunConfig.Command} (Ctrl+F5)` : 'No run command detected for this file');

  if (btnOptions) {
    btnOptions.disabled = !enabled;
    btnOptions.setAttribute('aria-disabled', enabled ? 'false' : 'true');
  }
  if (!enabled && labelEl && !isSmartRunning) labelEl.textContent = 'Run';
  return enabled;
}

export function handleRunConfigDetected(config) {
  const labelEl = control('smart-run-label');
  const hasCommand = !!(config && (config.Command || (config.Alternatives && config.Alternatives.length > 0)));

  activeRunConfig = hasCommand ? config : null;

  if (!hasCommand) {
    const altList = control('smart-run-alternatives');
    if (altList) setHtml(altList, '');
    syncRunAvailability();
    return;
  }

  if (labelEl && !isSmartRunning) {
    labelEl.textContent = config.Label || 'Run';
  }

  // Render alternatives dropdown
  const listEl = control('smart-run-alternatives');
  if (listEl) {
    setHtml(listEl, '');
    const items = config.Alternatives || [];
    if (items.length === 0 && config.Command) {
      items.push({ Label: config.Label, Command: config.Command });
    }
    items.forEach(item => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'run-option-item' + (item.Command === config.Command ? ' active' : '');
      setHtml(btn, html`<span>${item.Label}</span><kbd>${item.Command}</kbd>`);
      btn.addEventListener('click', () => {
        activeRunConfig.Command = item.Command;
        activeRunConfig.Label = item.Label;
        if (labelEl && !isSmartRunning) labelEl.textContent = item.Label;
        control('smart-run-dropdown').style.display = 'none';
        toggleSmartRun(item.Command);
      });
      listEl.appendChild(btn);
    });
  }

  syncRunAvailability();
}

export function toggleSmartRun(forcedCmd) {
  const group = control('smart-run-group');
  const labelEl = control('smart-run-label');

  if (isSmartRunning && !forcedCmd) {
    // Stop running process
    sendHost('kill_terminal');
    isSmartRunning = false;
    group?.classList.remove('is-running');
    if (labelEl) labelEl.textContent = (activeRunConfig && activeRunConfig.Label) || 'Run';
    globalThis.showToast?.('Process terminated', 'info');
    return;
  }

  const cmdToRun = forcedCmd || (activeRunConfig && activeRunConfig.Command);
  if (!cmdToRun) {
    globalThis.showToast?.('No run command detected. Open a project or code file.', 'info');
    return;
  }

  if (cmdToRun === '__PREVIEW__') {
    globalThis.toggleMiniWindow?.('win-preview');
    globalThis.renderLivePreviewFromActiveTab?.();
    return;
  }

  // Ensure terminal pane is open
  const termPanel = control('terminal-panel');
  if (termPanel && termPanel.classList.contains('collapsed')) {
    globalThis.setTerminalCollapsed?.(false);
  }
  globalThis.toggleWorkspacePane?.('terminal', true);

  // Set running state
  isSmartRunning = true;
  group?.classList.add('is-running');
  if (labelEl) labelEl.textContent = 'Stop';

  lastTerminalRunCommand = cmdToRun;
  lastTerminalErrorSnippet = '';
  hideTerminalAiBanner();

  globalThis.executeTerminalCommand?.(cmdToRun);
}

export function handleSmartRunEnded(_exitCode) {
  isSmartRunning = false;
  const group = control('smart-run-group');
  const labelEl = control('smart-run-label');
  group?.classList.remove('is-running');
  if (labelEl) labelEl.textContent = (activeRunConfig && activeRunConfig.Label) || 'Run';
}

export function handleTerminalCommandDone(exitCode) {
  if (isSmartRunning) {
    handleSmartRunEnded(exitCode);
  }

  if (exitCode !== 0) {
    showTerminalAiBanner(exitCode, lastTerminalErrorSnippet || `Command exited with status code ${exitCode}`);
  } else {
    hideTerminalAiBanner();
  }
}

export function recordTerminalOutput(text, isErr) {
  if (isErr || /(?:error|exception|fail|failed|fatal|cannot find|build FAILED)/i.test(text)) {
    lastTerminalErrorSnippet += text;
    if (lastTerminalErrorSnippet.length > 5000) {
      lastTerminalErrorSnippet = lastTerminalErrorSnippet.slice(-5000);
    }
  }
}

// ==========================================
// TERMINAL "FIX WITH AI" BANNER
// ==========================================
//  Everything below reads the run state declared above.
export function setupTerminalAiBanner() {
  control('btn-terminal-fix-ai')?.addEventListener('click', () => {
    openAgentWithTerminalError(true);
  });
  control('btn-terminal-explain-ai')?.addEventListener('click', () => {
    openAgentWithTerminalError(false);
  });
  control('btn-terminal-dismiss-banner')?.addEventListener('click', () => {
    hideTerminalAiBanner();
  });
}

export function showTerminalAiBanner(exitCode, snippet) {
  const banner = control('terminal-ai-banner');
  const snippetEl = control('terminal-ai-error-snippet');
  if (!banner || !snippetEl) return;

  lastTerminalExitCode = exitCode;
  const cleanSnippet = (snippet || '').trim().replace(/\r?\n/g, ' ').slice(0, 80);
  snippetEl.textContent = cleanSnippet
    ? `Error: ${cleanSnippet}… (code ${exitCode})`
    : `Command failed with exit code ${exitCode}`;

  banner.style.display = 'flex';
}

export function hideTerminalAiBanner() {
  const banner = control('terminal-ai-banner');
  if (banner) banner.style.display = 'none';
}

export function openAgentWithTerminalError(isFix) {
  globalThis.toggleWorkspacePane?.('agent', true);

  const activeTab = window.VoidState.get('activeTab');
  const activePath = activeTab ? activeTab.path : 'the project';
  const cleanTrace = (lastTerminalErrorSnippet || '').trim().slice(-3000);
  let prompt = '';

  if (isFix) {
    prompt = `I ran \`${lastTerminalRunCommand || 'the build command'}\` and encountered an error (exit code ${lastTerminalExitCode}).\n\nError output:\n\`\`\`\n${cleanTrace || 'Command failed with non-zero exit code.'}\n\`\`\`\n\nPlease inspect the code in ${activePath}, diagnose the root cause, and fix it.`;
  } else {
    prompt = `Please explain why \`${lastTerminalRunCommand || 'this command'}\` failed with exit code ${lastTerminalExitCode}:\n\n\`\`\`\n${cleanTrace || 'Process exited with error.'}\n\`\`\``;
  }

  // Index.html calls this textarea agent-input-text; the old lookup used
  // "agent-input", so the prompt was dropped on the floor and the palette commands did nothing.
  const agentInput = control('agent-input-text');
  if (agentInput) {
    agentInput.value = prompt;
    control('btn-send-agent')?.click();
  }

  hideTerminalAiBanner();
}

// The Problems panel in app.js parses the captured build output, so it asks for it
// Here instead of reading this module's state directly.
export function getLastTerminalErrorSnippet() {
  return lastTerminalErrorSnippet;
}

// Main.js calls this once; the entry points go on globalThis for the classic app.js
//.
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  Object.assign(globalThis, {
    setupSmartRun,
    refreshRunConfig,
    syncRunAvailability,
    handleRunConfigDetected,
    toggleSmartRun,
    handleSmartRunEnded,
    handleTerminalCommandDone,
    recordTerminalOutput,
    setupTerminalAiBanner,
    showTerminalAiBanner,
    hideTerminalAiBanner,
    openAgentWithTerminalError,
    getLastTerminalErrorSnippet
  });
  return { setupSmartRun, refreshRunConfig, syncRunAvailability, handleRunConfigDetected, toggleSmartRun, handleSmartRunEnded, handleTerminalCommandDone, recordTerminalOutput, setupTerminalAiBanner, showTerminalAiBanner, hideTerminalAiBanner, openAgentWithTerminalError, getLastTerminalErrorSnippet };
}
