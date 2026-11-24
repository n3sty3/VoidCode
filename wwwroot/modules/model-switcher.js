// 1-Click Quick Model Switcher Popover (Ctrl+Alt+M, right-click topbar model chip, or Command Palette).
// Switches between curated flagship/fast cloud models and auto-detected local Ollama / LM Studio models
// in a single click without opening the full Settings dialog.
import { html, setHtml } from '../core/markdown.js';

let hostRequest = (action, data, timeoutMs) => globalThis.VoidBridge.request(action, data, timeoutMs);
let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

let popEl = null;
let localModelsCache = [];
let localScanDone = false;

const CURATED_MODELS = [
  { group: 'Flagship Coding & Reasoning', label: 'Claude Sonnet 4', provider: 'anthropic', baseUrl: 'https://api.anthropic.com/v1', model: 'claude-sonnet-4-20250514', tier: '$$' },
  { group: 'Flagship Coding & Reasoning', label: 'Claude Opus 4', provider: 'anthropic', baseUrl: 'https://api.anthropic.com/v1', model: 'claude-opus-4-20250514', tier: '$$$' },
  { group: 'Flagship Coding & Reasoning', label: 'Gemini 2.5 Pro', provider: 'gemini', baseUrl: 'https://generativelanguage.googleapis.com/v1beta/openai', model: 'gemini-2.5-pro', tier: '$$' },
  { group: 'Flagship Coding & Reasoning', label: 'GPT-4.1', provider: 'openai', baseUrl: 'https://api.openai.com/v1', model: 'gpt-4.1', tier: '$$' },
  { group: 'Flagship Coding & Reasoning', label: 'OpenAI o4-mini', provider: 'openai', baseUrl: 'https://api.openai.com/v1', model: 'o4-mini', tier: '$' },
  { group: 'Flagship Coding & Reasoning', label: 'DeepSeek R1 (OpenRouter)', provider: 'openrouter', baseUrl: 'https://openrouter.ai/api/v1', model: 'deepseek/deepseek-r1', tier: '$' },
  { group: 'Fast & Low-Cost', label: 'Gemini 2.5 Flash', provider: 'gemini', baseUrl: 'https://generativelanguage.googleapis.com/v1beta/openai', model: 'gemini-2.5-flash', tier: '$' },
  { group: 'Fast & Low-Cost', label: 'DeepSeek V3 (OpenRouter)', provider: 'openrouter', baseUrl: 'https://openrouter.ai/api/v1', model: 'deepseek/deepseek-chat', tier: '$' },
  { group: 'Fast & Low-Cost', label: 'Qwen 2.5 Coder 32B', provider: 'openrouter', baseUrl: 'https://openrouter.ai/api/v1', model: 'qwen/qwen-2.5-coder-32b-instruct', tier: '$' },
  { group: 'Fast & Low-Cost', label: 'Llama 3.3 70B (Groq)', provider: 'groq', baseUrl: 'https://api.groq.com/openai/v1', model: 'llama-3.3-70b-versatile', tier: '$' },
  { group: 'Fast & Low-Cost', label: 'Grok 3 (xAI)', provider: 'xai', baseUrl: 'https://api.x.ai/v1', model: 'grok-3', tier: '$$' }
];

function ensurePopover() {
  if (popEl && document.body.contains(popEl)) return popEl;
  const el = document.createElement('div');
  el.id = 'quick-model-switcher';
  el.className = 'context-menu glass glass--float';
  el.style.cssText = 'display:none;position:fixed;z-index:2650;width:340px;max-height:76vh;flex-direction:column;gap:6px;padding:10px;';
  el.setAttribute('role', 'dialog');
  el.setAttribute('aria-label', 'Quick Model Switcher');

  setHtml(el, html`
    <div style="display:flex;align-items:center;justify-content:space-between;gap:6px;">
      <span style="font-size:12px;font-weight:700;">Quick Model Switcher</span>
      <button type="button" class="btn btn--quiet btn--xs" id="btn-qms-scan-local" style="white-space:nowrap;flex-shrink:0;">Scan Local AI</button>
    </div>
    <input type="text" id="qms-filter" class="filter-input" placeholder="Filter models (claude, gemini, deepseek, ollama...)" autocomplete="off" spellcheck="false" style="width:100%;height:30px;padding:0 10px;border-radius:8px;border:1px solid var(--seam);background:rgba(0,0,0,0.22);color:var(--text);font-size:12px;" />
    <div id="qms-list" style="overflow-y:auto;display:flex;flex-direction:column;gap:2px;max-height:46vh;"></div>
    <div style="padding-top:6px;border-top:1px solid var(--seam);display:flex;justify-content:space-between;align-items:center;">
      <span style="font-size:10px;opacity:0.65;">Tip: Right-click topbar model or Ctrl+Alt+M</span>
      <button type="button" class="btn btn--quiet btn--xs" id="btn-qms-settings" style="white-space:nowrap;flex-shrink:0;">Provider Settings…</button>
    </div>
  `);

  document.body.appendChild(el);
  popEl = el;

  document.getElementById('qms-filter')?.addEventListener('input', renderList);
  document.getElementById('btn-qms-scan-local')?.addEventListener('click', () => scanLocalModels(true));
  document.getElementById('btn-qms-settings')?.addEventListener('click', () => {
    closeQuickModelSwitcher();
    globalThis.openSettingsToTab?.('tab-provider');
  });

  document.addEventListener('mousedown', (e) => {
    if (!popEl || popEl.style.display === 'none') return;
    if (!popEl.contains(/** @type {Node} */ (e.target))) closeQuickModelSwitcher();
  }, true);

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && popEl && popEl.style.display !== 'none') {
      e.preventDefault();
      closeQuickModelSwitcher();
    }
  });

  return el;
}

