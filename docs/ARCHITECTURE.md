# VoidCode Architecture

VoidCode is a desktop **agentic IDE**: a WPF (.NET) host process that runs the
agent/Services and hosts the entire UI inside **WebView2**. The frontend is plain
HTML/CSS/JS (no bundler); the backend is C# services invoked over a small
host bridge.

```
┌──────────────────────────── WPF Host (.NET 10) ───────────────────────────┐
│  MainWindow.xaml.cs ──▶ Bridge/BridgeRouter ──▶ Bridge/Handlers/*.cs ──┐  │
│        ▲                    │  (validated payloads, typed replies)     │  │
│        │                    ▼                                          ▼  │
│        │ SendToJs() / receive from JS                       Services/*.cs │
│  ┌─────┴───────────────── WebView2 (wwwroot/) ──────────────────────────┐ │
│  │  index.html ─▶ main.js (module entry) ─▶ core/* + modules/*          │ │
│  │                 app.js (orchestrator + Monaco + LSP)                  │ │
│  └──────────────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────────┘
```

## Backend (`Services/`, `Agent/`, `Bridge/`)

| Service | Responsibility |
| --- | --- |
| `AgentEngine.cs` | Public agent facade: connection test, loop entry, inline assist, commit message. |
| `Agent/AgentLoop.cs` | The turn: stream, assemble tool calls, approve, execute, repeat, account tokens. |
| `Agent/Tools/ToolCatalog.cs` | Tool schema sent to the model, approval set, plan-mode allow-list. |
| `Agent/Tools/ToolExecutor.cs` | Runs one tool call; workspace-escape gate; returns output + checkpoint id. |
| `Agent/ContextBuilder.cs` | Editor context and `[Referenced File: …]` expansion, with clipping bounds. |
| `Agent/Prompts.cs` | System prompt assembly, inline-assist contract, commit-message contract. |
| `Agent/AgentHttp.cs` | The single `HttpClient` and URL join shared by every provider call. |
| `Services/IGitClient.cs` | FIX_PLAN T6.4 seam: the 20 git operations the git domain performs. |
| `Services/GitClient.cs` | The real implementation: one delegation per member to `GitService`. |
| `Services/IFileSystem.cs` | FIX_PLAN T6.4 seam: the 19 filesystem calls the file-system and session domains make. |
| `Services/SystemFileSystem.cs` | The real implementation: one delegation per member to `FileSystemService`. |
| `Services/IProcessRunner.cs` | FIX_PLAN T6.4 seam: `RunAsync(fileName, args[, stdin])` → `ProcessResult`. |
| `Services/SystemProcessRunner.cs` | The real runner: hidden child process, UTF-8 capture, no shell. |
| `Services/WorkspaceWatcherService.cs` | One workspace watcher: tree/file debounce, ignore rules, index upkeep. |
| `Native/WindowInterop.cs` | Win32 chrome the window needs: dark title bar, drag, monitor-clamped maximize, themed icon. |
| `Services/LspFraming.cs` | LSP base-protocol framing (read/write) over a `Stream`, so it is testable. |
| `Services/LspService.cs` | Language-server process management + JSON-RPC provider plumbing. |
| `PtyService.cs` / `TerminalService.cs` | Pseudo-terminal + shell session management. |
| `CodebaseIndexService.cs` | Workspace file index (with fallback scanning) powering search/@-mentions. |
| `FileSystemService.cs` | File read/write, path resolution, safe listing. |
| `StorageService.cs` | Profile root: operators, sessions, checkpoints, recovery (see below). |
| `SettingsService.cs` | User settings + provider/API configuration persistence. |
| `LoggingService.cs` | Structured logging surfaced in the Debug panel. |

`StorageService.Root` is the one profile directory (`%APPDATA%\VoidCode`, or
`VOIDCODE_STORAGE_ROOT`). Settings, the DPAPI credential, the codebase index and the
logs all resolve through it, so a portable run - and the test suite - carries its
whole profile in one place.

`IProcessRunner`, `IFileSystem` and `IGitClient` are the seams that exist today. Every git
call goes through the runner, so the porcelain/diff/hunk parsers are tested against canned
output with no repository and no subprocess; it is swapped per test through
`GitService.UseProcessSeam(...)` and restored on teardown, in the non-parallel
`"Process seam"` collection. `FileSystemHandlers` **and** `SessionWindowHandlers` take an
`IFileSystem` (production passes nothing and gets `SystemFileSystem`), which is what makes
the *failure* paths testable: a locked file during save, a rename that collides, a read
that is denied, a checkpoint whose file has since been deleted. `GitHandlers` takes an
`IGitClient` (`GitClient` in production), which is what made the git domain's **success**
payloads testable at all - git is not installed on the machine this was written on, so
before the seam every one of the 21 routes only ever exercised the "not installed" branch.
The interfaces, not a container, are the point - the remaining T6.4 work is converting the
static services to instances so a composition root can inject these seams instead of a test
assigning statics.

