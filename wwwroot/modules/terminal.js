// PTY terminal (xterm.js + ConPTY): state, theme, sessions, tabs, input, fit.
//
// Main.js calls init(), the entry points are exported + published on
// GlobalThis for the app.js, and the session list stays private: app.js used to reach
// Into terminalSessions/activeTerminalSessionId in eight places, and those either moved here (the
// Pty_output/pty_exit bridge cases, terminal streaming, the transcript reader) or now go through
// GetActiveTerminalSession()/sendToActiveTerminal().
import { html, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

// ==========================================
// REAL INTERACTIVE PTY TERMINAL (xterm.js + ConPTY)
// ==========================================
let terminalSessions = [];
let activeTerminalSessionId = 'term-1';
let termSessionCounter = 1;
let terminalResizeObserver = null;

// ==========================================
// TERMINAL THEME
// The PTY borrows the live glass palette instead of a hardcoded GitHub Dark
// Ramp, so switching themes restains the shell instead of leaving a blue
// Foreign object in a teal/crimson/emerald workspace.
// ==========================================
// A hidden probe element resolves a CSS custom property to a live color. It is created on first use
// Rather than at module evaluation: a module body runs before the app boots, and touching the DOM
// There would also make this file impossible to import outside a browser ().
let termColorProbe = null;
function colorProbe() {
  if (!termColorProbe) {
    termColorProbe = document.createElement('span');
    termColorProbe.setAttribute('aria-hidden', 'true');
    termColorProbe.style.cssText = 'position:fixed;left:-9999px;top:-9999px;width:0;height:0;pointer-events:none;';
  }
  return termColorProbe;
}

export function resolveThemeColor(token, fallback) {
  try {
    const probe = colorProbe();
    if (!probe.isConnected) document.body.appendChild(probe);
    probe.style.color = '';
    probe.style.color = `var(${token})`;
    const resolved = getComputedStyle(probe).color;
    return resolved && resolved !== 'rgba(0, 0, 0, 0)' ? resolved : fallback;
  } catch {
    return fallback;
  }
}

// Token -> [css custom property, fallback for older engines]
const XTERM_THEME_TOKENS = {
  foreground: ['--term-fg', '#e7edf2'],
  cursor: ['--term-cursor', '#8fd3c1'],
  cursorAccent: ['--term-cursor-accent', '#0b1f1b'],
  selectionBackground: ['--term-selection', 'rgba(143, 211, 193, 0.26)'],
  black: ['--term-black', '#2b3547'],
  red: ['--term-red', '#f2918a'],
  green: ['--term-green', '#9ed7b6'],
  yellow: ['--term-yellow', '#e9b872'],
  blue: ['--term-blue', '#5f93b8'],
  magenta: ['--term-magenta', '#b485a6'],
  cyan: ['--term-cyan', '#8fd3c1'],
  white: ['--term-white', '#a9b6c2'],
  brightBlack: ['--term-bright-black', '#75849a'],
  brightRed: ['--term-bright-red', '#ffb4ae'],
  brightGreen: ['--term-bright-green', '#c3e8d5'],
  brightYellow: ['--term-bright-yellow', '#f5d2a3'],
  brightBlue: ['--term-bright-blue', '#b9cdd9'],
  brightMagenta: ['--term-bright-magenta', '#e0bccc'],
  brightCyan: ['--term-bright-cyan', '#d6f2ea'],
  brightWhite: ['--term-bright-white', '#f4f8fb']
};

export function buildXtermTheme() {
  const theme = { background: 'transparent' };
  Object.entries(XTERM_THEME_TOKENS).forEach(([key, [token, fallback]]) => {
    theme[key] = resolveThemeColor(token, fallback);
  });
  return theme;
}

// Xterm measures its cell size from the exact stack it renders with, so the
// Font token is resolved to a concrete stack instead of passing `var(...)`.
export function resolveCodeFontStack() {
  const token = getComputedStyle(document.documentElement).getPropertyValue('--font-code').trim();
  return token ? `${token}, 'Cascadia Code', Consolas, monospace` : "'JetBrains Mono', Consolas, monospace";
}

export function refreshTerminalThemes() {
  try {
    const theme = buildXtermTheme();
    terminalSessions.forEach(session => {
      if (session?.term) session.term.options.theme = theme;
    });
  } catch (err) { logSwallowed(err, "terminal.applyTheme"); }
}

/* The console never shows a black void: a boot overlay sits over the host until
   the shell's first byte arrives, and turns into an honest "no output" state with
   a restart action if the PTY stays silent. */
let terminalBootTimer = null;

/* A short banner so the console opens as a designed surface instead of a bare
   prompt on black. Colors come from the live theme, so it restains with it. */
export function ansiFromCssColor(css) {
  const m = /rgba?\(\s*(\d+)[,\s]+(\d+)[,\s]+(\d+)/.exec(String(css || ''));
  return m ? `\x1b[38;2;${m[1]};${m[2]};${m[3]}m` : '';
}

export function buildConsoleBanner(sessionName) {
  const accent = ansiFromCssColor(resolveThemeColor('--glacier', '#8fd3c1'));
  const dim = ansiFromCssColor(resolveThemeColor('--text-faint', '#8d97a5'));
  const reset = '\x1b[0m';
  const name = String(sessionName || 'shell');
  return `${accent}\u25c6 VoidCode${reset} ${dim}\u00b7 ${name}${reset}\r\n` +
    `${dim}Ctrl+\` toggles this panel \u00b7 type exit to close the session${reset}\r\n\r\n`;
}

export function showTerminalBoot(shellName) {
  const boot = document.getElementById('terminal-boot');
  const label = document.getElementById('terminal-boot-label');
  const retry = document.getElementById('btn-terminal-restart');
  if (!boot) return;
  boot.hidden = false;
  boot.classList.remove('term-boot--stalled');
  if (label) {
    setHtml(label, html`Starting ${String(shellName || 'shell').replace(/\s*\d+$/, '')}<span class="term-boot-dots"><i></i><i></i><i></i></span>`);
  }
  if (retry) retry.hidden = true;
  clearTimeout(terminalBootTimer);
  terminalBootTimer = setTimeout(() => {
    const b = document.getElementById('terminal-boot');
    if (!b || b.hidden) return;
    b.classList.add('term-boot--stalled');
    const l = document.getElementById('terminal-boot-label');
    const r = document.getElementById('btn-terminal-restart');
    if (l) l.textContent = 'Shell has not produced output yet';
    if (r) r.hidden = false;
  }, 7000);
}

export function hideTerminalBoot() {
  const boot = document.getElementById('terminal-boot');
  if (boot) boot.hidden = true;
  clearTimeout(terminalBootTimer);
}

/* Warp-style blocks: the prompt becomes a block reporter. On each prompt it closes
   the previous block with a status chip (ok / err + duration), draws a rule, then
   opens the next block. PSReadLine's Enter handler stamps the start so the duration
   measures the command, not your typing. Session-local: the profile is untouched. */
const PS_PROMPT_BLOCKS = [
  '$global:__vct0 = $null;',
  '$global:__vclast = 0;',
  'function global:prompt {',
  '$ok = $?;',
  '$code = $LASTEXITCODE;',
  '$e = [char]27;',
  '$r = "${e}[0m";',
  '$dim = "${e}[38;2;141;151;165m";',
  '$ac = "${e}[38;2;143;211;193m";',
  '$good = "${e}[38;2;107;226;179m";',
  '$bad = "${e}[38;2;242;145;138m";',
  'if ($global:__vct0) {',
  '$dur = [math]::Round(((Get-Date) - $global:__vct0).TotalSeconds, 2);',
  '$global:__vct0 = $null;',
  'if ($ok -and ($code -eq 0 -or $null -eq $code)) {',
  '$good + "ok" + $r + " " + $dim + $dur.ToString() + "s" + $r',
  '} else {',
  '$bad + "err" + $r + " " + $dim + ([string]$code) + " in " + $dur.ToString() + "s" + $r',
  '};',
  '$w = 56;',
  'try { $cw = $Host.UI.RawUI.WindowSize.Width; if ($cw -gt 24 -and $cw -lt 240) { $w = $cw - 2 } } catch { $null = $_ };',
  '$dim + ("-" * $w) + $r',
  '};',
  '$p = (Get-Location).Path;',
  '$leaf = Split-Path -Leaf $p;',
  'if (-not $leaf) { $leaf = $p };',
  '"";',
  '$ac + $leaf + $r + " " + $dim + $p + $r;',
  '$ac + ">" + $r + " "',
  '};',
  'try { Set-PSReadLineKeyHandler -Key Enter -ScriptBlock { $global:__vct0 = Get-Date; [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine() } } catch { $null = $_ }'
].join(' ');

export function consoleStyle() {
  try { return localStorage.getItem('voidcode_console_style') === 'warp' ? 'warp' : 'vscode'; } catch { return 'vscode'; }
}

/* VS Code model: a plain, quiet prompt. No chips, no rules, no banner. */
const PS_PROMPT_SIMPLE = [
  'function global:prompt {',
  '$e = [char]27;',
  '$r = "${e}[0m";',
  '$dim = "${e}[38;2;141;151;165m";',
  '$ac = "${e}[38;2;143;211;193m";',
  '$p = (Get-Location).Path;',
  '$leaf = Split-Path -Leaf $p;',
  'if (-not $leaf) { $leaf = $p };',
  '$ac + $leaf + $r + " " + $dim + $p + $r + "`n" + $ac + ">" + $r + " "',
  '}',
  // Keep the shell's tab-completion and history behaviour intact
  'try { Set-PSReadLineKeyHandler -Key Enter -ScriptBlock { [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine() } } catch { $null = $_ }'
].join(' ');

export function applyConsolePrompt(id, shellType) {
  if (shellType !== 'powershell') return;
  const payload = consoleStyle() === 'warp' ? PS_PROMPT_BLOCKS : PS_PROMPT_SIMPLE;
  try { sendHost('pty_input', { id, data: payload + '\r' }); } catch (err) { logSwallowed(err, "terminal.applyConsolePrompt"); }
}

/* The prompt has to be installed after the shell is actually alive, and the
   install itself is visible: sending keystrokes to a PTY echoes them, and the
   shell prints its own banner first. So: install, then wipe the screen and ask
   for a fresh prompt. What you see is the styled prompt and nothing else. */
/* The console head shows live state the app already knows: the active shell, the
   workspace folder and the git branch. No shell calls, no latency. */
export function updateConsoleStatus() {
  const shellEl = document.getElementById('console-chip-shell');
  if (!shellEl) return;
  const wsEl = document.getElementById('console-chip-workspace');
  const brEl = document.getElementById('console-chip-branch');

  const sess = terminalSessions.find(s => s.id === activeTerminalSessionId) || terminalSessions[0];
  const shell = (sess && sess.shellType) || 'void';
  shellEl.textContent = shell === 'void' ? 'void' : (shell === 'powershell' ? 'PowerShell' : shell);

  const workspace = window.VoidState.get('workspace');
  const leaf = workspace ? (workspace.split(/[\\/]/).filter(Boolean).pop() || workspace) : '';
  if (wsEl) wsEl.textContent = leaf || 'no folder';

  if (brEl) {
    // The git status lives in modules/git.js; ask it instead of reading a global.
    const branch = globalThis.getGitStatus?.()?.branch || '';
    const show = !!branch && branch !== 'HEAD';
    brEl.textContent = show ? branch : '';
    brEl.hidden = !show;
  }
}

export function primeConsole(sess) {
  if (!sess || !sess.term || sess.primed) return;
  sess.primed = true;
  hideTerminalBoot();
  // The default prompt is installed by the host at spawn time
  // (wwwroot/console/init.ps1), so there is nothing to type and nothing to echo.
  // Only the Warp block reporter still needs injecting.
  if (sess.shellType !== 'powershell' || consoleStyle() !== 'warp') return;
  applyConsolePrompt(sess.id, sess.shellType);
  setTimeout(() => {
    try { sess.term.clear(); } catch (err) { logSwallowed(err, "terminal.promptClear"); }
    try { sendHost('pty_input', { id: sess.id, data: '\r' }); } catch (err) { logSwallowed(err, "terminal.promptEnterKey"); }
  }, 900);
}

export function restartActiveTerminal() {
  const sess = terminalSessions.find(s => s.id === activeTerminalSessionId) || terminalSessions[0];
  if (!sess || !sess.term) return;
  sendHost('kill_terminal', { id: sess.id });
  try { sess.term.reset(); } catch (err) { logSwallowed(err, "terminal.restartReset"); }
  if (consoleStyle() === 'warp') { try { sess.term.write(buildConsoleBanner(sess.name)); } catch (err) { logSwallowed(err, "terminal.restartBanner"); } }
  sess.gotOutput = false;
  sess.primed = false;
  showTerminalBoot(sess.name);
  sendHost('pty_init', {
    id: sess.id,
    cols: sess.term.cols || 80,
    rows: sess.term.rows || 24,
    shellType: sess.shellType
  });
}

export function createXtermSession(id, name, shellType = 'powershell') {
  const host = document.getElementById('terminal-host');
  const TermClass = typeof window.Terminal === 'function' ? window.Terminal : window.Terminal?.Terminal;
  const FitClass = typeof window.FitAddon === 'function' ? window.FitAddon : window.FitAddon?.FitAddon;

  sendHost('js_log', { msg: `createXtermSession: host=${!!host}, TermClass=${!!TermClass}, FitClass=${!!FitClass}` });

  if (!host || !TermClass) {
    const stub = { id, name, shellType, term: null, fitAddon: null, container: null };
    terminalSessions.push(stub);
    return stub;
  }

  const container = document.createElement('div');
  container.className = 'term-instance' + (id === activeTerminalSessionId ? '' : ' is-hidden');
  container.id = `term-instance-${id}`;
  host.appendChild(container);

  const term = new TermClass({
    theme: buildXtermTheme(),
    fontFamily: resolveCodeFontStack(),
    fontSize: 13,
    fontWeight: '400',
    // Only 400/500 of JetBrains Mono ship with the app — anything heavier is
    // Synthesised by the engine and smears the glyphs
    fontWeightBold: '500',
    lineHeight: 1.45,
    letterSpacing: 0,
    cursorBlink: true,
    cursorStyle: 'bar',
    cursorWidth: 2,
    cursorInactiveStyle: 'outline',
    convertEol: true,
    allowTransparency: true,
    scrollback: 5000
  });

  const fitAddon = FitClass ? new FitClass() : null;
  if (fitAddon) {
    term.loadAddon(fitAddon);
  }
  term.open(container);

  // The banner lands before the shell's first prompt, so the console opens designed
  if (consoleStyle() === 'warp') { try { term.write(buildConsoleBanner(name)); } catch (err) { logSwallowed(err, "terminal.createBanner"); } }

  term.onData(data => {
    sendHost('pty_input', { id, data });
  });

  term.attachCustomKeyEventHandler(e => {
    if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'c') {
      if (term.hasSelection()) {
        navigator.clipboard.writeText(term.getSelection());
        return false;
      }
    }
    if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'v') {
      navigator.clipboard.readText().then(text => {
        if (text) sendHost('pty_input', { id, data: text });
      });
      return false;
    }
    return true;
  });

  const session = {
    id,
    name: name || `PowerShell ${termSessionCounter}`,
    shellType,
    term,
    fitAddon,
    container
  };

  terminalSessions.push(session);

  // The console shows a boot state until the shell speaks
  if (id === activeTerminalSessionId) showTerminalBoot(session.name);

  requestAnimationFrame(() => {
    try {
      fitAddon.fit();
      sendHost('pty_init', {
        id,
        cols: term.cols || 80,
        rows: term.rows || 24,
        shellType
      });
      // Priming waits for the shell's first output (see pty_output): a guessed
      // Timer here sent keystrokes before the PTY existed and they were dropped
    } catch (err) {
      console.warn('Terminal fit error:', err);
    }
  });

  return session;
}

