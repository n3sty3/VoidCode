// InstallBridge(target) runs the old IIFE body
// Against any global-like object: main.js calls it with the real global, and the unit tests call
// It with a fake one. It keeps publishing window.VoidBridge / window.logSwallowed as the
// Temporary shim for the classic scripts that have not been converted yet.
export function installBridge(global = globalThis) {
  // (modules are already strict, so the old 'use strict' directive is not needed here)

  const pending = new Map();

  // The nonce is no longer read from location.search. The host pushes it once,
  // After navigation, into this closure - so it is not in the URL, not on window, not in the
  // DOM, and no other frame or origin can read or forge it. Messages posted before the
  // Handshake arrive wait in `queued` rather than travelling with an empty nonce the host
  // Would reject.
  let nonce = '';
  const queued = [];
  let resolveHandshake = null;
  const handshakeReady = new Promise((resolve) => { resolveHandshake = resolve; });

  function createRequestId() {
    return `req_${crypto.randomUUID ? crypto.randomUUID() : Math.random().toString(36).slice(2)}`;
  }

  function post(action, data) {
    if (!global.chrome?.webview) return;
    if (!nonce) { queued.push([action, data]); return; }
    global.chrome.webview.postMessage(JSON.stringify({ action, id: data.id, ...data, bridgeNonce: nonce }));
  }

  function flush() {
    while (queued.length) {
      const [action, data] = queued.shift();
      post(action, data);
    }
  }

  function adoptNonce(payload) {
    if (nonce) return; // the host hands it over exactly once
    const value = payload && typeof payload.nonce === 'string' ? payload.nonce : '';
    if (!value) return;
    nonce = value;
    flush();
    if (resolveHandshake) { resolveHandshake(); resolveHandshake = null; }
  }

  function send(action, data = {}) {
    if (!/^[a-z][a-z0-9_]*$/i.test(action)) throw new Error(`Invalid bridge action: ${action}`);
    const id = data.id || createRequestId();
    post(action, { ...data, id });
    return id;
  }

  function request(action, data = {}, timeoutMs = 4000) {
    return new Promise((resolve) => {
      const id = createRequestId();
      const timer = global.setTimeout(() => {
        if (!pending.delete(id)) return;
        // A timeout is an error object, never a silent null, so callers
        // Surface it instead of no-oping.
        if (global.console && global.console.error) {
          global.console.error(`[VoidCode Bridge] host did not respond to "${action}" within ${timeoutMs} ms`);
        }
        resolve({ ok: false, error: 'timeout', action });
      }, timeoutMs);

      pending.set(id, (result) => {
        global.clearTimeout(timer);
        resolve(result);
      });
      send(action, { ...data, id });
    });
  }

  function resolve(id, result) {
    if (!id || !pending.has(id)) return false;
    const callback = pending.get(id);
    pending.delete(id);
    callback(result);
    return true;
  }

  const subscribers = new Set();
  const earlyMessages = [];

  function onWebViewMessage(event) {
    try {
      // Only the WebView2 host drives the page. In Microsoft Edge WebView2,
      // Chrome.webview message events set event.source === window.chrome.webview (not window).
      // Reject any foreign Window source while accepting host-originated messages.
      const src = event?.source;
      if (src && src !== global && src !== global.chrome?.webview) return;
      const message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
      if (!message || typeof message.action !== 'string') return;
      // The handshake is consumed here and never dispatched to the app.
      if (message.action === 'bridge_handshake') { adoptNonce(message.payload); return; }
      if (subscribers.size === 0) {
        earlyMessages.push([message.action, message.payload]);
        return;
      }
      subscribers.forEach((handler) => handler(message.action, message.payload));
    } catch (error) {
      console.error('[VoidCode Bridge] Invalid host message', error);
    }
  }

  // Attach immediately during installBridge() so an early NavigationCompleted handshake
  // Posted before app.js finishes loading is never missed.
  if (global.chrome?.webview) {
    global.chrome.webview.addEventListener('message', onWebViewMessage);
  }

  function subscribe(handler) {
    if (!global.chrome?.webview || typeof handler !== 'function') return;
    subscribers.add(handler);
    if (earlyMessages.length > 0) {
      const backlog = earlyMessages.splice(0, earlyMessages.length);
      for (const [action, payload] of backlog) {
        try { handler(action, payload); }
        catch (error) { console.error('[VoidCode Bridge] Subscriber failed on queued message', error); }
      }
    }
  }

  // The single sink for errors we deliberately swallow. It never throws and
  // Never toasts; it records to the host log (action "js_log") so empty catch blocks have a
  // Real, greppable destination. It lives in core/bridge.js because that file is already
  // Loaded - extracts it to the planned wwwroot/core/log.js ES module once the index.html
  // Script-tag budget is replaced by modules.
  function logSwallowed(err, context) {
    try {
      const where = context || 'unknown';
      const message = err && err.stack ? String(err.stack)
        : (err && err.message ? String(err.message) : String(err));
      const level = err && err.name === 'TypeError' ? 'warn' : 'debug';
      send('js_log', { level, msg: message, context: where });
    } catch (e) {
      // Logging must never break the caller. Fall back to the local console.
      if (global.console && global.console.warn) global.console.warn('[VoidCode] logSwallowed failed', e);
    }
  }
  global.logSwallowed = logSwallowed;

  global.VoidBridge = Object.freeze({ send, request, resolve, subscribe, pending, ready: handshakeReady });
  return global.VoidBridge;
}
