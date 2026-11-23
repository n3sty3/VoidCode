// The host-event dispatcher.
//
// App.js used to answer every host message from one 86-case switch, so every feature in the app had
// To edit the middle of a single function. The dispatcher turns an action into a Map lookup: a
// Feature registers the actions it owns
// And an unknown action is reported once instead of disappearing into a switch with no default.

/**
 * the options object was untyped, so `onUnknown` read as a property of `{}`.
 * @param {{ onUnknown?: (action: string) => void }} [options]
 */
export function createHostEventDispatcher({ onUnknown } = {}) {
  const handlers = new Map();
  const unknown = new Set();

  const reportUnknown = typeof onUnknown === 'function'
    ? onUnknown
    : (action) => { console.debug('[VoidCode] unhandled host event:', action); };

  return {
    // On(action | [actions], handler) - the array form serves a group that shares one handler.
    on(actions, handler) {
      if (typeof handler !== 'function') throw new TypeError('host event handler must be a function');
      for (const action of Array.isArray(actions) ? actions : [actions]) {
        if (typeof action !== 'string' || !action) throw new TypeError('host event action must be a non-empty string');
        handlers.set(action, handler);
      }
    },

    // Register({ action: handler }) - what app.js's handler factory returns.
    register(map) {
      if (!map || typeof map !== 'object') return 0;
      let added = 0;
      for (const [action, handler] of Object.entries(map)) {
        if (typeof handler !== 'function') continue;
        handlers.set(action, handler);
        added += 1;
      }
      return added;
    },

    // Returns true when an action was handled, so a caller can react to the rest.
    dispatch(action, payload) {
      const handler = handlers.get(action);
      if (!handler) {
        if (!unknown.has(action)) {
          unknown.add(action);
          reportUnknown(action);
        }
        return false;
      }
      handler(payload, action);
      return true;
    },

    has(action) { return handlers.has(action); },
    actions() { return Array.from(handlers.keys()); },
    unknownActions() { return Array.from(unknown); },
    reset() { handlers.clear(); unknown.clear(); }
  };
}

export const hostEvents = createHostEventDispatcher();

// The app.js registers through window.VoidHostEvents; a module that owns host actions
// Can import the shared instance instead.
export function installHostEvents(global = globalThis) {
  global.VoidHostEvents = hostEvents;
  return hostEvents;
}