export function fitActiveTerminal() {
  const sess = terminalSessions.find(s => s.id === activeTerminalSessionId);
  if (sess && sess.fitAddon && sess.term) {
    try {
      sess.fitAddon.fit();
      sendHost('pty_resize', {
        id: sess.id,
        cols: sess.term.cols || 80,
        rows: sess.term.rows || 24
      });
    } catch (err) { logSwallowed(err, "terminal.resizeSessions"); }
  }
}

export function focusActiveTerminal() {
  const sess = terminalSessions.find(s => s.id === activeTerminalSessionId);
  sess?.term?.focus();
}

export function setupTerminalTabs() {
  document.getElementById('btn-add-terminal-tab')?.addEventListener('click', () => addTerminalTab('powershell'));

  const btnShellMenu = document.getElementById('btn-terminal-shell-menu');
  const shellMenu = document.getElementById('terminal-profile-menu');
  if (btnShellMenu && shellMenu) {
    btnShellMenu.addEventListener('click', (e) => {
      e.stopPropagation();
      const rect = btnShellMenu.getBoundingClientRect();
      shellMenu.style.top = `${rect.bottom + 4}px`;
      shellMenu.style.left = `${rect.left}px`;
      shellMenu.style.display = shellMenu.style.display === 'none' ? 'block' : 'none';
      btnShellMenu.setAttribute('aria-expanded', shellMenu.style.display === 'block' ? 'true' : 'false');
    });

    shellMenu.querySelectorAll('[data-shell-profile]').forEach(item => {
      item.addEventListener('click', () => {
        const profile = item.getAttribute('data-shell-profile') || 'powershell';
        shellMenu.style.display = 'none';
        btnShellMenu.setAttribute('aria-expanded', 'false');
        addTerminalTab(profile);
      });
    });

    window.addEventListener('click', (e) => {
      if (!/** @type {HTMLInputElement} */ (e.target).closest('#terminal-profile-menu') && e.target !== btnShellMenu) {
        shellMenu.style.display = 'none';
        btnShellMenu.setAttribute('aria-expanded', 'false');
      }
    });
  }

  const host = document.getElementById('terminal-host');
  if (host && window.ResizeObserver) {
    terminalResizeObserver = new ResizeObserver(() => {
      fitActiveTerminal();
    });
    terminalResizeObserver.observe(host);
  }

  // VoidShell loads lazily. modules/layout.js calls ensureVoidShell() when the panel
  // Is revealed, and window.VoidConsole is a facade until the scripts land - so there is nothing to
  // Boot here, and nothing from console/ is requested at start.

  // The panel always declares which view it is showing, from the first paint
  document.getElementById('terminal-panel')?.setAttribute('data-active-view', 'terminal');

  renderTerminalTabs();
}

