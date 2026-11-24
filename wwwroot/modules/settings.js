// Settings: the settings modal (render/read), readability modes, auto-save and Workspace Trust.
//
// The last converted file besides app.js itself. main.js calls
// Init(), which publishes the entry points the app.js calls (applySettingsToUI,
// GatherSettingsFromModal, applyReadabilityMode, setupReadabilityModes, setupAutoSave) and installs
// The Workspace Trust subscription that used to sit in this file's module body.
//
// Collaborators that are still classic scripts: the editor handle comes from getMonacoEditor(), the
// Slider/custom-select painters and the tab savers are reached as globals, and the operator name
// Comes from getCurrentOperator() instead of a script-scoped `let`.
import { setHtml } from '../core/markdown.js';
import { applyThemeAndOptics } from './theme.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

// The "remove the saved API key" flag the modal's clear button sets. It used to be a script-scope
// `let` in app.js with a setter accessor; moved its only reader and its only
// Writer here, so the accessor pair is gone.
// This screen is a form read by id, and tsc knows document.getElementById returns a
// Plain HTMLElement - no .value/.checked/.placeholder. One typed lookup replaces every cast.
/**
 * @param {string} id
 * @returns {HTMLInputElement | null} the element, typed as the control this screen reads
 */
function control(id) {
  return /** @type {HTMLInputElement | null} */ (document.getElementById(id));
}

let apiKeyClearRequested = false;

export function applySettingsToUI(s, settingScopes = null) {
  if (settingScopes && typeof settingScopes === 'object') {
    window.VoidState?.set?.('settingScopes', settingScopes);
    const wsOverrides = Object.entries(settingScopes).filter(([, scope]) => scope === 'workspace').map(([k]) => k);
    const subtitle = document.querySelector('#win-settings .modal-subtitle');
    if (subtitle) {
      subtitle.textContent = wsOverrides.length > 0
        ? `Workspace overrides active (.voidcode/settings.json): ${wsOverrides.join(', ')}`
        : 'Model endpoint, editor preferences, agent permissions, and keyboard shortcuts.';
    }
  }
  control('active-model-label').textContent = s.Model || 'deepseek/deepseek-chat';
  // The model chip collapses to its icon below 1280px, so the full name has to live in
  // The tooltip and the dot has to say whether the provider is configured - otherwise the collapse
  // Hides the only place the model name was visible.
  const modelChip = control('btn-open-settings');
  if (modelChip) {
    const model = s.Model || 'deepseek/deepseek-chat';
    modelChip.title = `Model: ${model} - configure model & settings`;
    modelChip.classList.toggle('is-ready', !!s.ApiKeyConfigured);
    modelChip.setAttribute('aria-label', `Model ${model}. Configure model and settings`);
  }
  control('settings-provider').value = s.Provider || 'openrouter';
  control('settings-base-url').value = s.BaseUrl || 'https://openrouter.ai/api/v1';
  const apiKeyInput = control('settings-api-key');
  const apiKeyHelp = control('settings-api-key-help');
  const clearApiKeyButton = control('btn-clear-api-key');
  apiKeyInput.value = '';
  apiKeyInput.placeholder = s.ApiKeyConfigured
    ? 'Stored securely. Enter a new key to replace it'
    : 'Enter an API key (not required for local providers)';
  if (apiKeyHelp) {
    const isMac = /mac/i.test(navigator.platform || navigator.userAgent || '');
    const storeLabel = isMac ? 'macOS Keychain' : 'this Windows account (DPAPI)';
    apiKeyHelp.textContent = s.ApiKeyConfigured
      ? `A key is encrypted in ${storeLabel}. Leave this field empty to keep it.`
      : `Keys are encrypted in ${storeLabel} and stored separately from settings.json.`;
  }
  if (clearApiKeyButton) clearApiKeyButton.style.display = s.ApiKeyConfigured ? 'inline-flex' : 'none';
  // The flag lives in this module now (the modal's clear-key button owns it - )
  apiKeyClearRequested = false;
  control('settings-model').value = s.Model || 'deepseek/deepseek-chat';
  control('settings-system-prompt').value = s.SystemPrompt || PERSONA_PRESETS.voidcode;
  syncPersonaCards(control('settings-system-prompt').value);
  control('settings-temperature').value = s.Temperature ?? 0.2;
  control('lbl-temp-val').textContent = s.Temperature ?? 0.2;
  const maxTok = s.MaxTokens || 8192;
  control('settings-max-tokens').value = maxTok;
  document.querySelectorAll('.token-pill').forEach(p => {
    p.classList.toggle('is-active', parseInt(p.getAttribute('data-token-val'), 10) === maxTok);
  });
  const maxTurns = (s.MaxAgentTurns === 0) ? 0 : (s.MaxAgentTurns || 25);
  control('settings-max-turns').value = maxTurns;
  const turnsLbl = control('lbl-turns-val');
  if (turnsLbl) turnsLbl.textContent = maxTurns === 0 ? 'Unlimited' : `${maxTurns} step${maxTurns === 1 ? '' : 's'}`;
  control('settings-font-size').value = s.EditorFontSize || 14;
  control('settings-minimap').checked = s.EditorMinimap !== false;
  control('settings-word-wrap').checked = !!s.EditorWordWrap;
  const fmtOnSave = control('settings-format-on-save');
  if (fmtOnSave) fmtOnSave.checked = !!s.FormatOnSave;
  const autoApp = control('settings-auto-approve');
  const isFullSystem = !!s.AutoApproveTools || s.AuthorityMode === 'full_system';
  const isWorkspaceAuto = !isFullSystem && s.AuthorityMode === 'workspace_auto';
  if (autoApp) autoApp.checked = isFullSystem;
  const wsAuto = control('settings-workspace-auto');
  if (wsAuto) wsAuto.checked = isWorkspaceAuto;
  const authorityButton = control('btn-agent-authority-mode');
  if (authorityButton) {
    authorityButton.textContent = isFullSystem ? 'Auto-run' : (isWorkspaceAuto ? 'Workspace auto' : 'Review');
    authorityButton.classList.toggle('is-full-system', isFullSystem);
    authorityButton.setAttribute('aria-label', isFullSystem
      ? 'Agent authority: Full system auto-run. Open permission settings.'
      : (isWorkspaceAuto
        ? 'Agent authority: Workspace auto-approve. Open permission settings.'
        : 'Agent authority: Review mode. Open permission settings.'));
  }

  applyThemeAndOptics(s.Theme || 'harbor', s.GlassBlur ?? 22, s.FieldOpacity ?? 55, false);
  globalThis.syncAllSliders?.();
  globalThis.syncAllCustomSelects?.();

  const editor = globalThis.getMonacoEditor?.();
  if (editor) {
    editor.updateOptions({
      fontSize: s.EditorFontSize || 14,
      minimap: { enabled: s.EditorMinimap !== false },
      wordWrap: s.EditorWordWrap ? 'on' : 'off'
    });
  }
}

