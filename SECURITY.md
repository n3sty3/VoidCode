# Security Policy

## Threat Model & Boundaries

VoidCode runs a local WPF host (`.NET 10`) paired with a Chromium WebView2 frontend (`http://app.local`). Because the host bridge can access files, Git, language servers, and terminals, VoidCode enforces defense in depth across five layers:

1. **WebView & Bridge Isolation**
   - Only the top-level frame of `http://app.local` receives the per-session cryptographic bridge nonce via `PostWebMessageAsJson` (never in `location.search`).
   - All dynamic HTML rendering goes through `setHtml()` + the escaping `html` tagged template (enforced via `eslint-plugin-no-unsanitized`), with Trusted Types CSP monitoring and DOMPurify sanitization on Markdown output.
   - HTML/SVG previews run inside a sandboxed iframe without `allow-same-origin` or top-navigation rights.

2. **Filesystem Path Policy**
   - `PathPolicy.IsInsideWorkspace()` canonicalizes paths, resolves symlinks and NTFS junctions via `ResolveLinkTarget(true)`, and rejects UNC paths, `\\?\` extended prefixes, NTFS alternate data streams (`file:stream`), and trailing dots/spaces.

3. **Workspace Trust**
   - Opening an unknown folder defaults to **Restricted mode**: the agent is locked to **Review mode**, `run_command` and workspace tasks are blocked, workspace language servers stay off, and `.voidcode/mcp.json` servers are never started until the operator explicitly trusts the folder.
   - Workspace `.voidcode/settings.json` cannot override `AutoApproveTools`, `AuthorityMode`, or `ApiKey`.

4. **Agent Authority & Untrusted Content**
   - Default authority mode is **Review**; **Workspace auto-approve** limits automatic execution to strictly in-workspace file edits; **Full system** requires explicit opt-in.
   - File contents, `web_fetch` pages, and MCP tool outputs are wrapped in `<untrusted_content source="...">` boundaries. After external web or MCP content enters a turn, mutating tools escalate to explicit operator approval in every mode except Full System.
   - Every tool decision is appended to `%APPDATA%\VoidCode\logs\agent-audit.jsonl` with SHA-256-hashed arguments.

5. **Credential Storage & Log Redaction**
   - Provider API keys are encrypted with Windows DPAPI (`CurrentUser`) in `%APPDATA%\VoidCode\credentials\provider-key.dat` and never serialized to `settings.json` or sent back to the WebView.
   - Rotating logs and crash diagnostics pass through `LoggingService.Redact()` to strip bearer tokens, `Authorization` headers, `sk-*` keys, and stored secrets.

## Reporting a Vulnerability

Please open a private security advisory on the repository (or contact the maintainer directly) with reproduction steps and impact rather than filing a public issue.
