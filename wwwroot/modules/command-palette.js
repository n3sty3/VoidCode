// Command palette, quick open and the command registry.
//
// Owns "the
// Registry becomes the only shortcut dispatcher": it owns COMMAND_REGISTRY (the command table),
// The keybinding storage and matching (customKeybindings, parseShortcut, shortcutMatchesEvent),
// The capture-phase window keydown that turns a key press into a command, the palette and quick
// Open (openCommandPalette/openQuickOpenModal/renderQuickOpenList), and the Settings > Keybindings
// Table. Nothing else in the frontend listens for a global shortcut, so registering a command
// Here is the only way to get one - that is the invariant the next steps rely on.
//
// The registry entries call back into app.js and the other modules by name (saveCurrentFile,
// RunEditorActionId, toggleWorkspacePane, openBranchPicker, ...). Those are function declarations
// Or published module entry points, i.e. globalThis properties, and every entry is a thunk, so
// Nothing is resolved before the app has booted.
import { escapeHtml, html, raw, setHtml } from '../core/markdown.js';

// App.js's sendHost is a script-scope const (a module cannot see it); same pattern as the other
// Modules.
let hostSend = (action, data) => window.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

let customKeybindings = {};
try {
  customKeybindings = JSON.parse(globalThis.localStorage?.getItem('voidcode_keybindings_v1') || '{}');
} catch (err) { globalThis.logSwallowed?.(err, "app.loadKeybindings"); }

