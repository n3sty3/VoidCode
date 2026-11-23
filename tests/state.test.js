// Node unit test: the state store contract (get/set/subscribe/update/snapshot) and
// The temporary global accessor that keeps the classic scripts working while a slice moves.
// Run with: node tests/state.test.js
import assert from 'node:assert';
import { bindGlobalSlice, get, set, snapshot, subscribe, update } from '../wwwroot/core/state.js';

// Get with a fallback, and plain set/get
assert.equal(get('never-set', 'fallback'), 'fallback');
set('t', 1);
assert.equal(get('t'), 1);

// Subscribers are told (value, previous) and can be removed
const seen = [];
const off = subscribe('t', (value, previous) => seen.push([value, previous]));
set('t', 2);
assert.deepEqual(seen, [[2, 1]]);
off();
set('t', 3);
assert.equal(seen.length, 1, 'unsubscribing stops notifications');

// An identical value does not notify, a different one does
set('obj', { a: 1 });
let hits = 0;
subscribe('obj', () => { hits += 1; });
set('obj', get('obj'));
assert.equal(hits, 0, 'setting the same reference must not notify');
set('obj', { a: 2 });
assert.equal(hits, 1);

// Update goes through the store
update('n', (value) => (value || 0) + 1);
update('n', (value) => value + 1);
assert.equal(get('n'), 2);

// A throwing subscriber cannot break the setter
subscribe('bad', () => { throw new Error('boom'); });
assert.doesNotThrow(() => set('bad', 1));

// Snapshot exposes the whole store
set('snap', 'yes');
assert.equal(snapshot().snap, 'yes');

// The accessor the migrated slices use: writes land in the store, reads come from it
bindGlobalSlice('demo', 'demoSliceGlobal');
globalThis.demoSliceGlobal = 'from-app-js';
assert.equal(get('demo'), 'from-app-js');
set('demo', 'from-store');
assert.equal(globalThis.demoSliceGlobal, 'from-store');

// The real workspace slice is already migrated and published under its old name
assert.equal(get('workspace'), null);
assert.equal(globalThis.currentWorkspace, null);
globalThis.currentWorkspace = 'C:\\proj';
assert.equal(get('workspace'), 'C:\\proj');

// The settings slice is migrated too: it is an object held by reference, so property writes work
assert.deepEqual(get('settings'), {});
globalThis.appSettings.Theme = 'crimson';
assert.equal(get('settings').Theme, 'crimson', 'property writes land on the store object');
set('settings', { Theme: 'nebula' });
assert.equal(globalThis.appSettings.Theme, 'nebula', 'a replaced value is visible through the global');

// The activeTab slice: the open-tab descriptor, replaced as a whole when the user switches tabs
assert.equal(get('activeTab'), null);
globalThis.activeTab = { path: 'C:\\proj\\a.cs', name: 'a.cs', dirty: false };
assert.equal(get('activeTab').name, 'a.cs', 'the tab written through the global is in the store');
set('activeTab', { path: 'C:\\proj\\b.cs', name: 'b.cs', dirty: false });
assert.equal(globalThis.activeTab.name, 'b.cs', 'switching tabs through the store is visible globally');

// The agent slice: history is appended to in place, the mode and flag are plain values
assert.deepEqual(get('chatHistory'), []);
get('chatHistory').push({ role: 'user', content: 'hello' });
assert.equal(get('chatHistory').length, 1, 'the history array is held by reference');
assert.equal(get('currentAgentMode'), 'act');
globalThis.currentAgentMode = 'plan';
assert.equal(get('currentAgentMode'), 'plan', 'the mode written through the global is in the store');
assert.equal(get('agentAutoScroll'), true);
set('agentAutoScroll', false);
assert.equal(globalThis.agentAutoScroll, false, 'the flag replaced in the store is visible globally');

// Tabs + layout: no cross-file readers, but they live here now all the same
assert.deepEqual(get('openTabs'), []);
get('openTabs').push({ path: 'C:\\proj\\a.cs', name: 'a.cs', dirty: false });
assert.equal(globalThis.openTabs.length, 1, 'the tab list is held by reference');
assert.equal(get('activeSidebarView'), 'files');
globalThis.activeSidebarView = 'git';
assert.equal(get('activeSidebarView'), 'git', 'the layout slice follows the global accessor');

console.log('PASS: state store get/set/subscribe/update + global slice accessors behave');