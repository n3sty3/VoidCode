// Status bar: the slot painters and the one slot that owns real state (the language-server
// Indicator).
//
// . The status bar is shared furniture, and the audit before moving it found it
// Has **no state of its own** - the workspace name, checkpoints, git branch, autosave mode, cursor
// Position and console status are all written by the feature that owns them (app.js, modules/git.js,
// Modules/settings.js, modules/terminal.js, modules/editor-tabs.js). What is genuinely the status
// Bar's is: the "a slot stays silent when it has nothing true to say" rule (setStatusSlot), the
// Activity line (setActivityStatus), and the language-server slot with its last-reported status and
// The once-per-language "missing/failed" notification (updateLspStatus).
//
// Moving the LSP slot is what makes this worth a module rather than a re-export: `lastLspStatus` and
// `notifiedLsp` were script-scope state in app.js that only this slot ever needed, so they came along
// Instead of growing another accessor pair. modules/editor-tabs.js now imports setStatusSlot and
// UpdateLspStatus instead of reaching through the globals.
import { html, raw, setHtml } from '../core/markdown.js';

let lastLspStatus = null;
const notifiedLsp = Object.create(null);
let detectedRuntimes = [];
let activeRuntimeIndex = 0;

export function updateRuntimeStatus(report) {
  if (report && Array.isArray(report.runtimes)) {
    detectedRuntimes = report.runtimes.filter(r => r && r.found && r.name !== 'npm');
  }
  const el = document.getElementById('status-runtime');
  if (!el) return;
  if (detectedRuntimes.length === 0) {
    el.style.display = 'none';
    return;
  }
  activeRuntimeIndex = Math.min(activeRuntimeIndex, detectedRuntimes.length - 1);
  const current = detectedRuntimes[activeRuntimeIndex];
  window.VoidState?.set?.('selectedRuntime', current);
  el.textContent = current.name;
  el.title = `${current.name}: ${current.path || 'PATH'} (click to switch runtime / interpreter)`;
  el.style.display = '';
}

export function cycleActiveRuntime() {
  if (detectedRuntimes.length === 0) {
    globalThis.refreshCapabilities?.();
    globalThis.openHealthModal?.();
    return;
  }
  activeRuntimeIndex = (activeRuntimeIndex + 1) % detectedRuntimes.length;
  updateRuntimeStatus();
  const current = detectedRuntimes[activeRuntimeIndex];
  if (current) {
    showToast(`Active runtime: ${current.name} (${current.path || 'PATH'})`, 'info');
  }
}

// A status slot only speaks when it has something true to say: "Encoding:
// Unknown, EOL: Unknown, LSP: Idle" across the bar is noise, not information.
export function setStatusSlot(id, text) {
  const el = document.getElementById(id);
  if (!el) return;
  const value = (text || '').trim();
  const unknown = !value || /unknown|idle/i.test(value);
  el.textContent = unknown ? '' : value;
  el.style.display = unknown ? 'none' : '';
}

// Eight call sites used this helper but it was never defined, so every one of them
// (save, smart run, agent status, reload, Ready) threw ReferenceError right after doing its work.
// The text is surfaced where the status bar can show it without inventing a new visual element.
export function setActivityStatus(text) {
  const value = (text || '').trim();
  const statusbar = document.getElementById('statusbar');
  if (statusbar) statusbar.title = value;
  const slot = document.getElementById('status-activity');
  if (slot) {
    slot.textContent = value;
    slot.style.display = value ? '' : 'none';
  }
}

