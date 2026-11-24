// Environment Health & Capabilities modal + one-click LSP server installer.
import { html, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

/** @type {any} */
let lastCapabilities = null;
/** @type {any} */
let lastUpdateResult = null;

function byId(id) {
  return document.getElementById(id);
}

export function getLastCapabilities() {
  return lastCapabilities;
}

export function refreshCapabilities() {
  sendHost('get_capabilities', {});
}

export function checkForUpdates(silent = false) {
  const settings = window.VoidState?.get?.('settings') || {};
  sendHost('check_for_updates', {
    repo: settings.UpdateRepo || '',
    silent: !!silent
  });
}

export function openHealthModal() {
  const modal = byId('health-modal');
  if (!modal) return;
  modal.style.display = 'flex';
  const body = byId('health-modal-body');
  if (body) setHtml(body, html`<div class="empty-hint">Probing runtimes, language servers, search, and shells...</div>`);
  refreshCapabilities();
}

export function closeHealthModal() {
  const modal = byId('health-modal');
  if (modal) modal.style.display = 'none';
}

export function openAboutModal() {
  const modal = byId('about-modal');
  const body = byId('about-modal-body');
  if (!modal || !body) return;
  modal.style.display = 'flex';
  const settings = window.VoidState?.get?.('settings') || {};
  const workspace = window.VoidState?.get?.('workspace') || 'No folder open';
  const runtime = window.VoidState?.get?.('selectedRuntime');
  const trust = lastCapabilities?.workspaceTrust || 'trusted';
  setHtml(body, '');
  const appVer = lastUpdateResult?.currentVersion || '1.1.0';
  const verBadge = byId('about-version-badge');
  if (verBadge) verBadge.textContent = `v${appVer}`;
  const infoSec = makeSection('Release & Runtime Environment');
  infoSec.appendChild(makeRow('VoidCode IDE', 'Desktop Edition (.NET 10 + Monaco + Native Terminal)', `v${appVer}`));
  infoSec.appendChild(makeRow('Agent Engine', 'VoidAgent Core (EXPLORE -> PATCH -> VERIFY + Turn Transaction Ledger)', settings.AgentMode === 'plan' ? 'Plan mode' : 'Act mode'));
  infoSec.appendChild(makeRow('Active Model & Provider', `${settings.Provider || 'openrouter'} \u00b7 ${settings.Model || 'default'}`, settings.AuthorityMode || 'review'));
  infoSec.appendChild(makeRow('Active Workspace', workspace, `Trust: ${trust}`));
  if (runtime) {
    infoSec.appendChild(makeRow('Selected Interpreter / Runtime', runtime.path || 'PATH', runtime.name));
  }
  infoSec.appendChild(makeRow('Local Storage & Audit Log', '%APPDATA%\\VoidCode (DPAPI keys, settings.json, agent-audit.jsonl)', 'Local-only'));

  const upd = lastUpdateResult;
  let updBtn = null;
  if (upd && (upd.downloadUrl || upd.releaseUrl)) {
    updBtn = document.createElement('button');
    updBtn.type = 'button';
    updBtn.className = upd.updateAvailable ? 'btn btn--primary btn--xs' : 'btn btn--quiet btn--xs';
    const isDirectAsset = upd.updateAvailable && typeof upd.downloadUrl === 'string' && /\/releases\/download\//.test(upd.downloadUrl);
    updBtn.textContent = isDirectAsset
      ? `Install v${upd.latestVersion}`
      : (upd.updateAvailable ? `Download v${upd.latestVersion}` : 'Releases ↗');
    updBtn.addEventListener('click', async () => {
      if (isDirectAsset) {
        updBtn.disabled = true;
        updBtn.textContent = 'Downloading…';
        try {
          const res = await globalThis.VoidBridge?.request?.('update_download_install', { downloadUrl: upd.downloadUrl }, 120000);
          const data = res?.data || res;
          if (data?.ok) {
            updBtn.textContent = data.sha256Verified ? 'Verified & Launched' : 'Launched';
            globalThis.showToast?.(data.message || 'Installer launched', 'success');
          } else {
            updBtn.disabled = false;
            updBtn.textContent = `Install v${upd.latestVersion}`;
            globalThis.showToast?.(data?.message || 'Update download failed — opening browser instead', 'error');
            sendHost('open_external_url', { url: upd.downloadUrl || upd.releaseUrl });
          }
        } catch (err) {
          globalThis.logSwallowed?.(err, 'health.installUpdate');
          updBtn.disabled = false;
          updBtn.textContent = `Install v${upd.latestVersion}`;
          sendHost('open_external_url', { url: upd.downloadUrl || upd.releaseUrl });
        }
      } else {
        sendHost('open_external_url', { url: upd.downloadUrl || upd.releaseUrl });
      }
    });
  }
  infoSec.appendChild(makeRow(
    'GitHub Release Updates',
    upd ? `${upd.message} (${upd.repo || 'GitHub'})` : 'Click "Check for updates" to query GitHub Releases.',
    upd ? (upd.updateAvailable ? `Update: v${upd.latestVersion}` : (upd.checked ? 'Up to date' : 'Offline')) : `v${appVer}`,
    updBtn
  ));
  body.appendChild(infoSec);
}

export function closeAboutModal() {
  const modal = byId('about-modal');
  if (modal) modal.style.display = 'none';
}

function makeSection(title) {
  const sec = document.createElement('div');
  sec.className = 'health-section';
  const h = document.createElement('div');
  h.className = 'health-section-title';
  h.textContent = title;
  sec.appendChild(h);
  return sec;
}

function statusPillTone(text) {
  const s = String(text || '').toLowerCase();
  if (s === 'ready' || s === 'found' || s.startsWith('fast')) return ' is-ok';
  if (s === 'missing' || s === 'fallback' || s.includes('not configured')) return ' is-warn';
  return ' is-muted';
}

function makeRow(label, detail, badgeText, actionEl = null) {
  const row = document.createElement('div');
  row.className = 'health-row';

  const left = document.createElement('div');
  left.className = 'inspector-row-main';
  setHtml(left, html`
    <div class="inspector-row-title">${label}</div>
    <div class="inspector-row-detail" title="${detail || ''}">${detail || ''}</div>
  `);

  const right = document.createElement('div');
  right.className = 'inspector-row-actions';
  const badge = document.createElement('span');
  badge.className = 'val-badge' + statusPillTone(badgeText);
  badge.textContent = badgeText;
  right.appendChild(badge);
  if (actionEl) right.appendChild(actionEl);

  row.append(left, right);
  return row;
}

function renderCapabilities(report) {
  lastCapabilities = report;
  globalThis.updateRuntimeStatus?.(report);
  const body = byId('health-modal-body');
  if (!body || !report) return;
  setHtml(body, '');

  // 1. Provider
  const prov = report.provider || {};
  const provSec = makeSection('AI Provider & Workspace');
  const openSettingsBtn = document.createElement('button');
  openSettingsBtn.type = 'button';
  openSettingsBtn.className = 'btn btn--quiet btn--xs';
  openSettingsBtn.textContent = 'Configure';
  openSettingsBtn.addEventListener('click', () => {
    closeHealthModal();
    globalThis.openSettingsToTab?.('tab-provider');
  });
  const ctxK = prov.contextWindow ? `${Math.round(prov.contextWindow / 1000)}k window` : 'default window';
  provSec.appendChild(makeRow(
    prov.model || 'No model configured',
    `${prov.provider || 'none'} · ${ctxK} · trust: ${report.workspaceTrust || 'unknown'}`,
    prov.configured ? 'Ready' : 'Not configured',
    openSettingsBtn
  ));
  body.appendChild(provSec);

  // 2. Language Servers (with one-click consent install)
  const lspSec = makeSection('Language Servers (LSP)');
  (report.lsp || []).forEach(item => {
    let actionBtn = null;
    if (!item.found && item.installCommand) {
      actionBtn = document.createElement('button');
      actionBtn.type = 'button';
      actionBtn.className = 'btn btn--primary btn--xs';
      actionBtn.dataset.installLsp = item.language;
      actionBtn.textContent = 'Install';
      actionBtn.title = item.installCommand;
      actionBtn.addEventListener('click', async () => {
        const confirmed = globalThis.showConfirmDialog
          ? await globalThis.showConfirmDialog(
            `Install ${item.serverName}?`,
            `Run:\n${item.installCommand}`,
            'Install'
          )
          : true;
        if (!confirmed) return;
        actionBtn.disabled = true;
        actionBtn.textContent = 'Installing...';
        const logEl = byId('health-install-log');
        if (logEl) {
          logEl.style.display = 'block';
          logEl.textContent = `Installing ${item.serverName}...\n`;
        }
        sendHost('install_lsp_server', { language: item.language });
      });
    }
    const detail = item.found
      ? (item.path || 'Detected on PATH')
      : (item.installCommand || `Missing (${item.prerequisite || 'manual install'})`);
    lspSec.appendChild(makeRow(
      `${item.language} (${item.serverName})`,
      detail,
      item.found ? 'Found' : 'Missing',
      actionBtn
    ));
  });
  body.appendChild(lspSec);

  // 3. Search, Git & Debuggers
  const coreSec = makeSection('Search, Git & Debuggers');
  if (report.ripgrep) {
    coreSec.appendChild(makeRow(
      report.ripgrep.name || 'ripgrep (rg)',
      report.ripgrep.found ? report.ripgrep.path : `Using built-in C# search. Faster option: ${report.ripgrep.installHint || 'install rg'}`,
      report.ripgrep.found ? 'Fast (rg)' : 'Fallback'
    ));
  }
  if (report.git) {
    coreSec.appendChild(makeRow(
      report.git.name || 'Git',
      report.git.found ? report.git.path : (report.git.installHint || 'Install Git'),
      report.git.found ? 'Found' : 'Missing'
    ));
  }
  const dbg = report.debug || {};
  coreSec.appendChild(makeRow('Node.js Debugger (V8 CDP)', dbg.node ? 'Ready for .js/.mjs files' : 'Install Node.js to enable', dbg.node ? 'Ready' : 'Missing'));
  coreSec.appendChild(makeRow('.NET Debugger (netcoredbg)', dbg.dotnet ? 'Ready for .cs / .NET projects' : 'Install netcoredbg on PATH to enable', dbg.dotnet ? 'Ready' : 'Unsupported'));
  coreSec.appendChild(makeRow('Python Debugger (debugpy)', dbg.python ? 'Ready for .py files' : 'Install debugpy (pip install debugpy) to enable', dbg.python ? 'Ready' : 'Unsupported'));
  body.appendChild(coreSec);

  // 4. Runtimes & Shells
  const rtSec = makeSection('Runtimes & Shells');
  [...(report.runtimes || []), ...(report.shells || [])].forEach(rt => {
    rtSec.appendChild(makeRow(
      rt.name,
      rt.found ? (rt.path || 'Available') : (rt.installHint || 'Not found on PATH'),
      rt.found ? 'Found' : 'Missing'
    ));
  });
  body.appendChild(rtSec);
}

function handleInstallProgress(payload) {
  const logEl = byId('health-install-log');
  if (!logEl || !payload || !payload.line) return;
  logEl.style.display = 'block';
  logEl.textContent += `${payload.line}\n`;
  logEl.scrollTop = logEl.scrollHeight;
}

function handleInstallFinished(payload) {
  if (!payload) return;
  const logEl = byId('health-install-log');
  if (logEl) {
    logEl.style.display = 'block';
    logEl.textContent += `\n${payload.message || (payload.success ? 'Installed.' : 'Install failed.')}\n`;
  }
  globalThis.showToast?.(payload.message || (payload.success ? 'Language server installed.' : 'Language server install failed.'), payload.success ? 'info' : 'error');
  if (payload.success) {
    sendHost('lsp_restart', {});
  }
}

function handleUpdateCheckResponse(payload) {
  if (!payload) return;
  lastUpdateResult = payload;
  const statusBtn = byId('status-update');
  const targetUrl = payload.downloadUrl || payload.releaseUrl;
  if (payload.updateAvailable && statusBtn) {
    statusBtn.style.display = 'inline-flex';
    statusBtn.textContent = `\u2b06 Update: v${payload.latestVersion}`;
    statusBtn.title = `${payload.message} (${payload.repo} \u00b7 current v${payload.currentVersion}) — click to download`;
    statusBtn.onclick = () => {
      if (targetUrl) sendHost('open_external_url', { url: targetUrl });
    };
  }
  if (payload.updateAvailable) {
    globalThis.showToast?.(`New VoidCode update available: v${payload.latestVersion} (click status bar to download)`, 'success');
  } else if (!payload.silent) {
    globalThis.showToast?.(payload.message || (payload.checked ? 'VoidCode is up to date.' : 'Could not check for updates.'), payload.checked ? 'info' : 'error');
  }
  const aboutModal = byId('about-modal');
  if (aboutModal && aboutModal.style.display !== 'none') {
    openAboutModal();
  }
}

function setupHealthModal() {
  const modal = byId('health-modal');
  if (modal) {
    byId('btn-close-health-modal')?.addEventListener('click', closeHealthModal);
    byId('btn-refresh-health-modal')?.addEventListener('click', refreshCapabilities);
    byId('btn-open-about-from-health')?.addEventListener('click', () => {
      closeHealthModal();
      openAboutModal();
    });
    modal.addEventListener('click', (e) => {
      if (e.target === modal) closeHealthModal();
    });
    modal.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        closeHealthModal();
      }
    });
  }

  const aboutModal = byId('about-modal');
  if (aboutModal) {
    byId('btn-close-about-modal')?.addEventListener('click', closeAboutModal);
    byId('btn-about-check-updates')?.addEventListener('click', () => checkForUpdates(false));
    byId('btn-about-open-logs')?.addEventListener('click', () => sendHost('open_logs_folder', {}));
    byId('btn-about-copy-info')?.addEventListener('click', () => {
      const settings = window.VoidState?.get?.('settings') || {};
      const ws = window.VoidState?.get?.('workspace') || 'none';
      const appVer = lastUpdateResult?.currentVersion || '1.1.0';
      const info = [
        `VoidCode IDE v${appVer} (.NET 10 / Desktop)`,
        `Agent: VoidAgent Core (${settings.Provider || 'openrouter'} / ${settings.Model || 'default'})`,
        `Authority: ${settings.AuthorityMode || 'review'} | Mode: ${settings.AgentMode || 'act'}`,
        `Workspace: ${ws}`,
        `UserAgent: ${navigator.userAgent}`
      ].join('\n');
      navigator.clipboard?.writeText?.(info);
      globalThis.showToast?.('Copied VoidCode system info to clipboard', 'info');
    });
    aboutModal.addEventListener('click', (e) => {
      if (e.target === aboutModal) closeAboutModal();
    });
    aboutModal.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        closeAboutModal();
      }
    });
  }
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  window.VoidHostEvents?.register?.({
    capabilities_response: renderCapabilities,
    lsp_install_progress: handleInstallProgress,
    lsp_install_finished: handleInstallFinished,
    update_check_response: handleUpdateCheckResponse
  });

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', setupHealthModal, { once: true });
  } else {
    setupHealthModal();
  }

  if (globalThis.chrome?.webview || /** @type {any} */ (globalThis.external)?.sendMessage) {
    setTimeout(() => checkForUpdates(true), 3500);
  }

  Object.assign(globalThis, {
    openHealthModal,
    closeHealthModal,
    openAboutModal,
    closeAboutModal,
    refreshCapabilities,
    checkForUpdates,
    getLastCapabilities
  });

  return {
    openHealthModal,
    closeHealthModal,
    openAboutModal,
    closeAboutModal,
    refreshCapabilities,
    checkForUpdates,
    getLastCapabilities
  };
}