export function renderTerminalTabs() {
  const list = document.getElementById('terminal-tabs-list');
  if (!list) return;
  list.replaceChildren();

  terminalSessions.forEach(session => {
    // A div can legally hold the close button; nesting a button inside a button
    // Is invalid and stole the tab's click target
    const tab = document.createElement('div');
    tab.className = 'term-session-tab' + (session.id === activeTerminalSessionId ? ' is-active' : '');
    tab.setAttribute('data-term-id', session.id);
    tab.setAttribute('role', 'tab');
    tab.setAttribute('tabindex', session.id === activeTerminalSessionId ? '0' : '-1');
    tab.setAttribute('aria-selected', session.id === activeTerminalSessionId ? 'true' : 'false');
    tab.title = session.name;

    const label = document.createElement('span');
    label.className = 'term-tab-label';
    label.textContent = session.name;
    tab.appendChild(label);

    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'term-tab-close';
    close.setAttribute('aria-label', `Close ${session.name}`);
    close.title = 'Close session';
    setHtml(close, '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18"/></svg>');
    close.addEventListener('click', (event) => {
      event.stopPropagation();
      closeTerminalTab(session.id);
    });
    tab.appendChild(close);

    tab.addEventListener('click', () => switchTerminalTab(session.id));
    tab.addEventListener('keydown', (event) => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        switchTerminalTab(session.id);
        return;
      }
      // Roving tabindex needs arrow keys, otherwise the inactive tabs are
      // Unreachable once they carry tabindex="-1"
      if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
        event.preventDefault();
        const ids = terminalSessions.map(s => s.id);
        const at = ids.indexOf(session.id);
        if (at < 0 || ids.length < 2) return;
        const step = event.key === 'ArrowRight' ? 1 : -1;
        const nextId = ids[(at + step + ids.length) % ids.length];
        switchTerminalTab(nextId);
        requestAnimationFrame(() => {
          document.querySelector(`.term-session-tab[data-term-id="${nextId}"]`)?.focus();
        });
      }
    });

    list.appendChild(tab);
  });

  // A lone session does not need a tab: the console bar stays a bar
  const strip = document.getElementById('terminal-tab-strip');
  if (strip) strip.classList.toggle('is-single', terminalSessions.length < 2);
  const addBtn = document.getElementById('btn-add-terminal-tab');
  if (addBtn) addBtn.hidden = false;
  updateConsoleStatus();
}