const COMMAND_REGISTRY = [
  // File & Editor
  { id: 'file.save', category: 'File', title: 'Save Active File', defaultKeys: 'Ctrl+S', run: () => saveCurrentFile() },
  { id: 'file.format', category: 'Editor', title: 'Format Document', defaultKeys: 'Shift+Alt+F', run: () => formatActiveDocument() },
  { id: 'editor.rename', category: 'Editor', title: 'Rename Symbol / Selected File', defaultKeys: 'F2', run: () => { if (document.activeElement?.closest?.('#file-tree-container')) globalThis.renameSelectedTreeNode?.(); else runEditorActionId('editor.action.rename'); } },
  { id: 'editor.goToLine', category: 'Navigation', title: 'Go to Line…', defaultKeys: 'Ctrl+G', run: () => goToLine() },
  { id: 'editor.toggleComment', category: 'Editor', title: 'Toggle Line Comment', defaultKeys: '', run: () => runEditorActionId('editor.action.commentLine') },
  { id: 'editor.duplicateLine', category: 'Editor', title: 'Duplicate Line Down', defaultKeys: '', run: () => runEditorActionId('editor.action.copyLinesDownAction') },
  { id: 'editor.moveLineUp', category: 'Editor', title: 'Move Line Up', defaultKeys: '', run: () => runEditorActionId('editor.action.moveLinesUpAction') },
  { id: 'editor.moveLineDown', category: 'Editor', title: 'Move Line Down', defaultKeys: '', run: () => runEditorActionId('editor.action.moveLinesDownAction') },
  { id: 'editor.deleteLine', category: 'Editor', title: 'Delete Line', defaultKeys: '', run: () => runEditorActionId('editor.action.deleteLines') },
  { id: 'editor.find', category: 'Editor', title: 'Find in File', defaultKeys: '', run: () => runEditorActionId('actions.find') },
  { id: 'editor.replace', category: 'Editor', title: 'Replace in File', defaultKeys: '', run: () => runEditorActionId('editor.action.startFindReplaceAction') },
  { id: 'editor.selectOccurrences', category: 'Editor', title: 'Select All Occurrences', defaultKeys: '', run: () => runEditorActionId('editor.action.selectHighlights') },
  { id: 'editor.addNextMatch', category: 'Editor', title: 'Add Selection to Next Find Match', defaultKeys: '', run: () => runEditorActionId('editor.action.addSelectionToNextFindMatch') },
  { id: 'editor.cursorAbove', category: 'Editor', title: 'Add Cursor Above', defaultKeys: '', run: () => runEditorActionId('editor.action.insertCursorAbove') },
  { id: 'editor.cursorBelow', category: 'Editor', title: 'Add Cursor Below', defaultKeys: '', run: () => runEditorActionId('editor.action.insertCursorBelow') },
  { id: 'editor.cursorsLineEnds', category: 'Editor', title: 'Add Cursors to Line Ends', defaultKeys: '', run: () => runEditorActionId('editor.action.insertCursorAtEndOfEachLineSelected') },
  { id: 'editor.jumpToBracket', category: 'Editor', title: 'Go to Matching Bracket', defaultKeys: '', run: () => runEditorActionId('editor.action.jumpToBracket') },
  { id: 'editor.toUpper', category: 'Editor', title: 'Transform to Uppercase', defaultKeys: '', run: () => runEditorActionId('editor.action.transformToUppercase') },
  { id: 'editor.toLower', category: 'Editor', title: 'Transform to Lowercase', defaultKeys: '', run: () => runEditorActionId('editor.action.transformToLowercase') },
  { id: 'editor.split', category: 'View', title: 'Split Editor Right', defaultKeys: 'Ctrl+\\', run: () => toggleSplitEditor() },
  { id: 'editor.closeSplit', category: 'View', title: 'Close Split Editor', defaultKeys: '', run: () => closeSplitEditor() },
  { id: 'file.quickOpen', category: 'Navigation', title: 'Go to File / Quick Open', defaultKeys: 'Ctrl+P', run: () => openQuickOpenModal() },
  { id: 'navigation.commandPalette', category: 'Navigation', title: 'Command Palette', defaultKeys: 'Ctrl+Shift+P', run: () => openCommandPalette() },
  { id: 'navigation.symbols', category: 'Navigation', title: 'Go to Symbol in Document', defaultKeys: 'Ctrl+Shift+O', run: () => openSymbolQuickPick() },
  { id: 'file.new', category: 'File', title: 'New File in Workspace', defaultKeys: '', run: () => promptNewFile() },
  { id: 'file.newFolder', category: 'File', title: 'New Folder in Workspace', defaultKeys: '', run: () => promptNewFolder() },
  { id: 'file.openFromPC', category: 'File', title: 'Open File from PC in Editor', defaultKeys: '', run: () => sendHost('open_file_dialog', { target: 'editor' }) },
  { id: 'file.attachToAgent', category: 'File', title: 'Attach PC File to Agent Chat', defaultKeys: '', run: () => sendHost('open_file_dialog', { target: 'attach' }) },

  // Execution & Terminal
  { id: 'run.smartRun', category: 'Run', title: 'Run Without Debugging / Stop Project', defaultKeys: 'Ctrl+F5', run: () => toggleSmartRun() },
  { id: 'run.killProcess', category: 'Terminal', title: 'Stop Running Terminal Process', defaultKeys: '', run: () => sendHost('kill_terminal') },
  { id: 'view.toggleTerminal', category: 'Terminal', title: 'Toggle Terminal Panel', defaultKeys: 'Ctrl+`', run: () => toggleTerminalPanel() },
  { id: 'view.newTerminal', category: 'Terminal', title: 'New Terminal Tab', defaultKeys: 'Ctrl+Shift+`', run: () => addTerminalTab() },
  { id: 'view.clearTerminal', category: 'Terminal', title: 'Clear Active Terminal Buffer', defaultKeys: '', run: () => control('btn-clear-terminal')?.click() },

  // Sidebar & Views
  { id: 'view.focusEditor', category: 'View', title: 'View: Focus Editor (hide Agent, collapse Terminal)', defaultKeys: '', run: () => focusEditorView() },
  { id: 'view.focusAgent', category: 'View', title: 'View: Focus Agent (collapse Terminal)', defaultKeys: '', run: () => focusAgentView() },
  { id: 'view.toggleFiles', category: 'View', title: 'Show Files Explorer', defaultKeys: 'Ctrl+Shift+E', run: () => switchSidebarView('files') },
  { id: 'view.toggleFilesPane', category: 'View', title: 'Toggle Files Sidebar Pane', defaultKeys: 'Ctrl+B', run: () => toggleWorkspacePane('files') },
  { id: 'view.toggleSearch', category: 'View', title: 'Search in Workspace', defaultKeys: 'Ctrl+Shift+F', run: () => switchSidebarView('search') },
  { id: 'view.toggleGit', category: 'View', title: 'Source Control (Git)', defaultKeys: 'Ctrl+Shift+G', run: () => switchSidebarView('git') },
  { id: 'view.toggleProblems', category: 'View', title: 'Show Problems & Diagnostics', defaultKeys: 'Ctrl+Shift+M', run: () => { switchBottomView('problems'); toggleWorkspacePane('terminal', true); } },
  { id: 'view.toggleTests', category: 'View', title: 'Show Tests Runner', defaultKeys: '', run: () => { switchBottomView('tests'); toggleWorkspacePane('terminal', true); } },
  { id: 'view.toggleDebug', category: 'View', title: 'Show Run & Debug Sidebar', defaultKeys: 'Ctrl+Shift+D', run: () => { switchSidebarView('debug'); toggleWorkspacePane('files', true); } },
  { id: 'view.toggleDebugConsole', category: 'View', title: 'Show Debug Console', defaultKeys: '', run: () => { switchBottomView('debug'); toggleWorkspacePane('terminal', true); } },
  { id: 'debug.start', category: 'Debug', title: 'Start / Continue Debugging', defaultKeys: 'F5', run: () => debugStartOrContinue() },
  { id: 'debug.stop', category: 'Debug', title: 'Stop Debugging', defaultKeys: 'Shift+F5', run: () => sendHost('debug_stop') },
  { id: 'debug.restart', category: 'Debug', title: 'Restart Debugging', defaultKeys: 'Ctrl+Shift+F5', run: () => { sendHost('debug_stop'); setTimeout(startDebuggingActiveFile, 400); } },
  { id: 'debug.stepOver', category: 'Debug', title: 'Step Over', defaultKeys: 'F10', run: () => sendHost('debug_step_over') },
  { id: 'debug.stepInto', category: 'Debug', title: 'Step Into', defaultKeys: 'F11', run: () => sendHost('debug_step_into') },
  { id: 'debug.stepOut', category: 'Debug', title: 'Step Out', defaultKeys: 'Shift+F11', run: () => sendHost('debug_step_out') },
  { id: 'debug.toggleBreakpoint', category: 'Debug', title: 'Toggle Breakpoint', defaultKeys: 'F9', run: () => { const editor = globalThis.getMonacoEditor?.(); if (activeTab && editor) toggleEditorBreakpoint(editor.getPosition().lineNumber); } },
  { id: 'view.toggleAgent', category: 'AI', title: 'Focus AI Agent Chat', defaultKeys: 'Ctrl+L', run: () => { toggleWorkspacePane('agent', true); control('agent-input-text')?.focus(); } },
  { id: 'ai.toggleAutocomplete', category: 'AI', title: 'AI: Toggle Tab Autocomplete (FIM Ghost Text)', defaultKeys: '', run: () => globalThis.toggleTabAutocomplete?.() },
  { id: 'ai.missionControl', category: 'AI', title: 'AI: Open Mission Control (Parallel Worktree Agents)', defaultKeys: 'Ctrl+Shift+B', run: () => globalThis.openMissionControl?.() },
  { id: 'mcp.gallery', category: 'AI', title: 'MCP: Open MCP Server Gallery (Stdio & HTTP/SSE)', defaultKeys: '', run: () => globalThis.openMcpGallery?.() },
  { id: 'binary.workbench', category: 'Tools', title: 'Binary: Open Hex & x86-64 Disassembler Workbench', defaultKeys: 'Ctrl+Shift+X', run: () => globalThis.openBinaryWorkbench?.() },
  { id: 'window.new', category: 'Window', title: 'Window: Open New VoidCode Window', defaultKeys: 'Ctrl+Shift+N', run: () => globalThis.VoidBridge?.request?.('window_new', {}) },
  { id: 'codebase.search', category: 'Codebase', title: 'Search Indexed Code & Symbols', defaultKeys: 'Ctrl+T', run: () => openCodebaseSearch() },
  { id: 'language.restart', category: 'Language', title: 'Restart Language Servers', defaultKeys: '', run: () => sendHost('lsp_restart') },
  { id: 'git.generateCommit', category: 'AI', title: 'Generate AI Commit Message', defaultKeys: '', run: () => sendHost('generate_commit_message') },

  // Git Source Control
  { id: 'git.checkout', category: 'Git', title: 'Git: Checkout / Switch Branch...', defaultKeys: '', run: () => openBranchPicker() },
  { id: 'git.createBranch', category: 'Git', title: 'Git: Create New Branch...', defaultKeys: '', run: () => openBranchPicker() },
  { id: 'git.pull', category: 'Git', title: 'Git: Pull from Remote', defaultKeys: '', run: () => sendHost('git_pull') },
  { id: 'git.push', category: 'Git', title: 'Git: Push to Remote', defaultKeys: '', run: () => sendHost('git_push') },
  { id: 'git.fetch', category: 'Git', title: 'Git: Fetch Remotes', defaultKeys: '', run: () => sendHost('git_fetch') },
  { id: 'git.stash', category: 'Git', title: 'Git: Stash Changes', defaultKeys: '', run: async () => {
    const msg = await showInputDialog('Git Stash', '', 'Stash');
    if (msg !== null) sendHost('git_stash', { message: msg });
  }},
  { id: 'git.stashPop', category: 'Git', title: 'Git: Pop Stash', defaultKeys: '', run: () => sendHost('git_stash_pop') },
  { id: 'git.history', category: 'Git', title: 'Git: View Commit History', defaultKeys: '', run: () => sendHost('git_get_history') },

  // Tests
  { id: 'tests.runAll', category: 'Tests', title: 'Tests: Run All Tests', defaultKeys: '', run: () => { switchBottomView('tests'); toggleWorkspacePane('terminal', true); sendHost('run_tests'); } },
  { id: 'tests.view', category: 'Tests', title: 'Tests: View Tests Panel', defaultKeys: '', run: () => { switchBottomView('tests'); toggleWorkspacePane('terminal', true); } },

  // AI Workflows
  { id: 'ai.inlineAssist', category: 'AI', title: 'Inline AI Code Assistant', defaultKeys: 'Ctrl+K', run: () => openInlineAiAssist() },
  { id: 'ai.fixTerminal', category: 'AI', title: 'Fix Last Terminal Error with AI', defaultKeys: '', run: () => openAgentWithTerminalError(true) },
  { id: 'ai.explainTerminal', category: 'AI', title: 'Explain Last Terminal Error with AI', defaultKeys: '', run: () => openAgentWithTerminalError(false) },

  // Preferences & Utility Windows
  { id: 'window.settings', category: 'Preferences', title: 'Preferences: Open Settings', defaultKeys: 'Ctrl+,', run: () => control('btn-settings-toggle')?.click() },
  { id: 'window.shortcuts', category: 'Preferences', title: 'Preferences: Keyboard Shortcuts', defaultKeys: '', run: () => openSettingsToTab('tab-keybindings') },
  { id: 'window.themes', category: 'Appearance', title: 'Themes & Glass Customization', defaultKeys: '', run: () => toggleMiniWindow('win-customize', true) },
  { id: 'window.history', category: 'View', title: 'Chat History & Saved Sessions', defaultKeys: 'Ctrl+H', run: () => toggleMiniWindow('win-history', true) },
  { id: 'agent.inspectContext', category: 'Agent', title: 'Agent: Inspect Context & Tokens', defaultKeys: '', run: () => globalThis.openContextInspector?.() },
  { id: 'agent.reviewLastTurn', category: 'Agent', title: 'Agent: Review Last Turn Diff', defaultKeys: '', run: () => globalThis.openLastTurnReview?.() },
  { id: 'view.health', category: 'Preferences', title: 'VoidCode: Environment Health & Tooling', defaultKeys: '', run: () => globalThis.openHealthModal?.() },
  { id: 'view.about', category: 'Preferences', title: 'VoidCode: About & System Info', defaultKeys: '', run: () => globalThis.openAboutModal?.() },
  { id: 'view.checkUpdates', category: 'Preferences', title: 'VoidCode: Check for Updates (GitHub Releases)', defaultKeys: '', run: () => globalThis.checkForUpdates?.(false) },
  { id: 'runtime.select', category: 'Environment', title: 'Select Active Runtime / Python Interpreter', defaultKeys: '', run: () => globalThis.cycleActiveRuntime?.() },
  { id: 'tasks.run', category: 'Tasks', title: 'Tasks: Run Workspace Task', defaultKeys: '', run: () => globalThis.openTasksModal?.() },
  { id: 'terminal.blocks', category: 'Terminal', title: 'Terminal: Recent Command Blocks', defaultKeys: '', run: () => globalThis.openTerminalBlocksModal?.() },
  { id: 'git.blame', category: 'Git', title: 'Git: Toggle Inline Blame', defaultKeys: '', run: () => globalThis.toggleGitBlame?.() },
  { id: 'file.saveAs', category: 'File', title: 'File: Save As...', defaultKeys: 'Ctrl+Shift+S', run: () => globalThis.saveActiveFileAs?.() },
  { id: 'file.closeTab', category: 'File', title: 'File: Close Active Editor Tab', defaultKeys: 'Ctrl+W', run: () => { const t = window.VoidState?.get?.('activeTab'); if (t) globalThis.requestCloseTab?.(t); } },
  { id: 'file.pinTab', category: 'File', title: 'File: Pin / Unpin Active Editor Tab', defaultKeys: '', run: () => globalThis.togglePinTab?.() },
  { id: 'file.closeOtherTabs', category: 'File', title: 'File: Close Other Editor Tabs', defaultKeys: '', run: () => globalThis.closeOtherTabs?.() },
  { id: 'file.closeSavedTabs', category: 'File', title: 'File: Close Saved Editor Tabs', defaultKeys: '', run: () => globalThis.closeSavedTabs?.() },
  { id: 'file.closeAllTabs', category: 'File', title: 'File: Close All Editor Tabs', defaultKeys: '', run: () => globalThis.closeAllTabs?.() },
  { id: 'editor.nextTab', category: 'Navigation', title: 'View: Switch to Next Open Tab', defaultKeys: 'Ctrl+Tab', run: () => globalThis.cycleNextTab?.() },
  { id: 'editor.zoomIn', category: 'View', title: 'View: Zoom In Editor Font', defaultKeys: 'Ctrl+=', run: () => globalThis.adjustEditorZoom?.(1) },
  { id: 'editor.zoomOut', category: 'View', title: 'View: Zoom Out Editor Font', defaultKeys: 'Ctrl+-', run: () => globalThis.adjustEditorZoom?.(-1) },
  { id: 'editor.zoomReset', category: 'View', title: 'View: Reset Editor Font Zoom', defaultKeys: 'Ctrl+0', run: () => globalThis.adjustEditorZoom?.(0) },
  { id: 'editor.wordWrap', category: 'View', title: 'View: Toggle Word Wrap', defaultKeys: 'Alt+Z', run: () => globalThis.toggleEditorWordWrap?.() },
  { id: 'view.zenMode', category: 'View', title: 'View: Toggle Distraction-Free Zen Mode', defaultKeys: 'Ctrl+Shift+Z', run: () => globalThis.toggleZenMode?.() },
  { id: 'ai.switchModel', category: 'AI', title: 'AI: Switch Active Model...', defaultKeys: '', run: () => globalThis.switchActiveModelQuick?.() },
  { id: 'agent.exportMarkdown', category: 'Agent', title: 'Agent: Export Conversation as Markdown (.md)', defaultKeys: '', run: () => globalThis.exportCurrentSessionAsMarkdown?.() },
  { id: 'agent.initRules', category: 'Agent', title: 'Agent: Initialize AGENTS.md Project Rules', defaultKeys: '', run: () => {
    globalThis.toggleWorkspacePane?.('agent', true);
    globalThis.attachContextItem?.({ id: 'folder', type: 'folder', label: 'Workspace Tree', content: globalThis.getActiveFolderTree?.() || '' });
    const box = control('agent-input-text');
    if (box) {
      box.value = 'Inspect the workspace directory tree, project files, build/test scripts, and architecture, then create or update root AGENTS.md with concise build/test commands, module boundaries, and coding conventions.';
      box.focus();
    }
  }},
  { id: 'search.todos', category: 'Search', title: 'Workspace: Find All TODO / FIXME Comments', defaultKeys: '', run: () => {
    switchSidebarView('search');
    toggleWorkspacePane('files', true);
    const inp = control('global-search-input');
    if (inp) {
      inp.value = 'TODO';
      inp.focus();
      control('btn-run-global-search')?.click();
    }
  }},
  { id: 'file.reopenClosed', category: 'File', title: 'File: Reopen Closed Editor', defaultKeys: 'Ctrl+Shift+T', run: () => globalThis.reopenLastClosedTab?.() },
  { id: 'file.compare', category: 'File', title: 'File: Compare Active File With Another Tab', defaultKeys: '', run: () => globalThis.compareActiveFileWith?.() },
  { id: 'window.timeline', category: 'View', title: 'Activity & Checkpoints Timeline', defaultKeys: '', run: () => toggleMiniWindow('win-timeline', true) },
  { id: 'window.playbooks', category: 'View', title: 'Playbooks & Saved Prompts', defaultKeys: '', run: () => toggleMiniWindow('win-prompts', true) },
  { id: 'window.inspector', category: 'View', title: 'Binary & Hash Inspector', defaultKeys: '', run: () => toggleMiniWindow('win-inspector', true) },
  { id: 'project.hub', category: 'Project', title: 'Switch Project / Project Hub', defaultKeys: '', run: () => openProjectHub() }
];

