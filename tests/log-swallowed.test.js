// JS unit test: logSwallowed(err, context) is the sink that replaces empty JS
// Catch blocks. It must post a js_log bridge message and must never throw.
// Run with: node tests/log-swallowed.test.js
import assert from 'node:assert';
import { installBridge } from '../wwwroot/core/bridge.js';

const sent = [];
const listeners = [];
// The test installs it against a fake global.
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
const logSwallowed = fakeGlobal.logSwallowed;
assert.ok(VoidBridge && typeof logSwallowed === 'function', 'installBridge should publish VoidBridge and logSwallowed');

// The nonce arrives over the host handshake now, so nothing is posted before it
// Lands and the page URL is not read at all.
VoidBridge.subscribe(() => {});
assert.equal(sent.length, 0, 'nothing may leave the page before the handshake');
assert.equal('location' in fakeGlobal, false, 'the bridge must not need the page URL any more');
listeners.forEach((fn) => fn({ data: JSON.stringify({ action: 'bridge_handshake', payload: { nonce: 'unit-nonce' } }) }));
await VoidBridge.ready;

logSwallowed(new TypeError('bad thing happened'), 'UnitTest/where');

assert.equal(sent.length, 1, 'logSwallowed should post exactly one bridge message');
const payload = JSON.parse(sent[0]);
assert.equal(payload.action, 'js_log', 'the sink must use the js_log action');
assert.equal(payload.bridgeNonce, 'unit-nonce', 'every message carries the handshake nonce');
assert.equal(payload.context, 'UnitTest/where', 'the sink must record where');
assert.equal(payload.level, 'warn', 'a TypeError is reported at warn level');
assert.match(payload.msg, /bad thing happened/, 'the sink must record the error message');

// It must never throw, whatever it is handed.
const emitted = sent.length;
assert.doesNotThrow(() => logSwallowed(null, 'null-case'));
assert.doesNotThrow(() => logSwallowed(undefined));
assert.equal(sent.length, emitted + 2);

console.log('PASS: logSwallowed posts js_log { level, msg, context } and never throws');
