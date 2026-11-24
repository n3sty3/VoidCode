// VoidCode Frontend Controller — Harbor Glass, Auth Gate, Project Hub & Draggable Utility Windows
const xtermStylesheet = document.getElementById('xterm-stylesheet');
if (xtermStylesheet) xtermStylesheet.rel = 'stylesheet';

window.addEventListener('error', (event) => {
  console.error('[VoidCode Uncaught Error]', event.error || event.message);
});
window.addEventListener('unhandledrejection', (event) => {
  console.error('[VoidCode Unhandled Rejection]', event.reason);
});

let monacoEditor = null;
// A module cannot see a classic script's `let`, so the modules that still need the
// Editor handle ask for it through this getter.
window.getMonacoEditor = function getMonacoEditor() { return monacoEditor; };
// Host Bridge
const pendingHostRequests = window.VoidBridge.pending;
const sendHost = (action, data = {}) => window.VoidBridge.send(action, data);

// Toasts, input/confirm dialogs, selects, auth gate & utility windows moved to modules/dialogs.js.

// Sessions, timeline & playbooks moved to modules/workspace-tools.js.

// Built-in Monaco snippets moved to modules/editor-features.js.

// LSP client lifecycle + Monaco providers moved to modules/lsp.js ():
// MapLspKindToMonaco/mapLspSymbolKindToMonaco/lspDocumentationValue, the semantic-token type and
// Modifier lists, ensureModelsForLspEdits/lspEditsToMonaco/compareLspPositions/
// ValidateLspEditsForModel/applyReviewedLspWorkspaceEdits + queueLspWorkspaceEditReview,
// NotifyLspDidOpen/Change/Save/Close with the doc-version and debounce maps, and
// RegisterLspProviders() - which initMonaco() still calls once Monaco is up (below).

// Initialize Monaco Editor
function initMonaco() {
  return new Promise((resolve) => {
    if (window.monaco || typeof require === 'undefined') {
      resolve();
      return;
    }
    require.config({ paths: { 'vs': 'vendor/vs' } });
    require(['vs/editor/editor.main'], function () {
      monaco.editor.defineTheme('harbor-glass', {
        base: 'vs-dark',
        inherit: true,
        semanticHighlighting: true,
        rules: [
          { token: 'comment', foreground: '6d7f93', fontStyle: 'italic' },
          { token: 'keyword', foreground: '8fd3c1' },
          { token: 'string', foreground: 'e9b872' },
          { token: 'number', foreground: 'f2918a' }
        ],
        colors: {
          'editor.background': '#00000000',
          'editor.foreground': '#e7edf2',
          'editor.lineHighlightBackground': '#ffffff08',
          'editorLineNumber.foreground': '#526275',
          'editorLineNumber.activeForeground': '#8fd3c1',
          'editorGutter.background': '#00000000',
          'editor.selectionBackground': '#8fd3c12e',
          'editorCursor.foreground': '#8fd3c1',
          'minimap.background': '#08101766'
        }
      });

      monacoEditor = monaco.editor.create(document.getElementById('monaco-mount'), {
        value: '',
        language: 'plaintext',
        theme: 'harbor-glass',
        automaticLayout: true,
        fontSize: 14,
        lineHeight: 22,
        fontFamily: "'JetBrains Mono', 'Cascadia Code', Consolas, monospace",
        fontLigatures: true,
        minimap: { enabled: true },
        scrollBeyondLastLine: false,
        smoothScrolling: true,
        cursorBlinking: 'smooth',
        glyphMargin: true,
        tabSize: 4,
        padding: { top: 14, bottom: 14 }
      });

      monaco.editor.registerEditorOpener({
        async openCodeEditor(_source, resource, selection) {
          const path = resource?.fsPath;
          if (!path) return null;
          const line = selection?.startLineNumber || selection?.lineNumber || 1;
          const col = selection?.startColumn || selection?.column || 1;
          const existing = openTabs.find(tab => normalizePath(tab.path) === normalizePath(path));
          if (existing) {
            setActiveTab(existing);
            monacoEditor.setPosition({ lineNumber: line, column: col });
            monacoEditor.revealPositionInCenter({ lineNumber: line, column: col });
          } else {
            setPendingNavigateLocation({ line, col });
            sendHost('read_file', { path });
          }
          return monacoEditor;
        }
      });

      monacoEditor.onMouseDown((e) => {
        if (e.target.type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN) {
          const line = e.target.position.lineNumber;
          toggleEditorBreakpoint(line);
        }
      });

      monacoEditor.onDidChangeCursorPosition((e) => {
        document.getElementById('status-cursor').textContent = `Ln ${e.position.lineNumber}, Col ${e.position.column}`;
      });

      monacoEditor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
        saveCurrentFile();
      });

      monacoEditor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyK, () => {
        openInlineAiAssist();
      });

      monacoEditor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Backslash, () => {
        toggleSplitEditor();
      });

      monacoEditor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyO, () => {
        openSymbolQuickPick();
      });

      monacoEditor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyP, () => {
        openCommandPalette();
      });

      monacoEditor.addCommand(monaco.KeyMod.Shift | monaco.KeyMod.Alt | monaco.KeyCode.KeyF, () => {
        formatActiveDocument();
      });

      setupMonacoSnippets();
      registerLspProviders();

      resolve();
    });
  });
}

// Receive Messages from C# Host
// Subscribe a stable thunk, not the function itself. The reliability hooks below
// Reassign handleHostMessage, and a reference captured here would keep pointing at the original -
// That is why request_close / file_changed / the post-save hooks never ran before this fix.
window.VoidBridge.subscribe((action, payload) => handleHostMessage(action, payload));

