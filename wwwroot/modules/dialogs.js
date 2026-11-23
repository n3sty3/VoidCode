// Toasts, input/confirm dialogs, glass sliders, themed custom selects,
// Local operator gate, and the tabbed utility/mini-window controller.
// Modal dialogs, custom selects, auth gate, and floating utility window chrome.js).
import { html, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

/**
 * @param {string} id
 * @returns {HTMLInputElement | null}
 */
function control(id) {
  return /** @type {HTMLInputElement | null} */ (document.getElementById(id));
}

let topWindowZ = 410;
let authMode = 'login';
let currentOperator = 'operator';
let customSelectSeq = 0;
let customSelectDocBound = false;
const enhancedSelects = new WeakMap();

const DOCK_MAP = {
  'win-preview': 'btn-open-preview',
  'win-history': 'btn-open-history',
  'win-timeline': 'btn-open-timeline',
  'win-prompts': 'btn-open-prompts',
  'win-inspector': 'btn-open-inspector',
  'win-customize': 'btn-open-customize'
};

const UTILITY_TABS = [
  { id: 'win-preview', label: 'Preview' },
  { id: 'win-history', label: 'Sessions' },
  { id: 'win-timeline', label: 'Timeline' },
  { id: 'win-prompts', label: 'Playbooks' }
];

export function getCurrentOperator() {
  return currentOperator;
}

export function showToast(message, type = 'info') {
  const container = document.getElementById('toast-container');
  if (!container) return;
  const el = document.createElement('div');
  el.className = `toast-item ${type}`;
  el.textContent = message;
  container.appendChild(el);
  setTimeout(() => {
    el.style.opacity = '0';
    setTimeout(() => el.remove(), 220);
  }, 3000);
}

export function showInputDialog(title, defaultValue = '', confirmLabel = 'Create') {
  return new Promise((resolve) => {
    const modal = document.getElementById('input-dialog-modal');
    const titleEl = document.getElementById('input-dialog-title');
    const subEl = document.getElementById('input-dialog-sub');
    const inputEl = control('input-dialog-field');
    const btnConfirm = document.getElementById('input-dialog-confirm');
    const btnCancel = document.getElementById('input-dialog-cancel');
    if (!modal || !titleEl || !inputEl || !btnConfirm || !btnCancel) {
      resolve(null);
      return;
    }

    titleEl.textContent = title;
    if (subEl) subEl.style.display = 'none';
    inputEl.readOnly = false;
    inputEl.placeholder = '';
    btnConfirm.textContent = confirmLabel;
    inputEl.value = defaultValue;
    modal.style.display = 'flex';
    inputEl.focus();
    inputEl.select();

    function cleanup(val) {
      modal.style.display = 'none';
      btnConfirm.onclick = null;
      btnCancel.onclick = null;
      inputEl.onkeydown = null;
      resolve(val);
    }

    btnConfirm.onclick = () => cleanup(inputEl.value.trim());
    btnCancel.onclick = () => cleanup(null);
    inputEl.onkeydown = (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        e.stopPropagation();
        cleanup(inputEl.value.trim());
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        cleanup(null);
      }
    };
  });
}

