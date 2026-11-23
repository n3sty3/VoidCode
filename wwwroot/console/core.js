/* VoidShell core: state, path handling, rendering, host bridge, command context. */
(function () {
  'use strict';
  const S = window.VoidShell;
  const state = { cwd: '', history: [], hIndex: -1, draft: '' };
  const el = { root: null, log: null, prompt: null, input: null };

  function workspacePath() {
    // Read the state store instead of peeking at app.js's global.
    try {
      const workspace = window.VoidState && window.VoidState.get ? window.VoidState.get('workspace') : '';
      return typeof workspace === 'string' ? workspace : '';
    } catch { return ''; }
  }

  function norm(p) { return String(p || '').replace(/\//g, '\\').replace(/\\+$/, ''); }

  function resolvePath(p) {
    let value = String(p == null ? '' : p).trim();
    if (!value) return state.cwd;
    if (value === '~') return workspacePath() || state.cwd;
    if (value.indexOf('~/') === 0 || value.indexOf('~\\') === 0) value = (workspacePath() || state.cwd) + value.slice(1);
    if (/^[a-zA-Z]:/.test(value) || value.indexOf('\\\\') === 0) return norm(value);
    const base = state.cwd || workspacePath();
    return norm(base.replace(/\\+$/, '') + '\\' + value.replace(/^[\\/]+/, ''));
  }

  function displayPath(p) {
    const root = workspacePath();
    if (root && p && p.toLowerCase().indexOf(root.toLowerCase()) === 0) {
      const rest = p.slice(root.length);
      return rest ? '.' + rest : '.';
    }
    return p;
  }

  function appendLine(text, kind) {
    if (!el.log) return null;
    const line = document.createElement('div');
    line.className = 'vc-line' + (kind ? ' vc-line--' + kind : '');
    line.textContent = text == null ? '' : String(text);
    el.log.appendChild(line);
    el.log.scrollTop = el.log.scrollHeight;
    return line;
  }

  function appendBlock(text, kind) {
    const value = text == null ? '' : String(text);
    if (!value) return;
    value.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n').forEach(function (line) {
      appendLine(line, kind);
    });
  }

  function refreshPrompt() {
    if (el.prompt) el.prompt.textContent = 'void \u276f ' + displayPath(state.cwd);
  }

  function pushHistory(line) {
    if (!line) return;
    if (state.history[0] !== line) state.history.unshift(line);
    if (state.history.length > 200) state.history.pop();
    state.hIndex = -1;
    state.draft = '';
  }

  function request(action, payload, timeout) {
    return window.VoidBridge.request(action, payload, timeout || 8000).then(function (res) {
      // A host timeout is a visible error, never a silent null.
      if (res && res.error === 'timeout') {
        if (typeof showToast === 'function') showToast('Host did not respond: ' + action, 'error');
        // The line now carries the fix - the same request again - so a timeout no longer
        // Leaves the user retyping a command that was already correct.
        const line = appendLine('void: no response from host for ' + action, 'err');
        if (line) {
          const retry = document.createElement('button');
          retry.type = 'button';
          retry.className = 'btn btn--quiet btn--xs';
          retry.textContent = 'Retry';
          retry.addEventListener('click', function () {
            retry.remove();
            request(action, payload, timeout);
          });
          line.appendChild(retry);
        }
      }
      return res;
    });
  }

  const fs = {
    list: function (p) { return request('list_dir', { path: p }, 8000); },
    read: function (p) { return request('read_text_file', { path: p }, 10000); },
    write: function (p, c) { return request('fs_write', { path: p, content: c }, 10000); },
    mkdir: function (p) { return request('create_entry', { path: p, isDir: true }, 8000); },
    touch: function (p) { return request('create_entry', { path: p, isDir: false }, 8000); },
    remove: function (p) { return request('delete_entry', { path: p }, 8000); }
  };

  function spawn(file, args, cwd, stdin) {
    return request('shell_spawn', { file: file, args: args, cwd: cwd, stdin: stdin == null ? null : stdin }, 120000);
  }

  function makeCtx(argv, options) {
    const opts = options || {};
    const renderLive = opts.render !== false;
    const buffer = [];
    const ctx = {
      name: argv[0],
      args: argv.slice(1),
      cwd: state.cwd,
      root: workspacePath(),
      stdin: opts.stdin || '',
      history: state.history.slice(),
      resolve: resolvePath,
      display: displayPath,
      isPiped: opts.piped === true,
      fs: fs,
      print: function (text, kind) {
        const value = text == null ? '' : String(text);
        buffer.push(value);
        if (renderLive) appendBlock(value, kind === 'err' ? 'err' : (kind || null));
      },
      err: function (text) { ctx.print(text, 'err'); },
      clear: function () { if (el.log) setHtml(el.log, ''); },
      setCwd: function (p) { state.cwd = norm(p); refreshPrompt(); },
      openFile: function (p) { try { window.VoidBridge.send('read_file', { path: p }); } catch (e) { logSwallowed(e, "console.openFile"); } },
      setTheme: function (name) {
        try {
          const settings = window.VoidState.get('settings') || {};
          if (typeof window.applyThemeAndOptics === 'function') {
            window.applyThemeAndOptics(name, settings.GlassBlur, settings.FieldOpacity, true);
            return true;
          }
          return false;
        } catch (err) { logSwallowed(err, "console.setTheme"); return false; }
      },
      themes: ['harbor', 'obsidian', 'crimson', 'emerald', 'nebula'],
      close: function () { try { toggleWorkspacePane('terminal', false); } catch (e) { logSwallowed(e, "console.close"); } },
      _buffer: buffer
    };
    ctx.stdout = function () { return buffer.join('\n'); };
    return ctx;
  }

  S.core = { state: state, el: el, workspacePath: workspacePath, norm: norm, resolvePath: resolvePath,
    displayPath: displayPath, appendLine: appendLine, appendBlock: appendBlock, refreshPrompt: refreshPrompt,
    pushHistory: pushHistory, fs: fs, spawn: spawn, makeCtx: makeCtx, request: request };
})();