// Continue affordance moved to modules/agent-composer.js.

// ==========================================
// HOST EVENTS ()
// A Map<action, handler> in core/host-events.js replaces the 86-case switch this file used to
// Hold. The bodies below are the old case bodies unchanged; steps 2-10 move them out of
// This file one feature per session, and an action with no handler is reported by the registry.
// ==========================================
const resolveBridgeRequest = (payload) => window.VoidBridge.resolve(payload?.reqId, payload);
const resolvePendingHostRequest = (payload) => {
  if (payload && payload.reqId && pendingHostRequests.has(payload.reqId)) {
    const cb = pendingHostRequests.get(payload.reqId);
    pendingHostRequests.delete(payload.reqId);
    cb(payload.result);
  }
};
const resolveLspResult = (payload) => window.VoidBridge.resolve(payload?.reqId, payload?.result);

function createHostEventHandlers() {
  return {
    'init_state_response': (payload) => {
      appSettings = payload.settings || {};
      currentWorkspace = payload.workspace;
      setRecoveryPayload(payload.recovery || null);
      updateWorkspaceUI(currentWorkspace);
      if (currentWorkspace) restorePaneLayoutForWorkspace(currentWorkspace);
      applySettingsToUI(appSettings, payload.settingScopes);
      if (payload.tree) renderFileTree(payload.tree);
      hydrateSessions(payload.sessions);
      hydrateCheckpoints(payload.checkpoints);
      renderProjectHubRecents();
      if (currentWorkspace) {
        sendHost('get_all_files');
      }
    },
    'auth_operator_response': (payload) => {
      const errEl = document.getElementById('auth-error-msg');
      if (!payload.success) {
        if (errEl) {
          errEl.textContent = payload.message || 'Authentication failed.';
          errEl.style.display = 'block';
        }
      } else {
        completeAuth(payload.username, payload.role, true);
      }
    },
    'workspace_changed': (payload) => {
      currentWorkspace = payload.workspace;
      setRecoveryPayload(payload.recovery || null);
      if (payload.recentWorkspaces) {
        appSettings.RecentWorkspaces = payload.recentWorkspaces;
      }
      updateWorkspaceUI(currentWorkspace);
      restorePaneLayoutForWorkspace(currentWorkspace);
      if (payload.tree) renderFileTree(payload.tree);
      renderProjectHubRecents();
      closeProjectHub();
      allWorkspaceFiles = [];
      allWorkspaceFilesTruncated = false;
      sendHost('get_all_files');
      try { window.VoidConsole?.setWorkspace(currentWorkspace); } catch (err) { logSwallowed(err, "app.setWorkspaceConsole"); }
      showToast(`Opened ${currentWorkspace.split(/[\\/]/).pop()}`, 'success');
    },
    'files_selected': (payload) => {
      const files = payload.files || [];
      const target = payload.target || 'attach';
      if (files.length === 0) return;

      if (target === 'attach') {
        files.forEach(f => attachFileToPrompt(f));
        toggleWorkspacePane('agent', true);
        showToast(`Attached ${files.length} file${files.length === 1 ? '' : 's'} from PC`, 'success');
      } else if (target === 'editor') {
        files.forEach(f => sendHost('read_file', { path: f }));
      } else if (target === 'inspector') {
        document.getElementById('inspector-path-input').value = files[0];
        sendHost('inspect_file', { path: files[0] });
      }
    },
    // Tree_refreshed / all_files_response / directory_children_response are
    // Registered by modules/explorer.js, which owns the tree and the workspace file index.
    // Read_file_response is handled by modules/editor-tabs.js ().
    'read_text_file_response': (payload) => {
      window.VoidBridge.resolve(payload?.reqId, payload);
      return;

    // VoidShell host responses
    },
    'shell_spawn_response': resolveBridgeRequest,
    'list_dir_response': resolveBridgeRequest,
    'fs_write_response': resolveBridgeRequest,
    'create_entry_response': resolveBridgeRequest,
    'delete_entry_response': resolveBridgeRequest,
    'image_preview_response': (payload) => openImagePreview(payload.path, payload.dataUri, payload.sizeBytes),
    'checkpoint_diff_response': (payload) => openCheckpointDiff(payload.path, payload.beforeContent, payload.afterContent),
    // Save_file_response is handled by modules/editor-tabs.js ().
    'lsp_hover_response': resolvePendingHostRequest,
    'lsp_definition_response': resolvePendingHostRequest,
    'lsp_completion_response': (payload) => {
      if (payload && payload.reqId && pendingHostRequests.has(payload.reqId)) {
        const cb = pendingHostRequests.get(payload.reqId);
        pendingHostRequests.delete(payload.reqId);
        cb(payload.items);
      }
    },
    'lsp_references_response': resolveLspResult,
    'lsp_signature_help_response': resolveLspResult,
    'lsp_document_symbols_response': resolveLspResult,
    'lsp_inlay_hints_response': resolveLspResult,
    'lsp_semantic_tokens_response': resolveLspResult,
    'lsp_rename_response': resolveLspResult,
    'lsp_formatting_response': resolveLspResult,
    'lsp_code_actions_response': resolveLspResult,
    'lsp_resolve_code_action_response': resolveLspResult,
    'lsp_execute_command_response': resolveLspResult,
    'lsp_status': (payload) => updateLspStatus(payload),
    'lsp_workspace_edit_review': (payload) => queueLspWorkspaceEditReview(payload),
    'lsp_restarted': () => {
      if (activeTab) notifyLspDidOpen(activeTab);
      showToast('Language servers restarted.', 'success');
    },
    'lsp_diagnostics': (payload) => {
      if (payload && payload.path) {
        const diags = Array.isArray(payload.diagnostics) ? payload.diagnostics : [];
        const tab = openTabs.find(t => normalizePath(t.path) === normalizePath(payload.path));
        if (tab && tab.model && !tab.model.isDisposed() && window.monaco && monaco.editor && monaco.editor.setModelMarkers) {
          const markers = diags.map(d => {
            const sLine = d.startLine ?? d.StartLine ?? 1;
            const sCol = d.startCol ?? d.StartCol ?? 1;
            const eLine = d.endLine ?? d.EndLine ?? sLine;
            const eCol = d.endCol ?? d.EndCol ?? (sCol + 1);
            const sev = d.severity ?? d.Severity ?? 1;
            const msg = d.message ?? d.Message ?? '';
            const code = d.code ?? d.Code ?? '';
            const source = d.source ?? d.Source ?? 'LSP';

            let monacoSeverity = monaco.MarkerSeverity.Error;
            if (sev === 2) monacoSeverity = monaco.MarkerSeverity.Warning;
            else if (sev === 3) monacoSeverity = monaco.MarkerSeverity.Info;
            else if (sev === 4) monacoSeverity = monaco.MarkerSeverity.Hint;

            return {
              severity: monacoSeverity,
              message: (source ? `[${source}] ` : '') + msg + (code ? ` (${code})` : ''),
              startLineNumber: sLine,
              startColumn: sCol,
              endLineNumber: eLine,
              endColumn: eCol
            };
          });
          monaco.editor.setModelMarkers(tab.model, 'lsp', markers);
          scheduleDiagnosticsUpdate();
        }
      }
    },
    'entry_deleted': (payload) => {
      closeTab(payload.path);
      if (payload.checkpointId) {
        recordTimelineEvent('Deleted', payload.path, payload.checkpointId);
      }
      showToast('Deleted', 'info');
    },
    'checkpoint_restored': (payload) => {
      showToast(`Reverted ${payload.path.split(/[\\/]/).pop()}`, 'success');
      if (payload.exists && payload.content !== null) {
        refreshOpenTabIfLoaded(payload.path, payload.content);
      } else {
        closeTab(payload.path);
      }
    },
    'inspect_file_response': (payload) => {
      document.getElementById('inspector-path-input').value = payload.path || '';
      document.getElementById('inspector-output-box').textContent = payload.report || 'No output.';
      toggleMiniWindow('win-inspector', true);
    },
    'pty_output': (payload) => {
      // The PTY cases moved into modules/terminal.js, which owns the sessions.
      handlePtyOutput(payload);
    },
    'pty_exit': (payload) => handlePtyExit(payload),
    'terminal_stream': (payload) => {
      appendTerminalStream(payload.text, payload.isError);
      recordTerminalOutput(payload.text, payload.isError);
    },
    'terminal_command_done': (payload) => {
      setActivityStatus(`Exited (${payload.exitCode})${payload.cwd ? ' \u00b7 ' + payload.cwd.split(/[\\/]/).pop() : ''}`);
      handleTerminalCommandDone(payload.exitCode);
    },
    'run_config_detected': (payload) => handleRunConfigDetected(payload.config),
    'smart_run_ended': (payload) => handleSmartRunEnded(payload.exitCode),
    'search_workspace_response': (payload) => handleSearchResponse(payload),
    'search_workspace_cancelled': (payload) => handleSearchCancelled(payload),
    'replace_workspace_response': (payload) => handleReplaceResponse(payload),
    'git_status_response': (payload) => handleGitStatusResponse(payload),
    'git_branches_response': (payload) => handleGitBranchesResponse(payload),
    'git_history_response': (payload) => openGitHistoryModal(payload.commits),
    'git_diff_response': (payload) => handleGitDiffResponse(payload),
    'git_hunks_response': (payload) => handleGitHunksResponse(payload),
    'generate_commit_message_response': (payload) => {
      const btn = document.getElementById('btn-git-ai-commit');
      if (btn) btn.disabled = false;
      const msgInput = document.getElementById('git-commit-message');
      if (msgInput && payload.commitMessage) {
        msgInput.value = payload.commitMessage;
        msgInput.focus();
        showToast('AI commit message generated', 'success');
      }
    },
    'debug_started': (payload) => handleDebugStarted(payload),
    'debug_paused': (payload) => handleDebugPaused(payload),
    'debug_resumed': () => handleDebugResumed(),
    'debug_output': (payload) => appendDebugConsoleLine(payload.text, payload.category || payload.level || 'stdout'),
    'debug_stopped': () => handleDebugStopped(),
    'debug_breakpoints_updated': (payload) => handleDebugBreakpoints(payload),
    'debug_variables_response': (payload) => {
      if (payload.variables && payload.variables.length > 0) {
        renderVariables(payload.variables, payload.variablesReference);
      }
    },
    'debug_evaluate_response': (payload) => {
      const watchEl = document.getElementById(`watch-val-${payload.expression}`);
      if (watchEl) {
        watchEl.textContent = payload.result?.success ? payload.result.result : (payload.result?.error || 'error');
      } else {
        appendDebugConsoleLine(payload.result?.success ? payload.result.result : `Error: ${payload.result?.error}`, payload.result?.success ? 'console' : 'error');
      }
    },
    'test_connection_response': (payload) => {
      const fb = document.getElementById('test-connection-status');
      fb.textContent = payload.message;
      fb.className = 'connection-feedback ' + (payload.success ? 'ok' : 'err');
      if (payload.success && payload.models && payload.models.length > 0) {
        const select = document.getElementById('settings-model-presets');
        setHtml(select, html`<option value="">Available models (${payload.models.length})</option>`);
        payload.models.forEach(m => {
          const opt = document.createElement('option');
          opt.value = m;
          opt.textContent = m;
          select.appendChild(opt);
        });
        syncCustomSelect('settings-model-presets');
      }
    },
    'settings_saved': (payload) => {
      appSettings = payload.settings;
      applySettingsToUI(appSettings);
      document.getElementById('settings-modal').style.display = 'none';
      showToast('Settings saved', 'success');
      return;

    // Agent Streaming Events
    },
    'agent_status': (payload) => {
      setAgentStatus(payload.status, true);
      setActivityStatus(payload.status);
    },
    'agent_stream_start': () => startAssistantStreamMessage(),
    'agent_reasoning_delta': (payload) => appendStreamReasoningDelta(payload.delta),
    'agent_stream_delta': (payload) => appendStreamContentDelta(payload.delta),
    'agent_stream_end': (payload) => finalizeAssistantStreamMessage(payload.fullContent, payload.isIntermediate),
    'agent_image': (payload) => renderChatImage(payload.path, payload.dataUri, payload.sizeBytes),
    'agent_tool_start': (payload) => renderToolCardStart(payload.id, payload.tool, payload.args),
    'agent_tool_approval_needed': (payload) => renderToolApprovalPrompt(payload.id),
    'agent_tool_finish': (payload) => {
      renderToolCardFinish(payload.id, payload.tool, payload.output, payload.checkpointId);
      if (payload.checkpointId) {
        currentTurnEdits.push({
          tool: payload.tool,
          checkpointId: payload.checkpointId,
          path: payload.modifiedFilePath || (payload.args && payload.args.path) || ''
        });
      }
      if (payload.modifiedFilePath) {
        sendHost('read_file', { path: payload.modifiedFilePath });
      }
    },
    'agent_done': (payload) => {
      setAgentStatus('Idle', false);
      setActivityStatus('Ready');
      document.getElementById('btn-send-agent').style.display = 'inline-flex';
      document.getElementById('btn-cancel-agent').style.display = 'none';
      const hasSummaryChanges = payload && payload.turnSummary && ((payload.turnSummary.files && payload.turnSummary.files.length > 0) || (payload.turnSummary.irreversibleActions && payload.turnSummary.irreversibleActions.length > 0));
      if (currentTurnEdits.length > 0 || hasSummaryChanges) {
        renderTurnReviewCard([...currentTurnEdits], payload ? payload.turnSummary : null);
      }
      if (lastRunMode === 'plan') {
        renderPlanActions();
      }
      if (payload && typeof payload.totalTokens === 'number') {
        renderRunFooter(payload);
      }
      // FinalizeToolGroup() is a no-op when no group is open.
      finalizeToolGroup();
      if (payload && shouldOfferContinue(payload.finalMessage)) {
        renderContinueAction();
      }
      saveCurrentSessionToStorage();
    },
    'agent_error': (payload) => {
      // One primary action instead of a dead end (the text stays as the fallback).
      globalThis.renderAgentError?.(payload) || appendStaticAssistantMessage(`**Error:** ${payload.message}`);
      finalizeToolGroup();
      setAgentStatus('Error', false);
      setActivityStatus('Agent error');
      document.getElementById('btn-send-agent').style.display = 'inline-flex'; document.getElementById('btn-cancel-agent').style.display = 'none';
    },
    'toast': (payload) => showToast(payload.message, payload.type || 'info'),
    'inline_assist_status': (payload) => updateInlineAiStatus(payload.status),
    'inline_assist_delta': (payload) => updateInlineAiDelta(payload.delta),
    'inline_assist_complete': (payload) => handleInlineAssistComplete(payload),
    'inline_assist_error': (payload) => handleInlineAssistError(payload.message),
    'inline_assist_cancelled': () => handleInlineAssistCancelled(),
  };
}

