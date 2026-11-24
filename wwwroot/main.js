import { installBridge } from './core/bridge.js';
import { installHostEvents } from './core/host-events.js';
import { installModalFocus } from './core/modal-focus.js'; // dialog focus + inert
import { trustedScriptUrl } from './core/markdown.js'; // also publishes the html/raw/setHtml shims + CSP listener
import './core/state.js'; // installs the store and the migrated-slice accessors before the classic scripts
import { init as initIcons } from './modules/icons.js';
import { init as initDialogs } from './modules/dialogs.js';
import { init as initEditorFeatures } from './modules/editor-features.js';
import { init as initProblemsTests } from './modules/problems-tests.js';
import { init as initWorkspaceTools } from './modules/workspace-tools.js';
import { init as initAgentComposer } from './modules/agent-composer.js';
import { init as initTopbar } from './modules/topbar.js'; // operator label + tooltip sync
import { init as initTheme } from './modules/theme.js';
import { init as initCodebaseSearch } from './modules/codebase-search.js';
import { init as initSearch } from './modules/search.js';
import { init as initRunDebug } from './modules/run-debug.js';
import { init as initAgentChat } from './modules/agent-chat.js';
import { init as initAgentStream } from './modules/agent-stream.js';
import { init as initTurnReview } from './modules/turn-review.js';
import { init as initContextInspector } from './modules/context-inspector.js';
import { init as initHealth } from './modules/health.js';
import { init as initWorkflowTools } from './modules/workflow-tools.js';
import { init as initDebug } from './modules/debug.js';
import { init as initInlineAi } from './modules/inline-ai.js';
import { init as initAutocomplete } from './modules/autocomplete.js';
import { init as initInlineDiff } from './modules/inline-diff.js';
import { init as initMissionControl } from './modules/mission-control.js';
import { init as initMcpGallery } from './modules/mcp-gallery.js';
import { init as initBinaryWorkbench } from './modules/binary-workbench.js';
import { init as initEditorContextMenu } from './modules/editor-context-menu.js';
import { init as initMemoryBank } from './modules/memory-bank.js';
import { init as initBranchReview } from './modules/branch-review.js';
import { init as initApiClient } from './modules/api-client.js';
import { init as initIdeImport } from './modules/ide-import.js';
import { init as initDbExplorer } from './modules/db-explorer.js';
import { init as initModelSwitcher } from './modules/model-switcher.js';
import { init as initGitGraph } from './modules/git-graph.js';
import { init as initOnboarding } from './modules/onboarding.js';
import { init as initSymbolJump } from './modules/symbol-jump.js';
import { init as initVimMode } from './modules/vim-mode.js';
import { init as initAgentSignals } from './modules/agent-signals.js';
import { init as initGitGutter } from './modules/git-gutter.js';
import { init as initAgentConvSwitcher } from './modules/agent-conv-switcher.js';
import { init as initZenShortcuts } from './modules/zen-shortcuts.js';
import { init as initGit } from './modules/git.js';
import { init as initTerminal } from './modules/terminal.js';
import { init as initShell } from './modules/shell.js';
import { init as initSettings } from './modules/settings.js';
import { init as initExplorer } from './modules/explorer.js';
import { init as initEditorTabs } from './modules/editor-tabs.js';
import { init as initCommandPalette } from './modules/command-palette.js';
import { init as initProjectHub } from './modules/project-hub.js';
import { init as initStatusBar } from './modules/status-bar.js';
import { init as initLayout } from './modules/layout.js';
import { init as initSession } from './modules/session.js';
import { init as initLsp } from './modules/lsp.js';

// The single frontend entry point (ES module, no bundler).
//
// Index.html now loads exactly one script of ours. This module installs the converted core
// Helpers (bridge + markdown), publishes their temporary window shims, then loads the
// Scripts in their original order.
//
// App.js is the only classic script left. The 20 console/ scripts that used to be
// Listed here (the icon pack, the registry, 14 command files, the parser, core, the executor and the
// UI) are gone from startup: the icon pack is modules/icons.js, the 14 commands are one
// Console/commands.js, and the shell itself is lazy - modules/shell.js loads its six scripts when the
// Console view is revealed (modules/layout.js calls ensureVoidShell() on the expand path).
const CLASSIC_SCRIPTS = ['app.js'];

function loadClassicScript(src) {
  return new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = trustedScriptUrl(src); // a script URL is a Trusted Types sink
    script.async = false; // keep the declared order
    script.onload = () => resolve(src);
    script.onerror = () => reject(new Error(`Could not load ${src}`));
    document.body.appendChild(script);
  });
}