function getKeybinding(cmdId) {
  if (customKeybindings && customKeybindings[cmdId] !== undefined) return customKeybindings[cmdId];
  const cmd = COMMAND_REGISTRY.find(c => c.id === cmdId);
  return cmd ? cmd.defaultKeys : '';
}

function normalizeShortcutKey(key) {
  const normalized = String(key || '').trim().toLowerCase();
  const aliases = {
    control: 'ctrl',
    command: 'meta',
    cmd: 'meta',
    option: 'alt',
    esc: 'escape',
    ' ': 'space'
  };
  return aliases[normalized] || normalized;
}

function parseShortcut(shortcut) {
  const parts = String(shortcut || '')
    .split('+')
    .map(normalizeShortcutKey)
    .filter(Boolean);
  const modifiers = new Set(parts.filter(part => ['ctrl', 'shift', 'alt', 'meta'].includes(part)));
  const key = parts.findLast(part => !modifiers.has(part)) || '';
  return { modifiers, key };
}

function normalizeKeyboardEventKey(event) {
  const codeAliases = {
    Backquote: '`',
    Backslash: '\\',
    Comma: ',',
    Period: '.',
    Slash: '/'
  };
  return codeAliases[event.code] || normalizeShortcutKey(event.key);
}

function isMacPlatform() {
  return typeof navigator !== 'undefined' && /mac/i.test(navigator.platform || navigator.userAgent || '');
}

