// AI Branch / Pre-PR Code Reviewer modal UI.
// Audits current feature branch vs main/master (or uncommitted changes) and presents
// severity-tagged findings with clickable Monaco file:line jumps and 1-click Fix with VoidAgent.
import { html, setHtml } from '../core/markdown.js';

let hostRequest = (action, data, timeoutMs) => globalThis.VoidBridge.request(action, data, timeoutMs);
let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);
let modalEl = null;

function ensureModal() {
  if (modalEl && document.body.contains(modalEl)) return modalEl;

  const overlay = document.createElement('div');
  overlay.id = 'branch-review-modal';
  overlay.className = 'modal-overlay';
  overlay.style.cssText = 'display:none;z-index:2600;';

  const card = document.createElement('div');
  card.className = 'glass glass--float';
  card.setAttribute('role', 'dialog');
  card.setAttribute('aria-modal', 'true');
  card.setAttribute('aria-label', 'AI Pre-PR Code Reviewer');
  card.style.cssText = 'width:min(860px,94vw);max-height:86vh;display:flex;flex-direction:column;border-radius:16px;padding:18px;gap:12px;';

  setHtml(card, html`
    <div style="display:flex;align-items:center;justify-content:space-between;gap:12px;">
      <div>
        <div style="font-size:16px;font-weight:700;">AI Branch &amp; Pre-PR Code Reviewer</div>
        <div style="font-size:12px;opacity:0.75;">Audits your branch diff (or uncommitted changes) for bugs, security risks, and edge cases before you push.</div>
      </div>
      <button type="button" class="icon-btn icon-btn--sm" id="btn-br-close" aria-label="Close Pre-PR Reviewer">&times;</button>
    </div>

    <div style="display:flex;gap:8px;align-items:center;">
      <input type="text" id="br-base-ref" class="text-input" placeholder="Base ref (auto-detects main/master, or enter e.g. origin/main, HEAD)" style="flex:1;" />
      <button type="button" class="btn btn--primary btn--sm" id="btn-br-run" style="white-space:nowrap;flex-shrink:0;">Run AI Review</button>
    </div>

    <div id="br-status" style="font-size:12px;opacity:0.8;"></div>
    <div id="br-results" style="overflow-y:auto;display:flex;flex-direction:column;gap:10px;min-height:220px;max-height:54vh;"></div>
  `);

  overlay.appendChild(card);
  document.body.appendChild(overlay);
  modalEl = overlay;

  overlay.addEventListener('click', (e) => {
    if (e.target === overlay) closeBranchReviewModal();
  });
  document.getElementById('btn-br-close')?.addEventListener('click', closeBranchReviewModal);
  document.getElementById('btn-br-run')?.addEventListener('click', runBranchReview);
  document.getElementById('br-base-ref')?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') runBranchReview();
  });

  return overlay;
}

function jumpToFileLine(relFile, line) {
  if (!relFile) return;
  const ws = globalThis.VoidState?.get?.('workspace') || '';
  const sep = ws.includes('\\') ? '\\' : '/';
  const cleanRel = relFile.replace(/^[/\\]+/, '').replace(/[/\\]/g, sep);
  const fullPath = ws ? `${ws}${ws.endsWith(sep) ? '' : sep}${cleanRel}` : relFile;
  closeBranchReviewModal();
  if (typeof globalThis.setPendingNavigateLocation === 'function') {
    globalThis.setPendingNavigateLocation({ line: Math.max(1, line || 1), col: 1 });
  }
  hostSend('read_file', { path: fullPath });
}

function fixFindingWithAgent(finding) {
  closeBranchReviewModal();
  globalThis.toggleWorkspacePane?.('agent', true);
  const box = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
  if (box) {
    const loc = finding.file ? `${finding.file}${finding.line ? `:${finding.line}` : ''}` : 'the diff';
    box.value = `Fix this Pre-PR review [${finding.severity.toUpperCase()}] finding in ${loc}:\n\n${finding.title}\n${finding.detail}${finding.suggestedFix ? `\n\nSuggested direction: ${finding.suggestedFix}` : ''}`;
    box.focus();
  }
}