// App.js and the modules initialise from DOMContentLoaded, and (measured) a module entry does NOT
// Hold that event back for scripts it injects itself. So track the event from here and replay it
// Once if it fired while we were loading - the parse-order <script> tags used to guarantee app.js
// Ran before it. `domContentLoadedFired` is only ever set by the real event, so a run where the
// Event still arrives after the last script is left untouched.
let domContentLoadedFired = false;
document.addEventListener('DOMContentLoaded', () => { domContentLoadedFired = true; });

// The two converted modules publish window.VoidBridge / window.logSwallowed / html / raw /
// SetHtml / escapeHtml so the classic scripts keep working unchanged.
installBridge(globalThis);
// The host-event registry app.js registers its handlers into (replaces its switch).
installHostEvents(globalThis);
// One focus trap for every dialog (Tab cycles, the rest of the window goes inert, focus
// Returns to the opener). It watches the DOM, so the dozen places that open a dialog need no change.
installModalFocus();
initTheme(); // publishes window.applyThemeAndOptics for the scripts
initDialogs(); // toasts, input/confirm dialogs, selects, auth gate, utility windows
initEditorFeatures(); // snippets, editor actions, breadcrumbs, split editor, symbols/outline
initProblemsTests(); // bottom panel switcher, problems, tests runner, roving keyboard a11y
initWorkspaceTools(); // sessions, timeline, playbooks, live preview, image viewer
initAgentComposer(); // composer, @ mentions, attachments, plan/act, terminal suggestions
initTopbar(); // publishes window.setTopbarOperator (operator label + collapsed-pill tooltip)
initCodebaseSearch(); // publishes the codebase search entry points
initSearch(); // publishes the workspace search entry points
initRunDebug(); // publishes the smart run / debug + terminal AI banner entry points
initAgentChat(); // publishes the agent chat helpers
initAgentStream(); // publishes the agent streaming renderers
initTurnReview(); // per-file & per-hunk turn review + diff viewer
initContextInspector(); // next-turn context & token inspector
initHealth(); // environment capabilities & managed LSP installer
initWorkflowTools(); // tasks, git blame, merge conflicts, editor basics
initDebug(); // publishes the debugger entry points + installs its debug_unsupported subscription
initInlineAi(); // publishes the inline AI assist entry points
initAutocomplete(); // Monaco Tab Autocomplete (FIM ghost text) + local AI discovery
initInlineDiff(); // floating inline AI Accept / Diff / Revert bar inside Monaco
initMissionControl(); // Mission Control parallel Git-worktree agents
initMcpGallery(); // 1-click MCP Server Gallery (Stdio & HTTP/SSE)
initBinaryWorkbench(); // Native Iced x86-64 Disassembler & Hex Workbench
initEditorContextMenu(); // Obsidian Glass custom Monaco editor right-click context menu
initMemoryBank(); // Persistent Cross-Session Agent Memory Bank
initBranchReview(); // AI Pre-PR Branch Code Reviewer
initApiClient(); // Built-In API / HTTP Client Studio
initIdeImport(); // 1-Click Import from VS Code / Cursor
initDbExplorer(); // Built-In SQLite Database Explorer
initSymbolJump(); // Ctrl+Shift+O SQLite Workspace Symbol Jump
initVimMode(); // Native Vim Mode for Monaco Editor
initAgentSignals(); // Agent sound chimes, desktop notifications & compact turn summary strip
initGitGutter(); // Live Git diff gutter bars & peek/revert popover in Monaco
initAgentConvSwitcher(); // Inline Agent conversation dropdown switcher
initZenShortcuts(); // Zen Mode, live Accent Color picker & searchable shortcuts reference
initModelSwitcher(); // 1-Click Quick Model Switcher Popover (Ctrl+Alt+M)
initGitGraph(); // Visual Git Commit Graph, Stash & Clone Repository
initOnboarding(); // 15-Second First-Run Onboarding Wizard
initGit(); // publishes the git source-control + diff entry points
initIcons(); // the themed icon pack, so the shell can be lazy
initTerminal(); // publishes the PTY terminal entry points
initShell(); // the lazy VoidShell facade (loads console/ on demand)
initSettings(); // publishes the settings/readability/auto-save entry points + subscribes to trust
initExplorer(); // the tree + the workspace index, and its own three host actions
initEditorTabs(); // the tab lifecycle, the tab bar and read_file_response/save_file_response
initCommandPalette(); // the command registry, the keybinding dispatch and quick open
initProjectHub(); // the project launcher (recents, open folder, new project)
initStatusBar(); // the status-slot painters and the language-server slot
initLayout(); // pane visibility/persistence, the splitters and the sidebar switcher
initSession(); // session restore, crash recovery, safe close, external file changes
initLsp(); // the LSP client lifecycle and the Monaco providers

for (const src of CLASSIC_SCRIPTS) {
  await loadClassicScript(src);
}

if (domContentLoadedFired) {
  document.dispatchEvent(new Event('DOMContentLoaded', { bubbles: true }));
}