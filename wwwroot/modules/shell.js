// Lazy VoidShell: the fallback console's six scripts, plus the facade the rest of the app calls
// ().
//
// Before this step every start loaded 20 console/ scripts - the icon pack, the registry, 14 command
// Files, the parser, core, the executor and the UI - even though the shell is the fallback terminal
// And is only in front when the bottom panel is expanded. Now:
//   - the icon pack is modules/icons.js (the rail and the status bar need it eagerly);
//   - the 14 command files are one console/commands.js;
//   - nothing from console/ is requested until the console view is actually revealed.
//
// The callers never learn about that: app.js, modules/terminal.js and modules/git.js all go through
// Window.VoidConsole, so init() installs a facade under that name and the real ui.js replaces it
// Once the scripts land. Each method decides for itself:
//   - execute()/print() are what a user typed, so they always load the shell;
//   - focus()/boot() load only when the console view is in front (focusing a hidden input is a no-op);
//   - setWorkspace() is passive - it remembers the path and applies it when the shell arrives.
// Calls that arrive before the scripts finish are queued and replayed in order, so the `print` +
// `execute` pair in modules/git.js cannot overtake itself.
//
// The expand path belongs to modules/layout.js, which owns the collapsed state and calls
// EnsureVoidShell() when the panel is revealed.
import { trustedScriptUrl } from '../core/markdown.js';

// Load order is the order the old CLASSIC_SCRIPTS list used: the registry first (every command
// Registers into it), then the commands, parser, core, executor and finally the UI, which boots
// Itself once it sees the DOM.
const SHELL_SCRIPTS = [
  'console/registry.js',
  'console/commands.js',
  'console/parser.js',
  'console/core.js',
  'console/exec.js',
  'console/ui.js'
];

let loadPromise = null;
let pendingCalls = [];
let pendingWorkspace = null;

function panelVisible() {
  const panel = document.getElementById('terminal-panel');
  if (!panel) return false;
  if (panel.style.display === 'none') return false;
  return !panel.classList.contains('collapsed');
}

// The real API once console/ui.js has published it; the facade is what sits there until then.
function realConsole() {
  const api = window.VoidConsole;
  return api && api !== facade ? api : null;
}

function loadScript(src) {
  return new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = trustedScriptUrl(src); // a script URL is a Trusted Types sink
    script.async = false; // keep the declared order
    script.onload = () => resolve(src);
    script.onerror = () => reject(new Error(`Could not load ${src}`));
    document.body.appendChild(script);
  });
}

function flushPending() {
  const api = realConsole();
  if (!api) return;
  if (pendingWorkspace !== null) {
    const path = pendingWorkspace;
    pendingWorkspace = null;
    try { api.setWorkspace(path); } catch (err) { globalThis.logSwallowed?.(err, 'shell.setWorkspace'); }
  }
  const calls = pendingCalls;
  pendingCalls = [];
  calls.forEach(([method, args]) => {
    try { api[method](...args); } catch (err) { globalThis.logSwallowed?.(err, `shell.${method}`); }
  });
}

// Loads the shell once. A failed load is reported and forgotten, so the next call can retry.
export function ensureVoidShell() {
  if (loadPromise) return loadPromise;
  loadPromise = (async () => {
    for (const src of SHELL_SCRIPTS) await loadScript(src);
    flushPending();
    return window.VoidShell || null;
  })();
  loadPromise.catch((error) => {
    loadPromise = null;
    if (window.console && window.console.warn) window.console.warn('[VoidCode shell] could not load the fallback shell', error);
    globalThis.logSwallowed?.(error, 'shell.load');
  });
  return loadPromise;
}

function call(method, args) {
  const api = realConsole();
  if (api) return api[method](...args);
  pendingCalls.push([method, args]);
  return ensureVoidShell();
}

const facade = {
  lazy: true, // test/tooling marker: window.VoidConsole is the facade until console/ui.js lands
  boot() { return panelVisible() ? ensureVoidShell() : Promise.resolve(null); },
  focus() { return panelVisible() || realConsole() ? call('focus', []) : undefined; },
  setWorkspace(path) {
    const api = realConsole();
    if (!api) { pendingWorkspace = path; return; }
    try { api.setWorkspace(path); } catch (err) { globalThis.logSwallowed?.(err, 'shell.setWorkspace'); }
  },
  print(text, kind) { return call('print', [text, kind]); },
  execute(line) { return call('execute', [line]); }
};

export function init() {
  window.VoidConsole = facade;
  window.ensureVoidShell = ensureVoidShell;
  return facade;
}