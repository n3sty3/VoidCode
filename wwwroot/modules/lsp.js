// The LSP client lifecycle and the Monaco providers that talk to the language servers.
//
// The
// Document-sync bookkeeping (lspDocVersions / lspChangeDebounceTimers and the four notifyLsp*
// Entry points, including the 250ms change debounce), the LSP->Monaco kind and documentation
// Mappers, the workspace-edit pipeline (ensureModelsForLspEdits / lspEditsToMonaco /
// CompareLspPositions / validateLspEditsForModel / applyReviewedLspWorkspaceEdits and the reviewed
// Queue that never runs two reviews at once), and registerLspProviders(), which is the part the
// Plan names: hover, definition, completion, references, signature help, document symbols, inlay
// Hints, semantic tokens, rename, formatting and code actions, registered for all seven languages
// Plus the voidcode.lsp.executeCommand command those code actions invoke.
//
// Two things needed adapting, and only two:
//   - `monacoEditor` is a script-scope `let` in app.js, so the definition provider's in-file branch
//     Asks for the editor through the editor() helper below (getMonacoEditor(), published ).
//     That was the slice's only use of it.
//   - `pendingNavigateLocation` is navigation state app.js owns: four other places there write it and
//     ApplyPendingLocation() reads it once the host delivers the file a jump asks for. It therefore
//     Stays in app.js and the definition provider keeps writing it through the shared global (a
//     Documented `writable` in eslint's appGlobals) rather than growing a setter for one assignment.
// Everything else the range touched is already visible from a module: monaco, activeTab/openTabs
// (store slices), normalizePath, getLanguageInfo, setActiveTab, openFileInEditor, showToast and
// ShowConfirmDialog - the slices because core/state.js publishes them, the functions because they
// Are app.js declarations and therefore globals.
//
// The two send helpers are the usual per-module pair: app.js's sendHost/sendHostRequest are
// Script-scope consts a module cannot see. sendHostRequest keeps app.js's exact shape
// (window.VoidBridge.request with the caller-supplied timeout).
//
// What stayed in app.js on purpose: initMonaco() (which calls registerLspProviders() once Monaco
// Is up, at the same place it always did - init() below registers no providers itself), the lsp_*
// Host-response resolvers (they belong to the bridge request plumbing), the diagnostics handler that
// Paints model markers, and the status slot that modules/status-bar.js owns since step 7.

let hostSend = (action, data) => window.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

function sendHostRequest(action, data = {}, timeoutMs = 4000) {
  return window.VoidBridge.request(action, data, timeoutMs);
}

// The only way this module touches the editor: `monacoEditor` is a script-scope `let` in app.js,
// Published  as getMonacoEditor() for exactly this reason.
function editor() {
  return globalThis.getMonacoEditor ? globalThis.getMonacoEditor() : null;
}

// LSP Client Lifecycle & Monaco Providers
const lspChangeDebounceTimers = new Map();
const lspDocVersions = new Map();
// LastLspStatus moved to modules/status-bar.js ().

// UpdateLspStatus() moved to modules/status-bar.js (), together with the
// LastLspStatus/notifiedLsp state it was the only user of.

// NotifiedLsp moved to modules/status-bar.js ().

function mapLspKindToMonaco(lspKind) {
  if (!window.monaco || !monaco.languages || !monaco.languages.CompletionItemKind) return 1;
  const k = monaco.languages.CompletionItemKind;
  switch (lspKind) {
    case 1: return k.Text;
    case 2: return k.Method;
    case 3: return k.Function;
    case 4: return k.Constructor;
    case 5: return k.Field;
    case 6: return k.Variable;
    case 7: return k.Class;
    case 8: return k.Interface;
    case 9: return k.Module;
    case 10: return k.Property;
    case 11: return k.Unit;
    case 12: return k.Value;
    case 13: return k.Enum;
    case 14: return k.Keyword;
    case 15: return k.Snippet;
    case 16: return k.Color;
    case 17: return k.File;
    case 18: return k.Reference;
    case 19: return k.Folder;
    case 20: return k.EnumMember;
    case 21: return k.Constant;
    case 22: return k.Struct;
    case 23: return k.Event;
    case 24: return k.Operator;
    case 25: return k.TypeParameter;
    default: return k.Property;
  }
}