export function showConfirmDialog(title, message, confirmLabel = 'Confirm', altLabel = null, timeoutMs = 0) {
  return new Promise((resolve) => {
    const modal = document.getElementById('input-dialog-modal');
    const titleEl = document.getElementById('input-dialog-title');
    const inputEl = control('input-dialog-field');
    const btnConfirm = document.getElementById('input-dialog-confirm');
    const btnCancel = document.getElementById('input-dialog-cancel');
    if (!modal || !titleEl || !btnConfirm || !btnCancel) {
      resolve(true);
      return;
    }

    titleEl.textContent = title;
    btnConfirm.textContent = confirmLabel;
    if (inputEl) inputEl.style.display = 'none';

    let btnAlt = document.getElementById('input-dialog-alt');
    if (!btnAlt) {
      btnAlt = document.createElement('button');
      btnAlt.setAttribute('type', 'button');
      btnAlt.id = 'input-dialog-alt';
      btnAlt.className = 'btn btn--quiet';
      btnCancel.parentNode?.insertBefore(btnAlt, btnCancel);
    }
    if (altLabel) {
      btnAlt.textContent = altLabel;
      btnAlt.style.display = '';
    } else {
      btnAlt.style.display = 'none';
    }

    let msgEl = document.getElementById('confirm-dialog-msg');
    if (!msgEl && inputEl) {
      msgEl = document.createElement('p');
      msgEl.id = 'confirm-dialog-msg';
      msgEl.className = 'empty-hint';
      msgEl.style.margin = '8px 0';
      inputEl.parentNode?.insertBefore(msgEl, inputEl);
    }
    if (msgEl) {
      msgEl.textContent = message;
      msgEl.style.display = 'block';
    }

    modal.style.display = 'flex';
    btnConfirm.focus();
    const timeoutId = timeoutMs > 0 ? setTimeout(() => cleanup(false), timeoutMs) : null;

    function cleanup(val) {
      if (timeoutId) clearTimeout(timeoutId);
      modal.style.display = 'none';
      if (inputEl) inputEl.style.display = 'block';
      if (msgEl) msgEl.style.display = 'none';
      btnAlt.style.display = 'none';
      btnConfirm.onclick = null;
      btnCancel.onclick = null;
      btnAlt.onclick = null;
      resolve(val);
    }

    btnConfirm.onclick = () => cleanup(true);
    btnAlt.onclick = () => cleanup('alt');
    btnCancel.onclick = () => cleanup(false);
  });
}

export function updateSliderFill(slider) {
  if (!slider) return;
  const min = parseFloat(slider.min) || 0;
  const max = parseFloat(slider.max) || 100;
  const val = parseFloat(slider.value) || 0;
  const pct = Math.max(0, Math.min(100, ((val - min) / (max - min)) * 100));
  slider.style.setProperty('--range-pct', `${pct}%`);
}

export function syncAllSliders() {
  document.querySelectorAll('.glass-slider').forEach(updateSliderFill);
}

function optionLabelFor(selectEl) {
  const opt = selectEl.options[selectEl.selectedIndex];
  return opt ? opt.textContent.trim() : '';
}

