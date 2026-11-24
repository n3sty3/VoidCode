// Custom Obsidian Glass Monaco Editor Context Menu.
// Replaces Monaco's default gray VS Code right-click menu with VoidCode's native
// .context-menu.glass.glass--float design system, complete with VoidAgent actions,
// LSP navigation/refactoring, clipboard operations, and keyboard navigation.
import { html, raw, setHtml } from '../core/markdown.js';

const IS_MAC = typeof navigator !== 'undefined' && /Mac|iPhone|iPad|iPod/i.test(navigator.platform || navigator.userAgent || '');
const MOD = IS_MAC ? '⌘' : 'Ctrl+';

const attachedEditors = new WeakSet();
let activeEditor = null;
let menuEl = null;

function svgIcon(paths) {
  return `<svg class="ctx-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${paths}</svg>`;
}

const ICONS = {
  spark: svgIcon('<path d="m12 3-1.9 5.8a2 2 0 0 1-1.3 1.3L3 12l5.8 1.9a2 2 0 0 1 1.3 1.3L12 21l1.9-5.8a2 2 0 0 1 1.3-1.3L21 12l-5.8-1.9a2 2 0 0 1-1.3-1.3z"/>'),
  attach: svgIcon('<path d="m21.44 11.05-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48"/>'),
  explain: svgIcon('<circle cx="12" cy="12" r="9"/><path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3"/><path d="M12 17h.01"/>'),
  bug: svgIcon('<path d="m8 2 1.88 1.88M14.12 3.88 16 2M9 7.13v-1a3.003 3.003 0 1 1 6 0v1"/><path d="M12 20c-3.3 0-6-2.7-6-6v-3a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v3c0 3.3-2.7 6-6 6"/><path d="M12 20v-9M6.53 9C4.6 8.8 3 7.1 3 5M6 13H2M3 21c0-2.1 1.7-3.9 3.8-4M20.97 5c0 2.1-1.6 3.8-3.5 4M22 13h-4M17.2 17c2.1.1 3.8 1.9 3.8 4"/>'),
  flask: svgIcon('<path d="M10 2v7.527a2 2 0 0 1-.211.896L4.72 20.55a1 1 0 0 0 .9 1.45h12.76a1 1 0 0 0 .9-1.45l-5.069-10.127A2 2 0 0 1 14 9.527V2"/><path d="M8.5 2h7"/><path d="M7 16h10"/>'),
  def: svgIcon('<path d="M15 3h6v6"/><path d="M10 14 21 3"/><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>'),
  refs: svgIcon('<circle cx="11" cy="11" r="7"/><path d="m21 21-4.3-4.3"/>'),
  rename: svgIcon('<path d="M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z"/>'),
  multi: svgIcon('<path d="M4 7V4h16v3"/><path d="M9 20h6"/><path d="M12 4v16"/>'),
  format: svgIcon('<line x1="21" y1="6" x2="3" y2="6"/><line x1="15" y1="12" x2="3" y2="12"/><line x1="17" y1="18" x2="3" y2="18"/>'),
  cut: svgIcon('<circle cx="6" cy="6" r="3"/><circle cx="6" cy="18" r="3"/><line x1="20" y1="4" x2="8.12" y2="15.88"/><line x1="14.47" y1="14.48" x2="20" y2="20"/><line x1="8.12" y1="8.12" x2="12" y2="12"/>'),
  copy: svgIcon('<rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>'),
  paste: svgIcon('<rect x="8" y="2" width="8" height="4" rx="1"/><path d="M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2"/>'),
  palette: svgIcon('<polyline points="4 17 10 11 4 5"/><line x1="12" y1="19" x2="20" y2="19"/>')
};