function mapLspSymbolKindToMonaco(lspKind) {
  const kinds = monaco.languages.SymbolKind;
  const map = {
    1: kinds.File, 2: kinds.Module, 3: kinds.Namespace, 4: kinds.Package,
    5: kinds.Class, 6: kinds.Method, 7: kinds.Property, 8: kinds.Field,
    9: kinds.Constructor, 10: kinds.Enum, 11: kinds.Interface, 12: kinds.Function,
    13: kinds.Variable, 14: kinds.Constant, 15: kinds.String, 16: kinds.Number,
    17: kinds.Boolean, 18: kinds.Array, 19: kinds.Object, 20: kinds.Key,
    21: kinds.Null, 22: kinds.EnumMember, 23: kinds.Struct, 24: kinds.Event,
    25: kinds.Operator, 26: kinds.TypeParameter
  };
  return map[lspKind] ?? kinds.Variable;
}

function lspDocumentationValue(documentation) {
  if (!documentation) return undefined;
  if (typeof documentation === 'string') return { value: documentation };
  if (typeof documentation.value === 'string') return { value: documentation.value };
  return { value: String(documentation) };
}

const LSP_SEMANTIC_TOKEN_TYPES = Object.freeze([
  'namespace', 'type', 'class', 'enum', 'interface', 'struct', 'typeParameter',
  'parameter', 'variable', 'property', 'enumMember', 'event', 'function', 'method',
  'macro', 'keyword', 'modifier', 'comment', 'string', 'number', 'regexp', 'operator',
  'decorator'
]);
const LSP_SEMANTIC_TOKEN_MODIFIERS = Object.freeze([
  'declaration', 'definition', 'readonly', 'static', 'deprecated', 'abstract',
  'async', 'modification', 'documentation', 'defaultLibrary'
]);

async function ensureModelsForLspEdits(edits, maximumFiles = 40) {
  const affectedPaths = [...new Set((edits || []).map(edit => edit.path).filter(Boolean))];
  if (affectedPaths.length > maximumFiles) {
    return { ok: false, error: `The edit affects ${affectedPaths.length} files. Use the Agent transaction flow for this large change.` };
  }

  const originalTab = activeTab;
  for (const affectedPath of affectedPaths) {
    if (openTabs.some(tab => normalizePath(tab.path) === normalizePath(affectedPath))) continue;
    const file = await sendHostRequest('read_text_file', { path: affectedPath }, 10000);
    if (!file || file.error) {
      if (originalTab) setActiveTab(originalTab);
      return { ok: false, error: `Could not load ${affectedPath.split(/[\\/]/).pop()} for the workspace edit.` };
    }
    openFileInEditor(file.path, file.content, file.metadata);
  }
  if (originalTab) setActiveTab(originalTab);
  return { ok: true };
}

function lspEditsToMonaco(edits) {
  return (edits || []).map(edit => ({
    resource: monaco.Uri.file(edit.path),
    textEdit: {
      range: new monaco.Range(
        edit.startLine || 1,
        edit.startColumn || 1,
        edit.endLine || edit.startLine || 1,
        edit.endColumn || edit.startColumn || 1
      ),
      text: edit.newText || ''
    }
  }));
}

function compareLspPositions(aLine, aColumn, bLine, bColumn) {
  return aLine === bLine ? aColumn - bColumn : aLine - bLine;
}

function validateLspEditsForModel(model, edits) {
  const ranges = [];
  const lineCount = model.getLineCount();
  for (const edit of edits) {
    const values = [edit.startLine, edit.startColumn, edit.endLine, edit.endColumn];
    if (!values.every(Number.isInteger)) return 'The language server returned an invalid edit range.';
    if (edit.startLine < 1 || edit.endLine < 1 || edit.startLine > lineCount || edit.endLine > lineCount) {
      return 'The language server returned an edit outside the current document.';
    }
    if (edit.startColumn < 1 || edit.startColumn > model.getLineMaxColumn(edit.startLine) ||
        edit.endColumn < 1 || edit.endColumn > model.getLineMaxColumn(edit.endLine) ||
        compareLspPositions(edit.startLine, edit.startColumn, edit.endLine, edit.endColumn) > 0) {
      return 'The language server returned an invalid edit range.';
    }
    ranges.push(edit);
  }

  ranges.sort((a, b) => compareLspPositions(a.startLine, a.startColumn, b.startLine, b.startColumn));
  for (let index = 1; index < ranges.length; index++) {
    const previous = ranges[index - 1];
    const current = ranges[index];
    if (compareLspPositions(current.startLine, current.startColumn, previous.endLine, previous.endColumn) < 0) {
      return 'The language server returned overlapping edits.';
    }
  }
  return null;
}

