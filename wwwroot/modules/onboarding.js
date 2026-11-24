// 15-Second First-Run Onboarding Wizard for VoidCode.
// Auto-scans local Ollama / LM Studio & environment keys, offers 1-click Import from VS Code / Cursor,
// and lets new users Open a Folder or Clone a Git Repo immediately.
// Never auto-opens during automated Playwright tests (navigator.webdriver check).
import { html, setHtml } from '../core/markdown.js';

let hostRequest = (action, data, timeoutMs) => globalThis.VoidBridge.request(action, data, timeoutMs);
let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);
let modalEl = null;
const DONE_KEY = 'voidcode_onboarding_v110_done';

function ensureModal() {
  if (modalEl && document.body.contains(modalEl)) return modalEl;

  const overlay = document.createElement('div');
  overlay.id = 'onboarding-modal';
  overlay.className = 'modal-overlay';
  overlay.style.cssText = 'display:none;z-index:2620;';

  const card = document.createElement('div');
  card.className = 'glass glass--float';
  card.setAttribute('role', 'dialog');
  card.setAttribute('aria-modal', 'true');
  card.setAttribute('aria-label', 'Welcome to VoidCode');
  card.style.cssText = 'width:min(680px,92vw);display:flex;flex-direction:column;border-radius:18px;padding:22px;gap:14px;';

  setHtml(card, html`
    <div style="display:flex;align-items:center;justify-content:space-between;gap:12px;">
      <div>
        <div style="font-size:19px;font-weight:700;letter-spacing:-0.01em;">Welcome to VoidCode</div>
        <div style="font-size:12px;opacity:0.75;">Get coding with VoidAgent v2 in 15 seconds — local or cloud AI, zero lock-in.</div>
      </div>
      <button type="button" class="icon-btn icon-btn--sm" id="btn-onb-close" aria-label="Skip Welcome">&times;</button>
    </div>

    <div class="control-well" style="gap:8px;">
      <div class="label-row">
        <label style="font-weight:600;">1. Connect AI Engine</label>
        <button type="button" class="btn btn--quiet btn--xs" id="btn-onb-settings" style="white-space:nowrap;">Configure API Key…</button>
      </div>
      <div id="onb-ai-scan" style="font-size:12px;opacity:0.8;">Scanning local Ollama / LM Studio and environment API keys...</div>
    </div>

    <div class="control-well" style="gap:8px;">
      <div style="display:flex;align-items:center;justify-content:space-between;gap:12px;width:100%;">
        <div>
          <div style="font-size:13px;font-weight:600;">2. Bring Your Editor Ergonomics</div>
          <div style="font-size:11px;opacity:0.72;">Import font, tabs, minimap, wrap, and recent folders from VS Code or Cursor.</div>
        </div>
        <button type="button" class="btn btn--quiet btn--sm" id="btn-onb-import" style="white-space:nowrap;flex-shrink:0;">Import from VS Code / Cursor</button>
      </div>
    </div>

    <div class="control-well" style="gap:8px;">
      <div style="font-size:13px;font-weight:600;">3. Open a Project</div>
      <div style="display:flex;gap:8px;flex-wrap:wrap;">
        <button type="button" class="btn btn--primary btn--sm" id="btn-onb-open-folder" style="white-space:nowrap;">Open Folder…</button>
        <button type="button" class="btn btn--quiet btn--sm" id="btn-onb-clone" style="white-space:nowrap;">Clone Git Repository…</button>
        <button type="button" class="btn btn--quiet btn--sm" id="btn-onb-dismiss" style="white-space:nowrap;margin-left:auto;">Start Coding</button>
      </div>
    </div>
  `);

  overlay.appendChild(card);
  document.body.appendChild(overlay);
  modalEl = overlay;

  const finish = () => {
    try { localStorage.setItem(DONE_KEY, '1'); } catch (err) { globalThis.logSwallowed?.(err, 'onboarding.saveDone'); }
    closeOnboardingModal();
  };

  overlay.addEventListener('click', (e) => { if (e.target === overlay) finish(); });
  document.getElementById('btn-onb-close')?.addEventListener('click', finish);
  document.getElementById('btn-onb-dismiss')?.addEventListener('click', finish);
  document.getElementById('btn-onb-settings')?.addEventListener('click', () => {
    finish();
    globalThis.openSettingsToTab?.('tab-provider');
  });
  document.getElementById('btn-onb-import')?.addEventListener('click', () => {
    globalThis.openIdeImportModal?.();
  });
  document.getElementById('btn-onb-open-folder')?.addEventListener('click', () => {
    finish();
    hostSend('open_folder_dialog', {});
  });
  document.getElementById('btn-onb-clone')?.addEventListener('click', () => {
    finish();
    globalThis.openGitCloneModal?.();
  });

  return overlay;
}

