// In-editor AI inline assist (Ctrl+K): target range, preview/decorations, apply.
//
// Main.js calls init(), the entry points are exported + published on
// GlobalThis for the app.js (bridge switch, palette, Ctrl+K, boot), and the editor
// Handle comes from app.js's getMonacoEditor() accessor - a module cannot see another script's `let`.
let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

function getEditor() {
  return globalThis.getMonacoEditor?.() || null;
}

// ==========================================
// IN-EDITOR AI INLINE ASSIST (Ctrl+K)
// ==========================================
let inlineAiTargetRange = null;
let inlineAiOriginalCode = '';
let inlineAiProposedCode = '';
let inlineAiDecorations = [];

// One typed lookup instead of a cast per call site - the controls this module reads
// By id are inputs, and tsc only knows getElementById returns a plain HTMLElement.
/**
 * @param {string} id
 * @returns {HTMLInputElement | null}
 */
function control(id) {
  return /** @type {HTMLInputElement | null} */ (document.getElementById(id));
}

export function setupInlineAiAssist() {
  const widget = control('inline-ai-widget');
  const input = control('inline-ai-input');
  const btnTrigger = control('btn-inline-ai-trigger');
  const btnClose = control('btn-inline-ai-close');
  const btnCancel = control('btn-inline-ai-cancel');
  const btnSubmit = control('btn-inline-ai-submit');
  const btnAccept = control('btn-inline-ai-accept');
  const btnDiscard = control('btn-inline-ai-discard');

  if (!widget || !input) return;

  btnTrigger?.addEventListener('click', () => openInlineAiAssist());
  btnClose?.addEventListener('click', () => closeInlineAiAssist());
  btnCancel?.addEventListener('click', () => cancelInlineAiAssist());
  btnSubmit?.addEventListener('click', () => submitInlineAiAssist());
  btnAccept?.addEventListener('click', () => acceptInlineAiAssist());
  btnDiscard?.addEventListener('click', () => discardInlineAiAssist());

  // Quick Action Chips
  document.querySelectorAll('[data-inline-action]').forEach(btn => {
    btn.addEventListener('click', () => {
      const action = btn.getAttribute('data-inline-action');
      let prompt = '';
      switch (action) {
        case 'refactor':
          prompt = 'Refactor and modernize this code for maximum clarity and maintainability.';
          break;
        case 'comments':
          prompt = 'Add clear documentation comments explaining inputs, outputs, and logic.';
          break;
        case 'fix':
          prompt = 'Review this code and fix all potential bugs, edge cases, null references, and error handling.';
          break;
        case 'optimize':
          prompt = 'Optimize this code for execution performance, algorithmic efficiency, and low memory overhead.';
          break;
        case 'tests':
          prompt = 'Generate comprehensive unit tests with test cases and assertions for this code.';
          break;
      }
      input.value = prompt;
      submitInlineAiAssist(prompt);
    });
  });

  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      submitInlineAiAssist();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      closeInlineAiAssist();
    }
  });

  // Global Esc & Ctrl+Enter when widget is open
  window.addEventListener('keydown', (e) => {
    if (widget.style.display !== 'none') {
      if (e.key === 'Escape') {
        e.preventDefault();
        if (inlineAiProposedCode) {
          discardInlineAiAssist();
        } else {
          closeInlineAiAssist();
        }
      } else if (e.ctrlKey && e.key === 'Enter') {
        e.preventDefault();
        if (inlineAiProposedCode) {
          acceptInlineAiAssist();
        } else {
          submitInlineAiAssist();
        }
      }
    }
  });
}

export function openInlineAiAssist() {
  const stateActiveTab = window.VoidState.get('activeTab');
  const editor = getEditor();
  if (!editor || !stateActiveTab) {
    globalThis.showToast?.('Open a code file to use AI Inline Assist', 'info');
    return;
  }

  const widget = control('inline-ai-widget');
  const input = control('inline-ai-input');
  const container = control('editor-container');
  if (!widget || !input || !container) return;

  let selection = editor.getSelection();
  const model = editor.getModel();
  if (!selection || selection.isEmpty()) {
    const pos = editor.getPosition() || { lineNumber: 1, column: 1 };
    selection = new monaco.Selection(pos.lineNumber, 1, pos.lineNumber, model.getLineMaxColumn(pos.lineNumber));
    editor.setSelection(selection);
  }

  inlineAiTargetRange = selection;
  inlineAiOriginalCode = model.getValueInRange(selection);
  inlineAiProposedCode = '';
  clearInlineAiDecorations();

  // Update context label
  const startLine = selection.startLineNumber;
  const endLine = selection.endLineNumber;
  const lineCount = endLine - startLine + 1;
  const pill = control('inline-ai-context-pill');
  if (pill) {
    pill.textContent = lineCount === 1 ? `Line ${startLine}` : `Lines ${startLine}–${endLine} (${lineCount} lines)`;
  }

  // Position near selection
  const coords = editor.getScrolledVisiblePosition(selection.getStartPosition());
  if (coords && coords.top !== undefined) {
    const topOffset = Math.max(16, Math.min(coords.top - 76, container.clientHeight - 240));
    widget.style.top = `${topOffset}px`;
  } else {
    widget.style.top = '24px';
  }

  // Reset display states
  control('inline-ai-chips').style.display = 'flex';
  document.querySelector('.inline-ai-input-row').style.display = 'flex';
  control('inline-ai-status-row').style.display = 'none';
  control('inline-ai-diff-banner').style.display = 'none';
  input.value = '';
  widget.style.display = 'flex';

  setTimeout(() => input.focus(), 50);
}

