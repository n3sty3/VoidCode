// Zen Mode (Ctrl+K Z), Live Accent Color Swatches/Picker in Settings -> Appearance,
// and Searchable Keyboard Shortcuts reference inside Settings.
import { html, setHtml } from '../core/markdown.js';

const ACCENT_STORAGE_KEY = 'voidcode_custom_accent_v1';
const ACCENT_PRESETS = [
  { id: 'default', label: 'Theme Default', hex: '' },
  { id: 'glacier', label: 'Glacier Cyan', hex: '#60d8d8' },
  { id: 'violet', label: 'Electric Violet', hex: '#a78bfa' },
  { id: 'emerald', label: 'Emerald Mint', hex: '#34d399' },
  { id: 'amber', label: 'Amber Gold', hex: '#f5b963' },
  { id: 'coral', label: 'Rose Coral', hex: '#fb7185' }
];

let zenActive = false;
let preZenState = null;
let chordPending = false;
let chordTimer = null;
let lastEscTs = 0;

function hexToRgba(hex, alpha) {
  const clean = String(hex || '').replace('#', '');
  if (!/^[0-9a-fA-F]{6}$/.test(clean)) return '';
  const r = parseInt(clean.slice(0, 2), 16);
  const g = parseInt(clean.slice(2, 4), 16);
  const b = parseInt(clean.slice(4, 6), 16);
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

export function applyCustomAccent(hex) {
  const root = document.documentElement;
  const clean = String(hex || '').trim();
  if (!/^#[0-9a-fA-F]{6}$/.test(clean)) {
    root.style.removeProperty('--glacier');
    root.style.removeProperty('--glacier-wash');
    root.style.removeProperty('--glacier-line');
    try { localStorage.removeItem(ACCENT_STORAGE_KEY); } catch (err) { globalThis.logSwallowed?.(err, 'zen-shortcuts.clearAccent'); }
  } else {
    root.style.setProperty('--glacier', clean);
    root.style.setProperty('--glacier-wash', hexToRgba(clean, 0.14));
    root.style.setProperty('--glacier-line', hexToRgba(clean, 0.42));
    try { localStorage.setItem(ACCENT_STORAGE_KEY, clean); } catch (err) { globalThis.logSwallowed?.(err, 'zen-shortcuts.saveAccent'); }
  }
  syncAccentSwatches(clean);
}

function syncAccentSwatches(activeHex) {
  document.querySelectorAll('[data-accent-hex]').forEach((btn) => {
    const h = btn.getAttribute('data-accent-hex') || '';
    const on = h.toLowerCase() === String(activeHex || '').toLowerCase();
    btn.setAttribute('aria-pressed', on ? 'true' : 'false');
    /** @type {HTMLElement} */ (btn).style.outline = on ? '2px solid var(--text)' : 'none';
  });
}

export function toggleZenMode() {
  const explorer = document.getElementById('sidebar-explorer');
  const agent = document.getElementById('sidebar-agent');
  const bottom = document.getElementById('bottom-panel');

  if (!zenActive) {
    preZenState = {
      explorer: explorer && !explorer.classList.contains('collapsed') && getComputedStyle(explorer).display !== 'none',
      agent: agent && !agent.classList.contains('collapsed') && getComputedStyle(agent).display !== 'none',
      bottom: bottom && !bottom.classList.contains('collapsed') && getComputedStyle(bottom).display !== 'none'
    };
    zenActive = true;
    globalThis.toggleWorkspacePane?.('explorer', false);
    globalThis.toggleWorkspacePane?.('agent', false);
    globalThis.toggleWorkspacePane?.('bottom', false);
    document.body.classList.add('is-zen-mode');
    globalThis.showToast?.('Zen Mode enabled (press Esc twice or Ctrl+K Z to exit)', 'info');
  } else {
    zenActive = false;
    document.body.classList.remove('is-zen-mode');
    if (preZenState) {
      if (preZenState.explorer) globalThis.toggleWorkspacePane?.('explorer', true);
      if (preZenState.agent) globalThis.toggleWorkspacePane?.('agent', true);
      if (preZenState.bottom) globalThis.toggleWorkspacePane?.('bottom', true);
    }
    globalThis.showToast?.('Exited Zen Mode', 'info');
  }
}

function mountAccentPickerInAppearance() {
  const pane = document.getElementById('tab-appearance');
  if (!pane || document.getElementById('accent-picker-well')) return;

  const saved = (() => {
    try { return localStorage.getItem(ACCENT_STORAGE_KEY) || ''; }
    catch { /* ignore storage restrictions */ return ''; }
  })();

  const well = document.createElement('div');
  well.id = 'accent-picker-well';
  well.className = 'control-well';
  well.style.marginTop = '10px';
  setHtml(well, html`
    <div class="label-row">
      <label>Accent color</label>
      <span class="val-badge">Live CSS tokens</span>
    </div>
    <div id="accent-swatches-row" style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin-top:6px;"></div>
    <small style="margin-top:6px;">Rethemes active borders, pills, focus rings, and highlights instantly.</small>
  `);

  const row = well.querySelector('#accent-swatches-row');
  ACCENT_PRESETS.forEach((p) => {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn--quiet btn--xs';
    btn.setAttribute('data-accent-hex', p.hex);
    btn.style.cssText = 'display:inline-flex;align-items:center;gap:6px;min-height:28px;';
    const dotColor = p.hex || 'var(--glacier)';
    setHtml(btn, html`
      <span style="width:10px;height:10px;border-radius:999px;background:${dotColor};display:inline-block;border:1px solid rgba(255,255,255,0.25);"></span>
      <span>${p.label}</span>
    `);
    btn.addEventListener('click', () => applyCustomAccent(p.hex));
    row?.appendChild(btn);
  });

  const colorInput = document.createElement('input');
  colorInput.type = 'color';
  colorInput.ariaLabel = 'Custom accent color';
  colorInput.value = /^#[0-9a-fA-F]{6}$/.test(saved) ? saved : '#60d8d8';
  colorInput.style.cssText = 'width:32px;height:28px;border-radius:6px;border:1px solid var(--seam);background:transparent;cursor:pointer;padding:1px;';
  colorInput.addEventListener('input', () => applyCustomAccent(colorInput.value));
  row?.appendChild(colorInput);

  pane.appendChild(well);
  syncAccentSwatches(saved);
}

const SHORTCUT_REFERENCE = [
  { group: 'Navigation', label: 'Quick Open File', keys: 'Ctrl+P' },
  { group: 'Navigation', label: 'Command Palette', keys: 'Ctrl+Shift+P' },
  { group: 'Navigation', label: 'Go to Symbol in File', keys: 'Ctrl+Shift+O' },
  { group: 'Navigation', label: 'Go to Symbol in Workspace (SQLite)', keys: 'Ctrl+Alt+T' },
  { group: 'Navigation', label: 'Search Indexed Code & Symbols', keys: 'Ctrl+T' },
  { group: 'AI & Agent', label: 'AI Inline Assist', keys: 'Ctrl+K' },
  { group: 'AI & Agent', label: 'Quick Switch Model / Fallback', keys: 'Ctrl+Alt+M' },
  { group: 'AI & Agent', label: 'Accept / Revert AI File Edit', keys: 'Alt+Y / Alt+N' },
  { group: 'AI & Agent', label: 'Mission Control (Parallel Worktrees)', keys: 'Ctrl+Shift+B' },
  { group: 'Tools & View', label: 'Toggle Zen Mode', keys: 'Ctrl+K Z' },
  { group: 'Tools & View', label: 'Built-In HTTP / API Client', keys: 'Ctrl+Shift+R' },
  { group: 'Tools & View', label: 'Binary & x86-64 Disassembler', keys: 'Ctrl+Shift+X' },
  { group: 'Tools & View', label: 'Open New VoidCode Window', keys: 'Ctrl+Shift+N' }
];

function mountShortcutsReferenceInSettings() {
  const pane = document.getElementById('tab-editor');
  if (!pane || document.getElementById('shortcuts-reference-well')) return;

  const well = document.createElement('div');
  well.id = 'shortcuts-reference-well';
  well.className = 'control-well';
  well.style.marginTop = '10px';
  setHtml(well, html`
    <div class="label-row">
      <label for="settings-shortcuts-search">Keyboard shortcuts</label>
      <span class="val-badge">Quick reference</span>
    </div>
    <input id="settings-shortcuts-search" type="text" placeholder="Search shortcuts (symbol, model, zen, diff...)" autocomplete="off" spellcheck="false" style="width:100%;height:30px;padding:0 9px;border-radius:8px;border:1px solid var(--seam);background:rgba(0,0,0,0.22);color:var(--text);font-size:12px;margin:6px 0;" />
    <div id="settings-shortcuts-list" style="display:flex;flex-direction:column;gap:4px;max-height:210px;overflow-y:auto;"></div>
  `);

  pane.appendChild(well);

  const listEl = well.querySelector('#settings-shortcuts-list');
  const searchEl = /** @type {HTMLInputElement | null} */ (well.querySelector('#settings-shortcuts-search'));

  const renderShortcuts = (q = '') => {
    if (!listEl) return;
    const clean = q.trim().toLowerCase();
    const items = SHORTCUT_REFERENCE.filter((s) => !clean || `${s.label} ${s.keys} ${s.group}`.toLowerCase().includes(clean));
    setHtml(listEl, '');
    items.forEach((item) => {
      const row = document.createElement('div');
      row.style.cssText = 'display:flex;align-items:center;justify-content:space-between;gap:8px;padding:5px 8px;border-radius:7px;background:rgba(255,255,255,0.025);font-size:12px;';
      setHtml(row, html`
        <span><span style="opacity:0.55;margin-right:6px;font-size:10.5px;">${item.group}</span>${item.label}</span>
        <kbd style="font-family:var(--font-code);font-size:11px;padding:2px 7px;border-radius:5px;background:rgba(0,0,0,0.35);border:1px solid var(--seam);color:var(--glacier);">${item.keys}</kbd>
      `);
      listEl.appendChild(row);
    });
  };

  searchEl?.addEventListener('input', () => renderShortcuts(searchEl.value));
  renderShortcuts('');
}

export function init() {
  try {
    const saved = localStorage.getItem(ACCENT_STORAGE_KEY);
    if (saved) applyCustomAccent(saved);
  } catch (err) {
    globalThis.logSwallowed?.(err, 'zen-shortcuts.loadAccent');
  }

  const bootstrap = () => {
    mountAccentPickerInAppearance();
    mountShortcutsReferenceInSettings();
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', bootstrap, { once: true });
  else bootstrap();

  window.addEventListener('keydown', (e) => {
    if (zenActive && e.key === 'Escape') {
      const now = Date.now();
      if (now - lastEscTs < 500) {
        toggleZenMode();
        lastEscTs = 0;
      } else {
        lastEscTs = now;
      }
      return;
    }

    if (chordPending) {
      chordPending = false;
      if (chordTimer) clearTimeout(chordTimer);
      if (e.key.toLowerCase() === 'z' && !e.ctrlKey && !e.metaKey) {
        e.preventDefault();
        toggleZenMode();
      }
      return;
    }

    if ((e.ctrlKey || e.metaKey) && e.altKey && e.key.toLowerCase() === 'z') {
      e.preventDefault();
      toggleZenMode();
    } else if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key.toLowerCase() === 'k') {
      chordPending = true;
      if (chordTimer) clearTimeout(chordTimer);
      chordTimer = setTimeout(() => { chordPending = false; }, 900);
    }
  });

  const api = { toggleZenMode, applyCustomAccent };
  Object.assign(globalThis, api);
  return api;
}