function formatShortcutDisplay(shortcut) {
  if (!shortcut || !isMacPlatform()) return shortcut;
  return String(shortcut).replace(/\bCtrl\+/gi, '⌘').replace(/\bAlt\+/gi, '⌥').replace(/\bShift\+/gi, '⇧');
}

function shortcutMatchesEvent(shortcut, event) {
  if (!shortcut) return false;
  const parsed = parseShortcut(shortcut);
  if (!parsed.key) return false;
  if (isMacPlatform() && parsed.modifiers.has('ctrl') && !parsed.modifiers.has('meta')) {
    if (!event.metaKey && !event.ctrlKey) return false;
  } else {
    if (parsed.modifiers.has('ctrl') !== event.ctrlKey) return false;
    if (parsed.modifiers.has('meta') !== event.metaKey) return false;
  }
  if (parsed.modifiers.has('shift') !== event.shiftKey) return false;
  if (parsed.modifiers.has('alt') !== event.altKey) return false;
  return normalizeKeyboardEventKey(event) === parsed.key;
}

function findKeybindingConflict(cmdId, keys) {
  const normalized = String(keys || '').trim().toLowerCase();
  if (!normalized) return null;
  return COMMAND_REGISTRY.find(command =>
    command.id !== cmdId && String(getKeybinding(command.id) || '').trim().toLowerCase() === normalized
  ) || null;
}