export function closeInlineAiAssist() {
  const widget = control('inline-ai-widget');
  if (widget) widget.style.display = 'none';
  clearInlineAiDecorations();
  const editor = getEditor();
  if (editor) editor.focus();
}

export function submitInlineAiAssist(overridePrompt) {
  const input = control('inline-ai-input');
  const prompt = (overridePrompt || (input ? input.value : '')).trim();
  if (!prompt) {
    input?.focus();
    return;
  }

  control('inline-ai-chips').style.display = 'none';
  control('inline-ai-status-row').style.display = 'flex';
  control('inline-ai-status-text').textContent = 'Connecting to model…';
  control('inline-ai-diff-banner').style.display = 'none';

  const stateActiveTab = window.VoidState.get('activeTab');
  sendHost('inline_assist_start', {
    instruction: prompt,
    selectedCode: inlineAiOriginalCode,
    fileContent: getEditor()?.getValue() || '',
    relativePath: stateActiveTab ? stateActiveTab.path : 'active_file',
    language: stateActiveTab ? stateActiveTab.language || 'plaintext' : 'plaintext'
  });
}

export function cancelInlineAiAssist() {
  sendHost('inline_assist_cancel');
  control('inline-ai-status-row').style.display = 'none';
  control('inline-ai-chips').style.display = 'flex';
}

export function updateInlineAiStatus(status) {
  const statusEl = control('inline-ai-status-text');
  if (statusEl) statusEl.textContent = status;
}

export function updateInlineAiDelta(_delta) {
  // Delta stream notification
}

export function handleInlineAssistComplete(payload) {
  inlineAiProposedCode = payload.replacement || '';

  const statusRow = control('inline-ai-status-row');
  const diffBanner = control('inline-ai-diff-banner');
  if (statusRow) statusRow.style.display = 'none';
  if (diffBanner) diffBanner.style.display = 'flex';

  // Compute line diff metrics
  const origLineCount = inlineAiOriginalCode.split('\n').length;
  const newLineCount = inlineAiProposedCode.split('\n').length;
  const lineDiff = newLineCount - origLineCount;
  const statsEl = control('inline-diff-stats');
  if (statsEl) {
    statsEl.textContent = `${lineDiff >= 0 ? '+' : ''}${lineDiff} lines (${newLineCount} new vs ${origLineCount} orig)`;
  }

  // Push proposed edit into Monaco model for live preview
  const editor = getEditor();
  if (editor && inlineAiTargetRange) {
    const model = editor.getModel();
    if (model) {
      const editOp = {
        range: inlineAiTargetRange,
        text: inlineAiProposedCode,
        forceMoveMarkers: true
      };
      model.pushEditOperations([], [editOp], () => null);

      // Highlight the modified region
      const endLine = inlineAiTargetRange.startLineNumber + (newLineCount - 1);
      const lastLineStr = inlineAiProposedCode.split('\n').pop() || '';
      const newRange = new monaco.Range(inlineAiTargetRange.startLineNumber, 1, endLine, lastLineStr.length + 1);

      inlineAiDecorations = editor.deltaDecorations(inlineAiDecorations, [
        {
          range: newRange,
          options: {
            isWholeLine: true,
            className: 'inline-ai-diff-highlight'
          }
        }
      ]);
    }
  }
}

export function handleInlineAssistError(message) {
  const statusEl = control('inline-ai-status-text');
  if (statusEl) statusEl.textContent = `Error: ${message}`;
  globalThis.showToast?.(message, 'error');
}

export function handleInlineAssistCancelled() {
  control('inline-ai-status-row').style.display = 'none';
  control('inline-ai-chips').style.display = 'flex';
}

export function acceptInlineAiAssist() {
  clearInlineAiDecorations();
  closeInlineAiAssist();
  const stateActiveTab = window.VoidState.get('activeTab');
  if (stateActiveTab) {
    stateActiveTab.dirty = true;
    globalThis.renderTabs?.();
  }
  globalThis.showToast?.('AI edit accepted (Ctrl+Z to undo)', 'success');
}

export function discardInlineAiAssist() {
  const editor = getEditor();
  if (editor) {
    editor.trigger('inline-ai', 'undo');
  }
  clearInlineAiDecorations();
  closeInlineAiAssist();
  globalThis.showToast?.('Proposed edit discarded', 'info');
}

export function clearInlineAiDecorations() {
  const editor = getEditor();
  if (editor && inlineAiDecorations.length > 0) {
    inlineAiDecorations = editor.deltaDecorations(inlineAiDecorations, []);
  }
}

// Main.js calls this once; the entry points go on globalThis for the classic app.js
// (bridge switch, palette, Ctrl+K and boot).
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  Object.assign(globalThis, {
    setupInlineAiAssist,
    openInlineAiAssist,
    closeInlineAiAssist,
    submitInlineAiAssist,
    cancelInlineAiAssist,
    updateInlineAiStatus,
    updateInlineAiDelta,
    handleInlineAssistComplete,
    handleInlineAssistError,
    handleInlineAssistCancelled,
    acceptInlineAiAssist,
    discardInlineAiAssist,
    clearInlineAiDecorations
  });
  return { setupInlineAiAssist, openInlineAiAssist, closeInlineAiAssist, submitInlineAiAssist, cancelInlineAiAssist, updateInlineAiStatus, updateInlineAiDelta, handleInlineAssistComplete, handleInlineAssistError, handleInlineAssistCancelled, acceptInlineAiAssist, discardInlineAiAssist, clearInlineAiDecorations };
}
