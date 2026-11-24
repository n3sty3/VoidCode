// Native Vim Mode for Monaco Editor (Normal, Insert, Visual, Visual-Line, and :Command modes).
// Zero external dependencies; attaches cleanly via Monaco onKeyDown when enabled.

const STORAGE_KEY = 'voidcode_vim_mode_v1';

let enabled = false;
/** @type {'normal' | 'insert' | 'visual' | 'visual-line' | 'command'} */
let mode = 'normal';
let pendingKeys = '';
let registerText = '';
let registerLinewise = false;
let visualAnchor = null;
let commandBuffer = '';
let attachedEditor = null;
let keyDisposable = null;
let pillEl = null;

function getEditor() {
  return globalThis.getMonacoEditor?.() || null;
}

function setMode(nextMode) {
  mode = nextMode;
  pendingKeys = '';
  if (mode !== 'visual' && mode !== 'visual-line') visualAnchor = null;
  if (mode !== 'command') commandBuffer = '';

  const ed = getEditor();
  if (ed) {
    const cursorStyle = !enabled
      ? (window.VoidState?.get?.('settings')?.EditorCursorStyle || 'line')
      : (mode === 'insert' ? 'line' : 'block');
    ed.updateOptions?.({ cursorStyle });
  }
  updateStatusPill();
}

function updateStatusPill() {
  if (!pillEl) ensureStatusPill();
  if (!pillEl) return;

  if (!enabled) {
    pillEl.textContent = 'VIM';
    pillEl.title = 'Click to enable Native Vim Mode';
    pillEl.style.color = 'var(--text-muted)';
    pillEl.style.borderColor = 'transparent';
    pillEl.style.background = 'transparent';
    return;
  }

  if (mode === 'command') {
    pillEl.textContent = `:${commandBuffer}`;
    pillEl.style.color = '#f5b963';
    pillEl.style.borderColor = 'rgba(245,185,99,0.45)';
    pillEl.style.background = 'rgba(245,185,99,0.12)';
    return;
  }

  const map = {
    normal: { label: pendingKeys ? `NORMAL (${pendingKeys})` : 'NORMAL', color: 'var(--glacier)', bg: 'var(--glacier-wash)', border: 'var(--glacier-line)' },
    insert: { label: 'INSERT', color: '#6be2b3', bg: 'rgba(107,226,179,0.12)', border: 'rgba(107,226,179,0.38)' },
    visual: { label: 'VISUAL', color: '#f5b963', bg: 'rgba(245,185,99,0.12)', border: 'rgba(245,185,99,0.42)' },
    'visual-line': { label: 'V-LINE', color: '#f5b963', bg: 'rgba(245,185,99,0.12)', border: 'rgba(245,185,99,0.42)' }
  };
  const style = map[mode] || map.normal;
  pillEl.textContent = `VIM · ${style.label}`;
  pillEl.title = 'Native Vim Mode active (click to disable)';
  pillEl.style.color = style.color;
  pillEl.style.background = style.bg;
  pillEl.style.borderColor = style.border;
}

function ensureStatusPill() {
  if (pillEl) return pillEl;
  const bar = document.querySelector('.status-right') || document.getElementById('status-bar');
  if (!bar) return null;
  pillEl = document.createElement('button');
  pillEl.id = 'status-vim-pill';
  pillEl.type = 'button';
  pillEl.className = 'status-item';
  pillEl.style.cssText = 'font-family:var(--font-code);font-size:11px;font-weight:600;padding:1px 7px;border-radius:5px;border:1px solid transparent;cursor:pointer;min-height:24px;';
  pillEl.addEventListener('click', () => toggleVimMode());
  bar.prepend(pillEl);
  return pillEl;
}

function updateVisualSelection(ed) {
  const monaco = globalThis.monaco;
  if (!monaco || !visualAnchor) return;
  const pos = ed.getPosition();
  const model = ed.getModel();
  if (!pos || !model) return;

  if (mode === 'visual-line') {
    const startLine = Math.min(visualAnchor.lineNumber, pos.lineNumber);
    const endLine = Math.max(visualAnchor.lineNumber, pos.lineNumber);
    ed.setSelection(new monaco.Selection(startLine, 1, endLine, model.getLineMaxColumn(endLine)));
  } else {
    ed.setSelection(new monaco.Selection(visualAnchor.lineNumber, visualAnchor.column, pos.lineNumber, pos.column));
  }
}