export function switchTerminalTab(termId) {
  activeTerminalSessionId = termId;

  terminalSessions.forEach(s => {
    if (s.id === termId) {
      s.container?.classList.remove('is-hidden');
      requestAnimationFrame(() => {
        try {
          s.fitAddon?.fit();
          sendHost('pty_resize', { id: s.id, cols: s.term.cols || 80, rows: s.term.rows || 24 });
          s.term?.focus();
        } catch { /* pane resize is best-effort */ }
      });
    } else {
      s.container?.classList.add('is-hidden');
    }
  });

  renderTerminalTabs();
}

export function addTerminalTab(shellType = 'powershell') {
  termSessionCounter++;
  const newId = `term-${termSessionCounter}`;
  let prefix = 'PowerShell ';
  if (shellType === 'cmd') prefix = 'CMD ';
  else if (shellType === 'bash') prefix = 'Bash ';
  else if (shellType === 'wsl') prefix = 'WSL ';
  const name = prefix + termSessionCounter;
  createXtermSession(newId, name, shellType);
  switchTerminalTab(newId);
  globalThis.showToast?.(`Opened ${name}`, 'info');
}

export function closeTerminalTab(termId) {
  const idx = terminalSessions.findIndex(s => s.id === termId);
  if (idx < 0) return;
  const [closed] = terminalSessions.splice(idx, 1);
  try {
    closed.term?.dispose();
    closed.container?.remove();
    sendHost('pty_kill', { id: termId });
  } catch { /* session already torn down */ }

  if (activeTerminalSessionId === termId) {
    const next = terminalSessions[Math.max(0, idx - 1)] || terminalSessions[0];
    if (next) switchTerminalTab(next.id);
    else addTerminalTab('powershell');
  } else {
    renderTerminalTabs();
  }
}

