// FIX_PLAN T5.7: the typed contract between the classic scripts and the ES modules.
//
// Nothing here is compiled or emitted - this file only tells `tsc --checkJs` about the names that the
// classic scripts (app.js, console/*) publish on globalThis and the modules call by name. It mirrors
// the `appGlobals` block in eslint.config.js: add a name there and here together, or the module that
// uses it fails `npm run typecheck` with TS2304.
//
// Most declarations are deliberately `any`. app.js is still a classic script being emptied out one
// feature per session (T5.4/T5.5), so a precise signature here would be a second copy of the truth to
// keep in sync. The four things the modules lean on - the store, the bridge, the host-event registry
// and the console - do get real shapes, because those are the seams worth checking.

interface VoidStateApi {
  get(name: string, fallback?: any): any;
  set(name: string, value: any): void;
  update(name: string, mutator: (value: any) => any): any;
  subscribe(name: string, handler: (value: any) => void): () => void;
  snapshot(): Record<string, any>;
}

interface VoidBridgeApi {
  send(action: string, data?: Record<string, any>): string;
  request(action: string, data?: Record<string, any>, timeoutMs?: number): Promise<any>;
  resolve(id: string, result: any): boolean;
  subscribe(handler: (action: string, payload: any) => void): void;
  /** request ids awaiting a host answer - core/bridge.js exposes it for tests/diagnostics */
  pending: Map<string, any>;
  ready: Promise<void>;
}

type HostEventHandler = (payload: any, action?: string) => void;

interface VoidHostEventsApi {
  on(actions: string | string[], handler: HostEventHandler): void;
  register(map: Record<string, HostEventHandler>): number;
  dispatch(action: string, payload?: any): boolean;
  has(action: string): boolean;
  actions(): string[];
  unknownActions(): string[];
  reset(): void;
}

interface VoidConsoleApi {
  boot?(): any;
  focus?(): void;
  setWorkspace?(path: string): void;
  print?(text: any, kind?: string): void;
  execute?(line: string): any;
  /** present only while the lazy facade is installed (FIX_PLAN T5.5) */
  lazy?: boolean;
}

interface VoidIconsApi {
  apply(root?: any): void;
  refresh(): void;
  syncStates(): void;
  src(name: string, variant?: string): string;
  theme(): string;
}

declare var VoidState: VoidStateApi;
declare var VoidBridge: VoidBridgeApi;
declare var VoidHostEvents: VoidHostEventsApi;
declare var VoidConsole: VoidConsoleApi;
declare var VoidShell: any;
declare var VoidIcons: VoidIconsApi;

// Shared slices. The store owns them (core/state.js binds the accessors); the classic script mutates
// the objects in place, so a module reading them sees the same values.
declare var currentWorkspace: string;
declare var appSettings: Record<string, any>;
declare var activeTab: any;
declare var openTabs: any[];
declare var activeSidebarView: string;
declare var chatHistory: any[];
declare var currentAgentMode: string;
declare var agentAutoScroll: boolean;
declare var pendingNavigateLocation: any;
declare var streamRenderScheduled: boolean;
declare var updateConsoleStatus: any;
declare function logSwallowed(err: unknown, context?: string): void;