export function setupCustomSelects() {
  document.querySelectorAll('select').forEach(node => {
    const selectEl = /** @type {HTMLSelectElement} */ (node);
    selectEl.classList.add('native-select');
    if (selectEl.dataset.enhanced === 'true') return;
    const parent = selectEl.parentElement;
    if (!parent) return;

    const wrap = document.createElement('div');
    wrap.className = 'custom-select-wrap';

    const trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'custom-select-trigger';
    trigger.setAttribute('aria-haspopup', 'listbox');
    trigger.setAttribute('aria-expanded', 'false');
    const selectName = selectEl.getAttribute('aria-label') || selectEl.labels?.[0]?.textContent?.trim() || '';
    if (selectName) trigger.setAttribute('aria-label', selectName);

    const label = document.createElement('span');
    const chevron = document.createElement('span');
    chevron.className = 'custom-select-chevron';
    setHtml(chevron, '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="m6 9 6 6 6-6"/></svg>');
    trigger.append(label, chevron);

    const menu = document.createElement('div');
    menu.className = 'custom-select-menu glass glass--float';
    menu.setAttribute('role', 'listbox');
    menu.id = `custom-select-menu-${customSelectSeq++}`;
    trigger.setAttribute('aria-controls', menu.id);
    if (selectName) menu.setAttribute('aria-label', selectName);

    parent.insertBefore(wrap, selectEl);
    wrap.append(selectEl, trigger, menu);
    selectEl.classList.add('custom-select-source');
    selectEl.tabIndex = -1;
    selectEl.setAttribute('aria-hidden', 'true');
    selectEl.dataset.enhanced = 'true';

    function close(returnFocus) {
      wrap.classList.remove('is-open');
      trigger.setAttribute('aria-expanded', 'false');
      if (returnFocus) trigger.focus();
    }

    function choose(index) {
      if (index < 0 || index >= selectEl.options.length) return;
      selectEl.selectedIndex = index;
      selectEl.dispatchEvent(new Event('change', { bubbles: true }));
      buildMenu();
      close(true);
    }

    function buildMenu() {
      setHtml(menu, '');
      label.textContent = optionLabelFor(selectEl);
      Array.from(selectEl.options).forEach((opt, index) => {
        const isSel = index === selectEl.selectedIndex;
        const item = document.createElement('button');
        item.type = 'button';
        item.className = 'custom-select-option' + (isSel ? ' is-selected' : '');
        item.setAttribute('role', 'option');
        item.setAttribute('aria-selected', isSel ? 'true' : 'false');
        item.dataset.index = String(index);
        setHtml(item, html`<span>${(opt.textContent || '').trim()}</span>` +
          (isSel ? '<span class="custom-select-check" aria-hidden="true">\u2713</span>' : ''));
        item.addEventListener('click', e => {
          e.stopPropagation();
          choose(index);
        });
        menu.appendChild(item);
      });
    }

    function open() {
      buildMenu();
      wrap.classList.add('is-open');
      trigger.setAttribute('aria-expanded', 'true');
      const active = /** @type {HTMLElement | null} */ (menu.querySelector('.is-selected') || menu.firstElementChild);
      if (active) active.focus();
    }

    trigger.addEventListener('click', e => {
      e.stopPropagation();
      if (wrap.classList.contains('is-open')) close(true);
      else open();
    });

    trigger.addEventListener('keydown', e => {
      if (e.key === 'ArrowDown' || e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        open();
      }
    });

    menu.addEventListener('keydown', e => {
      const items = /** @type {HTMLElement[]} */ (Array.from(menu.querySelectorAll('.custom-select-option')));
      const current = items.indexOf(/** @type {HTMLElement} */ (document.activeElement));
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        return close(true);
      }
      const step = { ArrowDown: 1, ArrowUp: -1, Home: -items.length, End: items.length }[e.key];
      if (step === undefined || current < 0) return;
      e.preventDefault();
      items[Math.max(0, Math.min(items.length - 1, current + step))].focus();
    });

    selectEl.addEventListener('change', () => {
      label.textContent = optionLabelFor(selectEl);
      if (wrap.classList.contains('is-open')) buildMenu();
    });

    buildMenu();
    enhancedSelects.set(selectEl, { wrap, trigger, menu, rebuild: buildMenu });
  });

  if (!customSelectDocBound) {
    customSelectDocBound = true;
    document.addEventListener('click', () => {
      document.querySelectorAll('.custom-select-wrap.is-open').forEach(wrap => {
        wrap.classList.remove('is-open');
        wrap.querySelector('.custom-select-trigger')?.setAttribute('aria-expanded', 'false');
      });
    });
  }
}

export function syncCustomSelect(selectId) {
  const selectEl = typeof selectId === 'string' ? document.getElementById(selectId) : selectId;
  if (!selectEl) return;
  const entry = enhancedSelects.get(selectEl);
  if (entry && entry.rebuild) entry.rebuild();
}

export function syncAllCustomSelects() {
  document.querySelectorAll('select.custom-select-source').forEach(el => syncCustomSelect(el));
}