async function scanAiForOnboarding() {
  const box = document.getElementById('onb-ai-scan');
  if (!box) return;
  try {
    const res = await hostRequest('discover_local_models', {}, 3500);
    const data = res?.ok ? res.data : null;
    const models = Array.isArray(data?.models) ? data.models : [];
    const envKeys = Array.isArray(data?.detectedEnvProviders) ? data.detectedEnvProviders : [];
    setHtml(box, '');

    if (models.length > 0) {
      const top = models[0];
      const row = document.createElement('div');
      row.style.cssText = 'display:flex;align-items:center;justify-content:space-between;gap:10px;';
      setHtml(row, html`
        <span>Detected ${models.length} local model${models.length === 1 ? '' : 's'} (e.g. <code>${top.model}</code> via ${top.provider}).</span>
      `);
      const useBtn = document.createElement('button');
      useBtn.type = 'button';
      useBtn.className = 'btn btn--primary btn--xs';
      useBtn.style.cssText = 'white-space:nowrap;flex-shrink:0;';
      useBtn.textContent = `Use ${top.model}`;
      useBtn.addEventListener('click', () => {
        const s = { ...(window.VoidState?.get?.('settings') || {}) };
        s.Provider = top.provider;
        s.BaseUrl = top.baseUrl;
        s.Model = top.model;
        window.VoidState?.set?.('settings', s);
        globalThis.applySettingsToUI?.(s);
        hostSend('save_settings', { settings: s });
        globalThis.showToast?.(`Activated local model ${top.model}`, 'success');
      });
      row.appendChild(useBtn);
      box.appendChild(row);
    } else if (envKeys.length > 0) {
      setHtml(box, html`Environment API keys detected for: <strong>${envKeys.join(', ')}</strong> — VoidAgent is ready to go!`);
    } else {
      setHtml(box, html`No local Ollama/LM Studio server found yet. Click <strong>Configure API Key…</strong> to connect OpenRouter, Anthropic, Gemini, OpenAI, DeepSeek, or start Ollama locally.`);
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'onboarding.scan');
    box.textContent = 'Click Configure API Key… to connect your preferred AI provider.';
  }
}

export function openOnboardingModal() {
  const el = ensureModal();
  el.style.display = 'flex';
  scanAiForOnboarding();
}

export function closeOnboardingModal() {
  if (modalEl) modalEl.style.display = 'none';
}

export function init(ctx = {}) {
  if (typeof ctx.requestHost === 'function') hostRequest = ctx.requestHost;
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  if (window.VoidBridge && typeof window.VoidBridge.subscribe === 'function') {
    window.VoidBridge.subscribe((action, payload) => {
      if (action !== 'init_state_response' || !payload) return;
      if (typeof navigator !== 'undefined' && navigator.webdriver) return;
      let done = false;
      try { done = localStorage.getItem(DONE_KEY) === '1'; } catch (err) { globalThis.logSwallowed?.(err, 'onboarding.readDone'); }
      if (!done && !payload.workspace && payload.settings && !payload.settings.ApiKeyConfigured) {
        setTimeout(() => openOnboardingModal(), 500);
      }
    });
  }

  Object.assign(globalThis, {
    openOnboardingModal,
    closeOnboardingModal
  });
  return {
    openOnboardingModal,
    closeOnboardingModal
  };
}
