import globals from 'globals';
import noUnsanitized from 'eslint-plugin-no-unsanitized';

// Safety rules over first-party frontend code. Bundled third-party
// Libraries under wwwroot/vendor are ignored. no-unsanitized was "warn" while the
// InnerHTML migration was in flight; it is "error" now because
// Wwwroot/app.js, wwwroot/modules/* and wwwroot/console/* are innerHTML-free and
// SetHtml()/html``/raw() in core/markdown.js are the only sanctioned sinks.
const safetyRules = {
  'no-empty': ['error', { allowEmptyCatch: false }],
  'no-undef': 'error',
  'no-unused-vars': ['warn', { argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrorsIgnorePattern: '^_' }],
  'no-unsanitized/property': 'error',
  'no-unsanitized/method': 'error'
};

// Globals supplied by the 30+ classic <script> tags and the vendored libraries.
// (The app shares globals across files by design - turns these into modules.)
const appGlobals = {
  ...globals.browser,
  // Published as a global accessor by core/state.js while the workspace slice is
  // Migrated out of app.js, so it is a real (writable) global until the last reader becomes a module.
  currentWorkspace: 'writable',
  // Same temporary treatment for the settings slice while it migrates.
  appSettings: 'writable',
  // And the activeTab / agent slices.
  activeTab: 'writable',
  chatHistory: 'writable',
  currentAgentMode: 'writable',
  agentAutoScroll: 'writable',
  // And the tabs / layout slices.
  openTabs: 'writable',
  activeSidebarView: 'writable',
  // The per-turn agent state moved to the store when modules/agent-chat.js became a
  // Module (it writes them on regenerate); app.js still reads and writes them through the accessor.
  currentTurnEdits: 'writable',
  lastRunMode: 'writable',
  // The workspace file index moved to modules/explorer.js (store slices); app.js
  // Still reads and resets them as globals, and the tree entry points are module functions.
  allWorkspaceFiles: 'writable',
  allWorkspaceFilesTruncated: 'writable',
  renderFileTree: 'readonly',
  highlightActiveTreeNode: 'readonly',
  setupExplorer: 'readonly',
  // The tab lifecycle moved to modules/editor-tabs.js. app.js calls its entry
  // Points by name; the module calls these app.js functions back (they are function declarations,
  // Therefore globals) plus the ones the other modules publish.
  openFileInEditor: 'readonly',
  refreshOpenTabIfLoaded: 'readonly',
  setActiveTab: 'readonly',
  closeTab: 'readonly',
  requestCloseTab: 'readonly',
  renderTabs: 'readonly',
  updateEditorContextActions: 'readonly',
  saveTabNow: 'readonly',
  saveAllDirtyTabs: 'readonly',
  setStatusSlot: 'readonly',
  updateLspStatus: 'readonly',
  updateBreadcrumbs: 'readonly',
  applyPendingLocation: 'readonly',
  scheduleLivePreviewSync: 'readonly',
  scheduleDiagnosticsUpdate: 'readonly',
  updateSidebarOutline: 'readonly',
  renderEditorBreakpoints: 'readonly',
  refreshRunConfig: 'readonly',
  syncRunAvailability: 'readonly',
  renderLivePreviewFromActiveTab: 'readonly',
  openSplitEditor: 'readonly',
  toggleMiniWindow: 'readonly',
  formatActiveDocument: 'readonly',
  getLanguageInfo: 'readonly',
  normalizePath: 'readonly',
  notifyLspDidOpen: 'readonly',
  notifyLspDidClose: 'readonly',
  notifyLspDidChange: 'readonly',
  notifyLspDidSave: 'readonly',
  persistRecoveryNow: 'readonly',
  persistSessionNow: 'readonly',
  // Session restore / crash recovery / safe close / external file changes moved
  // To modules/session.js. app.js's reload wrapper calls these four; the two seams are how it feeds
  // The recovery payload and the pending-close save results back into the module.
  restoreSessionForWorkspace: 'readonly',
  advanceSessionRestore: 'readonly',
  handleCloseRequest: 'readonly',
  handleExternalFileChange: 'readonly',
  setRecoveryPayload: 'readonly',
  handlePendingCloseSaveResult: 'readonly',
  // The LSP client lifecycle and the Monaco providers moved to
  // Modules/lsp.js. initMonaco() still calls registerLspProviders() once Monaco is loaded, and the
  // Host-event registry calls the workspace-edit review queue when the host offers one.
  registerLspProviders: 'readonly',
  queueLspWorkspaceEditReview: 'readonly',
  setPendingNavigateLocation: 'readonly',
  recordTimelineEvent: 'readonly',
  setActivityStatus: 'readonly',
  showToast: 'readonly',
  showConfirmDialog: 'readonly',
  // The command registry + palette moved to modules/command-palette.js. app.js
  // Calls these by name (the Ctrl+Shift+P fallback, the quick-open button, the boot-time setup), and
  // The registry's entries call the app.js/module functions below back when a command runs.
  openCommandPalette: 'readonly',
  openQuickOpenModal: 'readonly',
  renderQuickOpenList: 'readonly',
  setupCommandPalette: 'readonly',
  saveCurrentFile: 'readonly',
  goToLine: 'readonly',
  runEditorActionId: 'readonly',
  toggleSplitEditor: 'readonly',
  closeSplitEditor: 'readonly',
  openSymbolQuickPick: 'readonly',
  promptNewFile: 'readonly',
  promptNewFolder: 'readonly',
  toggleSmartRun: 'readonly',
  toggleTerminalPanel: 'readonly',
  addTerminalTab: 'readonly',
  focusEditorView: 'readonly',
  focusAgentView: 'readonly',
  switchSidebarView: 'readonly',
  toggleWorkspacePane: 'readonly',
  switchBottomView: 'readonly',
  debugStartOrContinue: 'readonly',
  startDebuggingActiveFile: 'readonly',
  toggleEditorBreakpoint: 'readonly',
  openCodebaseSearch: 'readonly',
  openBranchPicker: 'readonly',
  showInputDialog: 'readonly',
  openInlineAiAssist: 'readonly',
  openAgentWithTerminalError: 'readonly',
  openSettingsToTab: 'readonly',
  showKeybindingDialog: 'readonly',
  // The project launcher moved to modules/project-hub.js. app.js still opens
  // And closes it from the host handlers (init_state_response, workspace_changed), from
  // CompleteAuth() and from the empty-state "Recent projects" button.
  openProjectHub: 'readonly',
  closeProjectHub: 'readonly',
  renderProjectHubRecents: 'readonly',
  setupProjectHub: 'readonly',
  // The settings modal's wiring moved into modules/settings.js, which calls the
  // Slider/custom-select painters back by name (they are app.js function declarations).
  setupSettingsUI: 'readonly',
  applySettingsToUI: 'readonly',
  setupAutoSave: 'readonly',
  setupReadabilityModes: 'readonly',
  syncCustomSelect: 'readonly',
  updateSliderFill: 'readonly',
  syncAllSliders: 'readonly',
  // The status bar's slot painters moved to modules/status-bar.js; app.js calls
  // The boot-time setup and keeps writing the workspace/checkpoint/cursor slots directly.
  setupStatusBar: 'readonly',
  // The panes/splitters/sidebar switcher moved to modules/layout.js. app.js calls
  // These by name during boot and from the workspace handlers; the module calls the terminal/git/debug
  // Entry points back when a pane opens.
  restorePaneLayoutForWorkspace: 'readonly',
  setTerminalCollapsed: 'readonly',
  setupResizers: 'readonly',
  applyInitialPaneLayout: 'readonly',
  setupSidebarSwitcher: 'readonly',
  setupPaneButtons: 'readonly',
  fitActiveTerminal: 'readonly',
  refreshGitStatus: 'readonly',
  renderBreakpointsList: 'readonly',
  // Module-published globals called by the remaining thin app.js controller
  applyThemeAndOptics: 'readonly',
  setupCustomSelects: 'readonly',
  setupAuthGate: 'readonly',
  completeAuth: 'readonly',
  setupDraggableWindows: 'readonly',
  setupUtilityTabs: 'readonly',
  setupMonacoSnippets: 'readonly',
  setupSplitEditor: 'readonly',
  setupSymbolQuickPick: 'readonly',
  setupProblemsPanel: 'readonly',
  setupTestsPanel: 'readonly',
  setupKeyboardA11y: 'readonly',
  loadStoredState: 'readonly',
  hydrateSessions: 'readonly',
  hydrateCheckpoints: 'readonly',
  saveCurrentSessionToStorage: 'readonly',
  openImagePreview: 'readonly',
  renderChatImage: 'readonly',
  setupWorkspaceTools: 'readonly',
  shouldOfferContinue: 'readonly',
  renderContinueAction: 'readonly',
  attachFileToPrompt: 'readonly',
  renderPlanActions: 'readonly',
  setupAgentComposer: 'readonly',
  setupAgentMentions: 'readonly',
  setupTerminalHelper: 'readonly',
  setupTerminalTabs: 'readonly',
  handlePtyOutput: 'readonly',
  handlePtyExit: 'readonly',
  appendTerminalStream: 'readonly',
  killActiveTerminal: 'readonly',
  restartActiveTerminal: 'readonly',
  clearActiveTerminal: 'readonly',
  updateConsoleStatus: 'readonly',
  setupSearchAndReplace: 'readonly',
  handleSearchResponse: 'readonly',
  handleSearchCancelled: 'readonly',
  handleReplaceResponse: 'readonly',
  setupGitPanel: 'readonly',
  handleGitStatusResponse: 'readonly',
  handleGitBranchesResponse: 'readonly',
  openGitHistoryModal: 'readonly',
  handleGitDiffResponse: 'readonly',
  handleGitHunksResponse: 'readonly',
  setupInlineAiAssist: 'readonly',
  updateInlineAiStatus: 'readonly',
  updateInlineAiDelta: 'readonly',
  handleInlineAssistComplete: 'readonly',
  handleInlineAssistError: 'readonly',
  handleInlineAssistCancelled: 'readonly',
  setupSmartRun: 'readonly',
  recordTerminalOutput: 'readonly',
  handleTerminalCommandDone: 'readonly',
  handleRunConfigDetected: 'readonly',
  handleSmartRunEnded: 'readonly',
  setupCodebaseSearch: 'readonly',
  setupDebugger: 'readonly',
  handleDebugStarted: 'readonly',
  handleDebugPaused: 'readonly',
  handleDebugResumed: 'readonly',
  appendDebugConsoleLine: 'readonly',
  handleDebugStopped: 'readonly',
  handleDebugBreakpoints: 'readonly',
  renderVariables: 'readonly',
  startAssistantStreamMessage: 'readonly',
  appendStreamReasoningDelta: 'readonly',
  appendStreamContentDelta: 'readonly',
  finalizeAssistantStreamMessage: 'readonly',
  renderToolCardStart: 'readonly',
  renderToolApprovalPrompt: 'readonly',
  renderToolCardFinish: 'readonly',
  renderRunFooter: 'readonly',
  finalizeToolGroup: 'readonly',
  appendStaticAssistantMessage: 'readonly',
  openCheckpointDiff: 'readonly',
  renderTurnReviewCard: 'readonly',
  openContextInspector: 'readonly',
  openLastTurnReview: 'readonly',
  openHealthModal: 'readonly',
  logSwallowed: 'readonly',
  html: 'readonly',
  raw: 'readonly',
  setHtml: 'readonly',
  monaco: 'readonly',
  require: 'readonly',
  define: 'readonly',
  marked: 'readonly',
  DOMPurify: 'readonly',
  Terminal: 'readonly',
  FitAddon: 'readonly',
  WebLinksAddon: 'readonly',
  xterm: 'readonly'
};

export default [
  {
    ignores: [
      '**/node_modules/**',
      '**/bin/**',
      '**/obj/**',
      '**/dist/**',
      '**/publish/**',
      '**/wwwroot/vendor/**',
      'playwright-report/**',
      'test-results/**',
      'tests/ui/smoke.spec.js-snapshots/**'
    ]
  },
  {
    files: ['wwwroot/**/*.js'],
    plugins: { 'no-unsanitized': noUnsanitized },
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'script',
      globals: appGlobals
    },
    rules: safetyRules
  },
  {
    // The converted core files (and the module entry point) are ES modules.
    // Every file under wwwroot/modules is an ES module, so this is a glob rather than a
    // Hand-maintained list - adding a module used to mean editing this array.
    files: ['wwwroot/main.js', 'wwwroot/core/**/*.js', 'wwwroot/modules/**/*.js'],
    plugins: { 'no-unsanitized': noUnsanitized },
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'module',
      globals: appGlobals
    },
    rules: safetyRules
  },
  {
    files: ['scripts/**/*.js', 'tests/**/*.js', 'playwright.config.js'],
    plugins: { 'no-unsanitized': noUnsanitized },
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'module',
      globals: { ...globals.node, ...globals.browser }
    },
    rules: safetyRules
  }
];