async function applyReviewedLspWorkspaceEdits(edits) {
  if (!Array.isArray(edits) || edits.length === 0) return { ok: false, error: 'No text edits were provided.' };
  const prepared = await ensureModelsForLspEdits(edits);
  if (!prepared.ok) return prepared;

  const groups = new Map();
  for (const edit of edits) {
    const key = normalizePath(edit.path);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(edit);
  }

  const operations = [];
  for (const [pathKey, fileEdits] of groups) {
    const tab = openTabs.find(candidate => normalizePath(candidate.path) === pathKey);
    if (!tab?.model || tab.model.isDisposed()) return { ok: false, error: `The model for ${fileEdits[0].path} is unavailable.` };
    const validationError = validateLspEditsForModel(tab.model, fileEdits);
    if (validationError) return { ok: false, error: validationError };
    operations.push({ tab, fileEdits });
  }

  try {
    for (const operation of operations) {
      operation.tab.model.pushStackElement();
      operation.tab.model.pushEditOperations(
        [],
        operation.fileEdits.map(edit => ({
          range: new monaco.Range(edit.startLine, edit.startColumn, edit.endLine, edit.endColumn),
          text: edit.newText || ''
        })),
        () => null
      );
      operation.tab.model.pushStackElement();
    }
    return { ok: true };
  } catch (error) {
    return { ok: false, error: `Could not apply the reviewed workspace edit: ${error?.message || error}` };
  }
}

let lspWorkspaceEditReviewQueue = Promise.resolve();

function queueLspWorkspaceEditReview(payload) {
  lspWorkspaceEditReviewQueue = lspWorkspaceEditReviewQueue
    .then(() => reviewLspWorkspaceEdit(payload))
    .catch((error) => {
      if (payload?.reviewId) {
        sendHost('resolve_lsp_workspace_edit', {
          reviewId: payload.reviewId,
          applied: false,
          failureReason: error?.message || 'Workspace-edit review failed.'
        });
      }
    });
}

async function reviewLspWorkspaceEdit(payload) {
  if (!payload?.reviewId) return;
  const edits = Array.isArray(payload.edits) ? payload.edits : [];
  const files = [...new Set(edits.map(edit => edit.path).filter(Boolean))];
  const workspacePrefix = normalizePath(currentWorkspace).replace(/\\$/, '') + '\\';
  const visibleFiles = files.slice(0, 3).map(path => {
    const normalized = normalizePath(path);
    return normalized.startsWith(workspacePrefix) ? path.slice(workspacePrefix.length) : path;
  });
  const remaining = Math.max(0, files.length - visibleFiles.length);
  const fileSummary = visibleFiles.join(', ') + (remaining ? `, and ${remaining} more` : '');
  const label = payload.label ? ` “${payload.label}”` : '';
  const approved = await showConfirmDialog(
    'Review language-server edits',
    `${payload.language || 'Language server'} requested${label}: ${edits.length} edit${edits.length === 1 ? '' : 's'} across ${files.length} file${files.length === 1 ? '' : 's'} (${fileSummary}). The files will open as unsaved tabs and will not be written until you save them.`,
    'Apply edits',
    null,
    80000
  );

  if (!approved) {
    sendHost('resolve_lsp_workspace_edit', {
      reviewId: payload.reviewId,
      applied: false,
      failureReason: 'The operator declined the workspace edit.'
    });
    return;
  }

  const result = await applyReviewedLspWorkspaceEdits(edits);
  sendHost('resolve_lsp_workspace_edit', {
    reviewId: payload.reviewId,
    applied: !!result.ok,
    failureReason: result.ok ? null : result.error
  });
  showToast(result.ok ? 'Language-server edits are open for review.' : result.error, result.ok ? 'success' : 'error');
}