function ensureMenuElement() {
  if (menuEl && document.body.contains(menuEl)) return menuEl;
  const existing = document.getElementById('editor-context-menu');
  if (existing) {
    menuEl = existing;
    return menuEl;
  }
  const el = document.createElement('div');
  el.id = 'editor-context-menu';
  el.className = 'context-menu glass glass--float';
  el.style.cssText = 'display:none;position:fixed;z-index:9500;min-width:255px;max-width:310px;padding:6px;';
  el.setAttribute('role', 'menu');
  el.setAttribute('aria-label', 'Editor context menu');
  document.body.appendChild(el);
  menuEl = el;
  return el;
}

export function hideEditorContextMenu() {
  if (menuEl) {
    menuEl.style.display = 'none';
  }
}

function getSelectedText(editor) {
  try {
    const model = editor?.getModel?.();
    const sel = editor?.getSelection?.();
    if (!model || !sel || sel.isEmpty()) return '';
    return model.getValueInRange(sel) || '';
  } catch (err) {
    globalThis.logSwallowed?.(err, 'editor-context-menu.getSelectedText');
    return '';
  }
}

function sendSelectionOrLineToAgent(editor, promptText) {
  const selCtx = globalThis.getActiveSelectionContext?.();
  if (selCtx) {
    globalThis.attachContextItem?.({
      id: 'selection',
      type: 'selection',
      label: `Selection (${selCtx.label})`,
      content: selCtx.text
    });
  } else {
    const model = editor?.getModel?.();
    const pos = editor?.getPosition?.();
    const activeTab = globalThis.VoidState?.get?.('activeTab');
    if (model && pos) {
      const start = Math.max(1, pos.lineNumber - 8);
      const end = Math.min(model.getLineCount(), pos.lineNumber + 8);
      const snippet = model.getValueInRange({
        startLineNumber: start,
        startColumn: 1,
        endLineNumber: end,
        endColumn: model.getLineMaxColumn(end)
      });
      const fileName = (activeTab?.path || 'editor').split(/[\\/]/).pop();
      globalThis.attachContextItem?.({
        id: 'selection',
        type: 'selection',
        label: `${fileName}:${start}-${end}`,
        content: snippet
      });
    }
  }
  globalThis.toggleWorkspacePane?.('agent', true);
  const box = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('agent-input-text'));
  if (box) {
    if (promptText) box.value = promptText;
    box.focus();
  }
}

async function runAction(actionId) {
  const editor = activeEditor || globalThis.getMonacoEditor?.();
  hideEditorContextMenu();
  if (!editor) return;
  editor.focus();

  const triggerMonaco = (id) => {
    try {
      const action = editor.getAction?.(id);
      if (action) {
        action.run();
      } else {
        editor.trigger('voidcode-ctx', id, null);
      }
    } catch (err) {
      globalThis.logSwallowed?.(err, `editor-context-menu.${id}`);
    }
  };

  switch (actionId) {
    case 'ai.inline':
      globalThis.openInlineAiAssist?.();
      break;
    case 'ai.attach':
      sendSelectionOrLineToAgent(editor, '');
      break;
    case 'ai.explain':
      sendSelectionOrLineToAgent(editor, 'Explain how this code works, its invariants, and any edge cases.');
      break;
    case 'ai.audit':
      sendSelectionOrLineToAgent(editor, 'Audit this code for bugs, edge cases, race conditions, or security issues.');
      break;
    case 'ai.tests':
      sendSelectionOrLineToAgent(editor, 'Write thorough unit tests covering normal paths and edge cases for this code.');
      break;
    case 'nav.definition':
      triggerMonaco('editor.action.revealDefinition');
      break;
    case 'nav.references':
      triggerMonaco('editor.action.goToReferences');
      break;
    case 'refactor.rename':
      triggerMonaco('editor.action.rename');
      break;
    case 'refactor.occurrences':
      triggerMonaco('editor.action.changeAll');
      break;
    case 'refactor.format':
      if (typeof globalThis.formatActiveDocument === 'function') {
        globalThis.formatActiveDocument();
      } else {
        triggerMonaco('editor.action.formatDocument');
      }
      break;
    case 'edit.cut': {
      const text = getSelectedText(editor);
      if (text) {
        try { await navigator.clipboard.writeText(text); } catch (err) { globalThis.logSwallowed?.(err, 'ctx.cut'); }
        const sel = editor.getSelection();
        if (sel) editor.executeEdits('voidcode-ctx', [{ range: sel, text: '', forceMoveMarkers: true }]);
      } else {
        triggerMonaco('editor.action.clipboardCutAction');
      }
      break;
    }
    case 'edit.copy': {
      const text = getSelectedText(editor);
      if (text) {
        try { await navigator.clipboard.writeText(text); } catch (err) { globalThis.logSwallowed?.(err, 'ctx.copy'); }
      } else {
        triggerMonaco('editor.action.clipboardCopyAction');
      }
      break;
    }
    case 'edit.paste': {
      try {
        const text = await navigator.clipboard.readText();
        if (typeof text === 'string' && text.length > 0) {
          const sel = editor.getSelection();
          if (sel) {
            editor.executeEdits('voidcode-ctx', [{ range: sel, text, forceMoveMarkers: true }]);
            break;
          }
        }
      } catch (err) {
        globalThis.logSwallowed?.(err, 'ctx.paste');
      }
      triggerMonaco('editor.action.clipboardPasteAction');
      break;
    }
    case 'edit.selectAll':
      triggerMonaco('editor.action.selectAll');
      break;
    case 'cmd.palette':
      globalThis.openCommandPalette?.();
      break;
    default:
      break;
  }
}

