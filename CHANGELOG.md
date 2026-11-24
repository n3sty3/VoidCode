# Changelog

## v1.1.0 — Cross-Platform & Flagship AI Workbench Release

### Native Cross-Platform (Windows, macOS Apple Silicon/Intel & Linux)
- **Native macOS (`VoidCode.app` + `.dmg`) & Linux (`.tar.gz` / `.deb`) Hosts:** Extracted `VoidCode.Core` (`net10.0`) and `HostController` shared between Windows WPF/WebView2 and `VoidCode.Mac` (`Photino.NET` WKWebView / WebKitGTK), complete with POSIX `openpty`/`posix_spawn` interactive PTY (`UnixPty`), macOS Keychain secret storage, Mach-O binary inspection, macOS `⌘` shortcuts, Homebrew Cask, Winget, and Scoop manifests.
- **Multi-Window Support (`Ctrl+Shift+N` / `⌘⇧N`):** Launch independent VoidCode windows with isolated per-PID WebView2 profiles.

### VoidAgent Core Upgrades & Flagship AI Workflows
- **Persistent Cross-Session Agent Memory Bank (`save_memory`, `delete_memory`, Command Palette -> Memory Bank):** VoidAgent now remembers project build commands, repo invariants, and global operator preferences across sessions (`.voidcode/memory.json` + global profile), with a full glass Memory Bank modal to inspect, add, filter, and delete memories.
- **Provider Reasoning Effort & Extended Thinking:** Configurable **Reasoning Effort** (`low` / `medium` / `high`) wired to OpenAI reasoning models (`o1`/`o3`/`o4`/`gpt-5`) and native Anthropic Claude 3.7/4 extended thinking budgets, plus **Auto-Verify after Edits** toggle.
- **Mission Control — Parallel Git-Worktree Agents (`Ctrl+Shift+B`):** Spawn multiple isolated background VoidAgent missions in parallel Git worktrees, inspect diffs, merge cleanly, or discard.
- **AI Tab Autocomplete (FIM Ghost Text) + Local AI Auto-Discovery:** Real-time inline Monaco ghost text backed by a dedicated fast/local model slot (Ollama / LM Studio / Groq / Gemini Flash) with 1-click **Scan Local AI**.
- **AI Branch & Pre-PR Code Reviewer:** Audits your feature branch vs `main`/`master` (or uncommitted changes) into severity-tagged findings (`CRITICAL`, `WARNING`, `SUGGESTION`) with clickable `file:line` jumps into Monaco and 1-click **Fix with VoidAgent**.
- **Live Cost (`$`) & Prompt-Cache Savings:** Real-time Anthropic ephemeral `cache_control`, cached token tracking, and per-turn USD cost estimation.
- **Hybrid AST-Boundary + Semantic Vector Codebase Index** and **Streamable HTTP/SSE MCP Transport + 1-Click MCP Gallery**.

### Built-In Developer Tools & Obsidian Glass Polish
- **Custom Obsidian Glass Monaco Right-Click Context Menu:** Replaced Monaco's default gray context menu with VoidCode's native frosted-glass `.context-menu` featuring grouped VoidAgent actions, LSP navigation/refactoring, clipboard controls, and keyboard navigation.
- **Built-In API / HTTP Client (`Ctrl+Shift+R`):** Native Postman/Thunder-Client-style REST workbench with headers, JSON body, request history, latency/status metrics, pretty JSON output, `.http` parser, and 1-click **Generate Types / Tests with VoidAgent**.
- **Built-In SQLite Database Explorer:** Powered by `Microsoft.Data.Sqlite` — auto-discovers `.db`/`.sqlite` files in the workspace, inspects tables/columns/row-counts, runs SQL queries in a scrollable grid, and attaches schemas directly to VoidAgent.
- **1-Click "Import from VS Code / Cursor":** Detects VS Code, VS Code Insiders, and Cursor profiles across Windows/macOS/Linux and imports editor settings and recent folders in a single click.
- **Native x86/x86-64 Disassembler & Hex Workbench (`Ctrl+Shift+X`):** Pure-C# `Iced` disassembler and interactive hex/ASCII workbench.
- **Expanded Live Monaco Editor Settings:** Added live controls for Font Family, Tab Size, Spaces vs Tabs, Font Ligatures, Line Numbers (`on`/`relative`/`off`), Render Whitespace, Cursor Style, Smooth Scrolling, Bracket Pair Colorization, and Sticky Scroll — plus fixed `Scan Local AI` button overflow in Settings.

## v1.0.0 — Production Release

### Standalone Windows Setup Installer & Desktop Distribution
- **Windows GUI Setup Installer (`VoidCode-Setup-1.0.0-win-x64.exe`):** Added a self-contained dark-themed Windows Setup Wizard (`scripts/VoidCodeSetup.cs`, built automatically by `scripts/package.ps1`) that installs VoidCode per-user to `%LOCALAPPDATA%\Programs\VoidCode` without requiring Administrator privileges, creates Desktop and Start Menu shortcuts, adds `voidcode` (`voidcode .`, `voidcode file:line:col`) to the user `PATH`, registers **"Open with VoidCode"** in the Windows Explorer context menu, registers VoidCode in **Windows Settings -> Installed Apps (Add/Remove Programs)**, and supports headless silent installs (`/S`).
- **In-App About & Diagnostics Modal:** Added `#about-modal` (`VoidCode: About & System Info`) with one-click system info copy and log folder access, plus assembly version metadata (`1.0.0.0`) on `VoidCode.exe`.