## Bridge (`Bridge/`)

`MainWindow.CoreWebView2_WebMessageReceived` does four things, in this order:
validate the frame + nonce (`BridgeProtocol`), let `WorkspaceTrustService` answer its
own messages, hand the action to `BridgeRouter`, and answer anything nobody owns with
an `unknown_action` envelope. There is no `switch` left.

`BridgeRouter` registers `action → handler` with a typed request record; payloads are
validated (`required` fields) and requests get `{id, ok, data | error}`. Routes that
predate the envelope - most of them - are registered with `envelope: false` and keep
their legacy replies (`*_response` events), while still getting validation and typed
arguments. The handlers are grouped by domain: `FileSystem`, `Workspace`, `Search`,
`Git`, `Lsp`, `LspEdit`, `Terminal`, `TestsDebug`, `SettingsAgent`, `SessionWindow`,
plus `BridgeActions` for window chrome and JS diagnostics. Handlers receive seams
(`Func<string?> workspace`, `Action<string, object> send`, explicit callbacks) instead
of reaching into the window.

`MainWindow.xaml.cs` is what is left after Phase 6: **436 lines** (from 2547 at the
baseline) of window chrome, WebView2 setup, the one "switch to this folder" entry point
(`ActivateWorkspace`) and the message entry point. Watching moved to
`Services/WorkspaceWatcherService`, the Win32 calls to `Native/WindowInterop`, and every
action to a handler - the metric ratchet keeps the line count from creeping back.

## Frontend (`wwwroot/`)

`index.html` loads five vendored libraries (xterm, addon-fit, marked, DOMPurify, the
Monaco loader) and exactly **one** script of ours: `<script type="module" src="main.js">`.
There is no bundler and no build step - WebView2 is Chromium, so native ES modules work
straight off `http://app.local/` (FIX_PLAN T5.1).

`main.js` installs the `core/*` helpers (bridge, markdown, state store, host-event
dispatcher), calls each `modules/<name>.js` `init(ctx)` in order, then loads `app.js` -
the one remaining classic script, which is the Monaco/LSP/bootstrap orchestrator.
Whatever is still in `app.js` is the tightly-coupled editor core; feature code moves
out one slice at a time (T5.4).

Host messages go through `core/host-events.js`: `createHostEventDispatcher` holds an
`action → handler` map plus an unknown-action sink, and `app.js` registers its map via
`window.VoidHostEvents`, so a module can take over the actions it owns without editing
`app.js`.

Dialogs are handled by `core/modal-focus.js`. Dialogs are opened from a dozen places by
toggling a class or an inline style, so the module watches the DOM rather than each opener:
the topmost visible `[role="dialog"]` inside a modal surface gets the Tab cycle, everything
outside its ancestor chain is marked `inert`, focus moves to its first control, and returns
to whatever had focus when it closes. Two details worth keeping: visibility is tested with
`getClientRects()` (an `offsetParent` check is always null for a `position: fixed` overlay,
which is exactly how the previous trap silently did nothing), and a `role="dialog"` that is
not in a modal surface - the inline-AI popover - is deliberately not treated as modal.

### Module map