function dispatchRegisteredShortcut(event) {
  const command = COMMAND_REGISTRY.find(candidate => shortcutMatchesEvent(getKeybinding(candidate.id), event));
  if (!command) return false;
  event.preventDefault();
  event.stopPropagation();
  try {
    command.run();
  } catch (error) {
    console.error(`Command failed: ${command.id}`, error);
    showToast(`Command failed: ${command.title}`, 'error');
  }
  return true;
}

function setKeybinding(cmdId, keys) {
  const conflict = findKeybindingConflict(cmdId, keys);
  if (conflict) {
    showToast(`Shortcut already used by “${conflict.title}”.`, 'error');
    return false;
  }
  if (!customKeybindings) customKeybindings = {};
  customKeybindings[cmdId] = keys.trim();
  try {
    localStorage.setItem('voidcode_keybindings_v1', JSON.stringify(customKeybindings));
  } catch (err) { logSwallowed(err, "app.saveKeybinding"); }
  renderKeybindingsTable();
  return true;
}

function resetKeybindingsToDefaults() {
  customKeybindings = {};
  try {
    localStorage.removeItem('voidcode_keybindings_v1');
  } catch (err) { logSwallowed(err, "app.resetKeybindings"); }
  renderKeybindingsTable();
  showToast('Keybindings reset to defaults', 'info');
}