async function runBranchReview() {
  const statusEl = document.getElementById('br-status');
  const resultsEl = document.getElementById('br-results');
  const baseRefEl = /** @type {HTMLInputElement | null} */ (document.getElementById('br-base-ref'));
  const btn = /** @type {HTMLButtonElement | null} */ (document.getElementById('btn-br-run'));
  if (!resultsEl || !statusEl) return;

  if (btn) btn.disabled = true;
  statusEl.textContent = 'Analyzing git diff with your configured AI model...';
  setHtml(resultsEl, html`<div style="padding:28px;text-align:center;opacity:0.7;font-size:12px;">Collecting git diff and running staff-engineer audit...</div>`);

  try {
    const res = await hostRequest('branch_review_run', { baseRef: (baseRefEl?.value || '').trim() }, 90000);
    const report = res?.ok ? res.data : null;
    if (!report || !report.ok) {
      statusEl.textContent = '';
      setHtml(resultsEl, html`<div style="padding:14px;border-radius:10px;border:1px solid rgba(255,100,100,0.35);font-size:12px;">${report?.error || res?.error || 'Branch review failed.'}</div>`);
      return;
    }

    const findings = Array.isArray(report.findings) ? report.findings : [];
    const crit = findings.filter((f) => f.severity === 'critical').length;
    const warn = findings.filter((f) => f.severity === 'warning').length;
    const sugg = findings.filter((f) => f.severity === 'suggestion').length;
    statusEl.textContent = `${report.diffMode || 'Diff'} — ${crit} critical, ${warn} warning, ${sugg} suggestion`;

    setHtml(resultsEl, '');
    const summaryBox = document.createElement('div');
    summaryBox.style.cssText = 'padding:12px;border-radius:10px;border:1px solid var(--border-subtle,rgba(255,255,255,0.12));background:var(--surface-1,rgba(255,255,255,0.04));font-size:13px;line-height:1.5;';
    setHtml(summaryBox, html`
      <div style="font-weight:600;margin-bottom:4px;">Executive Summary</div>
      <div>${report.summary}</div>
      ${report.diffStat ? html`<pre style="margin:8px 0 0;font-size:11px;opacity:0.75;white-space:pre-wrap;font-family:monospace;">${report.diffStat}</pre>` : ''}
    `);
    resultsEl.appendChild(summaryBox);

    if (findings.length === 0) {
      const clean = document.createElement('div');
      clean.style.cssText = 'padding:16px;text-align:center;font-size:12px;opacity:0.8;';
      clean.textContent = 'No issues flagged by VoidAgent.';
      resultsEl.appendChild(clean);
      return;
    }

    const sevColor = {
      critical: '#ff6b6b',
      warning: '#f5b942',
      suggestion: '#7ec8e3'
    };

    for (const f of findings) {
      const card = document.createElement('div');
      card.style.cssText = `padding:12px;border-radius:10px;border:1px solid var(--border-subtle,rgba(255,255,255,0.1));border-left:3px solid ${sevColor[f.severity] || '#7ec8e3'};background:var(--surface-1,rgba(255,255,255,0.03));display:flex;flex-direction:column;gap:6px;`;

      const head = document.createElement('div');
      head.style.cssText = 'display:flex;align-items:center;justify-content:space-between;gap:8px;flex-wrap:wrap;';
      setHtml(head, html`
        <div style="display:flex;align-items:center;gap:8px;">
          <span class="val-badge" style="color:${sevColor[f.severity] || '#7ec8e3'};text-transform:uppercase;">${f.severity}</span>
          <strong style="font-size:13px;">${f.title}</strong>
        </div>
      `);

      const actions = document.createElement('div');
      actions.style.cssText = 'display:flex;gap:6px;align-items:center;';
      if (f.file) {
        const jumpBtn = document.createElement('button');
        jumpBtn.type = 'button';
        jumpBtn.className = 'btn btn--quiet btn--xs';
        jumpBtn.style.cssText = 'white-space:nowrap;flex-shrink:0;font-family:monospace;';
        jumpBtn.textContent = `${f.file}${f.line ? `:${f.line}` : ''}`;
        jumpBtn.addEventListener('click', () => jumpToFileLine(f.file, f.line));
        actions.appendChild(jumpBtn);
      }

      const fixBtn = document.createElement('button');
      fixBtn.type = 'button';
      fixBtn.className = 'btn btn--primary btn--xs';
      fixBtn.style.cssText = 'white-space:nowrap;flex-shrink:0;';
      fixBtn.textContent = 'Fix with VoidAgent';
      fixBtn.addEventListener('click', () => fixFindingWithAgent(f));
      actions.appendChild(fixBtn);
      head.appendChild(actions);
      card.appendChild(head);

      const detail = document.createElement('div');
      detail.style.cssText = 'font-size:12px;line-height:1.45;opacity:0.9;';
      detail.textContent = f.detail || '';
      card.appendChild(detail);

      if (f.suggestedFix) {
        const fixBox = document.createElement('div');
        fixBox.style.cssText = 'font-size:11px;opacity:0.8;padding:6px 8px;border-radius:6px;background:rgba(255,255,255,0.03);font-family:monospace;white-space:pre-wrap;';
        fixBox.textContent = `Suggested fix: ${f.suggestedFix}`;
        card.appendChild(fixBox);
      }

      resultsEl.appendChild(card);
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'branch-review.run');
    statusEl.textContent = 'Review request failed.';
  } finally {
    if (btn) btn.disabled = false;
  }
}

export function openBranchReviewModal() {
  const el = ensureModal();
  el.style.display = 'flex';
}

export function closeBranchReviewModal() {
  if (modalEl) modalEl.style.display = 'none';
}

export function init(ctx = {}) {
  if (typeof ctx.requestHost === 'function') hostRequest = ctx.requestHost;
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  Object.assign(globalThis, {
    openBranchReviewModal,
    closeBranchReviewModal
  });
  return {
    openBranchReviewModal,
    closeBranchReviewModal
  };
}