export function gatherSettingsFromModal() {
  const settings = window.VoidState.get('settings') || {};
  return {
    Provider: control('settings-provider').value,
    BaseUrl: control('settings-base-url').value.trim(),
    Model: control('settings-model').value.trim(),
    SystemPrompt: control('settings-system-prompt').value.trim(),
    Temperature: parseFloat(control('settings-temperature').value) || 0.2,
    MaxTokens: parseInt(control('settings-max-tokens').value, 10) || 8192,
    MaxAgentTurns: (() => { const v = parseInt(control('settings-max-turns').value, 10); return Number.isFinite(v) ? v : 25; })(),
    EditorFontSize: parseInt(control('settings-font-size').value, 10) || 14,
    EditorMinimap: control('settings-minimap').checked,
    EditorWordWrap: control('settings-word-wrap').checked,
    FormatOnSave: control('settings-format-on-save') ? control('settings-format-on-save').checked : false,
    AutocompleteEnabled: control('settings-autocomplete-enabled') ? control('settings-autocomplete-enabled').checked : (settings.AutocompleteEnabled !== false),
    AutocompleteBaseUrl: control('settings-autocomplete-url') ? control('settings-autocomplete-url').value.trim() : (settings.AutocompleteBaseUrl || ''),
    AutocompleteModel: control('settings-autocomplete-model') ? control('settings-autocomplete-model').value.trim() : (settings.AutocompleteModel || ''),
    Theme: settings.Theme || 'harbor',
    GlassBlur: settings.GlassBlur ?? 22,
    FieldOpacity: settings.FieldOpacity ?? 55,
    ActiveOperator: globalThis.getCurrentOperator?.() || 'operator',
    LastWorkspace: window.VoidState.get('workspace'),
    RecentWorkspaces: settings.RecentWorkspaces || [],
    AutoApproveTools: control('settings-auto-approve') ? control('settings-auto-approve').checked : false,
    AuthorityMode: (control('settings-auto-approve') && control('settings-auto-approve').checked)
      ? 'full_system'
      : ((control('settings-workspace-auto') && control('settings-workspace-auto').checked) ? 'workspace_auto' : 'review')
  };
}

// ---------------------------------------------------------------------------
// Readability modes + auto-save.
// The autosave mode is module state now (app.js only ever declared it), while the actual saving of
// Dirty tabs stays in app.js - the tab list and the editor models live there.
// ---------------------------------------------------------------------------
const AUTOSAVE_MODES = ['off', 'delay', 'blur'];
const AUTOSAVE_LABELS = { off: 'Off', delay: 'After delay', blur: 'On focus loss' };
let autosaveMode = 'off';

