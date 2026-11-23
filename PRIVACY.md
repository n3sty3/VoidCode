# Privacy Policy

VoidCode is a local-first Windows IDE.

- **No telemetry by default:** VoidCode collects no analytics, usage metrics, crash uploads, or behavioral tracking.
- **Local storage only:** Preferences, DPAPI-encrypted API keys, recovery journals, checkpoints, audit logs, and crash reports stay strictly on your machine under `%APPDATA%\VoidCode` (or `VOIDCODE_STORAGE_ROOT` if overridden).
- **Network traffic you initiate:** VoidCode makes outbound network requests *only* when you explicitly configure and invoke:
  - An AI model provider (OpenRouter, Anthropic, DeepSeek, Ollama/LM Studio, or a custom endpoint) for chat, inline assist, or commit message generation.
  - The agent `web_fetch` tool (gated by approval in Review and Workspace Auto modes).
  - Managed language-server installation from the Environment Health modal (after explicit confirmation of the exact command).
  - External documentation/download links you click.
- **Context transparency:** Use **Agent: Inspect Context & Tokens** in the Command Palette at any time to see the exact prompt, rules, editor context, attachments, and history that will be sent on the next agent turn.
