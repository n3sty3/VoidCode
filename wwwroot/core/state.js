// The state store.
//
// The slices that used to be free-floating globals in app.js (workspace, tabs, activeTab,
// Settings, layout, agent) live here, one slice at a time, with a tiny get/set/subscribe API so a
// Feature can react to a slice instead of reaching into another file's variable.
//
// Values are held by reference: `set` replaces a value and notifies, it does not deep-clone. That
// Keeps the migration honest - code that mutates an array in place keeps working while the slice
// Moves, and the slices that need change detection replace the value instead.
const entries = new Map();

function entryFor(name) {
  let found = entries.get(name);
  if (!found) {
    found = { value: undefined, subscribers: new Set() };
    entries.set(name, found);
  }
  return found;
}

function reportSubscriberError(error, name) {
  try {
    if (typeof globalThis.logSwallowed === 'function') globalThis.logSwallowed(error, `state.subscriber.${name}`);
  } catch { /* reporting must never break the setter */ }
}

export function get(name, fallback = undefined) {
  const found = entries.get(name);
  const value = found ? found.value : undefined;
  return value === undefined ? fallback : value;
}

export function set(name, value) {
  const found = entryFor(name);
  const previous = found.value;
  if (Object.is(previous, value)) return value;
  found.value = value;
  for (const handler of [...found.subscribers]) {
    try { handler(value, previous); }
    catch (error) { reportSubscriberError(error, name); }
  }
  return value;
}

export function update(name, mutator) {
  return set(name, mutator(get(name)));
}

export function subscribe(name, handler) {
  if (typeof handler !== 'function') return () => {};
  const found = entryFor(name);
  found.subscribers.add(handler);
  return () => { found.subscribers.delete(handler); };
}

export function snapshot() {
  const out = {};
  for (const [name, found] of entries) out[name] = found.value;
  return out;
}

// The temporary bridge for the not-yet-converted classic scripts: a slice can be published as a
// Global accessor, so `currentWorkspace` reads and writes keep working while the value lives in
// The store. remove these once the readers are modules.
export function bindGlobalSlice(name, globalName) {
  Object.defineProperty(globalThis, globalName, {
    configurable: true,
    enumerable: false,
    get: () => get(name),
    set: (value) => { set(name, value); }
  });
}

globalThis.VoidState = Object.freeze({ get, set, update, subscribe, snapshot });

// Slice 1/6: app.js keeps using the identifier; the value lives here and the
// Five module readers now ask the store (see PROGRESS for the remaining slice list).
set('workspace', null);
bindGlobalSlice('workspace', 'currentWorkspace');

// Slice 2/6: appSettings  The object is held by reference, so
// `appSettings.Theme = x` style mutation keeps working while the readers are converted.
set('settings', {});
bindGlobalSlice('settings', 'appSettings');

// Slice 3/6: the open-tab descriptor. app.js keeps the identifier; the 24 module
// Readers now ask the store (local alias `stateActiveTab`).
set('activeTab', null);
bindGlobalSlice('activeTab', 'activeTab');

// Slice 4/6: the conversation, the agent mode and the auto-scroll flag. The history
// Array is held by reference because the transcript is appended to in place.
set('chatHistory', []);
bindGlobalSlice('chatHistory', 'chatHistory');
set('currentAgentMode', 'act');
bindGlobalSlice('currentAgentMode', 'currentAgentMode');
set('agentAutoScroll', true);
bindGlobalSlice('agentAutoScroll', 'agentAutoScroll');

// Slice 5/6 and 6/6 (layout). Neither has cross-file readers left: the editor tab
// List is only touched by app.js apart from the autosave interval, and the sidebar view is
// App.js-internal. They move here so every slice in the plan starts life in one place.
set('openTabs', []);
bindGlobalSlice('openTabs', 'openTabs');
set('activeSidebarView', 'files');
bindGlobalSlice('activeSidebarView', 'activeSidebarView');

// The per-turn agent state for the same
// Reason - modules/agent-chat.js (a module, therefore strict) writes it when it
// Regenerates an answer, and a bare assignment to another file's `let` cannot work
// Across that boundary. app.js keeps its bare reads/writes through the accessor.
set('currentTurnEdits', []);
bindGlobalSlice('currentTurnEdits', 'currentTurnEdits');
set('lastRunMode', 'act');
bindGlobalSlice('lastRunMode', 'lastRunMode');

// The workspace file index. modules/explorer.js owns it now (it fills it from
// All_files_response), but quick open, the agent context and smart run still read it as
// AllWorkspaceFiles, so it stays a store slice published under the old name.
set('workspaceFiles', []);
bindGlobalSlice('workspaceFiles', 'allWorkspaceFiles');
set('workspaceFilesTruncated', false);
bindGlobalSlice('workspaceFilesTruncated', 'allWorkspaceFilesTruncated');