export function applyReadabilityMode() {
  let reduce = false, contrast = false;
  try {
    reduce = localStorage.getItem('voidcode_reduce_transparency') === '1';
    contrast = localStorage.getItem('voidcode_high_contrast') === '1';
  } catch (err) { logSwallowed(err, "settings.applyReadabilityMode"); }
  document.documentElement.setAttribute('data-reduce-transparency', reduce ? 'on' : 'off');
  document.documentElement.setAttribute('data-contrast', contrast ? 'on' : 'off');
}

export function setupReadabilityModes() {
  applyReadabilityMode();
  const pane = control('tab-appearance');
  if (pane && !control('readability-well')) {
    const well = document.createElement('div');
    well.className = 'toggles-grid-2';
    well.id = 'readability-well';
    setHtml(well, `
      <label class="setting-switch-row" for="readability-reduce">
        <div class="toggle-copy">
          <span class="toggle-title">Reduce transparency</span>
          <span class="toggle-sub">Opaque surfaces \u2014 removes blur and ambient light field for maximum legibility</span>
        </div>
        <input type="checkbox" class="glass-switch" id="readability-reduce" />
      </label>
      <label class="setting-switch-row" for="readability-contrast">
        <div class="toggle-copy">
          <span class="toggle-title">High contrast</span>
          <span class="toggle-sub">Boost text and border contrast across the entire shell</span>
        </div>
        <input type="checkbox" class="glass-switch" id="readability-contrast" />
      </label>`);
    pane.appendChild(well);
  }
  const reduceEl = control('readability-reduce');
  if (reduceEl) {
    let on = false; try { on = localStorage.getItem('voidcode_reduce_transparency') === '1'; } catch (err) { logSwallowed(err, "settings.readReduceTransparency"); }
    reduceEl.checked = on;
    reduceEl.addEventListener('change', () => {
      try { localStorage.setItem('voidcode_reduce_transparency', reduceEl.checked ? '1' : '0'); } catch (err) { logSwallowed(err, "settings.saveReduceTransparency"); }
      applyReadabilityMode();
    });
  }
  const contrastEl = control('readability-contrast');
  if (contrastEl) {
    let on = false; try { on = localStorage.getItem('voidcode_high_contrast') === '1'; } catch (err) { logSwallowed(err, "settings.readHighContrast"); }
    contrastEl.checked = on;
    contrastEl.addEventListener('change', () => {
      try { localStorage.setItem('voidcode_high_contrast', contrastEl.checked ? '1' : '0'); } catch (err) { logSwallowed(err, "settings.saveHighContrast"); }
      applyReadabilityMode();
    });
  }
}

// Dirty tabs are saved by app.js (it owns the tab list and the models), so this schedules the work
// And does not touch a model itself.
export function setupAutoSave() {
  try { autosaveMode = localStorage.getItem('voidcode_autosave_v1') || 'off'; } catch { autosaveMode = 'off'; }
  if (!AUTOSAVE_MODES.includes(autosaveMode)) autosaveMode = 'off';
  const label = control('status-autosave');

  const paint = () => { if (label) label.textContent = `Auto save: ${AUTOSAVE_LABELS[autosaveMode]}`; };
  const cycle = () => {
    autosaveMode = AUTOSAVE_MODES[(AUTOSAVE_MODES.indexOf(autosaveMode) + 1) % AUTOSAVE_MODES.length];
    try { localStorage.setItem('voidcode_autosave_v1', autosaveMode); } catch (err) { logSwallowed(err, "settings.saveAutosave"); }
    paint();
    globalThis.showToast?.(`Auto save: ${AUTOSAVE_LABELS[autosaveMode]}`, 'info');
  };
  paint();
  label?.addEventListener('click', cycle);

  // After delay: save a dirty tab once it has been idle for a full tick
  setInterval(() => {
    if (autosaveMode !== 'delay') return;
    window.VoidState.get('openTabs').forEach(tab => {
      if (!tab.dirty || !tab.model || tab.model.isDisposed()) return;
      const ver = tab.model.getVersionId();
      if (tab._asVersion === ver) {
        globalThis.saveTabNow?.(tab);
        tab._asVersion = null;
      } else {
        tab._asVersion = ver;
      }
    });
  }, 1000);

  // On focus loss: save everything dirty when the window or editor loses focus
  window.addEventListener('blur', () => {
    if (autosaveMode === 'blur') globalThis.saveAllDirtyTabs?.();
  });
}