// Everything else the classic scripts publish, all `any` on purpose (see the note above).
declare var DOMPurify: any;
declare var FitAddon: any;
declare var Terminal: any;
declare var WebLinksAddon: any;
declare var addTerminalTab: any;
declare var advanceSessionRestore: any;
declare var allWorkspaceFiles: any;
declare var allWorkspaceFilesTruncated: any;
declare var applyInitialPaneLayout: any;
declare var applyPendingLocation: any;
declare var closeProjectHub: any;
declare var closeSplitEditor: any;
declare var closeTab: any;
declare var currentTurnEdits: any;
declare var debugStartOrContinue: any;
declare var define: any;
declare var fitActiveTerminal: any;
declare var focusAgentView: any;
declare var focusEditorView: any;
declare var formatActiveDocument: any;
declare var getLanguageInfo: any;
declare var goToLine: any;
declare var handleCloseRequest: any;
declare var handleExternalFileChange: any;
declare var handlePendingCloseSaveResult: any;
declare var highlightActiveTreeNode: any;
declare var html: any;
declare var lastRunMode: any;
declare var marked: any;
declare var monaco: any;
declare var normalizePath: any;
declare var notifyLspDidChange: any;
declare var notifyLspDidClose: any;
declare var notifyLspDidOpen: any;
declare var notifyLspDidSave: any;
declare var openAgentWithTerminalError: any;
declare var openBranchPicker: any;
declare var openCodebaseSearch: any;
declare var openCommandPalette: any;
declare var openFileInEditor: any;
declare var openInlineAiAssist: any;
declare var openProjectHub: any;
declare var openQuickOpenModal: any;
declare var openSettingsToTab: any;
declare var openSplitEditor: any;
declare var openSymbolQuickPick: any;
declare var persistRecoveryNow: any;
declare var persistSessionNow: any;
declare var promptNewFile: any;
declare var promptNewFolder: any;
declare var queueLspWorkspaceEditReview: any;
declare var raw: any;
declare var recordTimelineEvent: any;
declare var refreshGitStatus: any;
declare var refreshOpenTabIfLoaded: any;
declare var refreshRunConfig: any;
declare var registerLspProviders: any;
declare var renderBreakpointsList: any;
declare var renderEditorBreakpoints: any;
declare var renderFileTree: any;
declare var renderLivePreviewFromActiveTab: any;
declare var renderProjectHubRecents: any;
declare var renderQuickOpenList: any;
declare var renderTabs: any;
declare var requestCloseTab: any;
declare var require: any;
declare var restorePaneLayoutForWorkspace: any;
declare var restoreSessionForWorkspace: any;
declare var runEditorActionId: any;
declare var saveAllDirtyTabs: any;
declare var saveCurrentFile: any;
declare var saveTabNow: any;
declare var scheduleDiagnosticsUpdate: any;
declare var scheduleLivePreviewSync: any;
declare var setActiveTab: any;
declare var setActivityStatus: any;
declare var setHtml: any;
declare var setRecoveryPayload: any;
declare var setStatusSlot: any;
declare var setTerminalCollapsed: any;
declare var setupCommandPalette: any;
declare var setupExplorer: any;
declare var setupPaneButtons: any;
declare var setupProjectHub: any;
declare var setupResizers: any;
declare var setupSettingsUI: any;
declare var setupSidebarSwitcher: any;
declare var setupStatusBar: any;
declare var showConfirmDialog: any;
declare var showInputDialog: any;
declare var showKeybindingDialog: any;
declare var showToast: any;
declare var startDebuggingActiveFile: any;
declare var switchBottomView: any;
declare var switchSidebarView: any;
declare var syncAllSliders: any;
declare var syncCustomSelect: any;
declare var syncRunAvailability: any;
declare var toggleEditorBreakpoint: any;
declare var toggleMiniWindow: any;
declare var toggleSmartRun: any;
declare var toggleSplitEditor: any;
declare var toggleTerminalPanel: any;
declare var toggleWorkspacePane: any;
declare var updateBreadcrumbs: any;
declare var updateEditorContextActions: any;
declare var updateLspStatus: any;
declare var updateSidebarOutline: any;
declare var updateSliderFill: any;
declare var xterm: any;

// The DOM lib types querySelector/querySelectorAll as returning Element, which has no
// style/dataset/focus/click/tabIndex and no 'keydown' in its event map - but every element this app
// queries is an HTMLElement and is used exactly that way (the modules are full of
// `root.querySelector('.x').click()`). Declaring the five members once here is honest about that and
// keeps two dozen casts out of the modules; `value`/`checked` are NOT added, so reading a control
// still requires the typed control()/target cast above.
interface Element {
  readonly style: CSSStyleDeclaration;
  readonly dataset: DOMStringMap;
  tabIndex: number;
  focus(options?: FocusOptions): void;
  click(): void;
}

interface ElementEventMap {
  keydown: KeyboardEvent;
}

// Tool cards and other streamed elements stash their parsed arguments on the element itself
// (agent-stream.js `card._patch = {...}`, read back when the user approves the patch). Declaring the
// expando once is more honest than casting at every access.
interface HTMLElement {
  _patch?: { search: string; replace: string };
}

// The same names when reached through `window`, which is how the converted modules tend to use them.
interface Window {
  VoidState: VoidStateApi;
  VoidBridge: VoidBridgeApi;
  VoidHostEvents: VoidHostEventsApi;
  VoidConsole: VoidConsoleApi;
  VoidShell: any;
  VoidIcons: VoidIconsApi;
  monaco: any;
  Terminal: any;
  FitAddon: any;
  logSwallowed(err: unknown, context?: string): void;
  ensureVoidShell?: () => Promise<any>;
  applyThemeAndOptics?: (...args: any[]) => any;
  toggleWorkspacePane?: (pane: string, state?: boolean | null) => void;
  showToast?: (message: string, kind?: string) => void;
  showConfirmDialog?: (...args: any[]) => Promise<boolean> | any;
  showInputDialog?: (...args: any[]) => any;
  attachFileToPrompt?: (...args: any[]) => any;
  settings?: any;
  shortcuts?: any;
  themes?: any;
  playbooks?: any;
  inspector?: any;
  timeline?: any;
  chrome?: any;
}