export function setupAuthGate() {
  const gate = document.getElementById('auth-gate');
  const tabLogin = document.getElementById('tab-btn-login');
  const tabRegister = document.getElementById('tab-btn-register');
  const roleGroup = document.getElementById('auth-role-group');
  const errEl = document.getElementById('auth-error-msg');
  const form = document.getElementById('auth-form');
  const btnGuest = document.getElementById('btn-auth-guest');
  const btnSubmit = document.getElementById('btn-auth-submit');
  if (!gate || !tabLogin || !tabRegister || !roleGroup || !errEl || !form || !btnGuest || !btnSubmit) return;

  const rememberedOp = localStorage.getItem('voidcode_active_operator') || 'operator';
  currentOperator = rememberedOp;
  globalThis.setTopbarOperator?.(currentOperator, `${currentOperator} (Local session)`);
  gate.style.display = 'none';

  function setMode(mode) {
    authMode = mode;
    tabLogin.classList.toggle('active', mode === 'login');
    tabRegister.classList.toggle('active', mode === 'register');
    tabLogin.setAttribute('aria-selected', mode === 'login' ? 'true' : 'false');
    tabRegister.setAttribute('aria-selected', mode === 'register' ? 'true' : 'false');
    tabLogin.tabIndex = mode === 'login' ? 0 : -1;
    tabRegister.tabIndex = mode === 'register' ? 0 : -1;
    roleGroup.style.display = mode === 'register' ? 'flex' : 'none';
    btnSubmit.textContent = mode === 'register' ? 'Create profile & continue' : 'Continue';
    errEl.style.display = 'none';
  }

  tabLogin.addEventListener('click', () => setMode('login'));
  tabRegister.addEventListener('click', () => setMode('register'));
  [tabLogin, tabRegister].forEach((tab, index, tabs) => {
    tab.addEventListener('keydown', (event) => {
      if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
      event.preventDefault();
      const nextIndex = event.key === 'ArrowRight'
        ? (index + 1) % tabs.length
        : (index - 1 + tabs.length) % tabs.length;
      tabs[nextIndex].click();
      tabs[nextIndex].focus();
    });
  });

  form.addEventListener('submit', (e) => {
    e.preventDefault();
    const username = control('auth-username')?.value.trim() || '';
    const password = control('auth-password')?.value || '';
    const role = control('auth-role')?.value.trim() || 'Systems operator';
    if (!username) return;
    errEl.style.display = 'none';
    sendHost('auth_operator', { mode: authMode, username, password, role });
  });

  btnGuest.addEventListener('click', () => {
    completeAuth('operator', 'Local session', !window.VoidState?.get?.('workspace'));
  });

  document.getElementById('btn-operator-pill')?.addEventListener('click', () => {
    const u = control('auth-username');
    const p = control('auth-password');
    if (u) u.value = currentOperator;
    if (p) p.value = '';
    gate.style.display = 'flex';
  });
}

export function completeAuth(username, role, showHubAfter = false) {
  currentOperator = username;
  try { localStorage.setItem('voidcode_active_operator', username); } catch (err) { globalThis.logSwallowed?.(err, 'dialogs.completeAuth'); }
  globalThis.setTopbarOperator?.(username, `${username} (${role})`);
  const gate = document.getElementById('auth-gate');
  if (gate) gate.style.display = 'none';

  const settings = window.VoidState?.get?.('settings');
  if (settings) settings.ActiveOperator = username;
  if (showHubAfter) globalThis.openProjectHub?.();
}

export function clampMiniWindow(win) {
  if (!win) return;
  const pad = 8;
  const rect = win.getBoundingClientRect();
  if (rect.width === 0) return;

  const left = Math.min(Math.max(rect.left, pad), Math.max(pad, window.innerWidth - rect.width - pad));
  const top = Math.min(Math.max(rect.top, pad), Math.max(pad, window.innerHeight - rect.height - pad));

  win.style.transform = 'none';
  win.style.right = 'auto';
  win.style.left = Math.round(left) + 'px';
  win.style.top = Math.round(top) + 'px';
}

export function isUtilityWindow(winId) {
  return UTILITY_TABS.some((t) => t.id === winId);
}