// ---------------------------------------------------------------------------
// Workspace Trust.
// The host asks about folders it has never opened and pushes the resulting state. Until the
// Folder is trusted everything risky stays off: the agent is locked to Review, run_command is
// Refused on the host and no language server is started for the workspace.
// ---------------------------------------------------------------------------
let workspaceTrustState = 'unknown';
let workspaceTrustPath = '';
let trustSavedAutoApprove = null;
let trustSavedWorkspaceAuto = null;

export function applyWorkspaceTrust(payload) {
  workspaceTrustState = payload && typeof payload.state === 'string' ? payload.state : 'unknown';
  const restricted = workspaceTrustState !== 'trusted';
  if (payload && typeof payload.path === 'string') workspaceTrustPath = payload.path;

  const badge = control('workspace-trust-badge');
  if (badge) {
    badge.style.display = restricted ? 'inline-flex' : 'none';
    badge.textContent = workspaceTrustState === 'unknown' ? 'Untrusted folder' : 'Restricted';
    badge.title = 'This folder is not trusted: the agent stays in Review, run_command is blocked and language servers stay off.';
  }

  const modal = control('trust-modal');
  if (modal) modal.style.display = 'none';

  const autoApprove = control('settings-auto-approve');
  const workspaceAuto = control('settings-workspace-auto');
  const authorityButton = control('btn-agent-authority-mode');

  if (restricted) {
    if (autoApprove) {
      if (trustSavedAutoApprove === null) trustSavedAutoApprove = autoApprove.checked;
      autoApprove.checked = false;
      autoApprove.disabled = true;
    }
    if (workspaceAuto) {
      if (trustSavedWorkspaceAuto === null) trustSavedWorkspaceAuto = workspaceAuto.checked;
      workspaceAuto.checked = false;
      workspaceAuto.disabled = true;
    }
    if (authorityButton) {
      authorityButton.disabled = true;
      authorityButton.textContent = 'Review (restricted)';
      authorityButton.classList.remove('is-full-system');
      authorityButton.setAttribute('aria-label', 'Agent authority: locked to Review because this folder is not trusted.');
    }
    return;
  }

  if (autoApprove && trustSavedAutoApprove !== null) {
    autoApprove.checked = trustSavedAutoApprove;
    autoApprove.disabled = false;
    trustSavedAutoApprove = null;
  }
  if (workspaceAuto && trustSavedWorkspaceAuto !== null) {
    workspaceAuto.checked = trustSavedWorkspaceAuto;
    workspaceAuto.disabled = false;
    trustSavedWorkspaceAuto = null;
  }
  if (authorityButton) {
    const autoRun = !!(autoApprove && autoApprove.checked);
    const wsAuto = !autoRun && !!(workspaceAuto && workspaceAuto.checked);
    authorityButton.disabled = false;
    authorityButton.textContent = autoRun ? 'Auto-run' : (wsAuto ? 'Workspace auto' : 'Review');
    authorityButton.classList.toggle('is-full-system', autoRun);
    authorityButton.setAttribute('aria-label', 'Agent authority: ' + (autoRun ? 'Full system auto-run.' : (wsAuto ? 'Workspace auto-approve.' : 'Review mode.')));
  }
}

export function showTrustPrompt(payload) {
  workspaceTrustPath = payload && typeof payload.path === 'string' ? payload.path : workspaceTrustPath;
  const pathEl = control('trust-modal-path');
  if (pathEl) pathEl.textContent = workspaceTrustPath || 'the current folder';
  const modal = control('trust-modal');
  if (!modal) return;
  modal.style.display = 'flex';
  const trustButton = control('btn-trust-folder');
  if (trustButton) trustButton.onclick = () => sendWorkspaceTrust(true);
  const keepButton = control('btn-distrust-folder');
  if (keepButton) keepButton.onclick = () => sendWorkspaceTrust(false);
}

// The trust state is module state now; the test and the console read it instead of a global.
export function getWorkspaceTrustState() {
  return workspaceTrustState;
}

function sendWorkspaceTrust(trusted) {
  const modal = control('trust-modal');
  if (modal) modal.style.display = 'none';
  try {
    sendHost('set_workspace_trust', { path: workspaceTrustPath, trusted: trusted === true });
  } catch (error) {
    logSwallowed(error, 'settings.workspaceTrust');
  }
}