### VoidAgent Core & Multi-Provider AI Workflows
- **VoidAgent Core Identity & Specialist Studio:** Upgraded default agent doctrine to `VoidAgent Core` (`EXPLORE -> PATCH -> VERIFY` loop + `VOIDCODE IDE RUNTIME CONTRACT`) with a 5-profile **VoidAgent Specialist Studio** in Settings (`VoidAgent Core`, `Systems & Performance`, `Security & Reverse Engineering`, `Test-Driven Refactoring`, `Frontend & UI Architecture`).
- **9 First-Class AI Providers & Zero-Config Env Var Fallback:**
  - Native **Google Gemini** (`gemini-2.5-pro`, `gemini-2.5-flash`) with automatic `/v1beta/openai` endpoint normalization, `models/` prefix stripping, and `AIza...` log redaction.
  - Native **Anthropic Messages API** (`/v1/messages`, `tool_use`/`tool_result`, extended thinking deltas, base64 images) for `claude-sonnet-4` and `claude-opus-4`.
  - First-class **OpenAI** (`gpt-4.1`, `o4-mini`), **OpenRouter**, **xAI (Grok 3)**, **Groq** (`llama-3.3-70b-versatile`), **DeepSeek** (`deepseek-chat`, `deepseek-r1`), **Ollama (Local)**, and **LM Studio (Local)**.
  - Automatic environment variable API key fallback (`GEMINI_API_KEY`/`GOOGLE_API_KEY`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `OPENROUTER_API_KEY`, `GROQ_API_KEY`, `XAI_API_KEY`, `DEEPSEEK_API_KEY`) and a **"Get API key ↗"** launcher in Settings.
- **Clipboard Screenshot Paste (`Ctrl+V`) & Drag-and-Drop Attachments:** Paste screenshots directly into the Agent chat composer with live thumbnail previews in user chat bubbles and multimodal vision delivery, or drag-and-drop files onto the Agent sidebar.
- **10 `/slash` Workflows & `@` Context Mentions:** Added `/fix`, `/review`, `/test`, `/explain`, `/security`, `/init` (generates `AGENTS.md`), `/todo` (audits `TODO`/`FIXME` comments), `/commit` (drafts Conventional Commits from `@git`), `/doc` (generates docstrings/JSDoc/XML comments), and `/compact`, alongside `@file`, `@git`, `@terminal`, `@problems`, `@selection`, and `@folder`.
- **Clickable `file:line` Citations, Code Block Actions & Markdown Export:** Inline `file.ext:line` citations in Agent responses jump directly to that file and line in Monaco; code blocks include **Copy**, **Save to file**, and **Apply to editor**; conversations can be exported to `.md` via `Agent: Export Conversation as Markdown (.md)`.
- **Monaco Right-Click VoidAgent Context Menu & `Ctrl+K` Inline Assist:** Added 5 right-click `VoidAgent` actions in Monaco (`Edit Selection Inline (Ctrl+K)`, `Attach Selection to Chat`, `Explain Selection`, `Find Bugs & Edge Cases`, `Generate Unit Tests`).
- **Atomic Turn Transaction Ledger & Diff Review:** Added `TurnTransactionLedger` snapshotting creates/edits/deletes/moves (including binaries and files > 4 MB), per-file and per-hunk Accept/Reject diff review UI with `+added / -removed` line delta badges and `Prev`/`Next` navigation, one-click whole-turn revert, live token usage pill (`#btn-agent-token-pill`), and the **Agent Context Inspector** modal.
- **Stdio MCP Client:** Added `.voidcode/mcp.json` stdio MCP client gated by Workspace Trust and operator approval.

### Obsidian Glass Workbench, Editor, LSP, Terminal & Debugger
- **Unified 5-Tier Obsidian-Slate Design System:** Redesigned top bar, user avatar operator pill, custom themed `<select>` menus, 860px 2-column Settings modal, 680px Workspaces Hub, context & tools popovers, and a single-bar 46px floating utility window header (`Preview`, `Sessions`, `Timeline`, `Playbooks`).
- **Daily-Driver Editor Ergonomics:** Added file-type SVG icons on tabs, **Pin / Unpin tab**, **drag-and-drop tab reordering**, middle-click close, right-click `#tab-context-menu` (`Close others`, `Close saved`, `Close all`, `Split editor right`, `Copy relative path`, `Reveal in File Explorer`), live window title (`● file.cs — project — VoidCode`), interactive breadcrumbs, live Outline symbol filter, **Distraction-Free Zen Mode (`Ctrl+Shift+Z`)**, word-wrap toggle (`Alt+Z`), editor zoom (`Ctrl+=`/`Ctrl+-`/`Ctrl+0`), and clickable status-bar `Ln/Col` + `LF/CRLF` toggle.
- **ripgrep (`rg --json`) & Managed LSP Installer:** Added streaming ripgrep search with `.voidcodeignore` support, managed language-server installation (`typescript`, `python`, `csharp`, `go`, `rust`, `cpp`), CLI formatter fallback (`prettier`, `ruff format`, `gofmt`, `rustfmt`, `dotnet csharpier`), and context-aware `F2` rename.
- **Real DAP Debuggers & OSC 133 Terminal Blocks:** Added `DapClient` for `.NET` (`netcoredbg`) and `Python` (`debugpy`) plus Node.js V8 debugging with **conditional breakpoints** and **inline expandable nested variable trees**, status-bar runtime picker (`#status-runtime`), `.voidcode/tasks.json` runner, inline `git blame`, merge-conflict CodeLens resolver, and PowerShell OSC 133/633 terminal command blocks with one-click **Fix with AI**.
