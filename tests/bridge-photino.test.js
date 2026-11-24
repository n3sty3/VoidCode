// Node unit test: macOS Photino/WKWebView bridge transport (`window.external.sendMessage` / `receiveMessage`)
// Announces `bridge_ready`, queues outbound calls until `bridge_handshake` arrives, and attaches the nonce.
import assert from 'node:assert';
import { installBridge } from '../wwwroot/core/bridge.js';

const sent = [];
let receiveCallback = null;
const fakeGlobal = {
  setTimeout,
  clearTimeout,
  console,
  external: {
    sendMessage: (m) => sent.push(m),
    receiveMessage: (fn) => { receiveCallback = fn; }
  }
};

const VoidBridge = installBridge(fakeGlobal);
assert.ok(VoidBridge, 'installBridge should publish VoidBridge on Photino');
assert.equal(typeof receiveCallback, 'function', 'Photino receiveMessage callback should be registered');
assert.equal(sent.length, 1, 'installBridge should send bridge_ready immediately on Photino');
assert.equal(JSON.parse(sent[0]).action, 'bridge_ready');

const received = [];
VoidBridge.subscribe((action, payload) => received.push({ action, payload }));
VoidBridge.send('get_init_state', { id: 'req_mac_1' });
assert.equal(sent.length, 1, 'outbound messages must stay queued until handshake nonce arrives');

receiveCallback(JSON.stringify({ action: 'bridge_handshake', payload: { nonce: 'mac-nonce-42' } }));
await VoidBridge.ready;

assert.equal(sent.length, 2, 'queued message flushes after handshake');
const flushed = JSON.parse(sent[1]);
assert.equal(flushed.action, 'get_init_state');
assert.equal(flushed.bridgeNonce, 'mac-nonce-42');

receiveCallback(JSON.stringify({ action: 'toast', payload: { type: 'info', message: 'hello mac' } }));
assert.equal(received.length, 1);
assert.equal(received[0].action, 'toast');
assert.equal(received[0].payload.message, 'hello mac');

// Typed envelope responses (`{ id, ok, data }`) resolve pending VoidBridge.request() automatically.
const pendingEnv = VoidBridge.request('ai_inline_complete', { prefix: 'const x = ' }, 500);
const sentEnv = JSON.parse(sent[sent.length - 1]);
receiveCallback(JSON.stringify({ action: 'ai_inline_complete_response', payload: { id: sentEnv.id, ok: true, data: { completion: '42;' } } }));
const resolvedEnv = await pendingEnv;
assert.equal(resolvedEnv.ok, true);
assert.equal(resolvedEnv.data.completion, '42;');

console.log('PASS: Photino/macOS bridge transport handshakes, queues, dispatches, and resolves envelopes cleanly');