// ==========================================
// The surface app.js still needs (it used to read terminalSessions directly)
// ==========================================
export function getActiveTerminalSession() {
  return terminalSessions.find(s => s.id === activeTerminalSessionId) || terminalSessions[0] || null;
}

// Type into the live PTY session. Returns false when no session owns a terminal, so the caller can
// Fall back to the in-app console.
export function sendToActiveTerminal(text, withEnter = false) {
  const sess = getActiveTerminalSession();
  if (!sess || !sess.term) return false;
  sendHost('pty_input', { id: sess.id, data: withEnter ? `${text}\r` : text });
  sess.term.focus();
  return true;
}

export function killActiveTerminal() {
  if (activeTerminalSessionId) sendHost('kill_terminal', { id: activeTerminalSessionId });
}

export function clearActiveTerminal() {
  const sess = getActiveTerminalSession();
  if (sess && sess.term) sess.term.clear();
}

// Both cases only touch the xterm session.
// OSC 133 / OSC 633 shell integration tracker for command blocks.
const OSC_SHELL_RE = /\x1b\](133|633);([^\x07\x1b]*)(?:\x07|\x1b\\)/g;
const ANSI_STRIP_RE = /\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)/g;

export function parseOsc133Chunk(sess, rawChunk) {
  if (!sess || typeof rawChunk !== 'string' || !rawChunk) return;
  if (!Array.isArray(sess.commandBlocks)) sess.commandBlocks = [];

  let lastIndex = 0;
  OSC_SHELL_RE.lastIndex = 0;
  let match;
  while ((match = OSC_SHELL_RE.exec(rawChunk)) !== null) {
    const plain = rawChunk.slice(lastIndex, match.index).replace(ANSI_STRIP_RE, '');
    if (sess._activeBlock && plain) {
      if (sess._activeBlock.output.length < 8000) sess._activeBlock.output += plain;
    }
    lastIndex = OSC_SHELL_RE.lastIndex;

    const oscCode = match[1];
    const body = match[2] || '';
    if (oscCode === '633' && body.startsWith('E;')) {
      sess._pendingCommand = body.slice(2).trim();
    } else if (oscCode === '133') {
      const kind = body.charAt(0);
      if (kind === 'C') {
        sess._activeBlock = {
          id: `blk_${Date.now()}_${sess.commandBlocks.length}`,
          command: sess._pendingCommand || '(command)',
          exitCode: null,
          startedAt: Date.now(),
          finishedAt: null,
          output: ''
        };
        sess._pendingCommand = '';
      } else if (kind === 'D' && sess._activeBlock) {
        const parts = body.split(';');
        const parsedCode = parts.length > 1 ? parseInt(parts[1], 10) : 0;
        sess._activeBlock.exitCode = Number.isFinite(parsedCode) ? parsedCode : 0;
        sess._activeBlock.finishedAt = Date.now();
        sess.commandBlocks.push(sess._activeBlock);
        if (sess.commandBlocks.length > 50) sess.commandBlocks.shift();
        sess._activeBlock = null;
      }
    }
  }

  const tail = rawChunk.slice(lastIndex).replace(ANSI_STRIP_RE, '');
  if (sess._activeBlock && tail && sess._activeBlock.output.length < 8000) {
    sess._activeBlock.output += tail;
  }
}

