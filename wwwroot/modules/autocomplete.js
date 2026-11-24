// AI Tab Autocomplete (Fill-in-the-Middle ghost text in Monaco) +
// Dedicated fast/local model slot + 1-click Ollama / LM Studio Local AI Auto-Discovery.
import { html, setHtml } from '../core/markdown.js';

let hostRequest = (action, data, timeoutMs) => globalThis.VoidBridge.request(action, data, timeoutMs);
let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

let providerRegistered = false;
let requestSeq = 0;
let isThinking = false;

function getSettings() {
  return window.VoidState?.get?.('settings') || {};
}

function isEnabled() {
  const s = getSettings();
  return s.AutocompleteEnabled !== false;
}

function updateStatusChip() {
  const chip = document.getElementById('status-tab-ai');
  if (!chip) return;
  const enabled = isEnabled();
  const s = getSettings();
  const modelLabel = s.AutocompleteModel || s.Model || 'Active model';
  chip.textContent = !enabled ? 'Tab AI: Off' : (isThinking ? 'Tab AI…' : 'Tab AI');
  chip.title = enabled
    ? `AI Tab Autocomplete active (${modelLabel}) — click to toggle`
    : 'AI Tab Autocomplete disabled — click to enable';
  chip.style.opacity = enabled ? '1' : '0.55';
}

export function toggleTabAutocomplete() {
  const s = { ...getSettings() };
  s.AutocompleteEnabled = s.AutocompleteEnabled === false;
  window.VoidState?.set?.('settings', s);
  const toggleEl = /** @type {HTMLInputElement | null} */ (document.getElementById('settings-autocomplete-enabled'));
  if (toggleEl) toggleEl.checked = s.AutocompleteEnabled;
  hostSend('save_settings', { settings: s });
  updateStatusChip();
  globalThis.showToast?.(
    s.AutocompleteEnabled ? 'AI Tab Autocomplete enabled (press Tab to accept ghost text)' : 'AI Tab Autocomplete paused',
    'info'
  );
}

function mountStatusChip() {
  const bar = document.getElementById('statusbar');
  if (!bar || document.getElementById('status-tab-ai')) return;

  const chip = document.createElement('button');
  chip.id = 'status-tab-ai';
  chip.type = 'button';
  chip.className = 'status-item';
  chip.style.cssText = 'background:none;border:none;color:inherit;cursor:pointer;font:inherit;padding:0 6px;';
  chip.addEventListener('click', toggleTabAutocomplete);

  const lspSlot = document.getElementById('status-lsp');
  if (lspSlot && lspSlot.parentNode) {
    lspSlot.parentNode.insertBefore(chip, lspSlot.nextSibling);
  } else {
    bar.appendChild(chip);
  }
  updateStatusChip();
}

function registerMonacoInlineProvider() {
  if (providerRegistered || !window.monaco?.languages?.registerInlineCompletionsProvider) return;
  providerRegistered = true;

  window.monaco.languages.registerInlineCompletionsProvider({ pattern: '**' }, {
    provideInlineCompletions: async (model, position, _context, token) => {
      if (!isEnabled() || !model || !position) return { items: [] };

      // Debounce typing so we only call the model once the user pauses briefly.
      const mySeq = ++requestSeq;
      await new Promise((resolve) => setTimeout(resolve, 280));
      if (mySeq !== requestSeq || token.isCancellationRequested) return { items: [] };

      const activeTab = window.VoidState?.get?.('activeTab');
      const fullBefore = model.getValueInRange({
        startLineNumber: Math.max(1, position.lineNumber - 80),
        startColumn: 1,
        endLineNumber: position.lineNumber,
        endColumn: position.column
      });
      if (!fullBefore || !fullBefore.trim()) return { items: [] };

      const lineCount = model.getLineCount();
      const endLine = Math.min(lineCount, position.lineNumber + 30);
      const fullAfter = model.getValueInRange({
        startLineNumber: position.lineNumber,
        startColumn: position.column,
        endLineNumber: endLine,
        endColumn: model.getLineMaxColumn(endLine)
      });

      isThinking = true;
      updateStatusChip();
      try {
        const res = await hostRequest('ai_inline_complete', {
          path: activeTab?.path || '',
          language: model.getLanguageId?.() || 'plaintext',
          prefix: fullBefore,
          suffix: fullAfter,
          line: position.lineNumber,
          column: position.column
        }, 5500);

        if (mySeq !== requestSeq || token.isCancellationRequested) return { items: [] };
        const completion = res?.ok && res?.data ? String(res.data.completion || '') : '';
        if (!completion) return { items: [] };

        return {
          items: [
            {
              insertText: completion,
              range: new window.monaco.Range(
                position.lineNumber,
                position.column,
                position.lineNumber,
                position.column
              )
            }
          ]
        };
      } catch (err) {
        globalThis.logSwallowed?.(err, 'autocomplete.provideInlineCompletions');
        return { items: [] };
      } finally {
        if (mySeq === requestSeq) {
          isThinking = false;
          updateStatusChip();
        }
      }
    },
    freeInlineCompletions: () => {}
  });
}