function applyModelSelection(item) {
  const s = { ...(window.VoidState?.get?.('settings') || {}) };
  s.Provider = item.provider;
  s.BaseUrl = item.baseUrl;
  s.Model = item.model;
  window.VoidState?.set?.('settings', s);
  globalThis.applySettingsToUI?.(s);
  hostSend('save_settings', { settings: s });
  closeQuickModelSwitcher();
  globalThis.showToast?.(`Switched active model to ${item.label || item.model}`, 'success');
}

function renderList() {
  const listEl = document.getElementById('qms-list');
  const filterEl = /** @type {HTMLInputElement | null} */ (document.getElementById('qms-filter'));
  if (!listEl) return;

  const q = (filterEl?.value || '').trim().toLowerCase();
  const currentModel = (window.VoidState?.get?.('settings')?.Model || '').trim().toLowerCase();

  const localItems = localModelsCache.map((m) => ({
    group: 'Local AI (Detected)',
    label: m.model,
    provider: m.provider,
    baseUrl: m.baseUrl,
    model: m.model,
    tier: 'Free'
  }));

  const all = [...localItems, ...CURATED_MODELS].filter((item) => {
    if (!q) return true;
    return `${item.label} ${item.model} ${item.provider} ${item.group}`.toLowerCase().includes(q);
  });

  setHtml(listEl, '');
  if (all.length === 0) {
    setHtml(listEl, html`<div style="padding:14px;text-align:center;font-size:12px;opacity:0.7;">No matching models.</div>`);
    return;
  }

  let lastGroup = '';
  for (const item of all) {
    if (item.group !== lastGroup) {
      lastGroup = item.group;
      const g = document.createElement('div');
      g.className = 'ctx-group-label';
      g.textContent = lastGroup;
      listEl.appendChild(g);
    }

    const isActive = item.model.toLowerCase() === currentModel;
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'ctx-item';
    btn.style.cssText = `width:100%;height:34px;${isActive ? 'background:var(--glacier-wash);color:var(--glacier);' : ''}`;
    setHtml(btn, html`
      <span class="ctx-label" style="display:flex;align-items:center;gap:6px;">
        <span>${isActive ? '● ' : ''}${item.label}</span>
      </span>
      <span class="ctx-hint">${item.provider} · ${item.tier}</span>
    `);
    btn.addEventListener('click', () => applyModelSelection(item));
    listEl.appendChild(btn);
  }
}

async function scanLocalModels(force = false) {
  if (localScanDone && !force) return;
  localScanDone = true;
  try {
    const res = await hostRequest('discover_local_models', {}, 3500);
    if (res?.ok && Array.isArray(res.data?.models)) {
      localModelsCache = res.data.models;
      renderList();
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'model-switcher.scanLocal');
  }
}

export function openQuickModelSwitcher(anchorEl = null) {
  const el = ensurePopover();
  const anchor = anchorEl || document.getElementById('btn-open-settings');
  el.style.display = 'flex';
  renderList();

  if (anchor) {
    const rect = anchor.getBoundingClientRect();
    const popRect = el.getBoundingClientRect();
    const popH = Math.min(popRect.height || 380, window.innerHeight - 24);
    const x = Math.max(10, Math.min(rect.right - 340, window.innerWidth - 350));
    const openAbove = rect.bottom + popH + 14 > window.innerHeight && rect.top > popH;
    const y = openAbove
      ? Math.max(12, rect.top - popH - 8)
      : Math.min(rect.bottom + 6, window.innerHeight - popH - 12);
    el.style.left = `${x}px`;
    el.style.top = `${y}px`;
  } else {
    el.style.left = `${Math.max(10, (window.innerWidth - 340) / 2)}px`;
    el.style.top = '54px';
  }

  const filterEl = /** @type {HTMLInputElement | null} */ (document.getElementById('qms-filter'));
  if (filterEl) {
    filterEl.value = '';
    filterEl.focus();
  }
  scanLocalModels(false);
}

export function closeQuickModelSwitcher() {
  if (popEl) popEl.style.display = 'none';
}

export function init(ctx = {}) {
  if (typeof ctx.requestHost === 'function') hostRequest = ctx.requestHost;
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  const wireAnchor = () => {
    const pill = document.getElementById('btn-open-settings');
    if (pill && !pill.dataset.qmsWired) {
      pill.dataset.qmsWired = 'true';
      pill.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        e.stopPropagation();
        openQuickModelSwitcher(pill);
      });
    }
  };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', wireAnchor, { once: true });
  else wireAnchor();

  Object.assign(globalThis, {
    openQuickModelSwitcher,
    closeQuickModelSwitcher
  });

  return {
    openQuickModelSwitcher,
    closeQuickModelSwitcher
  };
}