function openCommandPalette() {
  const modal = control('quick-open-modal');
  const input = control('quick-open-input');
  if (!modal || !input) return;
  modal.style.display = 'flex';
  input.value = '>';
  input.focus();
  renderQuickOpenList('>');
}

function openQuickOpenModal() {
  const modal = control('quick-open-modal');
  const input = control('quick-open-input');
  if (!modal || !input) return;
  modal.style.display = 'flex';
  input.value = '';
  input.focus();
  sendHost('get_all_files');
  renderQuickOpenList('');
}

function renderQuickOpenList(query) {
  const list = control('quick-open-list');
  if (!list) return;
  setHtml(list, '');
  const rawQ = query || '';
  const isCmdMode = rawQ.startsWith('>');
  const cleanQ = (isCmdMode ? rawQ.slice(1) : rawQ).trim().toLowerCase();

  let itemIdx = 0;

  if (isCmdMode) {
    // Command Palette Mode
    const matchedCmds = COMMAND_REGISTRY.filter(cmd => {
      if (!cleanQ) return true;
      return cmd.title.toLowerCase().includes(cleanQ) || cmd.category.toLowerCase().includes(cleanQ);
    });

    matchedCmds.forEach(cmd => {
      const keys = getKeybinding(cmd.id);
      const item = document.createElement('div');
      item.className = 'quick-open-item' + (itemIdx++ === 0 ? ' selected' : '');
      item.dataset.cmdId = cmd.id;
      setHtml(item, html`
        <div class="quick-open-label-group">
          <span class="cmd-category-tag">${cmd.category}</span>
          <span class="cmd-title">${cmd.title}</span>
        </div>
        ${keys ? raw(`<kbd class="cmd-shortcut">${escapeHtml(formatShortcutDisplay(keys))}</kbd>`) : ''}
      `);
      item.addEventListener('click', () => {
        control('quick-open-modal').style.display = 'none';
        try { cmd.run(); } catch (err) { console.error(err); }
      });
      list.appendChild(item);
    });

    if (itemIdx === 0) {
      setHtml(list, '<div class="empty-state">No matching commands found.</div>');
    }
    return;
  }

  // Normal File Search Mode
  // One helper for both groups: escape, then wrap the matched run
  function highlightMatch(text, query) {
    const value = String(text || '');
    const q = String(query || '').trim();
    if (!q) return escapeHtml(value);
    const at = value.toLowerCase().indexOf(q.toLowerCase());
    if (at < 0) return escapeHtml(value);
    return escapeHtml(value.slice(0, at)) + '<mark>' + escapeHtml(value.slice(at, at + q.length)) + '</mark>' +
      escapeHtml(value.slice(at + q.length));
  }

  function appendSection(label) {
    const head = document.createElement('div');
    head.className = 'quick-open-section';
    head.textContent = label;
    list.appendChild(head);
  }

  const filteredFiles = allWorkspaceFiles.filter(f =>
    !cleanQ || f.name.toLowerCase().includes(cleanQ) || f.relativePath.toLowerCase().includes(cleanQ)
  ).slice(0, 30);

  if (filteredFiles.length) appendSection(cleanQ ? 'Files' : 'Files (recent)');

  filteredFiles.forEach(f => {
    const item = document.createElement('div');
    item.className = 'quick-open-item' + (itemIdx++ === 0 ? ' selected' : '');
    setHtml(item, raw(`<span>${highlightMatch(f.name, cleanQ)}</span><small>${highlightMatch(f.relativePath, cleanQ)}</small>`));
    item.addEventListener('click', () => {
      control('quick-open-modal').style.display = 'none';
      sendHost('read_file', { path: f.path });
    });
    list.appendChild(item);
  });

  if (allWorkspaceFilesTruncated) {
    const notice = document.createElement('div');
    notice.className = 'outline-empty';
    notice.textContent = 'Quick Open indexed the first 50,000 non-ignored files. Narrow the workspace or ignore generated folders for complete results.';
    list.appendChild(notice);
  }

  // Top command suggestions when query is typed
  if (cleanQ) {
    const matchedCmds = COMMAND_REGISTRY.filter(c =>
      c.title.toLowerCase().includes(cleanQ) || c.category.toLowerCase().includes(cleanQ)
    ).slice(0, 5);

    if (matchedCmds.length) appendSection('Commands');

    matchedCmds.forEach(cmd => {
      const keys = getKeybinding(cmd.id);
      const item = document.createElement('div');
      item.className = 'quick-open-item' + (itemIdx++ === 0 ? ' selected' : '');
      setHtml(item, html`
        <div class="quick-open-label-group">
          <span class="cmd-category-tag">${cmd.category}</span>
          <span class="cmd-title">${raw(highlightMatch(cmd.title, cleanQ))}</span>
        </div>
        ${keys ? raw(`<kbd class="cmd-shortcut">${escapeHtml(formatShortcutDisplay(keys))}</kbd>`) : ''}
      `);
      item.addEventListener('click', () => {
        control('quick-open-modal').style.display = 'none';
        cmd.run();
      });
      list.appendChild(item);
    });
  }

  if (itemIdx === 0) {
    setHtml(list, '<div class="empty-state">No matching files or commands.</div>');
  }
}