function renderMenuItems(hasSelection) {
  const el = ensureMenuElement();
  const selSuffix = hasSelection ? 'Selection' : 'Around Cursor';

  const items = [
    { group: 'VoidAgent' },
    { id: 'ai.inline', icon: ICONS.spark, label: 'Edit Inline with AI', hint: `${MOD}K` },
    { id: 'ai.attach', icon: ICONS.attach, label: `Attach ${selSuffix} to Chat` },
    { id: 'ai.explain', icon: ICONS.explain, label: `Explain ${selSuffix}` },
    { id: 'ai.audit', icon: ICONS.bug, label: 'Find Bugs & Edge Cases' },
    { id: 'ai.tests', icon: ICONS.flask, label: 'Generate Unit Tests' },
    { sep: true },
    { group: 'Navigate & Refactor' },
    { id: 'nav.definition', icon: ICONS.def, label: 'Go to Definition', hint: 'F12' },
    { id: 'nav.references', icon: ICONS.refs, label: 'Find All References', hint: 'Shift+F12' },
    { id: 'refactor.rename', icon: ICONS.rename, label: 'Rename Symbol', hint: 'F2' },
    { id: 'refactor.occurrences', icon: ICONS.multi, label: 'Change All Occurrences', hint: `${MOD}F2` },
    { id: 'refactor.format', icon: ICONS.format, label: 'Format Document', hint: IS_MAC ? '⇧⌥F' : 'Shift+Alt+F' },
    { sep: true },
    { group: 'Edit' },
    { id: 'edit.cut', icon: ICONS.cut, label: 'Cut', hint: `${MOD}X` },
    { id: 'edit.copy', icon: ICONS.copy, label: 'Copy', hint: `${MOD}C` },
    { id: 'edit.paste', icon: ICONS.paste, label: 'Paste', hint: `${MOD}V` },
    { sep: true },
    { id: 'cmd.palette', icon: ICONS.palette, label: 'Command Palette…', hint: `${MOD}Shift+P` }
  ];

  setHtml(el, '');
  for (const item of items) {
    if (item.sep) {
      const sep = document.createElement('div');
      sep.className = 'ctx-sep';
      el.appendChild(sep);
    } else if (item.group) {
      const g = document.createElement('div');
      g.className = 'ctx-group-label';
      g.setAttribute('aria-hidden', 'true');
      g.textContent = item.group;
      el.appendChild(g);
    } else {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'ctx-item';
      btn.setAttribute('role', 'menuitem');
      btn.dataset.editorCtxAction = item.id;
      btn.style.cssText = 'width:100%;text-align:left;background:transparent;border:none;';
      setHtml(btn, html`
        ${raw(item.icon)}
        <span class="ctx-label">${item.label}</span>
        ${item.hint ? html`<span class="ctx-hint">${item.hint}</span>` : ''}
      `);
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        runAction(item.id);
      });
      el.appendChild(btn);
    }
  }
  return el;
}