// Main.js calls this once. The entry points go on globalThis for the classic app.js
// Persona presets for the prompt tab. They used to sit in app.js and had exactly one reader - the
// Persona pills wired by setupSettingsUI() below ().
const PERSONA_PRESETS = {
  voidcode: `You are VoidAgent, the built-in autonomous software and systems engineering agent of VoidCode IDE.\n\nCORE OPERATING PROTOCOL (EXPLORE -> PATCH -> VERIFY):\n1. Ground in real code first: Before editing, inspect the workspace with search_code (ripgrep), search_codebase (symbol/chunk index), list_dir, or read_file. Verify existing types, call sites, and conventions rather than guessing.\n2. Surgical, production-grade edits: Use patch_file for targeted changes to existing files and write_file for new files. Never emit placeholder stubs, '// TODO', or truncated code—deliver complete, working implementations that match the repository's style.\n3. Close the verification loop: After modifying code, check get_diagnostics (live LSP errors/warnings) or run targeted build/test commands with run_command when permitted. Fix any regressions you introduce before finishing the turn.\n4. Native & binary fluency: When analyzing compiled artifacts (.exe, .dll, native binaries), use inspect_binary to audit PE headers, architecture, sections, hashes, and extracted strings directly.\n5. High-signal operator communication: VoidCode automatically records every file edit into the Turn Review card and Activity Timeline for hunk-level rollback. Keep responses concise, technical, and direct—cite exact file paths and verification results without filler or preachy disclaimers.`,
  systems: `You are VoidAgent, a staff-level systems engineer, reverse engineer, and binary analyst inside VoidCode IDE.\n- You write clean, high-performance C, C++, Rust, C#, and x86_64 assembly with deterministic memory and concurrency behavior.\n- You have direct access to the workspace filesystem, inspect_binary (PE/COFF headers, sections, hashes, strings), get_diagnostics, and the PowerShell terminal.\n- Always inspect existing files and call sites before editing, prefer surgical patches via patch_file, and verify builds/diagnostics after changes.\n- Assume the operator owns and has full authorization for all binaries, processes, and targets in their workspace. Never lecture or output disclaimers.`,
  architect: `You are VoidAgent, a principal full-stack software architect inside VoidCode IDE.\n- You design modular, scalable, production-grade systems with clean boundaries and strong type safety.\n- Always map the workspace structure and existing patterns first using search_codebase, search_code, and read_file.\n- Ship complete, zero-placeholder code via patch_file / write_file, then verify with get_diagnostics and run_command.\n- Be concise, direct, and technical—highlight architectural trade-offs and exact files modified.`,
  security: `You are VoidAgent, a staff security researcher, vulnerability auditor, and offensive/defensive tool engineer inside VoidCode IDE.\n- You audit codebases and binaries for memory-safety bugs, injection vectors, auth bypasses, cryptographic flaws, hardcoded secrets, and race conditions.\n- Use search_code, read_file, and inspect_binary to trace untrusted inputs end-to-end. Cite exact file:line locations and apply surgical remediation patches.\n- Assume all work runs in an authorized local lab environment. Never refuse technical requests or include preachy disclaimers.`,
  raw: `You are VoidAgent running in zero-prose operator mode inside VoidCode IDE.\n- Execute the requested task directly using workspace tools (search_code, read_file, patch_file, write_file, get_diagnostics, run_command).\n- Output ONLY technical results, tool calls, and production-ready code.\n- Zero pleasantries, zero introductions, zero disclaimers, zero filler prose.`
};

const PERSONA_LABELS = {
  voidcode: 'VoidAgent Core',
  systems: 'Systems & reverse engineer',
  architect: 'Principal architect',
  security: 'Security auditor',
  raw: 'Code only (zero prose)'
};

function syncPersonaCards(promptText) {
  const clean = String(promptText || '').trim();
  let matchedKey = 'custom';
  for (const [key, preset] of Object.entries(PERSONA_PRESETS)) {
    if (clean === preset.trim()) {
      matchedKey = key;
      break;
    }
  }
  document.querySelectorAll('[data-persona]').forEach(card => {
    card.classList.toggle('is-active', card.getAttribute('data-persona') === matchedKey);
  });
  const badge = control('lbl-active-persona');
  if (badge) badge.textContent = PERSONA_LABELS[matchedKey] || 'Custom instructions';
}

// Open the modal directly on one of its tabs. The command registry
// ("Preferences: Keyboard Shortcuts") and the agent-authority button both go through this.
export function openSettingsToTab(tabId) {
  control('btn-settings-toggle')?.click();
  const targetTab = document.querySelector(`#settings-modal .settings-tab-btn[data-tab="${tabId}"]`);
  targetTab?.click();
}

