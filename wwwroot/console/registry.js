/* VoidShell registry.
 * A command is a module: { name, description, run(args, ctx) }.
 * Adding a command = adding one window.VoidShell.register({...}) block to console/commands.js
 * (all 14 built-ins live in that one file since merged the per-command files, so the
 * lazy loader pulls the whole set in a single request). Nothing else needs to change. */
window.VoidShell = window.VoidShell || {};

(function (S) {
  'use strict';

  if (S.commands) return;

  S.commands = new Map();

  S.register = function register(command) {
    if (!command || !command.name || typeof command.run !== 'function') return;
    S.commands.set(command.name, command);
  };

  S.get = function get(name) {
    return S.commands.get(String(name || '').toLowerCase()) || null;
  };

  S.list = function list() {
    return Array.from(S.commands.values()).sort(function (a, b) {
      return a.name.localeCompare(b.name);
    });
  };

  /* every command module registers itself; this is where the boot happens once
     the DOM + scripts are ready */
  S.ready = false;
})(window.VoidShell);