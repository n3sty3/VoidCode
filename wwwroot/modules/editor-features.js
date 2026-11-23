// Monaco snippets, basic editor actions, breadcrumbs, pending navigation,
// Split editor dual-pane, document symbol quick-pick, and sidebar outline.
import { html, setHtml } from '../core/markdown.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);
let hostRequest = (action, data, timeoutMs) => globalThis.VoidBridge.request(action, data, timeoutMs);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

function sendHostRequest(action, data = {}, timeoutMs = 4000) {
  return hostRequest(action, data, timeoutMs);
}

function getEditor() {
  return globalThis.getMonacoEditor?.() || null;
}

function getActiveTab() {
  return globalThis.VoidState?.get?.('activeTab') || null;
}

function getOpenTabs() {
  return globalThis.VoidState?.get?.('openTabs') || [];
}

function getWorkspace() {
  return globalThis.VoidState?.get?.('workspace') || '';
}

function normPath(p) {
  return globalThis.normalizePath ? globalThis.normalizePath(p) : String(p || '').replace(/\//g, '\\').toLowerCase();
}

// ==========================================
// BUILT-IN MONACO SNIPPETS
// ==========================================
export function setupMonacoSnippets() {
  const m = globalThis.monaco;
  if (!m || !m.languages) return;

  const SNIPPET_DATA = {
    javascript: [
      { label: 'clg', detail: 'console.log snippet', insertText: 'console.log($1);' },
      { label: 'afn', detail: 'Arrow function snippet', insertText: 'const ${1:name} = (${2:args}) => {\n\t$0\n};' },
      { label: 'trycatch', detail: 'Try-catch block', insertText: 'try {\n\t$1\n} catch (${2:err}) {\n\tconsole.error(${2:err});\n}' },
      { label: 'promise', detail: 'New Promise', insertText: 'new Promise((resolve, reject) => {\n\t$1\n});' },
      { label: 'fetchjson', detail: 'Async fetch JSON', insertText: 'const res = await fetch(${1:url});\nconst data = await res.json();\n$0' }
    ],
    typescript: [
      { label: 'clg', detail: 'console.log snippet', insertText: 'console.log($1);' },
      { label: 'interface', detail: 'Interface declaration', insertText: 'interface ${1:Name} {\n\t${2:property}: ${3:string};\n}' },
      { label: 'type', detail: 'Type alias', insertText: 'type ${1:Name} = ${2:string};' },
      { label: 'afn', detail: 'Typed arrow function', insertText: 'const ${1:name} = (${2:args}: ${3:any}): ${4:void} => {\n\t$0\n};' }
    ],
    python: [
      { label: 'def', detail: 'Function definition', insertText: 'def ${1:function_name}(${2:params}):\n\t${3:pass}' },
      { label: 'class', detail: 'Class definition', insertText: 'class ${1:ClassName}:\n\tdef __init__(self, ${2:args}):\n\t\t${3:pass}' },
      { label: 'main', detail: 'if __name__ == "__main__"', insertText: 'if __name__ == "__main__":\n\t${1:main()}' },
      { label: 'tryexcept', detail: 'Try-except block', insertText: 'try:\n\t${1:pass}\nexcept Exception as e:\n\tprint(f"Error: {e}")' }
    ],
    csharp: [
      { label: 'cw', detail: 'Console.WriteLine snippet', insertText: 'Console.WriteLine($1);' },
      { label: 'prop', detail: 'Auto-property snippet', insertText: 'public ${1:string} ${2:MyProperty} { get; set; }' },
      { label: 'class', detail: 'Class snippet', insertText: 'public class ${1:ClassName}\n{\n\t$0\n}' },
      { label: 'method', detail: 'Public method snippet', insertText: 'public ${1:void} ${2:MethodName}(${3})\n{\n\t$0\n}' }
    ],
    css: [
      { label: 'flexc', detail: 'Flexbox center container', insertText: 'display: flex;\nalign-items: center;\njustify-content: center;\n$0' },
      { label: 'gridc', detail: 'Grid auto layout', insertText: 'display: grid;\ngrid-template-columns: repeat(auto-fill, minmax(${1:200px}, 1fr));\ngap: ${2:16px};\n$0' }
    ]
  };

  Object.entries(SNIPPET_DATA).forEach(([lang, snippets]) => {
    try {
      m.languages.registerCompletionItemProvider(lang, {
        provideCompletionItems: (model, position) => {
          const word = model.getWordUntilPosition(position);
          const range = {
            startLineNumber: position.lineNumber,
            endLineNumber: position.lineNumber,
            startColumn: word.startColumn,
            endColumn: word.endColumn
          };

          const suggestions = snippets.map(s => ({
            label: s.label,
            kind: m.languages.CompletionItemKind.Snippet,
            detail: s.detail,
            insertText: s.insertText,
            insertTextRules: m.languages.CompletionItemInsertTextRule.InsertAsSnippet,
            range
          }));

          return { suggestions };
        }
      });
    } catch (e) {
      console.warn(`Failed to register snippets for ${lang}`, e);
    }
  });

  registerMonacoVoidAgentActions();
}

export function registerMonacoVoidAgentActions() {
  const editor = getEditor();
  if (!editor || typeof editor.addAction !== 'function') return;

  const sendSelectionToAgent = (promptText) => {
    const sel = globalThis.getActiveSelectionContext?.();
    if (!sel) {
      globalThis.showToast?.('Select code in the editor first', 'info');
      return;
    }
    globalThis.attachContextItem?.({
      id: 'selection',
      type: 'selection',
      label: `Selection (${sel.label})`,
      content: sel.text
    });
    globalThis.toggleWorkspacePane?.('agent', true);
    const box = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
    if (box) {
      if (promptText) box.value = promptText;
      box.focus();
    }
  };

  const actions = [
    { id: 'voidcode.ai.inlineEdit', label: 'VoidAgent: Edit Selection Inline (Ctrl+K)', order: 1, run: () => globalThis.openInlineAiAssist?.() },
    { id: 'voidcode.ai.attachSelection', label: 'VoidAgent: Attach Selection to Chat', order: 2, run: () => sendSelectionToAgent('') },
    { id: 'voidcode.ai.explainSelection', label: 'VoidAgent: Explain Selection', order: 3, run: () => sendSelectionToAgent('Explain how this selected code works, its invariants, and edge cases.') },
    { id: 'voidcode.ai.auditSelection', label: 'VoidAgent: Find Bugs & Edge Cases in Selection', order: 4, run: () => sendSelectionToAgent('Audit this selected code for bugs, edge cases, race conditions, or security issues.') },
    { id: 'voidcode.ai.testSelection', label: 'VoidAgent: Generate Unit Tests for Selection', order: 5, run: () => sendSelectionToAgent('Write unit tests covering normal paths and edge cases for this selected code.') }
  ];

  actions.forEach(a => {
    try {
      editor.addAction({
        id: a.id,
        label: a.label,
        contextMenuGroupId: '1_voidagent',
        contextMenuOrder: a.order,
        run: a.run
      });
    } catch (err) { globalThis.logSwallowed?.(err, 'editor-features.addAction'); }
  });
}

export function adjustEditorZoom(delta) {
  const settings = globalThis.VoidState?.get?.('settings') || {};
  const current = Number(settings.EditorFontSize) || 14;
  const next = delta === 0 ? 14 : Math.max(10, Math.min(28, current + delta));
  settings.EditorFontSize = next;
  const fontInput = /** @type {HTMLInputElement | null} */ (document.getElementById('settings-font-size'));
  if (fontInput) fontInput.value = String(next);
  getEditor()?.updateOptions({ fontSize: next });
  if (monacoEditorSecondary) monacoEditorSecondary.updateOptions({ fontSize: next });
  globalThis.showToast?.(`Editor font size: ${next}px`, 'info');
}

export function toggleEditorWordWrap() {
  const settings = globalThis.VoidState?.get?.('settings') || {};
  const next = !settings.EditorWordWrap;
  settings.EditorWordWrap = next;
  const wrapBox = /** @type {HTMLInputElement | null} */ (document.getElementById('settings-word-wrap'));
  if (wrapBox) wrapBox.checked = next;
  const wrapVal = next ? 'on' : 'off';
  getEditor()?.updateOptions({ wordWrap: wrapVal });
  if (monacoEditorSecondary) monacoEditorSecondary.updateOptions({ wordWrap: wrapVal });
  globalThis.showToast?.(`Word wrap: ${next ? 'On' : 'Off'}`, 'info');
}

let zenModeSnapshot = null;

export function toggleZenMode() {
  const ids = ['topbar', 'activity-rail', 'sidebar-explorer', 'resizer-left', 'sidebar-agent', 'resizer-right', 'terminal-panel', 'resizer-bottom', 'statusbar'];
  if (!zenModeSnapshot) {
    zenModeSnapshot = {};
    ids.forEach(id => {
      const el = document.getElementById(id);
      if (el) {
        zenModeSnapshot[id] = el.style.display;
        el.style.display = 'none';
      }
    });
    setTimeout(() => getEditor()?.layout?.(), 40);
    globalThis.showToast?.('Zen Mode active (press Ctrl+Shift+Z to exit)', 'info');
  } else {
    ids.forEach(id => {
      const el = document.getElementById(id);
      if (el && zenModeSnapshot[id] !== undefined) {
        el.style.display = zenModeSnapshot[id];
      }
    });
    zenModeSnapshot = null;
    setTimeout(() => getEditor()?.layout?.(), 40);
    globalThis.showToast?.('Exited Zen Mode', 'info');
  }
}

export async function switchActiveModelQuick() {
  const settings = globalThis.VoidState?.get?.('settings') || {};
  if (typeof globalThis.showInputDialog !== 'function') return;
  const nextModel = await globalThis.showInputDialog(
    'Switch Active AI Model (e.g. gemini-2.5-pro, claude-sonnet-4-20250514, gpt-4.1, deepseek/deepseek-chat)',
    settings.Model || 'deepseek/deepseek-chat',
    'Switch model'
  );
  if (!nextModel || !nextModel.trim()) return;
  settings.Model = nextModel.trim();
  const modelInput = /** @type {HTMLInputElement | null} */ (document.getElementById('settings-model'));
  if (modelInput) modelInput.value = settings.Model;
  const lbl = document.getElementById('active-model-label');
  if (lbl) lbl.textContent = settings.Model;
  sendHost('save_settings', { settings, apiKey: '', clearApiKey: false });
}

// ==========================================
// FILE CREATION & BASIC MONACO ACTIONS
// ==========================================
export async function promptNewFile() {
  const ws = getWorkspace();
  if (!ws) {
    globalThis.showToast?.('Open a project folder first', 'info');
    return;
  }
  const name = await globalThis.showInputDialog?.('New file name', '', 'Create');
  if (name) sendHost('create_entry', { path: `${ws}\\${name}`, isDir: false });
}

export async function promptNewFolder() {
  const ws = getWorkspace();
  if (!ws) {
    globalThis.showToast?.('Open a project folder first', 'info');
    return;
  }
  const name = await globalThis.showInputDialog?.('New folder name', '', 'Create');
  if (name) sendHost('create_entry', { path: `${ws}\\${name}`, isDir: true });
}

export function runEditorActionId(actionId) {
  const editor = getEditor();
  if (!editor) {
    globalThis.showToast?.('Open a file to edit first', 'info');
    return;
  }
  try {
    const action = editor.getAction(actionId);
    if (action) { action.run(); editor.focus(); return; }
    editor.trigger('keyboard', actionId, null);
    editor.focus();
  } catch (err) {
    globalThis.logSwallowed?.(err, 'editor-features.runEditorActionId');
    globalThis.showToast?.('This action is not available for the current file', 'info');
  }
}

export async function goToLine() {
  const editor = getEditor();
  if (!editor) {
    globalThis.showToast?.('Open a file first', 'info');
    return;
  }
  const answer = await globalThis.showInputDialog?.('Go to Line', '', 'Go');
  if (answer === null || answer === undefined) return;
  const line = parseInt(answer, 10);
  if (!Number.isFinite(line) || line < 1) return;
  const model = editor.getModel();
  const target = model ? Math.min(line, model.getLineCount()) : line;
  editor.revealLineInCenter(target);
  editor.setPosition({ lineNumber: target, column: 1 });
  editor.focus();
}

export async function formatActiveDocument() {
  const activeTab = getActiveTab();
  const editor = getEditor();
  if (!activeTab || !editor) return;
  try {
    const action = editor.getAction('editor.action.formatDocument');
    if (action && action.isSupported()) {
      await action.run();
      globalThis.showToast?.('Document formatted', 'info');
      return;
    }
  } catch (err) { globalThis.logSwallowed?.(err, 'editor-features.formatDocument.monaco'); }

  try {
    const ext = activeTab.path.split('.').pop().toLowerCase();
    const val = editor.getValue();
    if (ext === 'json') {
      const parsed = JSON.parse(val);
      const formatted = JSON.stringify(parsed, null, 2);
      if (formatted !== val) {
        const state = editor.saveViewState();
        editor.setValue(formatted);
        if (state) editor.restoreViewState(state);
        globalThis.showToast?.('JSON formatted', 'info');
      }
    }
  } catch (err) { globalThis.logSwallowed?.(err, 'editor-features.formatJson'); }
}

// ==========================================
// BREADCRUMBS & PENDING NAVIGATION
// ==========================================
let pendingNavigateLocation = null;

export function setPendingNavigateLocation(loc) {
  pendingNavigateLocation = loc || null;
}

export function updateBreadcrumbs(path) {
  const wsEl = document.getElementById('breadcrumb-workspace');
  const fileEl = document.getElementById('breadcrumb-file');
  if (!wsEl || !fileEl) return;

  const bar = document.getElementById('editor-breadcrumbs');
  const ws = getWorkspace();
  const wsName = ws ? (ws.split(/[\\/]/).pop() || ws) : 'workspace';
  wsEl.textContent = wsName;

  if (!path) {
    fileEl.textContent = '';
    if (bar) bar.style.display = 'none';
    return;
  }
  if (bar) bar.style.display = '';

  let rel = path;
  if (ws && path.toLowerCase().startsWith(ws.toLowerCase())) {
    rel = path.slice(ws.length).replace(/^[\\/]/, '');
  }
  fileEl.textContent = rel.replace(/[\\/]/g, ' / ');
}

export function navigateToFileLocation(fullPath, line, col, length) {
  pendingNavigateLocation = { line, col, length };
  const existing = getOpenTabs().find(t => normPath(t.path) === normPath(fullPath));
  if (existing) {
    globalThis.setActiveTab?.(existing);
    applyPendingLocation();
  } else {
    sendHost('read_file', { path: fullPath });
  }
}

export function applyPendingLocation() {
  const editor = getEditor();
  if (!pendingNavigateLocation || !editor) return;
  const { line, col, length } = pendingNavigateLocation;
  pendingNavigateLocation = null;

  try {
    editor.setPosition({ lineNumber: line, column: col });
    if (length && length > 0 && globalThis.monaco) {
      editor.setSelection(new globalThis.monaco.Range(line, col, line, col + length));
    }
    editor.revealPositionInCenter({ lineNumber: line, column: col });
    editor.focus();
  } catch (err) { globalThis.logSwallowed?.(err, 'editor-features.goToPosition'); }
}

// ==========================================
// SPLIT EDITOR DUAL PANE (Ctrl+\)
// ==========================================
let isSplitEditorActive = false;
let monacoEditorSecondary = null;
let splitRatio = 0.5;

export function getIsSplitEditorActive() {
  return isSplitEditorActive;
}

function applySplitRatio() {
  const primary = document.getElementById('editor-pane-primary');
  const secondary = document.getElementById('editor-pane-secondary');
  if (primary && secondary) {
    primary.style.flex = `${splitRatio}`;
    secondary.style.flex = `${1 - splitRatio}`;
    getEditor()?.layout();
    monacoEditorSecondary?.layout();
  }
}

export function setupSplitEditor() {
  const btnSplit = document.getElementById('btn-split-editor');
  const btnClose = document.getElementById('btn-close-split');
  const divider = document.getElementById('editor-split-divider');
  const container = document.getElementById('editor-workspace-split');

  btnSplit?.addEventListener('click', () => toggleSplitEditor());
  btnClose?.addEventListener('click', () => closeSplitEditor());

  if (divider && container) {
    let isDragging = false;

    divider.addEventListener('mousedown', (e) => {
      isDragging = true;
      divider.classList.add('is-dragging');
      document.body.style.cursor = 'col-resize';
      e.preventDefault();
    });

    window.addEventListener('mousemove', (e) => {
      if (!isDragging) return;
      const rect = container.getBoundingClientRect();
      const offset = e.clientX - rect.left;
      splitRatio = Math.max(0.15, Math.min(0.85, offset / rect.width));
      applySplitRatio();
    });

    window.addEventListener('mouseup', () => {
      if (isDragging) {
        isDragging = false;
        divider.classList.remove('is-dragging');
        document.body.style.cursor = '';
        getEditor()?.layout();
        monacoEditorSecondary?.layout();
      }
    });

    // Keyboard alternative for resizing the split divider
    divider.addEventListener('keydown', (e) => {
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
      e.preventDefault();
      splitRatio = Math.max(0.15, Math.min(0.85, splitRatio + (e.key === 'ArrowRight' ? 0.05 : -0.05)));
      applySplitRatio();
      divider.setAttribute('aria-valuenow', String(Math.round(splitRatio * 100)));
    });
  }
}

export function toggleSplitEditor() {
  if (isSplitEditorActive) closeSplitEditor();
  else openSplitEditor();
}

export function openSplitEditor(tabToOpen) {
  const divider = document.getElementById('editor-split-divider');
  const secondaryPane = document.getElementById('editor-pane-secondary');
  const primaryPane = document.getElementById('editor-pane-primary');
  const btnSplit = document.getElementById('btn-split-editor');
  const titleEl = document.getElementById('secondary-editor-title');

  if (!divider || !secondaryPane || !primaryPane) return;

  isSplitEditorActive = true;
  divider.style.display = 'block';
  secondaryPane.style.display = 'flex';
  primaryPane.style.flex = `${splitRatio}`;
  secondaryPane.style.flex = `${1 - splitRatio}`;
  btnSplit?.classList.add('is-active');

  const openTabs = getOpenTabs();
  const activeTab = getActiveTab();
  let targetTab = tabToOpen;
  if (!targetTab) {
    if (openTabs.length > 1) {
      targetTab = openTabs.find(t => normPath(t.path) !== normPath(activeTab?.path)) || activeTab;
    } else {
      targetTab = activeTab;
    }
  }

  if (titleEl) {
    titleEl.textContent = targetTab ? targetTab.name : 'No file';
    titleEl.title = targetTab ? targetTab.path : '';
  }

  const m = globalThis.monaco;
  if (!monacoEditorSecondary && m) {
    const mount = document.getElementById('monaco-mount-secondary');
    if (mount) {
      monacoEditorSecondary = m.editor.create(mount, {
        value: '',
        language: 'plaintext',
        theme: 'harbor-glass',
        automaticLayout: true,
        fontSize: 14,
        lineHeight: 22,
        fontFamily: "'JetBrains Mono', 'Cascadia Code', Consolas, monospace",
        fontLigatures: true,
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        smoothScrolling: true,
        cursorBlinking: 'smooth',
        glyphMargin: true,
        tabSize: 4,
        padding: { top: 14, bottom: 14 }
      });

      monacoEditorSecondary.onMouseDown((e) => {
        if (e.target.type === m.editor.MouseTargetType.GUTTER_GLYPH_MARGIN) {
          const line = e.target.position.lineNumber;
          globalThis.toggleEditorBreakpoint?.(line);
        }
      });
    }
  }

  if (monacoEditorSecondary && targetTab && targetTab.model) {
    monacoEditorSecondary.setModel(targetTab.model);
  }

  setTimeout(() => {
    getEditor()?.layout();
    monacoEditorSecondary?.layout();
  }, 50);
}

export function closeSplitEditor() {
  isSplitEditorActive = false;

  const divider = document.getElementById('editor-split-divider');
  const secondaryPane = document.getElementById('editor-pane-secondary');
  const primaryPane = document.getElementById('editor-pane-primary');
  const btnSplit = document.getElementById('btn-split-editor');

  if (divider) divider.style.display = 'none';
  if (secondaryPane) secondaryPane.style.display = 'none';
  if (primaryPane) primaryPane.style.flex = '1';
  btnSplit?.classList.remove('is-active');

  setTimeout(() => {
    getEditor()?.layout();
  }, 50);
}

// ==========================================
// DOCUMENT OUTLINE & SYMBOLS (Ctrl+Shift+O)
// ==========================================
let currentSymbols = [];
let symbolSelectedIndex = 0;
let outlineRequestSequence = 0;
let outlineUpdateTimer = null;

export function extractDocumentSymbols(code, filename) {
  if (!code) return [];
  const lines = code.split('\n');
  const symbols = [];
  const ext = (filename || '').split('.').pop().toLowerCase();

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const lineNum = i + 1;

    if (['js', 'ts', 'jsx', 'tsx', 'mjs', 'cjs'].includes(ext)) {
      let m = line.match(/(?:async\s+)?function\s+([a-zA-Z0-9_$]+)\s*\(/);
      if (m) { symbols.push({ name: m[1], kind: 'func', line: lineNum, col: m.index + 1 }); continue; }

      m = line.match(/(?:const|let|var)\s+([a-zA-Z0-9_$]+)\s*=\s*(?:async\s*)?\([^)]*\)\s*=>/);
      if (m) { symbols.push({ name: m[1], kind: 'func', line: lineNum, col: m.index + 1 }); continue; }

      m = line.match(/class\s+([a-zA-Z0-9_$]+)/);
      if (m) { symbols.push({ name: m[1], kind: 'class', line: lineNum, col: m.index + 1 }); continue; }

      m = line.match(/(?:interface|type)\s+([a-zA-Z0-9_$]+)/);
      if (m) { symbols.push({ name: m[1], kind: 'class', line: lineNum, col: m.index + 1 }); continue; }
    } else if (['cs', 'vb'].includes(ext)) {
      let m = line.match(/(?:public|private|protected|internal|static|async|\s)+\s+(?:class|struct|interface|enum|record)\s+([A-Za-z0-9_]+)/);
      if (m) { symbols.push({ name: m[1], kind: 'class', line: lineNum, col: m.index + 1 }); continue; }

      m = line.match(/(?:public|private|protected|internal|static|async|\s)+\s+[A-Za-z0-9_<>,[\]?]+\s+([A-Za-z0-9_]+)\s*\(/);
      if (m && !line.includes('new ') && !line.includes('return ') && !line.includes('throw ')) {
        symbols.push({ name: m[1], kind: 'func', line: lineNum, col: m.index + 1 });
        continue;
      }
    } else if (ext === 'py') {
      let m = line.match(/^\s*def\s+([A-Za-z0-9_]+)\s*\(/);
      if (m) { symbols.push({ name: m[1], kind: 'func', line: lineNum, col: m.index + 1 }); continue; }

      m = line.match(/^\s*class\s+([A-Za-z0-9_]+)/);
      if (m) { symbols.push({ name: m[1], kind: 'class', line: lineNum, col: m.index + 1 }); continue; }
    } else if (['rs', 'go'].includes(ext)) {
      let m = line.match(/(?:fn|func)\s+(?:\([^)]+\)\s*)?([A-Za-z0-9_]+)/);
      if (m) { symbols.push({ name: m[1], kind: 'func', line: lineNum, col: m.index + 1 }); continue; }

      m = line.match(/(?:struct|type|enum)\s+([A-Za-z0-9_]+)/);
      if (m) { symbols.push({ name: m[1], kind: 'class', line: lineNum, col: m.index + 1 }); continue; }
    } else if (['css', 'scss', 'less'].includes(ext)) {
      const m = line.match(/^([.#a-zA-Z0-9_\-:,\s]+)\s*\{/);
      if (m && !m[1].includes('@media') && !m[1].includes('@keyframes')) {
        symbols.push({ name: m[1].trim(), kind: 'prop', line: lineNum, col: 1 });
        continue;
      }
    } else if (['html', 'htm', 'xml', 'svg'].includes(ext)) {
      const m = line.match(/<(h[1-6]|section|header|nav|footer|article|main)[^>]*>(.*?)<\/\1>/i);
      if (m) {
        const text = m[2].replace(/<[^>]+>/g, '').trim() || m[1];
        symbols.push({ name: `${m[1]}: ${text}`, kind: 'prop', line: lineNum, col: m.index + 1 });
        continue;
      }
    }
  }

  return symbols;
}

export function symbolCategoryFromLspKind(kind) {
  if ([5, 10, 11, 23].includes(kind)) return 'class';
  if ([6, 9, 12].includes(kind)) return 'func';
  return 'prop';
}

export async function getDocumentSymbolsForTab(tab) {
  if (!tab?.model || tab.model.isDisposed()) return [];
  const lang = globalThis.getLanguageInfo ? globalThis.getLanguageInfo(tab.path).id : 'plaintext';
  const supported = ['python', 'csharp', 'rust', 'go', 'cpp', 'typescript', 'javascript'];
  if (supported.includes(lang) && globalThis.chrome?.webview) {
    const lspSymbols = await sendHostRequest('lsp_document_symbols', { path: tab.path }, 10000);
    if (Array.isArray(lspSymbols) && lspSymbols.length > 0) {
      return lspSymbols.map(symbol => ({
        name: symbol.name || '(anonymous)',
        detail: symbol.detail || symbol.containerName || '',
        kind: symbolCategoryFromLspKind(symbol.kind),
        lspKind: symbol.kind,
        line: symbol.startLine || 1,
        col: symbol.startColumn || 1
      }));
    }
  }
  return extractDocumentSymbols(tab.model.getValue(), tab.name);
}

export function setupSymbolQuickPick() {
  const btnDocSymbols = document.getElementById('btn-document-symbols');
  const modal = document.getElementById('symbol-modal');
  const input = /** @type {HTMLInputElement | null} */ (document.getElementById('symbol-input'));
  const outlineHeader = document.getElementById('sidebar-outline-header');
  const outlineContainer = document.getElementById('sidebar-outline-container');

  btnDocSymbols?.addEventListener('click', () => openSymbolQuickPick());

  outlineHeader?.addEventListener('click', () => {
    if (!outlineContainer) return;
    const isHidden = outlineContainer.style.display === 'none';
    outlineContainer.style.display = isHidden ? 'block' : 'none';
    outlineHeader.querySelector('.tree-chevron')?.classList.toggle('expanded', isHidden);
  });

  if (input && modal) {
    input.addEventListener('input', () => {
      renderSymbolResults(input.value.trim());
    });

    input.addEventListener('keydown', (e) => {
      const items = modal.querySelectorAll('.symbol-result-item');
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        symbolSelectedIndex = Math.min(items.length - 1, symbolSelectedIndex + 1);
        updateSymbolSelectionHighlight(items);
      } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        symbolSelectedIndex = Math.max(0, symbolSelectedIndex - 1);
        updateSymbolSelectionHighlight(items);
      } else if (e.key === 'Enter') {
        e.preventDefault();
        const chosen = /** @type {HTMLElement | undefined} */ (items[symbolSelectedIndex]);
        if (chosen) chosen.click();
      } else if (e.key === 'Escape') {
        modal.style.display = 'none';
      }
    });

    modal.addEventListener('click', (e) => {
      if (e.target === modal) modal.style.display = 'none';
    });
  }
}

export async function openSymbolQuickPick() {
  const activeTab = getActiveTab();
  if (!activeTab || !getEditor()) {
    globalThis.showToast?.('Open a code file to browse symbols.', 'info');
    return;
  }

  const modal = document.getElementById('symbol-modal');
  const input = /** @type {HTMLInputElement | null} */ (document.getElementById('symbol-input'));
  if (!modal || !input) return;

  input.value = '';
  modal.style.display = 'flex';
  input.focus();
  symbolSelectedIndex = 0;
  setHtml(document.getElementById('symbol-results-list'), '<div class="outline-empty">Loading symbols...</div>');
  const sourceTab = activeTab;
  currentSymbols = await getDocumentSymbolsForTab(sourceTab);
  if (sourceTab !== getActiveTab() || modal.style.display === 'none') return;
  renderSymbolResults('');
}

export function renderSymbolResults(filterText) {
  const listEl = document.getElementById('symbol-results-list');
  if (!listEl) return;
  setHtml(listEl, '');

  const query = (filterText || '').toLowerCase();
  const matched = currentSymbols.filter(s => s.name.toLowerCase().includes(query));

  if (matched.length === 0) {
    setHtml(listEl, '<div class="outline-empty">No matching symbols found.</div>');
    return;
  }

  matched.forEach((sym, idx) => {
    const div = document.createElement('div');
    div.className = 'symbol-result-item' + (idx === symbolSelectedIndex ? ' is-selected' : '');
    setHtml(div, html`
      <div class="symbol-result-left">
        <span class="symbol-kind-badge kind-${sym.kind}">${sym.kind === 'func' ? 'fn' : sym.kind === 'class' ? 'cls' : 'prop'}</span>
        <span class="symbol-result-name">${sym.name}</span>
      </div>
      <span class="symbol-result-line">Ln ${sym.line}</span>
    `);

    div.addEventListener('click', () => {
      jumpToSymbol(sym.line, sym.col);
      const modal = document.getElementById('symbol-modal');
      if (modal) modal.style.display = 'none';
    });

    listEl.appendChild(div);
  });
}

export function updateSymbolSelectionHighlight(items) {
  items.forEach((item, idx) => {
    item.classList.toggle('is-selected', idx === symbolSelectedIndex);
    if (idx === symbolSelectedIndex) item.scrollIntoView({ block: 'nearest' });
  });
}

export function jumpToSymbol(line, col) {
  const editor = getEditor();
  if (!editor) return;
  editor.setPosition({ lineNumber: line, column: col || 1 });
  editor.revealLineInCenter(line);
  editor.focus();
}

export function updateSidebarOutline() {
  const container = document.getElementById('sidebar-outline-container');
  if (!container) return;

  const activeTab = getActiveTab();
  if (!activeTab || !getEditor()) {
    setHtml(container, '<div class="outline-empty">No file open.</div>');
    return;
  }

  clearTimeout(outlineUpdateTimer);
  const sourceTab = activeTab;
  const requestSequence = ++outlineRequestSequence;
  outlineUpdateTimer = setTimeout(async () => {
    const symbols = await getDocumentSymbolsForTab(sourceTab);
    if (requestSequence !== outlineRequestSequence || sourceTab !== getActiveTab()) return;
    renderSidebarOutlineSymbols(container, symbols);
  }, 320);
}

let sidebarOutlineFilter = '';

export function renderSidebarOutlineSymbols(container, symbols) {
  if (symbols.length === 0) {
    setHtml(container, '<div class="outline-empty">No symbols detected in current file.</div>');
    return;
  }

  setHtml(container, '');
  const filterInput = document.createElement('input');
  filterInput.type = 'text';
  filterInput.className = 'filter-input';
  filterInput.placeholder = 'Filter symbols...';
  filterInput.autocomplete = 'off';
  filterInput.spellcheck = false;
  filterInput.value = sidebarOutlineFilter;
  filterInput.style.margin = '4px 6px 6px';
  filterInput.style.width = 'calc(100% - 12px)';
  container.appendChild(filterInput);

  const listWrap = document.createElement('div');
  container.appendChild(listWrap);

  const renderFiltered = (query) => {
    const q = (query || '').trim().toLowerCase();
    setHtml(listWrap, '');
    const matched = q ? symbols.filter(s => s.name.toLowerCase().includes(q)) : symbols;
    if (matched.length === 0) {
      setHtml(listWrap, '<div class="outline-empty">No matching symbols.</div>');
      return;
    }
    matched.forEach(sym => {
      const div = document.createElement('div');
      div.className = 'outline-item';
      div.title = `${sym.name}${sym.detail ? `, ${sym.detail}` : ''} (Line ${sym.line})`;
      setHtml(div, html`
        <span class="symbol-kind-badge kind-${sym.kind}">${sym.kind === 'func' ? 'fn' : sym.kind === 'class' ? 'cls' : 'prop'}</span>
        <span>${sym.name}</span>
      `);
      div.addEventListener('click', () => jumpToSymbol(sym.line, sym.col));
      listWrap.appendChild(div);
    });
  };

  filterInput.addEventListener('input', () => {
    sidebarOutlineFilter = filterInput.value;
    renderFiltered(sidebarOutlineFilter);
  });

  renderFiltered(sidebarOutlineFilter);
}

export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  if (typeof ctx.sendHostRequest === 'function') hostRequest = ctx.sendHostRequest;

  const wireBreadcrumbs = () => {
    const wsCrumb = document.getElementById('breadcrumb-workspace');
    const fileCrumb = document.getElementById('breadcrumb-file');
    if (wsCrumb) {
      wsCrumb.style.cursor = 'pointer';
      wsCrumb.title = 'Quick Open file in workspace (Ctrl+P)';
      wsCrumb.addEventListener('click', () => globalThis.openQuickOpenModal?.());
    }
    if (fileCrumb) {
      fileCrumb.style.cursor = 'pointer';
      fileCrumb.title = 'Go to Symbol in file (Ctrl+Shift+O)';
      fileCrumb.addEventListener('click', () => globalThis.openSymbolQuickPick?.());
    }
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', wireBreadcrumbs, { once: true });
  else wireBreadcrumbs();

  const api = {
    setupMonacoSnippets,
    registerMonacoVoidAgentActions,
    adjustEditorZoom,
    toggleEditorWordWrap,
    toggleZenMode,
    switchActiveModelQuick,
    promptNewFile,
    promptNewFolder,
    runEditorActionId,
    goToLine,
    formatActiveDocument,
    setPendingNavigateLocation,
    updateBreadcrumbs,
    navigateToFileLocation,
    applyPendingLocation,
    getIsSplitEditorActive,
    setupSplitEditor,
    toggleSplitEditor,
    openSplitEditor,
    closeSplitEditor,
    extractDocumentSymbols,
    symbolCategoryFromLspKind,
    getDocumentSymbolsForTab,
    setupSymbolQuickPick,
    openSymbolQuickPick,
    renderSymbolResults,
    updateSymbolSelectionHighlight,
    jumpToSymbol,
    updateSidebarOutline,
    renderSidebarOutlineSymbols
  };
  Object.assign(globalThis, api);
  return api;
}
