// Agent Attention Signals: subtle Web Audio chimes, native OS desktop notifications when alt-tabbed,
// and compact inline Turn Change Summary strip ("N files changed  +X -Y  [Review]").
import { html, setHtml } from '../core/markdown.js';

/** @type {AudioContext | null} */
let audioCtx = null;

function getSettings() {
  return window.VoidState?.get?.('settings') || {};
}

function playTone(freqs, durationSec = 0.14) {
  const s = getSettings();
  if (s.AgentSoundEnabled === false) return;
  const vol = Math.max(0, Math.min(1, (Number(s.AgentSoundVolume ?? 65) / 100) * 0.18));
  if (vol <= 0.001) return;

  try {
    const Ctx = window.AudioContext || /** @type {any} */ (window).webkitAudioContext;
    if (!Ctx) return;
    if (!audioCtx) audioCtx = new Ctx();
    if (audioCtx.state === 'suspended') audioCtx.resume().catch(() => { /* user gesture required */ });

    const now = audioCtx.currentTime;
    freqs.forEach((freq, idx) => {
      if (!audioCtx) return;
      const osc = audioCtx.createOscillator();
      const gain = audioCtx.createGain();
      osc.type = 'sine';
      osc.frequency.setValueAtTime(freq, now + idx * 0.085);
      gain.gain.setValueAtTime(0.0001, now + idx * 0.085);
      gain.gain.exponentialRampToValueAtTime(vol, now + idx * 0.085 + 0.015);
      gain.gain.exponentialRampToValueAtTime(0.0001, now + idx * 0.085 + durationSec);
      osc.connect(gain);
      gain.connect(audioCtx.destination);
      osc.start(now + idx * 0.085);
      osc.stop(now + idx * 0.085 + durationSec + 0.02);
    });
  } catch (err) {
    globalThis.logSwallowed?.(err, 'agent-signals.playTone');
  }
}

function notifyDesktop(title, body) {
  const s = getSettings();
  if (s.AgentDesktopNotifications === false) return;
  if (!document.hidden) return;
  if (typeof Notification === 'undefined') return;

  try {
    if (Notification.permission === 'granted') {
      new Notification(title, { body });
    } else if (Notification.permission === 'default') {
      Notification.requestPermission().then((perm) => {
        if (perm === 'granted') new Notification(title, { body });
      }).catch(() => { /* ignore permission decline */ });
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'agent-signals.notifyDesktop');
  }
}

function renderCompactTurnStrip(turnSummary) {
  if (!turnSummary || !Array.isArray(turnSummary.files) || turnSummary.files.length === 0) return;
  const container = document.getElementById('agent-chat-container');
  if (!container) return;

  const files = turnSummary.files.filter((f) => f && !f.reverted);
  if (files.length === 0) return;

  const totalAdded = files.reduce((acc, f) => acc + (Number(f.addedLines) || 0), 0);
  const totalRemoved = files.reduce((acc, f) => acc + (Number(f.removedLines) || 0), 0);

  const strip = document.createElement('div');
  strip.className = 'agent-compact-turn-strip';
  strip.style.cssText =
    'display:flex;align-items:center;justify-content:space-between;gap:10px;padding:7px 11px;' +
    'border-radius:10px;border:1px solid var(--seam);background:rgba(255,255,255,0.03);font-size:12px;margin:4px 0 8px;';

  setHtml(strip, html`
    <div style="display:flex;align-items:center;gap:8px;min-width:0;">
      <span style="font-weight:600;color:var(--text);">${files.length} file${files.length === 1 ? '' : 's'} changed</span>
      ${totalAdded > 0 ? html`<span style="font-family:var(--font-code);font-size:11px;color:#6be2b3;">+${totalAdded}</span>` : ''}
      ${totalRemoved > 0 ? html`<span style="font-family:var(--font-code);font-size:11px;color:var(--coral);">-${totalRemoved}</span>` : ''}
    </div>
    <button type="button" class="btn btn--quiet btn--xs" data-act="open-diff" style="white-space:nowrap;flex-shrink:0;">Review</button>
  `);

  strip.querySelector('[data-act="open-diff"]')?.addEventListener('click', () => {
    const first = files[0];
    if (first?.path && turnSummary.turnId) {
      globalThis.VoidBridge?.send?.('get_turn_file_diff', { turnId: turnSummary.turnId, path: first.path });
    }
  });

  container.appendChild(strip);
  globalThis.scrollAgentToBottom?.();
}

export function playTestChime() {
  playTone([587.33, 880], 0.16);
}

export function init() {
  window.VoidBridge?.subscribe?.((action, payload) => {
    if (!action) return;
    const p = payload || {};

    if (action === 'agent_tool_approval_needed') {
      playTone([523.25, 659.25], 0.14);
      const toolName = p.tool || 'action';
      notifyDesktop('VoidAgent needs your approval', `Review ${toolName} before VoidAgent continues.`);
    } else if (action === 'agent_done') {
      const elapsed = Number(p.elapsedMs || 0);
      if (elapsed >= 3500 || document.hidden) {
        playTone([587.33, 880], 0.16);
        const steps = p.steps ? `${p.steps} step${p.steps === 1 ? '' : 's'}` : 'Task complete';
        notifyDesktop('VoidAgent finished', `${steps} completed.`);
      }
      if (p.turnSummary) {
        renderCompactTurnStrip(p.turnSummary);
      }
    }
  });

  const api = { playTestChime };
  Object.assign(globalThis, api);
  return api;
}