// Registered at load: the shared dispatcher is installed by main.js before this script runs.
window.VoidHostEvents.register(createHostEventHandlers());

function handleHostMessage(action, payload) {
  // The registry answers; core/host-events.js reports an unknown action once.
  return window.VoidHostEvents.dispatch(action, payload);
}

function normalizePath(p) {
  return (p || '').replace(/\//g, '\\').toLowerCase();
}

// Language Detector
window.getLanguageInfo = function getLanguageInfo(path) {
  const ext = path.split('.').pop().toLowerCase();
  const map = {
    'cs': { id: 'csharp', badge: 'cs', label: 'C#' },
    'cpp': { id: 'cpp', badge: 'cpp', label: 'C++' },
    'c': { id: 'c', badge: 'c', label: 'C' },
    'h': { id: 'cpp', badge: 'h', label: 'C/C++ header' },
    'hpp': { id: 'cpp', badge: 'hpp', label: 'C++ header' },
    'rs': { id: 'rust', badge: 'rs', label: 'Rust' },
    'py': { id: 'python', badge: 'py', label: 'Python' },
    'go': { id: 'go', badge: 'go', label: 'Go' },
    'js': { id: 'javascript', badge: 'js', label: 'JavaScript' },
    'ts': { id: 'typescript', badge: 'ts', label: 'TypeScript' },
    'json': { id: 'json', badge: 'json', label: 'JSON' },
    'html': { id: 'html', badge: 'html', label: 'HTML' },
    'css': { id: 'css', badge: 'css', label: 'CSS' },
    'md': { id: 'markdown', badge: 'md', label: 'Markdown' },
    'xml': { id: 'xml', badge: 'xml', label: 'XML' },
    'xaml': { id: 'xml', badge: 'xaml', label: 'XAML' },
    'sql': { id: 'sql', badge: 'sql', label: 'SQL' },
    'ps1': { id: 'powershell', badge: 'ps1', label: 'PowerShell' },
    'asm': { id: 'plaintext', badge: 'asm', label: 'Assembly' }
  };
  const info = map[ext] || { id: 'plaintext', badge: ext.slice(0, 4) || 'txt', label: 'Plain text' };
  return { ...info, kind: fileIconKind(ext, info.id) };
}

// Which glyph an entry gets in the tree. Text badges ("json", "giti") read as
// Generated; a shape per file family scans faster and matches the rest of the shell.
function fileIconKind(ext, languageId) {
  if (['png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'ico', 'bmp', 'avif'].includes(ext)) return 'image';
  if (['ps1', 'sh', 'bash', 'cmd', 'bat', 'zsh'].includes(ext)) return 'shell';
  if (['json', 'xml', 'xaml', 'yml', 'yaml', 'toml', 'ini', 'config'].includes(ext)) return 'data';
  if (['css', 'scss', 'sass', 'less'].includes(ext)) return 'style';
  if (['md', 'mdx', 'txt', 'rst'].includes(ext)) return 'doc';
  if (['ts', 'js', 'tsx', 'jsx', 'cs', 'cpp', 'c', 'h', 'hpp', 'rs', 'py', 'go', 'sql', 'asm', 'java', 'rb', 'php', 'swift', 'kt'].includes(ext)) return 'code';
  if (!ext || ext === languageId) return 'plain';
  return 'plain';
}

// FILE_ICON_PATHS + fileTypeIcon moved to modules/explorer.js - the file tree
// Is their only consumer.

// Open or Refresh File in Monaco Editor
// The tab lifecycle moved to modules/editor-tabs.js (): bindTabModel,
// OpenFileInEditor, refreshOpenTabIfLoaded, setActiveTab, closeTab, requestCloseTab and
// UpdateEditorContextActions. app.js keeps the editor instance, LSP, diagnostics, the outline,
// The breadcrumbs, live preview and recovery - the module calls those as globals.

// Live preview & image viewer moved to modules/workspace-tools.js.

// OpenCheckpointDiff moved to modules/turn-review.js ().

async function saveCurrentFile() {
  if (!activeTab || !monacoEditor) return;
  if (appSettings.FormatOnSave || document.getElementById('settings-format-on-save')?.checked) {
    await formatActiveDocument();
  }
  const content = monacoEditor.getValue();
  sendHost('save_file', { path: activeTab.path, content });
}

// RenderTabs() moved to modules/editor-tabs.js (), together with the tab
// Bar's click handling (activate / close / alt-click opens the split editor).

// File Explorer Tree moved to modules/explorer.js (): renderFileTree,
// BuildFileTreeNode, the lazy directory expansion, highlightActiveTreeNode and the tree context
// Menu, together with the tree_refreshed/directory_children_response/all_files_response handlers.
// App.js keeps calling renderFileTree()/highlightActiveTreeNode() through the published globals.

// Terminal Streaming moved to modules/terminal.js: appendTerminalStream + getActiveTerminalText.

// Agent Streaming & Run Trace moved to modules/agent-stream.js.

// Attached context chips, Plan/Act mode & plan actions moved to modules/agent-composer.js.

// RenderTurnReviewCard moved to modules/turn-review.js ().

// ==========================================
// PHASE 2: COMMAND PALETTE & KEYBINDING REGISTRY
// ==========================================
// The command registry, the keybinding helpers and the palette openers moved to
// Modules/command-palette.js (). app.js still calls openCommandPalette()
// And openQuickOpenModal() by name.

// Basic editor actions (new file/folder, goToLine, format) moved to modules/editor-features.js.

// OpenSettingsToTab() moved to modules/settings.js (), next to the modal it
// Opens; the command registry and the agent-authority button call it by name.

// RenderQuickOpenList(), renderKeybindingsTable() and setupKeybindingsSettings() moved to
// Modules/command-palette.js ().

// SetupResizers() moved to modules/layout.js ().

// Terminal command suggestions & history helper moved to modules/agent-composer.js.

// Breadcrumbs & cursor navigation moved to modules/editor-features.js.

// SwitchSidebarView() + setupSidebarSwitcher() moved to modules/layout.js ().

// Global Workspace Search & Replace moved to modules/search.js.

// Git Source Control panel + diff viewer moved to modules/git.js.


// PTY terminal (xterm.js + ConPTY) moved to modules/terminal.js.


// In-editor AI inline assist (Ctrl+K) moved to modules/inline-ai.js.


// One-click smart run & debug (F5) moved to modules/run-debug.js.


// Terminal "fix with AI" banner moved to modules/run-debug.js: it reads the run state.

// ==========================================
// Split editor & document symbols/outline moved to modules/editor-features.js.

// Problems, tests panel & diagnostics moved to modules/problems-tests.js.

// Settings Sync
function updateWorkspaceUI(path) {
  const folderName = path ? path.split(/[\\/]/).pop() || path : 'Open folder';
  document.getElementById('current-workspace-label').textContent = folderName;
  document.getElementById('btn-open-hub').title = path ? `${path} (Click to switch project)` : 'Choose project';
  document.getElementById('status-workspace').textContent = folderName;
  updateConsoleStatus();

  // The search panel must say where it searches
  const scopeEl = document.getElementById('search-scope');
  if (scopeEl) {
    setHtml(scopeEl, folderName && folderName !== 'Open folder'
      ? html`Searching <strong>${folderName}</strong>`
      : 'Open a folder to search its files');
  }
  updateBreadcrumbs(activeTab ? activeTab.path : null);
  refreshGitStatus();
  refreshRunConfig();
  syncRunAvailability();
  sendHost('detect_test_runner');
}

// ApplySettingsToUI()/gatherSettingsFromModal() moved to modules/settings.js ().

function setAgentStatus(text, isBusy) {
  const label = document.getElementById('agent-status-label');
  const dot = document.getElementById('agent-status-dot');
  const wrap = label?.closest('.agent-status-wrap');
  if (label) label.textContent = text || '';
  if (dot) dot.className = 'run-indicator ' + (isBusy ? 'busy' : 'idle');
  // The composer is not a dashboard: an "Idle" dot next to two action pills is
  // Decoration, so the status only appears while the agent is actually working
  if (wrap) wrap.style.display = isBusy && text ? '' : 'none';
}

// EscapeHtml() and renderSafeMarkdown() now live in core/markdown.js (loaded before app.js).

// Main Initialization
document.addEventListener('DOMContentLoaded', async () => {
  try {
    sendHost('js_log', { msg: 'DOMContentLoaded START' });
    loadStoredState();
    setupCustomSelects();
    setupAuthGate();
    renderProjectHubRecents();
    sendHost('get_init_state');
    setupResizers();
    applyInitialPaneLayout();
    setupDraggableWindows();
    setupUtilityTabs();
    sendHost('js_log', { msg: 'DOMContentLoaded before initMonaco' });
    await initMonaco();
    sendHost('js_log', { msg: 'DOMContentLoaded after initMonaco' });

  // Rich text is sanitized before insertion. External links are always opened
  // By the trusted host instead of navigating the privileged app WebView.
  document.addEventListener('click', (event) => {
    const anchor = event.target.closest?.('.msg-content a, .preview-markdown-body a');
    if (!anchor) return;
    const href = anchor.getAttribute('href') || '';
    if (!href || href.startsWith('#')) return;
    event.preventDefault();
    if (/^(https?:|mailto:)/i.test(href)) {
      sendHost('open_external_url', { url: href });
    } else {
      showToast('Relative Markdown links are not opened outside the workspace preview.', 'info');
    }
  });

  if (appSettings && appSettings.EditorFontSize) {
    applySettingsToUI(appSettings);
  }

  setupProjectHub(); // the hub listeners live in modules/project-hub.js

  // The hub’s customize button and the tile keyboard handling moved to
  // Modules/project-hub.js ().

  // Native PC File Explorer Attachment & Open Buttons
  document.getElementById('btn-attach-pc-file').addEventListener('click', () => {
    sendHost('open_file_dialog', { target: 'attach' });
  });
  document.getElementById('btn-composer-attach-pc').addEventListener('click', () => {
    sendHost('open_file_dialog', { target: 'attach' });
  });
  document.getElementById('btn-editor-open-pc-file').addEventListener('click', () => {
    sendHost('open_file_dialog', { target: 'editor' });
  });
  document.getElementById('btn-empty-open-file').addEventListener('click', () => {
    sendHost('open_file_dialog', { target: 'editor' });
  });
  document.getElementById('btn-inspector-browse-pc').addEventListener('click', () => {
    sendHost('open_file_dialog', { target: 'inspector' });
  });

  setupPaneButtons(); // pane toggles live in modules/layout.js

  // Topbar & Explorer actions
  document.getElementById('btn-open-workspace').addEventListener('click', () => sendHost('open_workspace_dialog'));
  document.getElementById('btn-quick-open').addEventListener('click', openQuickOpenModal);
  document.getElementById('btn-save-file').addEventListener('click', saveCurrentFile);

  // Floating Mini-Window Toggles
  document.getElementById('btn-open-preview').addEventListener('click', () => {
    toggleMiniWindow('win-preview');
    renderLivePreviewFromActiveTab();
  });
  document.getElementById('btn-preview-refresh').addEventListener('click', () => {
    renderLivePreviewFromActiveTab();
  });
  document.getElementById('btn-preview-viewport').addEventListener('click', (e) => {
    const frame = document.getElementById('preview-iframe');
    const isMob = frame.classList.toggle('is-mobile');
    e.target.textContent = isMob ? 'Mobile 390px' : 'Desktop';
  });

  document.getElementById('btn-open-history').addEventListener('click', () => toggleMiniWindow('win-history'));
  document.getElementById('btn-sidebar-history').addEventListener('click', () => toggleMiniWindow('win-history'));
  document.getElementById('btn-open-timeline').addEventListener('click', () => toggleMiniWindow('win-timeline'));
  document.getElementById('btn-open-prompts').addEventListener('click', () => toggleMiniWindow('win-prompts'));
  document.getElementById('btn-open-inspector').addEventListener('click', () => toggleMiniWindow('win-inspector'));
  document.getElementById('btn-open-customize').addEventListener('click', () => toggleMiniWindow('win-customize'));
  // A preference is not a first step: the third action opens the project hub,
  // Where recents actually live (the label says "Recent projects")
  document.getElementById('btn-empty-customize')?.addEventListener('click', () => openProjectHub());

  const moreToolsButton = document.getElementById('btn-more-tools');
  const moreToolsMenu = document.getElementById('top-tools-menu');
  const closeMoreTools = () => {
    moreToolsMenu.style.display = 'none';
    moreToolsButton.setAttribute('aria-expanded', 'false');
  };
  moreToolsButton.addEventListener('click', (event) => {
    event.stopPropagation();
    const willOpen = moreToolsMenu.style.display === 'none';
    if (!willOpen) {
      closeMoreTools();
      return;
    }
    const rect = moreToolsButton.getBoundingClientRect();
    moreToolsMenu.style.top = `${rect.bottom + 4}px`;
    moreToolsMenu.style.left = `${Math.max(8, rect.right - 210)}px`;
    moreToolsMenu.style.display = 'block';
    moreToolsButton.setAttribute('aria-expanded', 'true');
    moreToolsMenu.querySelector('[role="menuitem"]')?.focus();
  });
  moreToolsMenu.querySelectorAll('[data-tool-window]').forEach(item => {
    item.addEventListener('click', () => {
      const winId = item.getAttribute('data-tool-window');
      closeMoreTools();
      toggleMiniWindow(winId, true);
      if (winId === 'win-preview') renderLivePreviewFromActiveTab();
    });
  });
  moreToolsMenu.querySelectorAll('[data-tool-action]').forEach(item => {
    item.addEventListener('click', () => {
      const act = item.getAttribute('data-tool-action');
      closeMoreTools();
      if (act === 'health') globalThis.openHealthModal?.();
      else if (act === 'tasks') globalThis.openTasksModal?.();
    });
  });
  moreToolsMenu.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape') return;
    event.preventDefault();
    closeMoreTools();
    moreToolsButton.focus();
  });
  window.addEventListener('click', (event) => {
    if (!event.target.closest('#top-tools-menu') && event.target !== moreToolsButton) closeMoreTools();
  });

  // Customize Window & Settings Appearance Sliders
  document.querySelectorAll('[data-theme-pick]').forEach(btn => {
    btn.addEventListener('click', () => {
      const picked = btn.getAttribute('data-theme-pick');
      const persistImmediately = !btn.closest('#settings-modal');
      applyThemeAndOptics(picked, appSettings.GlassBlur, appSettings.FieldOpacity, persistImmediately);
      showToast(`${persistImmediately ? 'Applied' : 'Previewing'} ${btn.textContent.trim()} theme`, 'info');
    });
  });

  ['slider-glass-blur', 'settings-slider-blur'].forEach(id => {
    const el = document.getElementById(id);
    if (!el) return;
    el.addEventListener('input', (e) => {
      applyThemeAndOptics(appSettings.Theme, parseInt(e.target.value, 10), appSettings.FieldOpacity, false);
    });
    el.addEventListener('change', (e) => {
      applyThemeAndOptics(appSettings.Theme, parseInt(e.target.value, 10), appSettings.FieldOpacity, id === 'slider-glass-blur');
    });
  });

  ['slider-field-opacity', 'settings-slider-field'].forEach(id => {
    const el = document.getElementById(id);
    if (!el) return;
    el.addEventListener('input', (e) => {
      applyThemeAndOptics(appSettings.Theme, appSettings.GlassBlur, parseInt(e.target.value, 10), false);
    });
    el.addEventListener('change', (e) => {
      applyThemeAndOptics(appSettings.Theme, appSettings.GlassBlur, parseInt(e.target.value, 10), id === 'slider-field-opacity');
    });
  });

  // Font size stepper & live editor toggles
  const fontSizeInput = document.getElementById('settings-font-size');
  document.getElementById('btn-font-dec').addEventListener('click', () => {
    const next = Math.max(10, (parseInt(fontSizeInput.value, 10) || 14) - 1);
    fontSizeInput.value = next;
    if (monacoEditor) monacoEditor.updateOptions({ fontSize: next });
  });
  document.getElementById('btn-font-inc').addEventListener('click', () => {
    const next = Math.min(26, (parseInt(fontSizeInput.value, 10) || 14) + 1);
    fontSizeInput.value = next;
    if (monacoEditor) monacoEditor.updateOptions({ fontSize: next });
  });
  document.getElementById('settings-minimap').addEventListener('change', (e) => {
    if (monacoEditor) monacoEditor.updateOptions({ minimap: { enabled: e.target.checked } });
  });
  document.getElementById('settings-word-wrap').addEventListener('change', (e) => {
    if (monacoEditor) monacoEditor.updateOptions({ wordWrap: e.target.checked ? 'on' : 'off' });
  });

  document.querySelectorAll('[data-toggle-pane]').forEach(btn => {
    btn.addEventListener('click', () => {
      toggleWorkspacePane(btn.getAttribute('data-toggle-pane'));
    });
  });

  document.querySelectorAll('[data-toggle-win]').forEach(btn => {
    btn.addEventListener('click', () => {
      toggleMiniWindow(btn.getAttribute('data-toggle-win'));
    });
  });

  setupWorkspaceTools();

  document.getElementById('btn-run-inspector').addEventListener('click', () => {
    const p = document.getElementById('inspector-path-input').value.trim();
    if (p) sendHost('inspect_file', { path: p });
  });

  document.getElementById('btn-inspect-active').addEventListener('click', () => {
    if (activeTab) {
      document.getElementById('inspector-path-input').value = activeTab.path;
      sendHost('inspect_file', { path: activeTab.path });
    } else {
      showToast('Open a file tab first', 'info');
    }
  });

  document.getElementById('btn-send-inspection-agent').addEventListener('click', () => {
    const report = document.getElementById('inspector-output-box').textContent;
    if (!report) return;
    const box = document.getElementById('agent-input-text');
    box.value = `[Binary Inspection Report]\n${report}\n\nAnalyze this binary report:`;
    toggleWorkspacePane('agent', true);
    box.focus();
    showToast('Attached report to composer', 'info');
  });

  // Explorer setup (refresh, new file/folder, tree context menu, tree keyboard) moved to
  // Modules/explorer.js in .

  // Terminal Actions, History & Interactive Command Suggestions
  setupTerminalHelper();
  setupTerminalTabs();

  // Advanced IDE Panels: Explorer Switcher, Global Grep, Git, Inline AI
  setupSidebarSwitcher();
  setupExplorer(); // the explorer module wires its own buttons and context menu
  setupSearchAndReplace();
  setupGitPanel();
  setupInlineAiAssist();
  setupSmartRun();
  setupSplitEditor();
  setupSymbolQuickPick();
  setupProblemsPanel();
  setupTestsPanel();
  setupKeyboardA11y();
  setupAutoSave();
  setupReadabilityModes();
  setupCommandPalette(); // keybindings table + registry dispatch + quick open
  setupAgentMentions();
  setupCodebaseSearch();
  setupDebugger();
  updateBreadcrumbs(null);
  setupStatusBar(); // slot clearing + the LSP indicator's click live in modules/status-bar.js

  document.getElementById('btn-kill-terminal')?.addEventListener('click', () => {
    sendHost('shell_kill', {});
    killActiveTerminal();
  });
  document.getElementById('btn-clear-terminal')?.addEventListener('click', () => {
    const log = document.getElementById('vc-log');
    if (log) setHtml(log, '');
  });
  document.getElementById('btn-terminal-restart')?.addEventListener('click', () => {
    restartActiveTerminal();
  });
  document.getElementById('btn-clear-terminal')?.addEventListener('click', () => {
    clearActiveTerminal();
  });
  document.getElementById('btn-toggle-terminal')?.addEventListener('click', () => {
    setTerminalCollapsed(!document.getElementById('terminal-panel').classList.contains('collapsed'));
  });

  document.getElementById('btn-debug-idle-start')?.addEventListener('click', () => {
    document.getElementById('btn-sidebar-debug-start')?.click();
  });

  // The single idle state disappears as soon as a session starts.
  function syncDebugIdle() {
    const idle = document.getElementById('debug-idle');
    if (!idle) return;
    idle.classList.toggle('is-hidden', !!(window.__debugRunning === true));
  }

  window.addEventListener('load', syncDebugIdle);
  syncDebugIdle();

  // The command registry is the single shortcut source of truth. Capture-phase
  // Dispatch also makes user rebindings work while Monaco has focus.
  // The registry's capture-phase keydown dispatch (F1, F2 delegation, Escape) moved to
  // Modules/command-palette.js (): setupCommandPalette() installs it.

  // Quick Open filtering, arrow-key navigation and click-outside moved to
  // Modules/command-palette.js ().

  setupAgentComposer();

  setupSettingsUI(); // the modal logic lives in modules/settings.js
  sendHost('js_log', { msg: 'DOMContentLoaded FINISHED' });
  } catch (err) {
    sendHost('js_error', { msg: 'DOMContentLoaded catch: ' + (err.stack || err.message) });
  }
});

// ==========================================
// Roving keyboard a11y layer moved to modules/problems-tests.js.

// RELIABILITY: auto-save, session restore, safe close, external changes
// ==========================================
// AUTOSAVE_MODES/AUTOSAVE_LABELS/autosaveMode moved to modules/settings.js, which
// Owns the mode and asks back for saveTabNow/saveAllDirtyTabs when a tick fires.
// RestoringSession / sessionRestoredFor / closePending moved to modules/session.js.

(function installReliabilityHooks() {
  const baseHandleHostMessage = handleHostMessage;
  handleHostMessage = function (action, payload) {
    switch (action) {
      case 'request_close':
        handleCloseRequest();
        return;
      case 'file_changed':
        handleExternalFileChange(payload);
        return;
      case 'save_file_response':
        baseHandleHostMessage(action, payload);
        handlePendingCloseSaveResult(payload); // seam into modules/session.js ()
        return;
      case 'read_file_response':
        baseHandleHostMessage(action, payload);
        advanceSessionRestore(payload.path, payload.content, payload.error);
        return;
    }
    baseHandleHostMessage(action, payload);

    if ((action === 'init_state_response' || action === 'workspace_changed') && currentWorkspace) {
      setTimeout(() => restoreSessionForWorkspace(currentWorkspace), 350);
    }
  };
})();

// ---- Readability modes (Settings → Appearance) ----
// ApplyReadabilityMode()/setupReadabilityModes()/setupAutoSave() moved to modules/settings.js.

// SaveTabNow()/saveAllDirtyTabs() moved to modules/editor-tabs.js ().

// HandleCloseRequest() moved to modules/session.js ().
// HandleExternalFileChange() moved to modules/session.js ().
// SessionKey/collectSessionState/persistSessionNow/collectRecoveryState/persistRecoveryNow,
// RestoreSessionForWorkspace/advanceSessionRestore/finishSessionRestore and the autosave tick (with
// Its unload flush) moved to modules/session.js ().

// ==========================================
// Agent @ mentions moved to modules/agent-composer.js.

// Codebase search & debugger moved to modules/codebase-search.js and modules/debug.js.