function layoutUtilitySlot(win) {
  const isWide = win.id === 'win-preview' || win.id === 'win-diff';
  const width = Math.min(isWide ? 860 : 620, window.innerWidth - 48);
  const height = Math.min(isWide ? 640 : 540, window.innerHeight - 120);
  win.style.width = width + 'px';
  win.style.height = height + 'px';
  win.style.maxHeight = 'none';
  win.style.right = 'auto';
  win.style.left = Math.max(16, Math.round((window.innerWidth - width) / 2)) + 'px';
  win.style.top = Math.max(54, Math.round((window.innerHeight - height) / 2)) + 'px';
  win.style.transform = 'none';
}

export function openUtility(winId) {
  UTILITY_TABS.forEach((tab) => {
    const win = document.getElementById(tab.id);
    if (!win) return;
    if (tab.id === winId) {
      win.style.display = 'flex';
      layoutUtilitySlot(win);
      topWindowZ += 1;
      win.style.zIndex = String(topWindowZ);
      focusMiniWindow(win);
    } else if (win.style.display !== 'none') {
      win.style.display = 'none';
    }
  });

  document.querySelectorAll('.utility-tab').forEach((btn) => {
    btn.classList.toggle('is-active', btn.getAttribute('data-utility-tab') === winId);
  });

  if (winId === 'win-history') globalThis.renderHistorySessions?.();
  if (winId === 'win-timeline') globalThis.renderTimelineEntries?.();
  if (winId === 'win-prompts') globalThis.renderPlaybooks?.();
}

export function setupUtilityTabs() {
  UTILITY_TABS.forEach((tab) => {
    const win = document.getElementById(tab.id);
    const head = win && win.querySelector('.mini-win-head');
    if (!head || win.querySelector('.utility-tabs')) return;

    const nav = document.createElement('nav');
    nav.className = 'utility-tabs';
    nav.setAttribute('role', 'tablist');
    nav.setAttribute('aria-label', 'Workspace tools');

    UTILITY_TABS.forEach((entry) => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'utility-tab';
      btn.setAttribute('role', 'tab');
      btn.setAttribute('data-utility-tab', entry.id);
      btn.textContent = entry.label;
      btn.addEventListener('click', (event) => {
        event.stopPropagation();
        openUtility(entry.id);
      });
      nav.appendChild(btn);
    });

    const actions = head.querySelector('.mini-win-actions');
    if (actions) head.insertBefore(nav, actions);
    else head.appendChild(nav);
  });
}

export function prepareMiniWindow(win) {
  if (!win) return;
  if (!win.hasAttribute('tabindex')) win.setAttribute('tabindex', '-1');
  if (win.dataset.escBound !== '1') {
    win.dataset.escBound = '1';
    win.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        toggleMiniWindow(win.id, false);
      }
    });
  }
}

export function focusMiniWindow(win) {
  if (!win) return;
  prepareMiniWindow(win);
  requestAnimationFrame(() => {
    try { win.focus({ preventScroll: true }); }
    catch { try { win.focus(); } catch (err) { globalThis.logSwallowed?.(err, 'dialogs.focusMiniWindow'); } }
  });
}

export function toggleMiniWindow(winId, forceOpen = null) {
  if (isUtilityWindow(winId)) {
    const util = document.getElementById(winId);
    if (!util) return;
    const open = util.style.display !== 'none';
    const next = forceOpen !== null ? forceOpen : !open;
    if (next) openUtility(winId);
    else util.style.display = 'none';
    return;
  }

  const win = document.getElementById(winId);
  if (!win) return;
  const isVisible = win.style.display !== 'none';
  const nextState = forceOpen !== null ? forceOpen : !isVisible;

  win.style.display = nextState ? 'flex' : 'none';
  if (nextState) {
    if (winId === 'win-diff') layoutUtilitySlot(win);
    topWindowZ += 1;
    win.style.zIndex = String(topWindowZ);
    requestAnimationFrame(() => clampMiniWindow(win));
    focusMiniWindow(win);
  }

  const dockBtnId = DOCK_MAP[winId];
  if (dockBtnId) {
    const btn = document.getElementById(dockBtnId);
    if (btn) btn.classList.toggle('is-open', nextState);
  }
}