// Keybinding chord recorder. it used to live in app.js, but its only caller is
// RenderKeybindingsTable() below, so it belongs with the rest of the keybinding UI. Verbatim body.
function showKeybindingDialog(cmdTitle, currentBinding = '') {
  return new Promise((resolve) => {
    const modal = control('input-dialog-modal');
    const titleEl = control('input-dialog-title');
    const subEl = control('input-dialog-sub');
    const inputEl = control('input-dialog-field');
    const btnConfirm = control('input-dialog-confirm');
    const btnCancel = control('input-dialog-cancel');

    titleEl.textContent = `Shortcut for ${cmdTitle}`;
    if (subEl) {
      subEl.textContent = 'Press desired key combination on your keyboard (Backspace to clear, Enter to save, Esc to cancel)';
      subEl.style.display = 'block';
    }
    btnConfirm.textContent = 'Save shortcut';
    inputEl.value = currentBinding;
    inputEl.placeholder = 'Press keys (e.g. Ctrl+Shift+K)…';
    modal.style.display = 'flex';
    inputEl.focus();

    function formatChordFromEvent(e) {
      const mods = [];
      if (e.ctrlKey) mods.push('Ctrl');
      if (e.shiftKey) mods.push('Shift');
      if (e.altKey) mods.push('Alt');
      if (e.metaKey) mods.push('Meta');

      const rawKey = e.key;
      if (['Control', 'Shift', 'Alt', 'Meta'].includes(rawKey)) {
        return null;
      }

      const codeMap = {
        Backquote: '`',
        Backslash: '\\',
        Comma: ',',
        Period: '.',
        Slash: '/',
        Space: 'Space'
      };
      let keyLabel = codeMap[e.code] || (rawKey.length === 1 ? rawKey.toUpperCase() : rawKey);
      return [...mods, keyLabel].join('+');
    }

    function cleanup(val) {
      modal.style.display = 'none';
      if (subEl) subEl.style.display = 'none';
      inputEl.placeholder = '';
      btnConfirm.onclick = null;
      btnCancel.onclick = null;
      inputEl.onkeydown = null;
      resolve(val);
    }

    btnConfirm.onclick = () => cleanup(inputEl.value.trim());
    btnCancel.onclick = () => cleanup(null);
    inputEl.onkeydown = (e) => {
      e.preventDefault();
      e.stopPropagation();

      if (e.key === 'Escape' && !e.ctrlKey && !e.shiftKey && !e.altKey) {
        cleanup(null);
        return;
      }
      if (e.key === 'Enter' && !e.ctrlKey && !e.shiftKey && !e.altKey) {
        cleanup(inputEl.value.trim());
        return;
      }
      if ((e.key === 'Backspace' || e.key === 'Delete') && !e.ctrlKey && !e.shiftKey && !e.altKey) {
        inputEl.value = '';
        return;
      }

      const chord = formatChordFromEvent(e);
      if (chord) {
        inputEl.value = chord;
      }
    };
  });
}

function renderKeybindingsTable(filterQuery = '') {
  const container = control('keybindings-list');
  if (!container) return;
  setHtml(container, '');

  const q = (filterQuery || '').toLowerCase().trim();
  const filtered = COMMAND_REGISTRY.filter(cmd => {
    if (!q) return true;
    const key = getKeybinding(cmd.id);
    return cmd.title.toLowerCase().includes(q) || cmd.category.toLowerCase().includes(q) || key.toLowerCase().includes(q);
  });

  if (filtered.length === 0) {
    setHtml(container, '<div class="outline-empty">No matching commands or shortcuts found.</div>');
    return;
  }

  filtered.forEach(cmd => {
    const row = document.createElement('div');
    row.className = 'keybinding-row';
    row.tabIndex = 0;
    row.setAttribute('role', 'button');
    row.title = `Click to change shortcut for ${cmd.title}`;
    const key = getKeybinding(cmd.id);

    setHtml(row, html`
      <span class="keybinding-title">${cmd.title}</span>
      <span class="keybinding-cat">${cmd.category}</span>
      <div class="keybinding-actions">
        ${key ? raw(`<kbd class="cmd-shortcut">${escapeHtml(formatShortcutDisplay(key))}</kbd>`) : raw('<span class="keybinding-unset">Unassigned</span>')}
      </div>
    `);

    const promptRebind = async () => {
      const current = getKeybinding(cmd.id);
      const newKey = await showKeybindingDialog(cmd.title, current);
      if (newKey !== null) {
        setKeybinding(cmd.id, newKey);
      }
    };

    row.addEventListener('click', promptRebind);
    row.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        promptRebind();
      }
    });

    container.appendChild(row);
  });
}