async function runLocalDiscovery(resultsHost) {
  if (!resultsHost) return;
  setHtml(resultsHost, html`<div style="font-size:12px;opacity:0.75;">Scanning Ollama (11434), LM Studio (1234), and environment keys...</div>`);
  try {
    const res = await hostRequest('discover_local_models', {}, 4000);
    const report = res?.ok ? res.data : null;
    if (!report) {
      setHtml(resultsHost, html`<div style="font-size:12px;opacity:0.75;">Local discovery unavailable in this environment.</div>`);
      return;
    }

    const models = Array.isArray(report.models) ? report.models : [];
    const envProviders = Array.isArray(report.detectedEnvProviders) ? report.detectedEnvProviders : [];
    setHtml(resultsHost, '');

    if (models.length === 0 && envProviders.length === 0) {
      setHtml(resultsHost, html`<div style="font-size:12px;opacity:0.75;">No local Ollama/LM Studio servers detected on localhost. Start Ollama (<code>ollama serve</code>) or paste a cloud API key above.</div>`);
      return;
    }

    if (models.length > 0) {
      const label = document.createElement('div');
      label.style.cssText = 'font-size:11px;opacity:0.75;margin-bottom:6px;';
      label.textContent = `Detected ${models.length} local model${models.length === 1 ? '' : 's'} — click to assign as Agent or Tab AI:`;
      resultsHost.appendChild(label);

      const wrap = document.createElement('div');
      wrap.style.cssText = 'display:flex;flex-wrap:wrap;gap:6px;margin-bottom:6px;';
      models.slice(0, 12).forEach((m) => {
        const chip = document.createElement('div');
        chip.style.cssText = 'display:inline-flex;align-items:center;gap:4px;padding:3px 7px;border-radius:6px;border:1px solid var(--border-subtle,rgba(255,255,255,0.12));background:var(--surface-2,rgba(255,255,255,0.04));font-size:11px;';

        const nameSpan = document.createElement('span');
        nameSpan.textContent = `${m.provider}: ${m.model}${m.isFastCoder ? ' ⚡' : ''}`;
        chip.appendChild(nameSpan);

        const useAgentBtn = document.createElement('button');
        useAgentBtn.type = 'button';
        useAgentBtn.className = 'btn btn--quiet btn--xs';
        useAgentBtn.textContent = 'Agent';
        useAgentBtn.addEventListener('click', () => {
          const provEl = /** @type {HTMLInputElement | null} */ (document.getElementById('settings-provider'));
          const urlEl = /** @type {HTMLInputElement | null} */ (document.getElementById('settings-base-url'));
          const modEl = /** @type {HTMLInputElement | null} */ (document.getElementById('settings-model'));
          if (provEl) provEl.value = m.provider;
          if (urlEl) urlEl.value = m.baseUrl;
          if (modEl) modEl.value = m.model;
          globalThis.syncAllCustomSelects?.();
          globalThis.showToast?.(`Selected ${m.model} for VoidAgent (click Save to apply)`, 'info');
        });
        chip.appendChild(useAgentBtn);

        const useTabBtn = document.createElement('button');
        useTabBtn.type = 'button';
        useTabBtn.className = 'btn btn--quiet btn--xs';
        useTabBtn.textContent = 'Tab AI';
        useTabBtn.addEventListener('click', () => {
          const acUrlEl = /** @type {HTMLInputElement | null} */ (document.getElementById('settings-autocomplete-url'));
          const acModEl = /** @type {HTMLInputElement | null} */ (document.getElementById('settings-autocomplete-model'));
          if (acUrlEl) acUrlEl.value = m.baseUrl;
          if (acModEl) acModEl.value = m.model;
          globalThis.showToast?.(`Assigned ${m.model} to Tab Autocomplete (click Save to apply)`, 'info');
        });
        chip.appendChild(useTabBtn);

        wrap.appendChild(chip);
      });
      resultsHost.appendChild(wrap);
    }

    if (envProviders.length > 0) {
      const envNote = document.createElement('div');
      envNote.style.cssText = 'font-size:11px;opacity:0.8;';
      envNote.textContent = `Environment API keys detected: ${envProviders.join(', ')}`;
      resultsHost.appendChild(envNote);
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'autocomplete.runLocalDiscovery');
  }
}