function movePos(ed, line, col) {
  const model = ed.getModel();
  if (!model) return;
  const clampedLine = Math.max(1, Math.min(model.getLineCount(), line));
  const maxCol = Math.max(1, model.getLineMaxColumn(clampedLine));
  const clampedCol = Math.max(1, Math.min(maxCol, col));
  ed.setPosition({ lineNumber: clampedLine, column: clampedCol });
  ed.revealPositionInCenterIfOutsideViewport?.({ lineNumber: clampedLine, column: clampedCol });
  if (mode === 'visual' || mode === 'visual-line') {
    updateVisualSelection(ed);
  }
}

function nextWordCol(lineText, col) {
  const idx = Math.max(0, col - 1);
  const rest = lineText.slice(idx);
  const m = /^(\w+|[^\w\s]+|\s+)\s*/.exec(rest);
  if (!m) return lineText.length + 1;
  return Math.min(lineText.length + 1, idx + m[0].length + 1);
}

function prevWordCol(lineText, col) {
  const idx = Math.max(0, col - 2);
  let i = idx;
  while (i > 0 && /\s/.test(lineText[i])) i--;
  if (i <= 0) return 1;
  const isWord = /\w/.test(lineText[i]);
  while (i > 0 && (isWord ? /\w/.test(lineText[i - 1]) : !/\w|\s/.test(lineText[i - 1]))) i--;
  return i + 1;
}

function wordEndCol(lineText, col) {
  let i = Math.min(lineText.length - 1, col);
  while (i < lineText.length - 1 && /\s/.test(lineText[i])) i++;
  const isWord = /\w/.test(lineText[i] || '');
  while (i < lineText.length - 1 && (isWord ? /\w/.test(lineText[i + 1]) : !/\w|\s/.test(lineText[i + 1]))) i++;
  return Math.min(lineText.length + 1, i + 1);
}

function deleteLine(ed, lineNo, enterInsert = false) {
  const monaco = globalThis.monaco;
  const model = ed.getModel();
  if (!monaco || !model) return;
  const total = model.getLineCount();
  registerText = model.getLineContent(lineNo);
  registerLinewise = true;

  let range;
  if (enterInsert) {
    range = new monaco.Range(lineNo, 1, lineNo, model.getLineMaxColumn(lineNo));
  } else if (total === 1) {
    range = new monaco.Range(1, 1, 1, model.getLineMaxColumn(1));
  } else if (lineNo < total) {
    range = new monaco.Range(lineNo, 1, lineNo + 1, 1);
  } else {
    range = new monaco.Range(lineNo - 1, model.getLineMaxColumn(lineNo - 1), lineNo, model.getLineMaxColumn(lineNo));
  }
  ed.executeEdits('vim', [{ range, text: '' }]);
  if (enterInsert) setMode('insert');
}

function changeInsideDelimiters(ed, openChar, closeChar) {
  const monaco = globalThis.monaco;
  const model = ed.getModel();
  const pos = ed.getPosition();
  if (!monaco || !model || !pos) return false;
  const line = model.getLineContent(pos.lineNumber);
  const idx = pos.column - 1;
  const left = line.lastIndexOf(openChar, idx);
  const right = line.indexOf(closeChar, left >= 0 ? left + 1 : idx);
  if (left < 0 || right <= left) return false;
  const range = new monaco.Range(pos.lineNumber, left + 2, pos.lineNumber, right + 1);
  registerText = model.getValueInRange(range);
  registerLinewise = false;
  ed.executeEdits('vim', [{ range, text: '' }]);
  movePos(ed, pos.lineNumber, left + 2);
  setMode('insert');
  return true;
}

function executeExCommand(ed, cmd) {
  const trimmed = cmd.trim();
  if (!trimmed) return;
  if (trimmed === 'w') {
    globalThis.saveActiveFile?.();
  } else if (trimmed === 'q') {
    const tab = window.VoidState?.get?.('activeTab');
    if (tab) globalThis.closeTabByPath?.(tab.path);
  } else if (trimmed === 'wq' || trimmed === 'x') {
    globalThis.saveActiveFile?.();
    const tab = window.VoidState?.get?.('activeTab');
    if (tab) setTimeout(() => globalThis.closeTabByPath?.(tab.path), 120);
  } else if (/^\d+$/.test(trimmed)) {
    movePos(ed, parseInt(trimmed, 10), 1);
  } else {
    globalThis.showToast?.(`Vim: unknown command :${trimmed}`, 'info');
  }
}

