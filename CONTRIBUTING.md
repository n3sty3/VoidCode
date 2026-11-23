# Contributing to VoidCode

Thank you for your interest in contributing to **VoidCode**!

## Prerequisites

- **OS:** Windows 10 or Windows 11
- **.NET SDK:** .NET 10 SDK (`dotnet --version`)
- **WebView2:** Microsoft Edge WebView2 Runtime (pre-installed on Windows 10/11)
- **Node.js:** Node.js 20+ (`npm ci` installs ESLint, TypeScript `checkJs`, and Playwright)

## Architecture Overview

- **`MainWindow.xaml.cs` & `Bridge/`** — Native `.NET 10` WPF window and nonce-authenticated JSON bridge router (`BridgeRouter`, `Bridge/Handlers/*`).
- **`Agent/`** — **VoidAgent Core** autonomous engineering loop (`AgentLoop.cs`), provider implementations (`OpenAiCompatibleProvider.cs`, `AnthropicProvider.cs`), token budgeter (`ContextBudget.cs`), atomic rollback ledger (`TurnTransactionLedger.cs`), tool executor (`Tools/ToolExecutor.cs`), and stdio MCP client (`Mcp/McpManager.cs`).
- **`Services/`** — Host services for file system, `PathPolicy`, `SecretService` (Windows DPAPI), `LspService` + `LspInstallService`, `DebugService` + `DapClient`, `PtyService` (ConPTY), `GitService`, `TestService`, and `CodebaseIndexService`.
- **`wwwroot/`** — ES-module frontend (`main.js`, `core/*`, `modules/*`, `styles/*`) running inside WebView2 with Monaco Editor and xterm.js.
- **`scripts/`** — Quality ratchet (`metrics.js`), Windows GUI installer (`VoidCodeSetup.cs`), PowerShell installer (`install.ps1`), and release bundler (`package.ps1`).

## Quality Gates (Required Before Submitting a PR)

Every change must pass all repository quality gates with zero warnings:

```powershell
# 1. Metrics ratchet (file size, 0 innerHTML, 0 empty catches, 0 undefined CSS vars)
node scripts/metrics.js --check

# 2. Zero-warning ESLint + JSDoc TypeScript check + Node unit tests + Bridge payload audit
npm run lint -- --max-warnings=0
npm run typecheck
npm run test:unit
npm run audit:payloads

# 3. .NET 10 unit & integration test suite
dotnet test VoidCode.slnx -c Release

# 4. Playwright Chromium UI test suite
npx playwright test
```

To build the full Windows release package (`dist\VoidCode-Setup-1.0.0-win-x64.exe` and `dist\VoidCode-win-x64.zip`):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1
```