function notifyLspDidOpen(tab) {
  if (!tab || !tab.path || !tab.model) return;
  const langInfo = getLanguageInfo(tab.path);
  const supported = ['python', 'csharp', 'rust', 'go', 'cpp', 'typescript', 'javascript'];
  if (!supported.includes(langInfo.id)) return;

  lspDocVersions.set(tab.path, 1);
  sendHost('lsp_did_open', {
    path: tab.path,
    languageId: langInfo.id,
    content: tab.model.getValue()
  });
}

function notifyLspDidChange(tab) {
  if (!tab || !tab.path || !tab.model) return;
  const langInfo = getLanguageInfo(tab.path);
  const supported = ['python', 'csharp', 'rust', 'go', 'cpp', 'typescript', 'javascript'];
  if (!supported.includes(langInfo.id)) return;

  const path = tab.path;
  const timer = lspChangeDebounceTimers.get(path);
  if (timer) clearTimeout(timer);

  lspChangeDebounceTimers.set(path, setTimeout(() => {
    lspChangeDebounceTimers.delete(path);
    if (!tab.model || tab.model.isDisposed()) return;
    const version = (lspDocVersions.get(path) || 1) + 1;
    lspDocVersions.set(path, version);
    sendHost('lsp_did_change', {
      path: path,
      version: version,
      content: tab.model.getValue()
    });
  }, 250));
}

function notifyLspDidSave(path) {
  if (!path) return;
  const langInfo = getLanguageInfo(path);
  const supported = ['python', 'csharp', 'rust', 'go', 'cpp', 'typescript', 'javascript'];
  if (!supported.includes(langInfo.id)) return;

  sendHost('lsp_did_save', { path });
}

function notifyLspDidClose(path) {
  if (!path) return;
  const timer = lspChangeDebounceTimers.get(path);
  if (timer) {
    clearTimeout(timer);
    lspChangeDebounceTimers.delete(path);
  }
  lspDocVersions.delete(path);

  const langInfo = getLanguageInfo(path);
  const supported = ['python', 'csharp', 'rust', 'go', 'cpp', 'typescript', 'javascript'];
  if (!supported.includes(langInfo.id)) return;

  sendHost('lsp_did_close', { path });
}

