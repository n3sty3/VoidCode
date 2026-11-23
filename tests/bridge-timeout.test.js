// Node unit test: a bridge request that gets no host response must resolve an
// Error object ({ ok:false, error:'timeout', action }) - never a silent null.
// The nonce now arrives over the host handshake, so outbound messages wait in
// The bridge queue until it does instead of being posted with an empty nonce.
// Run with: node tests/bridge-timeout.test.js
import assert from 'node:assert';
import { installBridge } from '../wwwroot/core/bridge.js';

const sent = [];
const listeners = [];
// So the test installs it against a fake
// Global instead of evaluating its source in a vm context. No `location` is provided on
// Purpose: the bridge must not read the page URL any more.
const fakeGlobal = {
  setTimeout,
  clearTimeout,
  console,
  chrome: {
    webview: {
      postMessage: (m) => sent.push(m),
      addEventListener(t, fn) { if (t === 'message') listeners.push(fn); },
      removeEventListener() {}
    }
  }
};

const VoidBridge = installBridge(fakeGlobal);
assert.ok(VoidBridge, 'installBridge should publish VoidBridge');

// Nothing may leave the page before the nonce lands, and the page URL is not consulted any more.
VoidBridge.subscribe(() => {});
VoidBridge.send('pre_handshake', { id: 'pre_1' });
const pending = VoidBridge.request('nobody_home', {}, 40);
assert.equal(sent.length, 0, 'nothing may be posted before the handshake');
assert.equal('location' in fakeGlobal, false, 'the bridge must not need the page URL any more');

// Foreign event.source must be ignored (must never adopt a spoofed nonce)
listeners.forEach((fn) => fn({ data: JSON.stringify({ action: 'bridge_handshake', payload: { nonce: 'spoofed' } }), source: { foreign: true } }));
assert.equal(sent.length, 0, 'foreign event.source must be rejected');

// Real WebView2 sets event.source === window.chrome.webview on host messages
listeners.forEach((fn) => fn({ data: JSON.stringify({ action: 'bridge_handshake', payload: { nonce: 'unit-nonce' } }), source: fakeGlobal.chrome.webview }));
await VoidBridge.ready;

assert.equal(sent.length, 2, 'the handshake flushes everything that was waiting, in order');
assert.equal(JSON.parse(sent[0]).action, 'pre_handshake');
assert.equal(JSON.parse(sent[1]).action, 'nobody_home');
assert.equal(JSON.parse(sent[0]).bridgeNonce, 'unit-nonce', 'flushed messages carry the handshake nonce');

const res = await pending;

assert.equal(res && res.ok, false, 'a timeout must carry ok:false');
assert.equal(res.error, 'timeout', 'a timeout must carry error:"timeout"');
assert.equal(res.action, 'nobody_home', 'a timeout must name the action');

console.log('PASS: bridge timeout resolves { ok:false, error:"timeout", action } and queues until the handshake');