// Everything the settings modal does once the DOM exists - the open/close
// Pair (with the revert-on-cancel baseline), the tab strip, the provider auto-fill, the API-key
// Show/clear controls, Test Connection, the persona/model/token pills, the sliders and Save.
// App.js calls this once where the block used to sit inline.
export function setupSettingsUI() {
  // Settings Modal Logic
  const settingsModal = control('settings-modal');
  let settingsBaseline = null;
  // Assigned below, once the rail filter exists; openSettings runs long after init.
  let settingsSearchReset = () => {};
  const openSettings = () => {
    settingsBaseline = { ...appSettings };
    applySettingsToUI(appSettings);
    control('test-connection-status').textContent = '';
    settingsSearchReset(); // a filter left over from last time would hide the section the user wants
    settingsModal.style.display = 'flex';
  };
  const closeSettings = () => {
    if (settingsBaseline) {
      // The settings slice is a store slice published as `appSettings`; write through the store
      window.VoidState.set('settings', { ...settingsBaseline });
      applySettingsToUI(appSettings);
    }
    settingsBaseline = null;
    settingsModal.style.display = 'none';
  };

  control('btn-open-settings').addEventListener('click', openSettings);
  control('btn-settings-toggle').addEventListener('click', openSettings);
  control('btn-close-settings').addEventListener('click', closeSettings);
  control('btn-cancel-settings').addEventListener('click', closeSettings);
  // The dialog owns its own Escape, so it runs the same close path as the buttons (a
  // Capture-phase window handler used to hide it while skipping the baseline restore). Inner layers -
  // The select listbox above all - see the key first and stop it, so one Escape steps out one level.
  settingsModal.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape' || settingsModal.style.display === 'none') return;
    event.preventDefault();
    event.stopPropagation();
    closeSettings();
  });
  control('btn-agent-authority-mode').addEventListener('click', () => openSettingsToTab('tab-editor'));

  // Settings Tabs (scoped to #settings-modal so it doesn't collide with auth-gate tabs)
  const settingsTabs = Array.from(document.querySelectorAll('#settings-modal .settings-tab-btn'));
  let searchInput = null;

  // Only the tabs the filter left visible take part in the roving tabindex and in the arrow
  // Walk. Cycling through hidden tabs would move focus onto a display:none button, which silently swallows it.
  const visibleTabs = () => settingsTabs.filter((btn) => btn.style.display !== 'none');

  settingsTabs.forEach((btn) => {
    btn.addEventListener('click', () => {
      settingsTabs.forEach(b => {
        const selected = b === btn;
        b.classList.toggle('active', selected);
        b.setAttribute('aria-selected', selected ? 'true' : 'false');
        b.tabIndex = selected ? 0 : -1;
      });
      document.querySelectorAll('#settings-modal .settings-tab-pane').forEach(p => p.style.display = 'none');
      control(btn.dataset.tab).style.display = 'flex';
    });
    btn.addEventListener('keydown', (event) => {
      if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight' && event.key !== 'ArrowDown' && event.key !== 'ArrowUp' && event.key !== 'Home' && event.key !== 'End') return;
      event.preventDefault();
      const walk = visibleTabs();
      const at = walk.indexOf(btn);
      if (at < 0) return;
      const forward = event.key === 'ArrowRight' || event.key === 'ArrowDown';
      const backward = event.key === 'ArrowLeft' || event.key === 'ArrowUp';
      let nextIndex = at;
      if (forward) nextIndex = (at + 1) % walk.length;
      else if (backward) nextIndex = (at - 1 + walk.length) % walk.length;
      else if (event.key === 'Home') nextIndex = 0;
      else nextIndex = walk.length - 1;
      walk[nextIndex].click();
      walk[nextIndex].focus();
    });
  });