export function getTerminalCommandBlocks(termId = null) {
  const sess = termId
    ? terminalSessions.find(s => s.id === termId)
    : getActiveTerminalSession();
  return sess && Array.isArray(sess.commandBlocks) ? sess.commandBlocks.slice() : [];
}

export function closeTerminalBlocksModal() {
  const modal = document.getElementById('terminal-blocks-modal');
  if (modal) modal.style.display = 'none';
}

export function openTerminalBlocksModal() {
  const modal = document.getElementById('terminal-blocks-modal');
  const list = document.getElementById('terminal-blocks-list');
  if (!modal || !list) return;
  modal.style.display = 'flex';
  setHtml(list, '');

  const blocks = getTerminalCommandBlocks().reverse();
  if (blocks.length === 0) {
    setHtml(list, html`<div class="empty-hint">No command blocks captured yet. Run a command in the integrated PowerShell terminal to see exit badges, copy output, or rerun blocks here.</div>`);
    return;
  }

  blocks.forEach(blk => {
    const row = document.createElement('div');
    row.className = 'inspector-row inspector-row--col';
    row.dataset.blockId = blk.id;

    const head = document.createElement('div');
    head.className = 'inspector-row-head';
    const ok = blk.exitCode === 0;
    const dur = blk.finishedAt && blk.startedAt ? `${((blk.finishedAt - blk.startedAt) / 1000).toFixed(1)}s` : '';
    setHtml(head, html`
      <div class="inspector-row-main" style="display:flex;align-items:center;gap:8px;">
        <span class="val-badge ${ok ? 'is-ok' : 'is-danger'}">${ok ? 'OK (0)' : `Exit ${blk.exitCode ?? '?'}`}</span>
        <code class="inspector-row-detail" style="color:var(--text);">${blk.command}</code>
        ${dur ? html`<span class="empty-hint" style="font-size:var(--fs-2xs);flex-shrink:0;">${dur}</span>` : ''}
      </div>
    `);

    const btns = document.createElement('div');
    btns.style.cssText = 'display:flex;gap:4px;flex-shrink:0;';

    const copyBtn = document.createElement('button');
    copyBtn.type = 'button';
    copyBtn.className = 'btn btn--quiet btn--xs';
    copyBtn.textContent = 'Copy';
    copyBtn.addEventListener('click', () => {
      navigator.clipboard?.writeText?.(blk.output.trim() || blk.command);
      globalThis.showToast?.('Copied command block', 'info');
    });

    const rerunBtn = document.createElement('button');
    rerunBtn.type = 'button';
    rerunBtn.className = 'btn btn--primary btn--xs';
    rerunBtn.dataset.rerunCommand = blk.command;
    rerunBtn.textContent = 'Rerun';
    rerunBtn.addEventListener('click', () => {
      closeTerminalBlocksModal();
      globalThis.toggleWorkspacePane?.('terminal', true);
      sendToActiveTerminal(blk.command, true);
    });

    if (!ok) {
      const fixBtn = document.createElement('button');
      fixBtn.type = 'button';
      fixBtn.className = 'btn btn--accent-soft btn--xs';
      fixBtn.dataset.fixCommand = blk.command;
      fixBtn.textContent = 'Fix with AI';
      fixBtn.addEventListener('click', () => {
        closeTerminalBlocksModal();
        const box = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
        if (box) {
          const outSnippet = (blk.output || '').trim().slice(0, 600);
          box.value = `Command \`${blk.command}\` failed with exit code ${blk.exitCode ?? '?'}.${outSnippet ? `\n\nTerminal output:\n\`\`\`\n${outSnippet}\n\`\`\`` : ''}\n\nDiagnose the failure and patch the issue.`;
          globalThis.toggleWorkspacePane?.('agent', true);
          box.focus();
        }
      });
      btns.append(fixBtn);
    }

    btns.append(copyBtn, rerunBtn);
    head.appendChild(btns);
    row.appendChild(head);

    if (blk.output && blk.output.trim()) {
      const pre = document.createElement('pre');
      pre.className = 'inspector-output';
      pre.style.cssText = 'margin:0;max-height:96px;overflow:auto;font-size:var(--fs-xs);';
      pre.textContent = blk.output.trim().slice(0, 600);
      row.appendChild(pre);
    }

    list.appendChild(row);
  });
}

export function handlePtyOutput(payload = {}) {
  const sess = terminalSessions.find(s => s.id === payload.id) || terminalSessions[0];
  if (sess && sess.term) {
    if (!sess.gotOutput) {
      sess.gotOutput = true;
      hideTerminalBoot();
      // The shell is alive: install the prompt now, not on a guessed timer
      setTimeout(() => primeConsole(sess), 120);
    }
    parseOsc133Chunk(sess, payload.data);
    sess.term.write(payload.data);
  }
  globalThis.recordTerminalOutput?.(payload.data, false);
}

export function handlePtyExit(payload = {}) {
  const sess = terminalSessions.find(s => s.id === payload.id);
  if (sess && sess.term) {
    sess.term.writeln(`\r\n\x1b[90m[Process exited with code ${payload.exitCode}]\x1b[0m\r\n`);
  }
  globalThis.handleTerminalCommandDone?.(payload.exitCode);
}

// Writes into the active session, else the plain DOM log.
export function appendTerminalStream(text, isErr) {
  const activeSess = getActiveTerminalSession();
  if (activeSess && activeSess.term) {
    activeSess.term.write(text);
    return;
  }
  const out = document.getElementById('terminal-output');
  if (!out) return;
  const span = document.createElement('span');
  if (isErr) span.className = 'term-err';
  span.textContent = text;
  out.appendChild(span);
  out.scrollTop = out.scrollHeight;
}

// The transcript the agent context sends with a request.
export function getActiveTerminalText(maxLines = 80) {
  const sess = getActiveTerminalSession();
  if (!sess || !sess.term || !sess.term.buffer || !sess.term.buffer.active) return '';
  const buf = sess.term.buffer.active;
  const lines = [];
  const start = Math.max(0, buf.length - maxLines);
  for (let i = start; i < buf.length; i++) {
    const line = buf.getLine(i);
    if (line) lines.push(line.translateToString(true));
  }
  return lines.join('\n').trim();
}

// Main.js calls this once; the entry points go on globalThis for the classic app.js
// (bridge switch, boot, palette, terminal buttons).
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  const wireBlocksModal = () => {
    const modal = document.getElementById('terminal-blocks-modal');
    if (!modal) return;
    document.getElementById('btn-close-terminal-blocks')?.addEventListener('click', closeTerminalBlocksModal);
    modal.addEventListener('click', (e) => { if (e.target === modal) closeTerminalBlocksModal(); });
    modal.addEventListener('keydown', (e) => { if (e.key === 'Escape') { e.stopPropagation(); closeTerminalBlocksModal(); } });
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', wireBlocksModal, { once: true });
  else wireBlocksModal();

  Object.assign(globalThis, {
    parseOsc133Chunk,
    getTerminalCommandBlocks,
    openTerminalBlocksModal,
    closeTerminalBlocksModal,
    resolveThemeColor,
    buildXtermTheme,
    resolveCodeFontStack,
    refreshTerminalThemes,
    showTerminalBoot,
    hideTerminalBoot,
    applyConsolePrompt,
    updateConsoleStatus,
    primeConsole,
    restartActiveTerminal,
    createXtermSession,
    fitActiveTerminal,
    focusActiveTerminal,
    setupTerminalTabs,
    renderTerminalTabs,
    switchTerminalTab,
    addTerminalTab,
    closeTerminalTab,
    getActiveTerminalSession,
    sendToActiveTerminal,
    killActiveTerminal,
    clearActiveTerminal,
    handlePtyOutput,
    handlePtyExit,
    appendTerminalStream,
    getActiveTerminalText
  });
  return { setupTerminalTabs, renderTerminalTabs, switchTerminalTab, addTerminalTab, closeTerminalTab, refreshTerminalThemes, updateConsoleStatus, primeConsole, hideTerminalBoot, restartActiveTerminal, fitActiveTerminal, focusActiveTerminal, getActiveTerminalSession, sendToActiveTerminal, killActiveTerminal, clearActiveTerminal, handlePtyOutput, handlePtyExit, appendTerminalStream, getActiveTerminalText };
}
