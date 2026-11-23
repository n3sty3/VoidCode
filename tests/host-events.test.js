// Node unit test: the dispatcher that replaced app.js's 86-case switch.
import assert from 'node:assert';
import { createHostEventDispatcher, hostEvents, installHostEvents } from '../wwwroot/core/host-events.js';

// Single action: the handler gets (payload, action)
const seen = [];
const d = createHostEventDispatcher({ onUnknown: () => {} });
d.on('one', (payload, action) => seen.push([payload, action]));
assert.equal(d.dispatch('one', { a: 1 }), true);
assert.deepEqual(seen, [[{ a: 1 }, 'one']]);

// The array form serves a group that shares one handler
let grouped = 0;
d.on(['g1', 'g2', 'g3'], () => { grouped += 1; });
for (const a of ['g1', 'g2', 'g3']) assert.equal(d.dispatch(a, null), true);
assert.equal(grouped, 3);

// Register() takes the { action: handler } object app.js's factory returns
const added = d.register({ r1: () => {}, r2: () => {}, notAFunction: 42 });
assert.equal(added, 2, 'non-function entries are ignored');
assert.equal(d.has('r1'), true);
assert.equal(d.has('notAFunction'), false);
assert.equal(d.register(null), 0);

// An unknown action is reported once, not on every message the host sends
const unknown = [];
const u = createHostEventDispatcher({ onUnknown: (action) => unknown.push(action) });
assert.equal(u.dispatch('nope', {}), false);
assert.equal(u.dispatch('nope', {}), false);
assert.deepEqual(unknown, ['nope'], 'a missing handler is reported once');
assert.deepEqual(u.unknownActions(), ['nope']);

// A throwing handler propagates: the switch behaved the same way, and the bridge logs it
const t = createHostEventDispatcher({ onUnknown: () => {} });
t.on('boom', () => { throw new Error('handler blew up'); });
assert.throws(() => t.dispatch('boom', {}), /handler blew up/);

// Bad registrations fail loudly instead of silently dropping an action
assert.throws(() => t.on('x', null), TypeError);
assert.throws(() => t.on('', () => {}), TypeError);

// Introspection + reset
assert.deepEqual(t.actions(), ['boom'], 'only successfully registered actions are listed');
t.on('y', () => {});
assert.ok(t.actions().includes('y'));
t.reset();
assert.deepEqual(t.actions(), []);

// The shared instance is installed for the classic app.js
const fake = {};
installHostEvents(fake);
assert.equal(fake.VoidHostEvents, hostEvents, 'window.VoidHostEvents is the shared dispatcher');
assert.equal(typeof hostEvents.register, 'function');
assert.equal(typeof hostEvents.dispatch, 'function');

console.log('PASS: host-event dispatcher register/dispatch/group/unknown behave');