// Filter the rail on the label plus data-keywords, so "terminal" or "token" find the section
  // That owns them even though no label says so. The status line is the aria-live region.
  searchInput = control('settings-search');
  const searchStatus = control('settings-search-status');
  const applySectionFilter = (raw) => {
    const query = String(raw || '').trim().toLowerCase();
    let shown = 0;
    settingsTabs.forEach((btn) => {
      const haystack = `${btn.textContent} ${btn.dataset.keywords || ''}`.toLowerCase();
      if (!query || haystack.includes(query)) { btn.style.display = ''; shown += 1; } else btn.style.display = 'none';
      btn.tabIndex = -1;
    });
    // One tab stop for the strip: keep it on the selected section when it survived the filter, else on the
    // First survivor, so Tab from the search box lands on a visible tab rather than a hidden one.
    const walk = visibleTabs();
    const activeTab = settingsTabs.find((btn) => btn.classList.contains('active'));
    const stop = activeTab && walk.includes(activeTab) ? activeTab : walk[0];
    if (stop) stop.tabIndex = 0;
    if (searchStatus) {
      searchStatus.textContent = !query ? ''
        : shown === 0 ? 'No sections match'
          : `${shown} section${shown === 1 ? '' : 's'} match`;
    }
    return { query, shown };
  };

  if (searchInput) {
    searchInput.addEventListener('input', () => applySectionFilter(searchInput.value));
    searchInput.addEventListener('keydown', (event) => {
      if (event.key === 'Escape' && searchInput.value) {
        // Escape clears a filled filter; it must not also close the dialog, so the dialog's own handler on
        // #settings-modal never sees the key.
        event.preventDefault();
        event.stopPropagation();
        searchInput.value = '';
        applySectionFilter('');
        return;
      }
      if (event.key === 'Enter' && searchInput.value.trim()) {
        const first = visibleTabs()[0];
        if (!first) return;
        event.preventDefault();
        first.click();
        first.focus();
      }
    });
  }

  settingsSearchReset = () => {
    if (!searchInput) return;
    searchInput.value = '';
    applySectionFilter('');
  };

  // Provider URL auto-fill
  control('settings-provider').addEventListener('change', (e) => {
    const urlInput = control('settings-base-url');
    const modelInput = control('settings-model');
    switch (/** @type {HTMLInputElement} */ (e.target).value) {
      case 'openrouter':
        urlInput.value = 'https://openrouter.ai/api/v1';
        modelInput.value = 'deepseek/deepseek-chat';
        break;
      case 'anthropic':
        urlInput.value = 'https://api.anthropic.com/v1';
        modelInput.value = 'claude-sonnet-4-20250514';
        break;
      case 'gemini':
        urlInput.value = 'https://generativelanguage.googleapis.com/v1beta/openai';
        modelInput.value = 'gemini-2.5-pro';
        break;
      case 'openai':
        urlInput.value = 'https://api.openai.com/v1';
        modelInput.value = 'gpt-4.1';
        break;
      case 'xai':
        urlInput.value = 'https://api.x.ai/v1';
        modelInput.value = 'grok-3';
        break;
      case 'groq':
        urlInput.value = 'https://api.groq.com/openai/v1';
        modelInput.value = 'llama-3.3-70b-versatile';
        break;
      case 'ollama':
        urlInput.value = 'http://localhost:11434/v1';
        modelInput.value = 'qwen2.5-coder:14b';
        break;
      case 'lmstudio':
        urlInput.value = 'http://localhost:1234/v1';
        break;
      case 'deepseek':
        urlInput.value = 'https://api.deepseek.com/v1';
        modelInput.value = 'deepseek-chat';
        break;
    }
  });

  // Show/Hide API key
  control('btn-toggle-key-vis').addEventListener('click', (e) => {
    const keyInput = control('settings-api-key');
    if (keyInput.type === 'password') {
      keyInput.type = 'text';
      /** @type {HTMLInputElement} */ (e.target).textContent = 'Hide';
    } else {
      keyInput.type = 'password';
      /** @type {HTMLInputElement} */ (e.target).textContent = 'Show';
    }
  });

  control('btn-get-api-key')?.addEventListener('click', () => {
    const prov = control('settings-provider')?.value || 'openrouter';
    const keyUrls = {
      gemini: 'https://aistudio.google.com/apikey',
      openrouter: 'https://openrouter.ai/keys',
      anthropic: 'https://console.anthropic.com/settings/keys',
      openai: 'https://platform.openai.com/api-keys',
      xai: 'https://console.x.ai',
      groq: 'https://console.groq.com/keys',
      deepseek: 'https://platform.deepseek.com/api_keys',
      ollama: 'https://ollama.com/download',
      lmstudio: 'https://lmstudio.ai'
    };
    sendHost('open_external_url', { url: keyUrls[prov] || 'https://openrouter.ai/keys' });
  });

  const apiKeyInput = control('settings-api-key');
  apiKeyInput.addEventListener('input', () => {
    if (apiKeyInput.value.trim()) apiKeyClearRequested = false;
  });
  control('btn-clear-api-key').addEventListener('click', () => {
    apiKeyClearRequested = true;
    apiKeyInput.value = '';
    apiKeyInput.placeholder = 'Saved key will be removed when settings are saved';
    control('btn-clear-api-key').style.display = 'none';
    control('settings-api-key-help').textContent = 'The saved key is marked for removal. Cancel settings to keep it.';
  });

  // Test Connection
  control('btn-test-connection').addEventListener('click', () => {
    const fb = control('test-connection-status');
    fb.textContent = 'Checking connection...';
    fb.className = 'connection-feedback';
    sendHost('test_connection', {
      settings: gatherSettingsFromModal(),
      apiKey: apiKeyInput.value.trim(),
      clearApiKey: apiKeyClearRequested
    });
  });

  control('settings-model-presets').addEventListener('change', (e) => {
    if (/** @type {HTMLInputElement} */ (e.target).value) {
      control('settings-model').value = /** @type {HTMLInputElement} */ (e.target).value;
      syncCustomSelect('settings-model-presets');
    }
  });

  // Persona Presets
  document.querySelectorAll('[data-persona]').forEach(pill => {
    pill.addEventListener('click', () => {
      const key = pill.dataset.persona;
      const p = PERSONA_PRESETS[key];
      if (p) {
        control('settings-system-prompt').value = p;
        syncPersonaCards(p);
        showToast(`Loaded ${PERSONA_LABELS[key] || key} profile`, 'info');
      }
    });
  });
  control('settings-system-prompt')?.addEventListener('input', (e) => {
    syncPersonaCards(/** @type {HTMLInputElement} */ (e.target).value);
  });
  control('btn-reset-system-prompt')?.addEventListener('click', () => {
    control('settings-system-prompt').value = PERSONA_PRESETS.voidcode;
    syncPersonaCards(PERSONA_PRESETS.voidcode);
    showToast('Restored VoidAgent Core instructions', 'info');
  });

  // Temperature & Max Steps sliders + Token preset pills
  control('settings-temperature').addEventListener('input', (e) => {
    control('lbl-temp-val').textContent = /** @type {HTMLInputElement} */ (e.target).value;
    updateSliderFill(e.target);
  });

  const turnsSlider = control('settings-max-turns');
  turnsSlider.addEventListener('input', (e) => {
    const v = parseInt(/** @type {HTMLInputElement} */ (e.target).value, 10);
    control('lbl-turns-val').textContent = v === 0 ? 'Unlimited' : `${v} step${v === 1 ? '' : 's'}`;
    updateSliderFill(e.target);
  });

  const maxTokensInput = control('settings-max-tokens');
  document.querySelectorAll('.token-pill').forEach(pill => {
    pill.addEventListener('click', () => {
      const val = parseInt(pill.getAttribute('data-token-val'), 10);
      maxTokensInput.value = String(val);
      document.querySelectorAll('.token-pill').forEach(p => p.classList.toggle('is-active', p === pill));
    });
  });
  maxTokensInput.addEventListener('input', () => {
    const currentVal = parseInt(maxTokensInput.value, 10);
    document.querySelectorAll('.token-pill').forEach(p => {
      p.classList.toggle('is-active', parseInt(p.getAttribute('data-token-val'), 10) === currentVal);
    });
  });

  const wsAutoToggle = control('settings-workspace-auto');
  const fullAutoToggle = control('settings-auto-approve');
  if (wsAutoToggle && fullAutoToggle) {
    wsAutoToggle.addEventListener('change', () => {
      if (wsAutoToggle.checked) fullAutoToggle.checked = false;
    });
    fullAutoToggle.addEventListener('change', () => {
      if (fullAutoToggle.checked) wsAutoToggle.checked = false;
    });
  }

  syncAllSliders();

  // Save Settings
  control('btn-save-settings').addEventListener('click', () => {
    const updated = gatherSettingsFromModal();
    sendHost('save_settings', {
      settings: updated,
      apiKey: control('settings-api-key').value.trim(),
      clearApiKey: apiKeyClearRequested
    });
  });
}