function handleKeyDown(e) {
  if (!enabled) return;
  const ed = getEditor();
  const monaco = globalThis.monaco;
  if (!ed || !monaco) return;

  const browserEvent = e.browserEvent;
  if (!browserEvent) return;
  const key = browserEvent.key;
  const ctrl = browserEvent.ctrlKey || browserEvent.metaKey;
  const alt = browserEvent.altKey;

  // Never hijack IDE-wide shortcuts (Ctrl+S, Ctrl+P, Ctrl+K, Ctrl+T, Alt+Y/N, etc.)
  if (alt) return;
  if (ctrl && !['r', 'd', 'u', '['].includes(key.toLowerCase())) return;

  // INSERT MODE
  if (mode === 'insert') {
    if (key === 'Escape' || (ctrl && key === '[')) {
      e.preventDefault();
      e.stopPropagation();
      const pos = ed.getPosition();
      setMode('normal');
      if (pos && pos.column > 1) movePos(ed, pos.lineNumber, pos.column - 1);
    }
    return;
  }

  // COMMAND MODE (:)
  if (mode === 'command') {
    e.preventDefault();
    e.stopPropagation();
    if (key === 'Escape') {
      setMode('normal');
    } else if (key === 'Enter') {
      const toRun = commandBuffer;
      setMode('normal');
      executeExCommand(ed, toRun);
    } else if (key === 'Backspace') {
      if (commandBuffer.length === 0) setMode('normal');
      else {
        commandBuffer = commandBuffer.slice(0, -1);
        updateStatusPill();
      }
    } else if (key.length === 1 && !ctrl) {
      commandBuffer += key;
      updateStatusPill();
    }
    return;
  }

  // NORMAL & VISUAL MODES: intercept printable & navigation keys
  if (['Shift', 'Control', 'Alt', 'Meta', 'CapsLock', 'Tab'].includes(key)) return;
  e.preventDefault();
  e.stopPropagation();

  const model = ed.getModel();
  const pos = ed.getPosition();
  if (!model || !pos) return;
  const lineCount = model.getLineCount();
  const lineText = model.getLineContent(pos.lineNumber);

  if (key === 'Escape' || (ctrl && key === '[')) {
    setMode('normal');
    ed.setSelection(new monaco.Selection(pos.lineNumber, pos.column, pos.lineNumber, pos.column));
    return;
  }

  // Ctrl motions in Normal/Visual
  if (ctrl) {
    const lk = key.toLowerCase();
    if (lk === 'r') { ed.trigger('vim', 'redo', null); return; }
    if (lk === 'd') { movePos(ed, pos.lineNumber + 15, pos.column); return; }
    if (lk === 'u') { movePos(ed, pos.lineNumber - 15, pos.column); return; }
    return;
  }

  // Multi-key sequences (gg, dd, yy, cc, dw, cw, ciw, ci", ci', ci(, ci{, >>, <<)
  if (pendingKeys) {
    const seq = pendingKeys + key;
    pendingKeys = '';
    if (seq === 'gg') { movePos(ed, 1, 1); updateStatusPill(); return; }
    if (seq === 'dd') { deleteLine(ed, pos.lineNumber, false); updateStatusPill(); return; }
    if (seq === 'cc') { deleteLine(ed, pos.lineNumber, true); return; }
    if (seq === 'yy') {
      registerText = lineText;
      registerLinewise = true;
      globalThis.showToast?.('Yanked line', 'info');
      updateStatusPill();
      return;
    }
    if (seq === 'dw' || seq === 'cw') {
      const endCol = nextWordCol(lineText, pos.column);
      const range = new monaco.Range(pos.lineNumber, pos.column, pos.lineNumber, endCol);
      registerText = model.getValueInRange(range);
      registerLinewise = false;
      ed.executeEdits('vim', [{ range, text: '' }]);
      if (seq === 'cw') setMode('insert');
      else updateStatusPill();
      return;
    }
    if (seq === 'ci' || seq === 'di') {
      pendingKeys = seq;
      updateStatusPill();
      return;
    }
    if (seq === 'ciw' || seq === 'diw') {
      const word = model.getWordAtPosition(pos);
      if (word) {
        const range = new monaco.Range(pos.lineNumber, word.startColumn, pos.lineNumber, word.endColumn);
        registerText = model.getValueInRange(range);
        registerLinewise = false;
        ed.executeEdits('vim', [{ range, text: '' }]);
        movePos(ed, pos.lineNumber, word.startColumn);
        if (seq === 'ciw') setMode('insert');
      }
      updateStatusPill();
      return;
    }
    if (seq === 'ci"') { changeInsideDelimiters(ed, '"', '"'); return; }
    if (seq === "ci'") { changeInsideDelimiters(ed, "'", "'"); return; }
    if (seq === 'ci(' || seq === 'ci)') { changeInsideDelimiters(ed, '(', ')'); return; }
    if (seq === 'ci{' || seq === 'ci}') { changeInsideDelimiters(ed, '{', '}'); return; }
    if (seq === 'ci[' || seq === 'ci]') { changeInsideDelimiters(ed, '[', ']'); return; }
    if (seq === '>>') { ed.trigger('vim', 'editor.action.indentLines', null); updateStatusPill(); return; }
    if (seq === '<<') { ed.trigger('vim', 'editor.action.outdentLines', null); updateStatusPill(); return; }
    updateStatusPill();
    return;
  }

  // Visual mode operators
  if (mode === 'visual' || mode === 'visual-line') {
    const sel = ed.getSelection();
    if (sel && !sel.isEmpty()) {
      if (key === 'd' || key === 'x' || key === 'c') {
        registerText = model.getValueInRange(sel);
        registerLinewise = mode === 'visual-line';
        ed.executeEdits('vim', [{ range: sel, text: '' }]);
        setMode(key === 'c' ? 'insert' : 'normal');
        return;
      }
      if (key === 'y') {
        registerText = model.getValueInRange(sel);
        registerLinewise = mode === 'visual-line';
        navigator.clipboard?.writeText?.(registerText);
        setMode('normal');
        ed.setSelection(new monaco.Selection(sel.startLineNumber, sel.startColumn, sel.startLineNumber, sel.startColumn));
        return;
      }
      if (key === '>') { ed.trigger('vim', 'editor.action.indentLines', null); setMode('normal'); return; }
      if (key === '<') { ed.trigger('vim', 'editor.action.outdentLines', null); setMode('normal'); return; }
    }
  }

  switch (key) {
    case 'h': case 'ArrowLeft': movePos(ed, pos.lineNumber, pos.column - 1); break;
    case 'l': case 'ArrowRight': movePos(ed, pos.lineNumber, pos.column + 1); break;
    case 'j': case 'ArrowDown': movePos(ed, pos.lineNumber + 1, pos.column); break;
    case 'k': case 'ArrowUp': movePos(ed, pos.lineNumber - 1, pos.column); break;
    case 'w': movePos(ed, pos.lineNumber, nextWordCol(lineText, pos.column)); break;
    case 'b': movePos(ed, pos.lineNumber, prevWordCol(lineText, pos.column)); break;
    case 'e': movePos(ed, pos.lineNumber, wordEndCol(lineText, pos.column)); break;
    case '0': movePos(ed, pos.lineNumber, 1); break;
    case '^': {
      const m = /^\s*/.exec(lineText);
      movePos(ed, pos.lineNumber, (m ? m[0].length : 0) + 1);
      break;
    }
    case '$': movePos(ed, pos.lineNumber, Math.max(1, lineText.length)); break;
    case 'G': movePos(ed, lineCount, 1); break;
    case '{': {
      let ln = pos.lineNumber - 1;
      while (ln > 1 && model.getLineContent(ln).trim() !== '') ln--;
      movePos(ed, ln, 1);
      break;
    }
    case '}': {
      let ln = pos.lineNumber + 1;
      while (ln < lineCount && model.getLineContent(ln).trim() !== '') ln++;
      movePos(ed, ln, 1);
      break;
    }
    case '%': ed.trigger('vim', 'editor.action.jumpToBracket', null); break;
    case '/': ed.trigger('vim', 'actions.find', null); break;
    case 'n': ed.trigger('vim', 'editor.action.nextMatchFindAction', null); break;
    case 'N': ed.trigger('vim', 'editor.action.previousMatchFindAction', null); break;
    case 'i': setMode('insert'); break;
    case 'a': movePos(ed, pos.lineNumber, pos.column + 1); setMode('insert'); break;
    case 'I': {
      const m = /^\s*/.exec(lineText);
      movePos(ed, pos.lineNumber, (m ? m[0].length : 0) + 1);
      setMode('insert');
      break;
    }
    case 'A': movePos(ed, pos.lineNumber, model.getLineMaxColumn(pos.lineNumber)); setMode('insert'); break;
    case 'o': {
      const endCol = model.getLineMaxColumn(pos.lineNumber);
      const indent = (/^\s*/.exec(lineText) || [''])[0];
      ed.executeEdits('vim', [{ range: new monaco.Range(pos.lineNumber, endCol, pos.lineNumber, endCol), text: `\n${indent}` }]);
      movePos(ed, pos.lineNumber + 1, indent.length + 1);
      setMode('insert');
      break;
    }
    case 'O': {
      const indent = (/^\s*/.exec(lineText) || [''])[0];
      ed.executeEdits('vim', [{ range: new monaco.Range(pos.lineNumber, 1, pos.lineNumber, 1), text: `${indent}\n` }]);
      movePos(ed, pos.lineNumber, indent.length + 1);
      setMode('insert');
      break;
    }
    case 'v': visualAnchor = pos; setMode('visual'); updateVisualSelection(ed); break;
    case 'V': visualAnchor = pos; setMode('visual-line'); updateVisualSelection(ed); break;
    case ':': setMode('command'); break;
    case 'u': ed.trigger('vim', 'undo', null); break;
    case 'x': {
      if (pos.column < model.getLineMaxColumn(pos.lineNumber)) {
        const range = new monaco.Range(pos.lineNumber, pos.column, pos.lineNumber, pos.column + 1);
        registerText = model.getValueInRange(range);
        registerLinewise = false;
        ed.executeEdits('vim', [{ range, text: '' }]);
      }
      break;
    }
    case 'D':
    case 'C': {
      const range = new monaco.Range(pos.lineNumber, pos.column, pos.lineNumber, model.getLineMaxColumn(pos.lineNumber));
      registerText = model.getValueInRange(range);
      registerLinewise = false;
      ed.executeEdits('vim', [{ range, text: '' }]);
      if (key === 'C') setMode('insert');
      break;
    }
    case 'p':
    case 'P': {
      if (!registerText) break;
      if (registerLinewise) {
        if (key === 'p') {
          const endCol = model.getLineMaxColumn(pos.lineNumber);
          ed.executeEdits('vim', [{ range: new monaco.Range(pos.lineNumber, endCol, pos.lineNumber, endCol), text: `\n${registerText}` }]);
          movePos(ed, pos.lineNumber + 1, 1);
        } else {
          ed.executeEdits('vim', [{ range: new monaco.Range(pos.lineNumber, 1, pos.lineNumber, 1), text: `${registerText}\n` }]);
          movePos(ed, pos.lineNumber, 1);
        }
      } else {
        const insertCol = key === 'p' ? Math.min(model.getLineMaxColumn(pos.lineNumber), pos.column + 1) : pos.column;
        ed.executeEdits('vim', [{ range: new monaco.Range(pos.lineNumber, insertCol, pos.lineNumber, insertCol), text: registerText }]);
      }
      break;
    }
    case 'J': {
      if (pos.lineNumber < lineCount) {
        const nextLine = model.getLineContent(pos.lineNumber + 1).trimStart();
        const range = new monaco.Range(pos.lineNumber, model.getLineMaxColumn(pos.lineNumber), pos.lineNumber + 1, model.getLineMaxColumn(pos.lineNumber + 1));
        ed.executeEdits('vim', [{ range, text: nextLine ? ` ${nextLine}` : '' }]);
      }
      break;
    }
    case 'g':
    case 'd':
    case 'y':
    case 'c':
    case '>':
    case '<':
      pendingKeys = key;
      updateStatusPill();
      break;
    default:
      break;
  }
}

