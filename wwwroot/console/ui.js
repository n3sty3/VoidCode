/* VoidShell input surface: history, Tab completion, Ctrl+C / Ctrl+L, boot. */
(function () {
  'use strict';
  const S = window.VoidShell;
  const C = S.core;

  async function complete() {
    const input = C.el.input;
    const value = input.value;
    const caret = input.selectionStart == null ? value.length : input.selectionStart;
    const before = value.slice(0, caret);
    const match = /([^\s]*)$/.exec(before);
    const token = match ? match[1] : '';
    const firstWord = before.trim() === token;
    let candidates = [];

    if (firstWord) {
      candidates = S.list().map(function (c) { return c.name; })
        .filter(function (n) { return n.indexOf(token.toLowerCase()) === 0; });
    } else {
      const slash = Math.max(token.lastIndexOf('\\'), token.lastIndexOf('/'));
      const dirPart = slash >= 0 ? token.slice(0, slash + 1) : '';
      const base = dirPart ? C.resolvePath(dirPart) : C.state.cwd;
      const prefix = token.slice(dirPart.length).toLowerCase();
      const listing = await C.fs.list(base);
      candidates = ((listing && listing.entries) || [])
        .filter(function (e) { return String(e.name).toLowerCase().indexOf(prefix) === 0; })
        .map(function (e) { return dirPart + e.name + (e.isDir ? '\\' : ''); });
    }

    if (!candidates.length) return;
    if (candidates.length === 1) {
      const next = value.slice(0, caret - token.length) + candidates[0] + value.slice(caret);
      input.value = next;
      input.selectionStart = input.selectionEnd = next.length;
      return;
    }
    C.appendLine(candidates.join('   '), 'dim');
  }

  function wire() {
    const input = C.el.input;

    input.addEventListener('keydown', function (event) {
      if (event.key === 'Enter') {
        event.preventDefault();
        const line = input.value;
        input.value = '';
        C.appendLine('void \u276f ' + C.displayPath(C.state.cwd) + ' ' + line, 'cmd');
        S.execute(line).then(function () {
          if (C.el.log) C.el.log.scrollTop = C.el.log.scrollHeight;
          input.focus();
        });
        return;
      }

      if (event.key === 'ArrowUp') {
        event.preventDefault();
        if (!C.state.history.length) return;
        if (C.state.hIndex === -1) C.state.draft = input.value;
        C.state.hIndex = Math.min(C.state.hIndex + 1, C.state.history.length - 1);
        input.value = C.state.history[C.state.hIndex];
        input.selectionStart = input.selectionEnd = input.value.length;
        return;
      }

      if (event.key === 'ArrowDown') {
        event.preventDefault();
        if (C.state.hIndex === -1) return;
        C.state.hIndex -= 1;
        input.value = C.state.hIndex === -1 ? C.state.draft : C.state.history[C.state.hIndex];
        input.selectionStart = input.selectionEnd = input.value.length;
        return;
      }

      if (event.key === 'Tab') { event.preventDefault(); complete(); return; }

      if (event.ctrlKey && (event.key === 'c' || event.key === 'C')) {
        event.preventDefault();
        C.appendLine('void \u276f ' + C.displayPath(C.state.cwd) + ' ' + input.value + '^C', 'dim');
        input.value = '';
        try { window.VoidBridge.send('shell_kill', {}); } catch (e) { logSwallowed(e, "console.shellKill"); }
        return;
      }

      if (event.ctrlKey && (event.key === 'l' || event.key === 'L')) {
        event.preventDefault();
        if (C.el.log) setHtml(C.el.log, '');
      }
    });

    if (C.el.root) {
      C.el.root.addEventListener('click', function (event) {
        if (event.target === C.el.root) input.focus();
      });
    }
  }

  function boot() {
    C.el.root = document.getElementById('void-console');
    C.el.log = document.getElementById('vc-log');
    C.el.prompt = document.getElementById('vc-prompt');
    C.el.input = document.getElementById('vc-input');

    if (!C.el.root || !C.el.log || !C.el.input) return;
    if (!C.state.cwd) C.state.cwd = C.workspacePath();
    C.refreshPrompt();

    if (!C.el.log.childElementCount) {
      C.appendLine('void shell \u2014 type help for commands', 'dim');
      C.appendLine('', 'dim');
    }

    wire();
    const input = C.el.input;
    setTimeout(function () { try { input.focus(); } catch (e) { logSwallowed(e, "console.focusInput"); } }, 30);
    S.ready = true;
  }

  window.VoidConsole = {
    boot: boot,
    focus: function () { if (C.el.input) C.el.input.focus(); },
    setWorkspace: function (path) { C.state.cwd = C.norm(path || C.workspacePath()); C.refreshPrompt(); },
    print: C.appendLine,
    execute: function (line) { return S.execute(line); }
  };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();
})();