function mountSettingsPanel() {
  const providerTab = document.getElementById('tab-provider');
  if (!providerTab || document.getElementById('voidcode-autocomplete-well')) return;

  const well = document.createElement('div');
  well.id = 'voidcode-autocomplete-well';
  well.style.cssText = 'margin-top:14px;padding:12px;border-radius:10px;border:1px solid var(--border-subtle,rgba(255,255,255,0.1));background:var(--surface-1,rgba(255,255,255,0.02));';

  setHtml(well, html`
    <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:8px;">
      <div>
        <div style="font-size:13px;font-weight:600;">Tab Autocomplete (FIM Ghost Text) &amp; Local AI Discovery</div>
        <div style="font-size:11px;opacity:0.7;">Use a dedicated fast/local model (e.g. Ollama qwen2.5-coder:3b, Groq, Gemini Flash) or leave blank to reuse your main model.</div>
      </div>
      <button type="button" class="btn btn--quiet btn--xs" id="btn-discover-local-models">Scan Local AI</button>
    </div>
    <div style="display:flex;align-items:center;gap:8px;margin-bottom:8px;">
      <input type="checkbox" id="settings-autocomplete-enabled" checked />
      <label for="settings-autocomplete-enabled" style="font-size:12px;cursor:pointer;">Enable real-time Tab ghost-text autocomplete in editor</label>
    </div>
    <div style="display:grid;grid-template-columns:1fr 1fr;gap:8px;margin-bottom:8px;">
      <input type="text" class="text-input" id="settings-autocomplete-url" placeholder="Optional fast endpoint (e.g. http://localhost:11434/v1)" />
      <input type="text" class="text-input" id="settings-autocomplete-model" placeholder="Optional fast model (e.g. qwen2.5-coder:3b)" />
    </div>
    <div id="local-discovery-results"></div>
  `);

  providerTab.appendChild(well);

  const btnScan = document.getElementById('btn-discover-local-models');
  const resultsHost = document.getElementById('local-discovery-results');
  btnScan?.addEventListener('click', () => runLocalDiscovery(resultsHost));
  syncSettingsFields(getSettings());
}

export function syncAutocompleteSettingsFields(s) {
  syncSettingsFields(s || getSettings());
  updateStatusChip();
}

function syncSettingsFields(s) {
  const en = /** @type {HTMLInputElement | null} */ (document.getElementById('settings-autocomplete-enabled'));
  const url = /** @type {HTMLInputElement | null} */ (document.getElementById('settings-autocomplete-url'));
  const mod = /** @type {HTMLInputElement | null} */ (document.getElementById('settings-autocomplete-model'));
  if (en) en.checked = s?.AutocompleteEnabled !== false;
  if (url) url.value = s?.AutocompleteBaseUrl || '';
  if (mod) mod.value = s?.AutocompleteModel || '';
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  if (typeof ctx.requestHost === 'function') hostRequest = ctx.requestHost;

  const boot = () => {
    mountStatusChip();
    mountSettingsPanel();
    const waitMonaco = setInterval(() => {
      if (window.monaco?.languages?.registerInlineCompletionsProvider) {
        clearInterval(waitMonaco);
        registerMonacoInlineProvider();
      }
    }, 350);
    setTimeout(() => clearInterval(waitMonaco), 20000);
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot, { once: true });
  } else {
    boot();
  }

  window.VoidState?.subscribe?.('settings', (s) => {
    syncSettingsFields(s);
    updateStatusChip();
  });

  Object.assign(globalThis, {
    toggleTabAutocomplete,
    syncAutocompleteSettingsFields
  });

  return {
    toggleTabAutocomplete,
    syncAutocompleteSettingsFields
  };
}
