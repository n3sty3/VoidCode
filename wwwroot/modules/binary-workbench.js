// Native Binary Reverse-Engineering Workbench UI:
// Interactive x86/x86-64 Disassembler (Iced), Paginated Hex+ASCII Viewer, PE/Mach-O/ELF Headers,
// and 1-click "Explain / Decompile with VoidAgent".
import { html, setHtml } from '../core/markdown.js';

let hostRequest = (action, data, timeoutMs) => globalThis.VoidBridge.request(action, data, timeoutMs);

let currentPath = '';
let currentHexOffset = 0;
let currentReport = null;
let activeTabName = 'disasm';

function ensureModal() {
  let modal = document.getElementById('binary-workbench-modal');
  if (modal) return modal;

  modal = document.createElement('div');
  modal.id = 'binary-workbench-modal';
  modal.className = 'modal-overlay';
  modal.style.cssText = 'display:none;position:fixed;inset:0;z-index:530;align-items:center;justify-content:center;background:rgba(5,9,15,0.72);backdrop-filter:blur(8px);';

  const card = document.createElement('div');
  card.className = 'glass glass--float';
  card.setAttribute('role', 'dialog');
  card.setAttribute('aria-label', 'Binary Hex & Disassembler Workbench');
  card.style.cssText = 'width:min(940px,94vw);height:min(82vh,760px);display:flex;flex-direction:column;border-radius:14px;border:1px solid var(--border-subtle,rgba(255,255,255,0.12));overflow:hidden;box-shadow:0 24px 64px rgba(0,0,0,0.6);';

  setHtml(card, html`
    <div style="display:flex;align-items:center;justify-content:space-between;padding:12px 16px;border-bottom:1px solid var(--border-subtle,rgba(255,255,255,0.08));">
      <div>
        <div style="font-size:15px;font-weight:600;">Binary Workbench &mdash; x86-64 Disassembler &amp; Hex Inspector</div>
        <div id="bw-subtitle" style="font-size:11px;opacity:0.72;">Inspect PE32/PE32+, Mach-O, ELF, or raw binaries with native Iced disassembly.</div>
      </div>
      <button type="button" class="icon-btn icon-btn--sm" id="btn-close-bw" aria-label="Close Binary Workbench">&times;</button>
    </div>
    <div style="display:flex;align-items:center;gap:8px;padding:10px 16px;border-bottom:1px solid var(--border-subtle,rgba(255,255,255,0.08));">
      <input type="text" class="text-input" id="bw-path-input" style="flex:1;" placeholder="Path to binary (.exe, .dll, .dylib, .so, .bin)..." />
      <button type="button" class="btn btn--primary btn--xs" id="btn-bw-load">Inspect</button>
      <button type="button" class="btn btn--quiet btn--xs" id="btn-bw-ai">Decompile / Explain with VoidAgent</button>
    </div>
    <div style="display:flex;align-items:center;justify-content:space-between;padding:8px 16px;border-bottom:1px solid var(--border-subtle,rgba(255,255,255,0.08));">
      <div style="display:flex;gap:6px;">
        <button type="button" class="btn btn--quiet btn--xs" data-bw-tab="disasm">Disassembly (Iced)</button>
        <button type="button" class="btn btn--quiet btn--xs" data-bw-tab="hex">Hex + ASCII</button>
        <button type="button" class="btn btn--quiet btn--xs" data-bw-tab="headers">Headers &amp; Strings</button>
      </div>
      <div id="bw-hex-nav" style="display:none;align-items:center;gap:6px;font-size:11px;">
        <button type="button" class="btn btn--quiet btn--xs" id="btn-bw-hex-prev">&larr; Prev 512B</button>
        <span id="bw-hex-pos">0x00000000</span>
        <button type="button" class="btn btn--quiet btn--xs" id="btn-bw-hex-next">Next 512B &rarr;</button>
      </div>
    </div>
    <pre id="bw-output" style="flex:1;margin:0;padding:14px 16px;overflow:auto;font-family:'JetBrains Mono','Cascadia Code',Consolas,monospace;font-size:12px;line-height:1.5;background:rgba(0,0,0,0.28);"></pre>
  `);

  modal.appendChild(card);
  document.body.appendChild(modal);

  modal.addEventListener('click', (e) => { if (e.target === modal) closeBinaryWorkbench(); });
  document.getElementById('btn-close-bw')?.addEventListener('click', closeBinaryWorkbench);
  document.getElementById('btn-bw-load')?.addEventListener('click', () => {
    const inp = /** @type {HTMLInputElement | null} */ (document.getElementById('bw-path-input'));
    if (inp?.value.trim()) loadBinary(inp.value.trim(), 0);
  });
  document.getElementById('btn-bw-ai')?.addEventListener('click', sendDisassemblyToAgent);
  document.getElementById('btn-bw-hex-prev')?.addEventListener('click', () => {
    if (currentPath) loadBinary(currentPath, Math.max(0, currentHexOffset - 512));
  });
  document.getElementById('btn-bw-hex-next')?.addEventListener('click', () => {
    if (currentPath) loadBinary(currentPath, currentHexOffset + 512);
  });
  modal.querySelectorAll('[data-bw-tab]').forEach((btn) => {
    btn.addEventListener('click', () => {
      activeTabName = btn.getAttribute('data-bw-tab') || 'disasm';
      renderActiveTab();
    });
  });

  return modal;
}