function showMenuAt(editor, clientX, clientY) {
  activeEditor = editor;
  const hasSelection = getSelectedText(editor).length > 0;
  const el = renderMenuItems(hasSelection);

  el.style.visibility = 'hidden';
  el.style.display = 'block';
  el.style.left = `${clientX}px`;
  el.style.top = `${clientY}px`;

  const rect = el.getBoundingClientRect();
  const pad = 10;
  const x = Math.max(pad, Math.min(clientX, window.innerWidth - rect.width - pad));
  const y = Math.max(pad, Math.min(clientY, window.innerHeight - rect.height - pad));
  el.style.left = `${x}px`;
  el.style.top = `${y}px`;
  el.style.visibility = 'visible';

  const firstItem = /** @type {HTMLElement | null} */ (el.querySelector('.ctx-item'));
  firstItem?.focus?.();
}

export function attachVoidCodeContextMenu(editor) {
  if (!editor || attachedEditors.has(editor)) return;
  attachedEditors.add(editor);

  // Suppress Monaco's built-in default gray context menu.
  try {
    editor.updateOptions?.({ contextmenu: false });
  } catch (err) {
    globalThis.logSwallowed?.(err, 'editor-context-menu.disableDefault');
  }

  const domNode = editor.getDomNode?.();
  if (!domNode) return;

  domNode.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    e.stopPropagation();

    // Move cursor to right-click position if there is no active selection covering it.
    try {
      const target = editor.getTargetAtClientPoint?.(e.clientX, e.clientY);
      const pos = target?.position;
      const sel = editor.getSelection?.();
      if (pos && (!sel || sel.isEmpty() || !sel.containsPosition(pos))) {
        editor.setPosition(pos);
      }
    } catch (err) {
      globalThis.logSwallowed?.(err, 'editor-context-menu.targetPosition');
    }

    showMenuAt(editor, e.clientX, e.clientY);
  }, true);
}

function installGlobalDismissHandlers() {
  document.addEventListener('mousedown', (e) => {
    if (!menuEl || menuEl.style.display === 'none') return;
    if (!menuEl.contains(/** @type {Node} */ (e.target))) {
      hideEditorContextMenu();
    }
  }, true);

  window.addEventListener('blur', hideEditorContextMenu);
  window.addEventListener('resize', hideEditorContextMenu);

  document.addEventListener('keydown', (e) => {
    if (!menuEl || menuEl.style.display === 'none') return;
    if (e.key === 'Escape') {
      e.preventDefault();
      hideEditorContextMenu();
      activeEditor?.focus?.();
      return;
    }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const items = Array.from(menuEl.querySelectorAll('.ctx-item'));
      if (!items.length) return;
      const idx = items.indexOf(document.activeElement);
      const nextIdx = e.key === 'ArrowDown'
        ? (idx + 1) % items.length
        : (idx - 1 + items.length) % items.length;
      /** @type {HTMLElement} */ (items[nextIdx]).focus();
    }
  });
}

export function init() {
  installGlobalDismissHandlers();

  const tryAttachPrimary = () => {
    const ed = globalThis.getMonacoEditor?.();
    if (ed) {
      attachVoidCodeContextMenu(ed);
      return true;
    }
    return false;
  };

  if (!tryAttachPrimary()) {
    const timer = setInterval(() => {
      if (tryAttachPrimary()) clearInterval(timer);
    }, 300);
    setTimeout(() => clearInterval(timer), 20000);
  }

  Object.assign(globalThis, {
    attachVoidCodeContextMenu,
    hideEditorContextMenu
  });

  return {
    attachVoidCodeContextMenu,
    hideEditorContextMenu
  };
}