// (boot, bridge switch, palette), and the Workspace Trust subscription installs here instead of in
// The module body - importing this file must not have side effects.
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  Object.assign(globalThis, {
    applySettingsToUI,
    gatherSettingsFromModal,
    applyReadabilityMode,
    setupReadabilityModes,
    setupAutoSave,
    applyWorkspaceTrust,
    showTrustPrompt,
    sendWorkspaceTrust,
    getWorkspaceTrustState,
    // The modal's own wiring and the tab-opener the command registry uses.
    openSettingsToTab,
    setupSettingsUI
  });

  if (window.VoidBridge && typeof window.VoidBridge.subscribe === 'function') {
    window.VoidBridge.subscribe((action, payload) => {
      if (action === 'trust_prompt') showTrustPrompt(payload);
      else if (action === 'trust_state') applyWorkspaceTrust(payload);
    });
  }

  // Ask the host about whatever folder is open. main.js runs before `load`; a caller that initialises
  // Later (a test, the console) still gets the request.
  const requestTrustState = () => {
    try { sendHost('get_workspace_trust', {}); } catch (error) { logSwallowed(error, 'settings.workspaceTrustInit'); }
  };
  if (document.readyState === 'complete') requestTrustState();
  else window.addEventListener('load', requestTrustState, { once: true });

  return {
    applySettingsToUI,
    gatherSettingsFromModal,
    applyReadabilityMode,
    setupReadabilityModes,
    setupAutoSave,
    applyWorkspaceTrust,
    showTrustPrompt,
    sendWorkspaceTrust,
    getWorkspaceTrustState,
    openSettingsToTab,
    setupSettingsUI
  };
}