function renderActiveTab() {
  const out = document.getElementById('bw-output');
  const nav = document.getElementById('bw-hex-nav');
  if (!out) return;
  if (nav) nav.style.display = activeTabName === 'hex' ? 'inline-flex' : 'none';

  if (!currentReport) {
    out.textContent = 'Enter a binary file path above and click Inspect.';
    return;
  }
  if (!currentReport.ok) {
    out.textContent = currentReport.error || 'Inspection failed.';
    return;
  }

  if (activeTabName === 'disasm') {
    const rows = Array.isArray(currentReport.disassembly) ? currentReport.disassembly : [];
    if (rows.length === 0) {
      out.textContent = `No x86/x86-64 disassembly available for ${currentReport.architecture}. Switch to Hex + ASCII or Headers & Strings.`;
      return;
    }
    const header = `; ${currentReport.architecture} (${currentReport.bitness}-bit) @ VA ${currentReport.codeVirtualAddress} (raw offset 0x${(currentReport.codeStartOffset || 0).toString(16)})\n\n`;
    out.textContent = header + rows.map((r) => `${r.address}   ${String(r.bytes || '').padEnd(26)}  ${r.instruction}`).join('\n');
  } else if (activeTabName === 'hex') {
    const rows = Array.isArray(currentReport.hexRows) ? currentReport.hexRows : [];
    const posEl = document.getElementById('bw-hex-pos');
    if (posEl) posEl.textContent = `0x${currentHexOffset.toString(16).padStart(8, '0')}`;
    out.textContent = rows.map((r) => `${r.offset}  ${String(r.hex || '').padEnd(49)} |${r.ascii}|`).join('\n');
  } else {
    out.textContent = currentReport.summaryText || '(no header summary)';
  }
}

async function loadBinary(path, hexOffset = 0) {
  currentPath = path;
  currentHexOffset = hexOffset;
  const out = document.getElementById('bw-output');
  const sub = document.getElementById('bw-subtitle');
  if (out) out.textContent = `Inspecting ${path}...`;
  try {
    const res = await hostRequest('binary_workbench_inspect', {
      path,
      hexOffset,
      hexLength: 512,
      maxInstructions: 120
    }, 8000);
    currentReport = res?.ok ? res.data : { ok: false, error: res?.error || 'Request failed' };
    if (sub && currentReport?.ok) {
      sub.textContent = `${currentReport.architecture} · ${Number(currentReport.fileSize || 0).toLocaleString()} bytes · Entry VA ${currentReport.codeVirtualAddress}`;
    }
    renderActiveTab();
  } catch (err) {
    globalThis.logSwallowed?.(err, 'binaryWorkbench.loadBinary');
  }
}

function sendDisassemblyToAgent() {
  if (!currentReport?.ok) return;
  const rows = (currentReport.disassembly || []).slice(0, 60);
  const snippet = rows.length > 0
    ? rows.map((r) => `${r.address}  ${r.instruction}`).join('\n')
    : (currentReport.summaryText || '').slice(0, 2000);
  const prompt = `Analyze and explain/decompile this binary (${currentReport.path}, ${currentReport.architecture}):\n\n\`\`\`asm\n${snippet}\n\`\`\``;
  closeBinaryWorkbench();
  globalThis.toggleWorkspacePane?.('agent', true);
  const input = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
  if (input) {
    input.value = prompt;
    input.focus();
  }
  globalThis.showToast?.('Loaded disassembly into VoidAgent composer', 'info');
}

export function openBinaryWorkbench(path) {
  const modal = ensureModal();
  modal.style.display = 'flex';
  const activeTab = window.VoidState?.get?.('activeTab');
  const initialPath = path || activeTab?.path || '';
  const inp = /** @type {HTMLInputElement | null} */ (document.getElementById('bw-path-input'));
  if (inp && initialPath) inp.value = initialPath;
  if (initialPath) loadBinary(initialPath, 0);
  else renderActiveTab();
}

export function closeBinaryWorkbench() {
  const modal = document.getElementById('binary-workbench-modal');
  if (modal) modal.style.display = 'none';
}

export function init(ctx = {}) {
  if (typeof ctx.requestHost === 'function') hostRequest = ctx.requestHost;
  Object.assign(globalThis, { openBinaryWorkbench, closeBinaryWorkbench });
  return { openBinaryWorkbench, closeBinaryWorkbench };
}