function registerLspProviders() {
  const supportedLanguages = ['python', 'csharp', 'rust', 'go', 'cpp', 'typescript', 'javascript'];

  monaco.editor.registerCommand('voidcode.lsp.executeCommand', async (_accessor, payload) => {
    if (!payload?.path || !payload?.command) return;
    const execution = await sendHostRequest('lsp_execute_command', {
      path: payload.path,
      command: payload.command,
      arguments: payload.arguments || []
    }, 105000);
    if (!execution?.completed) {
      showToast(execution?.error || `Language action timed out: ${payload.title || payload.command}`, 'error');
      return;
    }
    showToast(`Completed language action: ${payload.title || payload.command}`, 'success');
  });

  supportedLanguages.forEach((langId) => {
    try {
      // 1. Hover
      monaco.languages.registerHoverProvider(langId, {
        async provideHover(model, position) {
          const tab = openTabs.find(t => t.model === model);
          if (!tab || !tab.path) return null;

          const res = await sendHostRequest('lsp_hover', {
            path: tab.path,
            line: position.lineNumber,
            col: position.column
          });

          if (res == null || res.error) return null;

          let contents = [];
          if (typeof res === 'string') {
            contents = [{ value: res }];
          } else if (Array.isArray(res)) {
            contents = res.map(v => ({ value: typeof v === 'string' ? v : (v.value || JSON.stringify(v)) }));
          } else if (typeof res === 'object') {
            contents = [{ value: res.value || JSON.stringify(res) }];
          }

          return contents.length > 0 ? { contents } : null;
        }
      });

      // 2. Definition
      monaco.languages.registerDefinitionProvider(langId, {
        async provideDefinition(model, position) {
          const tab = openTabs.find(t => t.model === model);
          if (!tab || !tab.path) return null;

          const res = await sendHostRequest('lsp_definition', {
            path: tab.path,
            line: position.lineNumber,
            col: position.column
          });

          if (res == null || res.error || !res.path) return null;

          const uri = monaco.Uri.file ? monaco.Uri.file(res.path) : monaco.Uri.parse('file:///' + res.path.replace(/\\/g, '/'));
          const range = new monaco.Range(
            res.line || 1,
            res.column || 1,
            res.line || 1,
            res.column || 1
          );

          if (normalizePath(tab.path) !== normalizePath(res.path)) {
            globalThis.setPendingNavigateLocation?.({ line: res.line || 1, col: res.column || 1 });
            sendHost('read_file', { path: res.path });
          } else {
            // Was `else if (monacoEditor)`, but that is a script-scope `let`
            // In app.js - a module cannot see it, so ask for the editor instead.
            const currentEditor = editor();
            if (currentEditor) {
              currentEditor.setPosition({ lineNumber: res.line || 1, column: res.column || 1 });
              currentEditor.revealPositionInCenter({ lineNumber: res.line || 1, column: res.column || 1 });
            }
          }

          return { uri, range };
        }
      });

      // 3. Completion
      monaco.languages.registerCompletionItemProvider(langId, {
        triggerCharacters: ['.', ':', '>', '"', '/', '@', '<'],
        async provideCompletionItems(model, position) {
          const tab = openTabs.find(t => t.model === model);
          if (!tab || !tab.path) return { suggestions: [] };

          const items = await sendHostRequest('lsp_completion', {
            path: tab.path,
            line: position.lineNumber,
            col: position.column
          });

          if (!Array.isArray(items) || items.length === 0) {
            return { suggestions: [] };
          }

          const word = model.getWordUntilPosition(position);
          const defaultRange = {
            startLineNumber: position.lineNumber,
            endLineNumber: position.lineNumber,
            startColumn: word.startColumn,
            endColumn: word.endColumn
          };

          const suggestions = items.map(item => ({
            label: item.label,
            kind: mapLspKindToMonaco(item.kind),
            detail: item.detail || '',
            insertText: item.insertText || item.label,
            insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
            range: defaultRange
          }));

          return { suggestions };
        }
      });

      // 4. References
      monaco.languages.registerReferenceProvider(langId, {
        async provideReferences(model, position) {
          const tab = openTabs.find(candidate => candidate.model === model);
          if (!tab?.path) return [];
          const references = await sendHostRequest('lsp_references', {
            path: tab.path,
            line: position.lineNumber,
            col: position.column
          }, 12000);
          if (!Array.isArray(references)) return [];
          return references.map(reference => ({
            uri: monaco.Uri.file(reference.path),
            range: new monaco.Range(
              reference.startLine || 1,
              reference.startColumn || 1,
              reference.endLine || reference.startLine || 1,
              reference.endColumn || reference.startColumn || 1
            )
          }));
        }
      });

      // 5. Signature help
      monaco.languages.registerSignatureHelpProvider(langId, {
        signatureHelpTriggerCharacters: ['(', ','],
        signatureHelpRetriggerCharacters: [','],
        async provideSignatureHelp(model, position) {
          const tab = openTabs.find(candidate => candidate.model === model);
          if (!tab?.path) return null;
          const response = await sendHostRequest('lsp_signature_help', {
            path: tab.path,
            line: position.lineNumber,
            col: position.column
          });
          if (!response || !Array.isArray(response.signatures)) return null;
          return {
            value: {
              signatures: response.signatures.map(signature => ({
                label: signature.label || '',
                documentation: lspDocumentationValue(signature.documentation),
                parameters: Array.isArray(signature.parameters)
                  ? signature.parameters.map(parameter => ({
                      label: parameter.label,
                      documentation: lspDocumentationValue(parameter.documentation)
                    }))
                  : []
              })),
              activeSignature: response.activeSignature || 0,
              activeParameter: response.activeParameter || 0
            },
            dispose() { }
          };
        }
      });

      // 6. Document symbols
      monaco.languages.registerDocumentSymbolProvider(langId, {
        async provideDocumentSymbols(model) {
          const tab = openTabs.find(candidate => candidate.model === model);
          if (!tab?.path) return [];
          const symbols = await sendHostRequest('lsp_document_symbols', { path: tab.path }, 10000);
          if (!Array.isArray(symbols)) return [];
          return symbols.map(symbol => ({
            name: symbol.name || '(anonymous)',
            detail: symbol.detail || symbol.containerName || '',
            kind: mapLspSymbolKindToMonaco(symbol.kind),
            range: new monaco.Range(
              symbol.startLine || 1,
              symbol.startColumn || 1,
              symbol.endLine || symbol.startLine || 1,
              symbol.endColumn || symbol.startColumn || 1
            ),
            selectionRange: new monaco.Range(
              symbol.startLine || 1,
              symbol.startColumn || 1,
              symbol.startLine || 1,
              symbol.startColumn || 1
            ),
            children: []
          }));
        }
      });

      // 7. Inlay hints
      monaco.languages.registerInlayHintsProvider(langId, {
        async provideInlayHints(model, range, cancellationToken) {
          const tab = openTabs.find(candidate => candidate.model === model);
          if (!tab?.path) return { hints: [], dispose() { } };
          const hints = await sendHostRequest('lsp_inlay_hints', {
            path: tab.path,
            startLine: range.startLineNumber,
            startColumn: range.startColumn,
            endLine: range.endLineNumber,
            endColumn: range.endColumn
          }, 12000);
          if (cancellationToken.isCancellationRequested || !Array.isArray(hints)) {
            return { hints: [], dispose() { } };
          }
          return {
            hints: hints.map(hint => ({
              position: { lineNumber: hint.line || 1, column: hint.column || 1 },
              label: hint.label || '',
              kind: hint.kind === 1 || hint.kind === 2 ? hint.kind : undefined,
              paddingLeft: !!hint.paddingLeft,
              paddingRight: !!hint.paddingRight,
              tooltip: hint.tooltip ? { value: hint.tooltip } : undefined,
              textEdits: Array.isArray(hint.textEdits)
                ? hint.textEdits.map(edit => ({
                    range: new monaco.Range(
                      edit.startLine || 1,
                      edit.startColumn || 1,
                      edit.endLine || edit.startLine || 1,
                      edit.endColumn || edit.startColumn || 1
                    ),
                    text: edit.newText || ''
                  }))
                : undefined
            })),
            dispose() { }
          };
        }
      });

      // 8. Full-document semantic tokens, remapped by the host to the fixed
      // Legend below so server-specific legend ordering cannot color the
      // Wrong token type.
      monaco.languages.registerDocumentSemanticTokensProvider(langId, {
        getLegend() {
          return {
            tokenTypes: [...LSP_SEMANTIC_TOKEN_TYPES],
            tokenModifiers: [...LSP_SEMANTIC_TOKEN_MODIFIERS]
          };
        },
        async provideDocumentSemanticTokens(model, _lastResultId, cancellationToken) {
          const tab = openTabs.find(candidate => candidate.model === model);
          if (!tab?.path) return null;
          const result = await sendHostRequest('lsp_semantic_tokens', { path: tab.path }, 18000);
          if (cancellationToken.isCancellationRequested || !result || !Array.isArray(result.data)) return null;
          return {
            data: new Uint32Array(result.data),
            resultId: result.resultId || undefined
          };
        },
        releaseDocumentSemanticTokens() { }
      });

      // 9. Rename. Closed affected files are opened as tabs before Monaco
      // Applies the workspace edit so every changed buffer stays visible,
      // Dirty, recoverable, and subject to the normal save flow.
      monaco.languages.registerRenameProvider(langId, {
        resolveRenameLocation(model, position) {
          const word = model.getWordAtPosition(position);
          if (!word) return { range: new monaco.Range(position.lineNumber, position.column, position.lineNumber, position.column), text: '' };
          return {
            range: new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn),
            text: word.word
          };
        },
        async provideRenameEdits(model, position, newName) {
          const sourceTab = openTabs.find(candidate => candidate.model === model);
          if (!sourceTab?.path) return { edits: [], rejectReason: 'The source file is not available.' };

          const edits = await sendHostRequest('lsp_rename', {
            path: sourceTab.path,
            line: position.lineNumber,
            col: position.column,
            newName
          }, 18000);
          if (!Array.isArray(edits) || edits.length === 0) {
            return { edits: [], rejectReason: 'The language server did not return rename edits.' };
          }

          const prepared = await ensureModelsForLspEdits(edits);
          if (!prepared.ok) return { edits: [], rejectReason: prepared.error };

          return {
            edits: lspEditsToMonaco(edits)
          };
        }
      });

      // 10. Document formatting
      monaco.languages.registerDocumentFormattingEditProvider(langId, {
        async provideDocumentFormattingEdits(model, options) {
          const tab = openTabs.find(candidate => candidate.model === model);
          if (!tab?.path) return [];
          const edits = await sendHostRequest('lsp_formatting', {
            path: tab.path,
            tabSize: options.tabSize || 4,
            insertSpaces: options.insertSpaces !== false
          }, 18000);
          if (!Array.isArray(edits)) return [];
          return edits
            .filter(edit => normalizePath(edit.path) === normalizePath(tab.path))
            .map(edit => ({
              range: new monaco.Range(
                edit.startLine || 1,
                edit.startColumn || 1,
                edit.endLine || edit.startLine || 1,
                edit.endColumn || edit.startColumn || 1
              ),
              text: edit.newText || ''
            }));
        }
      });

      // 11. Quick Fix and refactor code actions
      monaco.languages.registerCodeActionProvider(langId, {
        providedCodeActionKinds: [
          'quickfix',
          'refactor',
          'source'
        ],
        async provideCodeActions(model, range, context) {
          const tab = openTabs.find(candidate => candidate.model === model);
          if (!tab?.path) return { actions: [], dispose() { } };
          const diagnostics = (context.markers || []).map(marker => ({
            range: {
              start: { line: marker.startLineNumber - 1, character: marker.startColumn - 1 },
              end: { line: marker.endLineNumber - 1, character: marker.endColumn - 1 }
            },
            severity: marker.severity === monaco.MarkerSeverity.Error ? 1
              : marker.severity === monaco.MarkerSeverity.Warning ? 2
              : marker.severity === monaco.MarkerSeverity.Info ? 3 : 4,
            message: marker.message || '',
            code: marker.code?.value || marker.code || undefined,
            source: marker.source || undefined
          }));
          const actions = await sendHostRequest('lsp_code_actions', {
            path: tab.path,
            startLine: range.startLineNumber,
            startColumn: range.startColumn,
            endLine: range.endLineNumber,
            endColumn: range.endColumn,
            diagnostics
          }, 12000);
          if (!Array.isArray(actions)) return { actions: [], dispose() { } };
          return {
            actions: actions.map(action => ({
              title: action.title,
              kind: action.kind || 'quickfix',
              isPreferred: !!action.isPreferred,
              disabled: action.disabledReason || undefined,
              diagnostics: context.markers,
              data: { path: tab.path, backend: action }
            })),
            dispose() { }
          };
        },
        async resolveCodeAction(action) {
          let backend = action.data?.backend;
          const path = action.data?.path;
          if (!backend || !path) return action;
          if ((!backend.edits || backend.edits.length === 0) && !backend.command && backend.raw) {
            backend = await sendHostRequest('lsp_resolve_code_action', {
              path,
              codeAction: backend.raw
            }, 12000) || backend;
          }

          if (backend.disabledReason) {
            action.disabled = backend.disabledReason;
            return action;
          }

          if (Array.isArray(backend.edits) && backend.edits.length > 0) {
            const prepared = await ensureModelsForLspEdits(backend.edits);
            if (!prepared.ok) {
              action.disabled = prepared.error;
              return action;
            }
            action.edit = { edits: lspEditsToMonaco(backend.edits) };
          }

          if (backend.command) {
            action.command = {
              id: 'voidcode.lsp.executeCommand',
              title: backend.title || action.title,
              arguments: [{
                path,
                command: backend.command,
                arguments: backend.arguments || [],
                title: backend.title || action.title
              }]
            };
          }
          return action;
        }
      });
    } catch (e) {
      console.warn(`Failed to register LSP provider for ${langId}:`, e);
    }
  });
}

// Publishes the entry points app.js and modules/editor-tabs.js call by name.
// Init() does not register anything itself - registerLspProviders() still runs from initMonaco(),
// Because the providers need the loaded monaco namespace.
export function init() {
  Object.assign(globalThis, {
    registerLspProviders,
    notifyLspDidOpen,
    notifyLspDidChange,
    notifyLspDidSave,
    notifyLspDidClose,
    queueLspWorkspaceEditReview,
    applyReviewedLspWorkspaceEdits
  });

  return {
    registerLspProviders,
    notifyLspDidOpen,
    notifyLspDidChange,
    notifyLspDidSave,
    notifyLspDidClose,
    queueLspWorkspaceEditReview,
    applyReviewedLspWorkspaceEdits
  };
}