export function updateLspStatus(payload) {
  if (!payload) return;
  lastLspStatus = payload;
  const element = document.getElementById('status-lsp');
  if (!element) return;
  const language = payload.language || 'Language';
  const labels = { starting: 'Starting', ready: 'Ready', missing: 'Missing', failed: 'Failed', builtin: 'Built-in' };
  const label = `${language}: ${labels[payload.status] || payload.status || 'Idle'}`;
  const needsAttention = payload.status === 'missing' || payload.status === 'failed';

  element.style.display = '';
  element.className = (payload.status ? `is-${payload.status}` : '') + (needsAttention ? ' is-attention' : '');
  element.title = payload.detail || `${language} language server: ${payload.status || 'idle'}`;

  // A missing or failed server is a problem, not a metadata fact: give it a
  // Glyph (not colour alone) and surface it once as a real notification
  setHtml(element, html`${needsAttention
    ? raw('<span class="icon" data-icon="alert-triangle" data-variant="ui" style="--icon-size: 12px;"></span>')
    : ''}<span>${label}</span>`);
  if (window.VoidIcons) { try { window.VoidIcons.apply(element); } catch (e) { logSwallowed(e, "app.applyLspStatusIcon"); } }

  if (needsAttention && notifiedLsp[language] !== payload.status) {
    notifiedLsp[language] = payload.status;
    const detail = payload.detail ? ' ' + payload.detail : ' Features that need it will stay limited until it is available.';
    showToast(`${language} language server ${payload.status}.${detail}`, 'error');
  }

  if (!needsAttention) delete notifiedLsp[language];
}

// App.js calls this once while wiring the DOM. It used to be five inline
// SetStatusSlot() calls plus the LSP slot's click handler - the slot starts silent, and clicking the
// Indicator explains what the last reported status means.
export function setupStatusBar() {
  setStatusSlot('status-eol', '');
  setStatusSlot('status-encoding', '');
  setStatusSlot('status-cursor', '');
  setStatusSlot('status-lsp', '');
  setStatusSlot('status-lang', '');
  document.getElementById('status-runtime')?.addEventListener('click', cycleActiveRuntime);

  const cursorSlot = document.getElementById('status-cursor');
  if (cursorSlot) {
    cursorSlot.style.cursor = 'pointer';
    cursorSlot.title = 'Click to Go to Line (Ctrl+G)';
    cursorSlot.addEventListener('click', () => globalThis.goToLine?.());
  }

  const eolSlot = document.getElementById('status-eol');
  if (eolSlot) {
    eolSlot.style.cursor = 'pointer';
    eolSlot.title = 'Click to toggle line endings (LF / CRLF)';
    eolSlot.addEventListener('click', () => {
      const tab = window.VoidState?.get?.('activeTab');
      const m = globalThis.monaco;
      if (!tab || !tab.model || tab.model.isDisposed() || !m) return;
      const isCrlf = /crlf/i.test(eolSlot.textContent || '');
      const nextSeq = isCrlf ? m.editor.EndOfLineSequence.LF : m.editor.EndOfLineSequence.CRLF;
      tab.model.setEOL(nextSeq);
      tab.dirty = true;
      globalThis.renderTabs?.();
      const nextLabel = isCrlf ? 'LF' : 'CRLF';
      setStatusSlot('status-eol', nextLabel);
      showToast(`Line endings set to ${nextLabel}`, 'info');
    });
  }

  document.getElementById('status-lsp')?.addEventListener('click', () => {
    // After the one-time toast is gone, this slot is the only place a missing server is still
    // Visible, so clicking it goes to the section that owns the fix instead of repeating the detail back.
    if (lastLspStatus && (lastLspStatus.status === 'missing' || lastLspStatus.status === 'failed')
      && typeof globalThis.openSettingsToTab === 'function') {
      globalThis.openSettingsToTab('tab-editor');
      return;
    }
    if (lastLspStatus?.detail) showToast(lastLspStatus.detail, lastLspStatus.status === 'ready' ? 'success' : 'info');
    else showToast('Open a supported source file to start its language server.', 'info');
  });
}

// Main.js calls this once, before the classic scripts load. app.js still paints
// The workspace/checkpoint slots and answers the lsp_status host action, so the painters stay
// Published; modules/editor-tabs.js imports them directly.
export function init() {
  Object.assign(globalThis, {
    setStatusSlot,
    setActivityStatus,
    updateLspStatus,
    updateRuntimeStatus,
    cycleActiveRuntime,
    setupStatusBar
  });

  return {
    setStatusSlot,
    setActivityStatus,
    updateLspStatus,
    updateRuntimeStatus,
    cycleActiveRuntime,
    setupStatusBar
  };
}