function attachToEditorIfReady() {
  const ed = getEditor();
  if (!ed || ed === attachedEditor) return;
  if (keyDisposable) {
    keyDisposable.dispose?.();
    keyDisposable = null;
  }
  attachedEditor = ed;
  keyDisposable = ed.onKeyDown(handleKeyDown);
  if (enabled) setMode('normal');
}

export function setVimMode(on) {
  enabled = !!on;
  try {
    localStorage.setItem(STORAGE_KEY, enabled ? '1' : '0');
  } catch (err) {
    globalThis.logSwallowed?.(err, 'vim-mode.persist');
  }
  attachToEditorIfReady();
  setMode(enabled ? 'normal' : 'insert');
  globalThis.showToast?.(enabled ? 'Native Vim Mode enabled (Esc = Normal, i = Insert)' : 'Native Vim Mode disabled', 'info');
}

export function toggleVimMode() {
  setVimMode(!enabled);
}

export function isVimModeEnabled() {
  return enabled;
}

export function init() {
  try {
    enabled = localStorage.getItem(STORAGE_KEY) === '1';
  } catch (err) {
    globalThis.logSwallowed?.(err, 'vim-mode.load');
  }

  const bootstrap = () => {
    ensureStatusPill();
    updateStatusPill();
    const timer = setInterval(() => {
      if (getEditor()) {
        attachToEditorIfReady();
        clearInterval(timer);
      }
    }, 350);
    setTimeout(() => clearInterval(timer), 12000);
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', bootstrap, { once: true });
  } else {
    bootstrap();
  }

  const api = { setVimMode, toggleVimMode, isVimModeEnabled };
  Object.assign(globalThis, api);
  return api;
}