| Module | Owns |
| --- | --- |
| `core/bridge.js` | `sendHost()`, nonce handshake, inbound dispatch, request timeouts. |
| `core/markdown.js` | `renderMarkdown`, the `html`/`raw` helpers, trusted-types shims. |
| `core/state.js` | The slice store (`workspace`, `tabs`, `activeTab`, `settings`, `layout`, `agent`). |
| `core/host-events.js` | `action → handler` map for host events (replaces the old `switch`). |
| `core/modal-focus.js` | One focus trap for every dialog: Tab cycles, the window behind goes `inert`, focus returns to the opener. |
| `modules/agent-chat.js` | Chat message actions, regeneration/editing, run footer, patch preview. |
| `modules/agent-stream.js` | Streaming deltas + reasoning, tool cards, run-trace grouping. |
| `modules/codebase-search.js` | Indexed symbol / across-files results. |
| `modules/command-palette.js` | Palette + command registry. |
| `modules/debug.js` | Debug/log panel rendering. |
| `modules/editor-tabs.js` | Editor tabs, dirty state, save. |
| `modules/explorer.js` | File tree, context menu, create/rename/delete. |
| `modules/git.js` | Source-control panel + status/diff. |
| `modules/icons.js` | Icon pack. |
| `modules/inline-ai.js` | Inline ghost-text / quick actions. |
| `modules/layout.js` | Panes, resizers, drawers, lazy shell reveal. |
| `modules/lsp.js` | LSP client + Monaco provider wiring. |
| `modules/project-hub.js` | Start screen / recent projects. |
| `modules/run-debug.js` | Run/debug controls + output. |
| `modules/search.js` | Workspace text search. |
| `modules/session.js` | Chat sessions + crash-recovery prompts. |
| `modules/settings.js` | Settings modal, provider config, shared `appSettings`. |
| `modules/shell.js` | Lazy VoidShell fallback terminal. |
| `modules/status-bar.js` | Status bar. |
| `modules/terminal.js` | xterm wiring, terminal tabs. |
| `modules/theme.js` | Theme, readability modes, icon theme, window icon. |
| `modules/topbar.js` | Top-bar chips: keeps the operator label, hub label and collapsed-pill tooltip in sync. |

## Conventions

- **New feature → its own `modules/<name>.js`** exporting `init(ctx)`; register it in
  `main.js`. Host actions it owns go through `core/host-events.js`. Every file under
  `wwwroot/modules/` is linted as an ES module by glob, so a new module needs no config edit.
- **Nothing in the top bar disappears.** Below its breakpoints a control collapses to an
  icon with its full name in the tooltip (`#btn-open-settings`, `#btn-operator-pill`), and
  the "..." menu is the one permanent route to the six tool windows.
- Shared UI state goes through `core/state.js` slices - never a cross-module global.
- No `innerHTML`: use `html`/`raw` from `core/markdown.js` (the metrics ratchet keeps
  the count at 0).
- C# side: a new bridge action is a typed `Register<TReq, TRes>` in the domain handler
  under `Bridge/Handlers/`, not a `case` in the window.
- All generated assets live under `wwwroot/`; the WPF project packs `wwwroot/**`.
- Generated/build output (`bin/`, `obj/`, `dist/`, `publish/`) is git-ignored.

## Build, test, run

```powershell
dotnet build VoidCode.slnx -c Debug        # compile
dotnet test  VoidCode.slnx                 # xUnit (host)
npm run test:unit                          # node tests for core helpers
npm run typecheck                          # tsc --noEmit (checkJs over core + modules)
npm run lint                               # eslint
npx playwright test                        # UI harness (mock host)
node scripts/metrics.js --check            # ratchet
scripts/package.ps1                        # self-contained publish
```

CI (`.github/workflows/ci.yml`) runs all of the above on two jobs - the .NET build and
tests, then `npm ci` + unit tests + lint + typecheck + the metrics ratchet + Playwright -
and uploads the Playwright report.

## Testing

- **Host**: `tests/VoidCode.Tests/` (xUnit). `BridgeRouterTests` and one file per
  handler domain, plus `BridgeProtocolTests`, `PathPolicyTests`, `WorkspaceTrustTests`,
  `LoggingRedactionTests`, `CspPolicyTests`, `StorageServiceTests`, `AgentSplitTests`
  and `ServiceTests` for the services themselves. Tests that touch the profile run under
  a temp `StorageService.Root`; the redirect for a whole run lives in `TestStorageRoot`.
- **Frontend**: `tests/ui/*.spec.js` (Playwright) against `tests/ui/mock-host.js`, so the
  UI can be exercised without WebView2 or real services. `tests/*.test.js` cover the pure
  helpers (`bridge-timeout`, `log-swallowed`, `html-template`, `state`, `host-events`).
- **Ratchet**: `scripts/metrics.js` compares line counts, branch counts, test counts and
  forbidden patterns against `scripts/baselines.json`; a metric may only improve.
- **Payload contract**: `npm run audit:payloads` (`tools/payload-audit.mjs`) compares every key
  the C# handlers put on the wire with every key the page reads, resolves POCO payloads through
  their `[JsonPropertyName]`, and fails when a key would arrive with the wrong casing. It exists
  because two shipping bugs came from exactly that, and because the UI harness' mock host speaks
  camelCase and therefore cannot see it.