function setupKeybindingsSettings() {
  const searchInput = control('keybindings-search');
  searchInput?.addEventListener('input', () => renderKeybindingsTable(searchInput.value));
  control('btn-reset-keybindings')?.addEventListener('click', resetKeybindingsToDefaults);

  const keyTabBtn = document.querySelector('#settings-modal .settings-tab-btn[data-tab="tab-keybindings"]');
  keyTabBtn?.addEventListener('click', () => {
    renderKeybindingsTable(searchInput?.value || '');
  });
}

// App.js calls this once while wiring the DOM (it used to call
// SetupKeybindingsSettings() plus a block of palette listeners inline). Everything here needs the
// Document, so it stays out of init().
// One typed lookup instead of a cast per call site - the controls this module reads
// By id are inputs, and tsc only knows getElementById returns a plain HTMLElement.
/**
 * @param {string} id
 * @returns {HTMLInputElement | null}
 */
function control(id) {
  return /** @type {HTMLInputElement | null} */ (document.getElementById(id));
}

export function setupCommandPalette() {
  setupKeybindingsSettings();

  window.addEventListener('keydown', (event) => {
    const inputModal = control('input-dialog-modal');
    if (inputModal && inputModal.style.display !== 'none') {
      return;
    }

    if (event.key === 'F1') {
      event.preventDefault();
      event.stopPropagation();
      openCommandPalette();
      return;
    }

    // File rename is scoped to the explorer. F2 remains available to Monaco/LSP for symbol rename
    // When the editor is focused. the tree owns the selection now, so it does the work.
    if (event.key === 'F2' && document.activeElement?.closest?.('#file-tree-container')) {
      event.preventDefault();
      event.stopPropagation();
      globalThis.renameSelectedTreeNode?.();
      return;
    }

    if (dispatchRegisteredShortcut(event)) return;
  }, true);
  // Quick Open Filtering
  const qoInput = control('quick-open-input');
  qoInput.addEventListener('input', () => renderQuickOpenList(qoInput.value));
  qoInput.addEventListener('keydown', (e) => {
    // Escape belongs to the dialog that has focus, not to a capture-phase window handler.
    // The old global handler also hid #settings-modal behind the close button's back (no baseline
    // Restore) and, being on capture, beat the select listbox's own Escape inside Settings.
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      control('quick-open-modal').style.display = 'none';
      return;
    }
    const items = Array.from(document.querySelectorAll('.quick-open-item'));
    if (!items.length) return;
    const currentIndex = items.findIndex(el => el.classList.contains('selected'));

    if (e.key === 'ArrowDown') {
      e.preventDefault();
      const nextIndex = currentIndex < items.length - 1 ? currentIndex + 1 : 0;
      items.forEach((el, idx) => el.classList.toggle('selected', idx === nextIndex));
      items[nextIndex]?.scrollIntoView({ block: 'nearest' });
      return;
    }
    if (e.key === 'ArrowUp') {
      e.preventDefault();
      const prevIndex = currentIndex > 0 ? currentIndex - 1 : items.length - 1;
      items.forEach((el, idx) => el.classList.toggle('selected', idx === prevIndex));
      items[prevIndex]?.scrollIntoView({ block: 'nearest' });
      return;
    }
    if (e.key === 'Enter') {
      e.preventDefault();
      const selected = document.querySelector('.quick-open-item.selected') || items[0];
      if (selected) selected.click();
      return;
    }
  });
  control('quick-open-modal').addEventListener('click', (e) => {
    if (/** @type {HTMLInputElement} */ (e.target).id === 'quick-open-modal') /** @type {HTMLInputElement} */ (e.target).style.display = 'none';
  });
}

// Main.js calls this once, before the classic scripts load. These are the
// Entry points app.js (line 2196's Ctrl+Shift+P fallback, the button wiring) and modules/explorer.js
// (the all_files_response refresh) still call by name.
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  Object.assign(globalThis, {
    openCommandPalette,
    openQuickOpenModal,
    renderQuickOpenList,
    renderKeybindingsTable,
    resetKeybindingsToDefaults,
    getKeybinding,
    setupCommandPalette
  });

  return {
    openCommandPalette,
    openQuickOpenModal,
    renderQuickOpenList,
    renderKeybindingsTable,
    resetKeybindingsToDefaults,
    getKeybinding,
    setupCommandPalette
  };
}