export function setupDraggableWindows() {
  document.querySelectorAll('[data-native-drag]').forEach(bar => {
    bar.addEventListener('mousedown', (e) => {
      const me = /** @type {MouseEvent} */ (e);
      if (me.button !== 0) return;
      if (/** @type {Element} */ (me.target)?.closest?.('button, input, select, textarea, a, [role="button"]')) return;
      sendHost('window_drag');
    });
    bar.addEventListener('dblclick', (e) => {
      if (/** @type {Element} */ (e.target)?.closest?.('button, input, select, textarea, a, [role="button"]')) return;
      sendHost('window_maximize');
    });
  });

  document.querySelectorAll('[data-win-cmd]').forEach(btn => {
    btn.addEventListener('click', () => {
      const cmd = btn.getAttribute('data-win-cmd');
      if (cmd) sendHost(cmd);
    });
  });

  document.querySelectorAll('[data-drag-window]').forEach(header => {
    const winId = header.getAttribute('data-drag-window');
    const win = winId ? document.getElementById(winId) : null;
    if (!win) return;

    let offsetX = 0;
    let offsetY = 0;

    win.addEventListener('mousedown', () => {
      if (win.classList.contains('mini-window')) {
        topWindowZ += 1;
        win.style.zIndex = String(topWindowZ);
      }
    });

    header.addEventListener('mousedown', (e) => {
      const me = /** @type {MouseEvent} */ (e);
      if (/** @type {Element} */ (me.target)?.closest?.('button')) return;
      me.preventDefault();
      const startX = me.clientX - offsetX;
      const startY = me.clientY - offsetY;

      const onMove = (ev) => {
        offsetX = ev.clientX - startX;
        offsetY = ev.clientY - startY;
        win.style.transform = `translate3d(${offsetX}px, ${offsetY}px, 0)`;
      };

      const onUp = () => {
        window.removeEventListener('mousemove', onMove);
        window.removeEventListener('mouseup', onUp);
        if (win.classList.contains('mini-window')) {
          clampMiniWindow(win);
          offsetX = 0;
          offsetY = 0;
        }
      };

      window.addEventListener('mousemove', onMove);
      window.addEventListener('mouseup', onUp);
    });
  });

  document.querySelectorAll('[data-close-window]').forEach(btn => {
    btn.addEventListener('click', () => {
      const winId = btn.getAttribute('data-close-window');
      if (winId) toggleMiniWindow(winId, false);
    });
  });
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  Object.assign(globalThis, {
    getCurrentOperator,
    showToast,
    showInputDialog,
    showConfirmDialog,
    updateSliderFill,
    syncAllSliders,
    setupCustomSelects,
    syncCustomSelect,
    syncAllCustomSelects,
    setupAuthGate,
    completeAuth,
    clampMiniWindow,
    isUtilityWindow,
    openUtility,
    setupUtilityTabs,
    prepareMiniWindow,
    focusMiniWindow,
    toggleMiniWindow,
    setupDraggableWindows
  });
  return {
    getCurrentOperator,
    showToast,
    showInputDialog,
    showConfirmDialog,
    updateSliderFill,
    syncAllSliders,
    setupCustomSelects,
    syncCustomSelect,
    syncAllCustomSelects,
    setupAuthGate,
    completeAuth,
    clampMiniWindow,
    isUtilityWindow,
    openUtility,
    setupUtilityTabs,
    prepareMiniWindow,
    focusMiniWindow,
    toggleMiniWindow,
    setupDraggableWindows
  };
}
