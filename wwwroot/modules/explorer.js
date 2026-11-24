// Explorer: the file tree (render, lazy directory expansion, selection, context menu, keyboard).
//
// The first feature The tree code, the state it owns
// (expanded paths, per-directory views, the selection and the context-menu target) and the three
// Host actions it answers (tree_refreshed, directory_children_response, all_files_response) live
// Here now; app.js keeps the editor, tabs and sessions until their own steps.
import { html, raw, setHtml } from '../core/markdown.js';
import { hostEvents } from '../core/host-events.js';

let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

// App.js still owns normalizePath (a function declaration, so it is on globalThis); the fallback
// Keeps this module usable without it.
function pathKey(p) {
  return globalThis.normalizePath
    ? globalThis.normalizePath(p)
    : String(p || '').replace(/\//g, '\\').toLowerCase();
}

// The workspace file index belongs to the explorer, but quick open, the agent context and smart run
// Still read it as `allWorkspaceFiles`, so it lives in the store and core/state.js publishes it
// Under that name.
function setWorkspaceFiles(files, truncated) {
  window.VoidState.set('workspaceFiles', Array.isArray(files) ? files : []);
  if (typeof truncated === 'boolean') window.VoidState.set('workspaceFilesTruncated', truncated);
}

function getWorkspaceFiles() {
  return window.VoidState.get('workspaceFiles') || [];
}

function activeTreeTab() {
  return window.VoidState.get('activeTab');
}

// Only one directory level is returned initially; deeper levels are loaded on demand so large
// Repositories do not block startup.
const expandedTreePaths = new Set();
const treeDirectoryViews = new Map();
let renderedTreeWorkspace = null;
let selectedTreeNode = null;
let ctxTargetNode = null;

const FILE_ICON_PATHS = {
  code: '<path d="m9.5 8-3.5 4 3.5 4"/><path d="m14.5 8 3.5 4-3.5 4"/>',
  data: '<path d="M10 5.5H9a2 2 0 0 0-2 2v2.2a2 2 0 0 1-2 1.8 2 2 0 0 1 2 1.8V18a2 2 0 0 0 2 2h1"/><path d="M14 5.5h1a2 2 0 0 1 2 2v2.2a2 2 0 0 0 2 1.8 2 2 0 0 0-2 1.8V18a2 2 0 0 1-2 2h-1"/>',
  style: '<path d="M9.5 6.5 8 17.5"/><path d="M16 6.5 14.5 17.5"/><path d="M6.5 10h11"/><path d="M6 14h11"/>',
  doc: '<path d="M7 4.5h6.5L18 9v10.5H7z"/><path d="M9.5 12.5h6"/><path d="M9.5 16h4"/>',
  shell: '<path d="m8 9.5 3 2.5-3 2.5"/><path d="M13 15h4"/>',
  image: '<rect x="6.5" y="5" width="11" height="14" rx="1.5"/><circle cx="10.5" cy="9.5" r="1.2"/><path d="m7.5 16 3.5-3.5 2 2 2-1.5 2.5 3"/>',
  plain: '<path d="M7 4.5h6.5L18 9v10.5H7z"/>'
};

// The tree's file glyphs. They used to live in app.js; the tree is their only consumer.
function fileTypeIcon(kind) {
  const paths = FILE_ICON_PATHS[kind] || FILE_ICON_PATHS.plain;
  return `<svg class="file-glyph file-glyph--${kind}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true">${paths}</svg>`;
}

// One typed lookup instead of a cast per call site - the controls this module reads
// By id are inputs, and tsc only knows getElementById returns a plain HTMLElement.
/**
 * @param {string} id
 * @returns {HTMLInputElement | null}
 */
function control(id) {
  return /** @type {HTMLInputElement | null} */ (document.getElementById(id));
}

export function renderFileTree(node) {
  const container = control('file-tree-container');
  setHtml(container, '');
  container.setAttribute('role', 'tree');
  container.setAttribute('aria-label', 'Workspace files');
  treeDirectoryViews.clear();

  if (node?.path && pathKey(renderedTreeWorkspace) !== pathKey(node.path)) {
    expandedTreePaths.clear();
    renderedTreeWorkspace = node.path;
  }

  container.oncontextmenu = (event) => {
    const workspace = window.VoidState.get('workspace');
    if (event.target === container && workspace) {
      showTreeContextMenu(event, { isDirectory: true, path: workspace, name: 'Workspace Root' });
    }
  };

  if (!node || !node.children || node.children.length === 0) {
    setHtml(container, '<div class="empty-state"><p>Folder is empty.</p><p class="empty-hint">Create a file with the + button above.</p></div>');
    return;
  }

  node.children.forEach(child => container.appendChild(buildFileTreeNode(child, 0)));
  const activeTab = activeTreeTab();
  if (activeTab) highlightActiveTreeNode(activeTab.path);
}

function buildFileTreeNode(node, depth = 0) {
  const row = document.createElement('div');
  row.className = 'tree-node';
  row.tabIndex = 0;
  row.dataset.path = node.path;
  row.style.paddingLeft = `${10 + depth * 14}px`;
  row.setAttribute('role', 'treeitem');
  row.setAttribute('aria-level', String(depth + 1));

  if (!node.isDirectory) {
    const info = globalThis.getLanguageInfo?.(node.name) || { kind: 'file' };
    setHtml(row, html`
      <span class="tree-glyph-slot">${raw(fileTypeIcon(info.kind))}</span>
      <span class="tree-label">${node.name}</span>
    `);
    row.addEventListener('click', () => {
      selectedTreeNode = node;
      row.focus();
      if (/\.(db|sqlite|sqlite3)$/i.test(node.name || node.path || '') && typeof globalThis.openDbExplorerModal === 'function') {
        globalThis.openDbExplorerModal(node.path);
        return;
      }
      sendHost('read_file', { path: node.path });
    });
    row.addEventListener('contextmenu', (event) => {
      selectedTreeNode = node;
      row.focus();
      showTreeContextMenu(event, node);
    });
    return row;
  }

  const key = pathKey(node.path);
  const hasChildren = node.hasChildren !== false;
  node.hasChildren = hasChildren;
  const isExpanded = hasChildren && expandedTreePaths.has(key);
  row.setAttribute('aria-expanded', isExpanded ? 'true' : 'false');
  setHtml(row, html`
    <span class="tree-glyph-slot">
      <svg class="tree-chevron ${isExpanded ? 'expanded' : ''} ${hasChildren ? '' : 'tree-chevron--empty'}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
        <path d="m9 18 6-6-6-6"/>
      </svg>
    </span>
    <span class="tree-label">${node.name}</span>
  `);

  const childWrapper = document.createElement('div');
  childWrapper.className = 'tree-children';
  childWrapper.setAttribute('role', 'group');
  childWrapper.style.display = isExpanded ? 'block' : 'none';

  const wrapper = document.createElement('div');
  wrapper.className = 'tree-branch';
  wrapper.append(row, childWrapper);

  const view = {
    node,
    row,
    childWrapper,
    depth,
    loaded: Array.isArray(node.children),
    loading: false,
    renderedCount: Array.isArray(node.children) ? node.children.length : 0
  };
  treeDirectoryViews.set(key, view);

  if (Array.isArray(node.children)) {
    node.children.forEach(child => childWrapper.appendChild(buildFileTreeNode(child, depth + 1)));
  }

  row.addEventListener('click', () => {
    selectedTreeNode = node;
    if (!node.hasChildren) return;
    const expand = !expandedTreePaths.has(key);
    if (expand) expandedTreePaths.add(key);
    else expandedTreePaths.delete(key);
    row.setAttribute('aria-expanded', expand ? 'true' : 'false');
    row.querySelector('.tree-chevron')?.classList.toggle('expanded', expand);
    childWrapper.style.display = expand ? 'block' : 'none';
    if (expand) requestDirectoryChildren(view);
  });

  row.addEventListener('contextmenu', (event) => {
    selectedTreeNode = node;
    showTreeContextMenu(event, node);
  });

  if (isExpanded) requestDirectoryChildren(view);
  return wrapper;
}

function requestDirectoryChildren(view, offset = 0) {
  if (!view || view.loading || !view.node.hasChildren) return;
  if (offset === 0 && view.loaded) return;
  view.loading = true;
  view.row.setAttribute('aria-busy', 'true');
  view.childWrapper.querySelector('.tree-load-more')?.remove();
  if (offset === 0) view.childWrapper.replaceChildren();
  const loading = document.createElement('div');
  loading.className = 'tree-loading';
  loading.style.paddingLeft = `${24 + (view.depth + 1) * 14}px`;
  loading.textContent = 'Loading folder…';
  view.childWrapper.appendChild(loading);
  sendHost('get_directory_children', { path: view.node.path, offset });
}

export function renderDirectoryChildren(path, children, error = null, offset = 0, hasMore = false, total = 0) {
  const view = treeDirectoryViews.get(pathKey(path));
  if (!view) return;
  view.loading = false;
  view.loaded = true;
  view.row.removeAttribute('aria-busy');
  view.childWrapper.querySelector('.tree-loading')?.remove();
  view.childWrapper.querySelector('.tree-load-more')?.remove();
  if (offset === 0) {
    view.childWrapper.replaceChildren();
    view.node.children = [];
    view.renderedCount = 0;
  }

  if (error) {
    const message = document.createElement('div');
    message.className = 'tree-loading tree-loading--error';
    message.textContent = 'Folder could not be loaded.';
    view.childWrapper.appendChild(message);
    return;
  }

  view.node.children = [...(view.node.children || []), ...children];
  view.node.hasChildren = total > 0 || children.length > 0;
  if (offset === 0 && children.length === 0) {
    expandedTreePaths.delete(pathKey(path));
    view.row.setAttribute('aria-expanded', 'false');
    const chevron = view.row.querySelector('.tree-chevron');
    chevron?.classList.remove('expanded');
    chevron?.classList.add('tree-chevron--empty');
    view.childWrapper.style.display = 'none';
    return;
  }

  children.forEach(child => view.childWrapper.appendChild(buildFileTreeNode(child, view.depth + 1)));
  view.renderedCount += children.length;
  if (hasMore) {
    const loadMore = document.createElement('button');
    loadMore.type = 'button';
    loadMore.className = 'tree-load-more';
    const remaining = Math.max(0, total - view.renderedCount);
    loadMore.textContent = `Load more (${remaining.toLocaleString()})`;
    loadMore.addEventListener('click', () => requestDirectoryChildren(view, view.renderedCount));
    view.childWrapper.appendChild(loadMore);
  }
  const activeTab = activeTreeTab();
  if (activeTab) highlightActiveTreeNode(activeTab.path);
}

export function highlightActiveTreeNode(path) {
  document.querySelectorAll('.tree-node').forEach(el => {
    el.classList.toggle('active', pathKey(el.dataset.path) === pathKey(path));
  });
}

// Context Menu
function showTreeContextMenu(e, node) {
  e.preventDefault();
  e.stopPropagation();
  ctxTargetNode = node;
  const menu = control('tree-context-menu');
  menu.style.display = 'block';
  menu.style.left = `${Math.min(e.clientX, window.innerWidth - 210)}px`;
  menu.style.top = `${Math.min(e.clientY, window.innerHeight - 250)}px`;
}

// Shift+F10 / the ContextMenu key on a tree row opens the same menu as a right-click, and the menu
// Focuses its first item on open with Escape closing it.
function installTreeKeyboard() {
  control('file-tree-container')?.addEventListener('keydown', (e) => {
    if (e.key === 'ContextMenu' || (e.shiftKey && e.key === 'F10')) {
      const row = /** @type {HTMLInputElement} */ (e.target).closest?.('.tree-node');
      if (!row) return;
      e.preventDefault();
      const rect = row.getBoundingClientRect();
      row.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: rect.left + 24, clientY: rect.bottom }));
    }
  });

  const menu = control('tree-context-menu');
  if (!menu) return;
  let returnFocusTo = null;
  new MutationObserver(() => {
    if (menu.style.display === 'block') {
      returnFocusTo = document.activeElement;
      menu.querySelector('.ctx-item')?.focus();
    }
  }).observe(menu, { attributes: true, attributeFilter: ['style'] });
  menu.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      e.stopPropagation();
      menu.style.display = 'none';
      returnFocusTo?.focus?.();
    }
  });
}

// F2 while the explorer has focus. app.js's global keydown keeps its ordering and delegates here,
// Because the selection belongs to the tree.
export function renameSelectedTreeNode() {
  const target = selectedTreeNode;
  if (!target || typeof window.showInputDialog !== 'function') return;
  window.showInputDialog('Rename', target.name, 'Rename').then((newName) => {
    if (newName && newName !== target.name) {
      sendHost('rename_entry', { path: target.path, newName });
    }
  });
}

export function setupExplorer(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  // A click anywhere closes the tree context menu (it used to be an app.js module-level listener).
  window.addEventListener('click', () => {
    const menu = control('tree-context-menu');
    if (menu) menu.style.display = 'none';
  });

  control('btn-refresh-tree')?.addEventListener('click', () => sendHost('refresh_workspace'));

  control('btn-new-file')?.addEventListener('click', async () => {
    const workspace = window.VoidState.get('workspace');
    if (!workspace) return;
    const name = await window.showInputDialog('New file name', '');
    if (name) sendHost('create_entry', { path: `${workspace}\\${name}`, isDir: false });
  });

  control('btn-new-folder')?.addEventListener('click', async () => {
    const workspace = window.VoidState.get('workspace');
    if (!workspace) return;
    const name = await window.showInputDialog('New folder name', '');
    if (name) sendHost('create_entry', { path: `${workspace}\\${name}`, isDir: true });
  });

  // Context Menu Actions
  control('ctx-new-file')?.addEventListener('click', async () => {
    if (!ctxTargetNode) return;
    const baseDir = ctxTargetNode.isDirectory ? ctxTargetNode.path : ctxTargetNode.path.replace(/[\\/][^\\/]+$/, '');
    const name = await window.showInputDialog('New file name', '');
    if (name) sendHost('create_entry', { path: `${baseDir}\\${name}`, isDir: false });
  });

  control('ctx-new-folder')?.addEventListener('click', async () => {
    if (!ctxTargetNode) return;
    const baseDir = ctxTargetNode.isDirectory ? ctxTargetNode.path : ctxTargetNode.path.replace(/[\\/][^\\/]+$/, '');
    const name = await window.showInputDialog('New folder name', '');
    if (name) sendHost('create_entry', { path: `${baseDir}\\${name}`, isDir: true });
  });

  control('ctx-send-agent')?.addEventListener('click', () => {
    if (ctxTargetNode) {
      window.attachFileToPrompt?.(ctxTargetNode.path);
      window.toggleWorkspacePane?.('agent', true);
    }
  });

  control('ctx-inspect-file')?.addEventListener('click', () => {
    if (ctxTargetNode && !ctxTargetNode.isDirectory) {
      sendHost('inspect_file', { path: ctxTargetNode.path });
    }
  });

  control('ctx-reveal-explorer')?.addEventListener('click', () => {
    if (ctxTargetNode) {
      sendHost('reveal_in_explorer', { path: ctxTargetNode.path });
    }
  });

  control('ctx-copy-path')?.addEventListener('click', () => {
    if (ctxTargetNode && navigator.clipboard) {
      navigator.clipboard.writeText(ctxTargetNode.path);
      window.showToast?.('Copied full path', 'info');
    }
  });

  control('ctx-copy-rel-path')?.addEventListener('click', () => {
    if (ctxTargetNode && navigator.clipboard) {
      let rel = ctxTargetNode.path;
      const workspace = window.VoidState.get('workspace');
      if (workspace && rel.startsWith(workspace)) {
        rel = rel.substring(workspace.length).replace(/^[\\/]/, '');
      }
      navigator.clipboard.writeText(rel);
      window.showToast?.('Copied relative path', 'info');
    }
  });

  control('ctx-rename')?.addEventListener('click', async () => {
    if (!ctxTargetNode) return;
    const newName = await window.showInputDialog('Rename', ctxTargetNode.name, 'Rename');
    if (newName && newName !== ctxTargetNode.name) {
      sendHost('rename_entry', { path: ctxTargetNode.path, newName });
    }
  });

  control('ctx-delete')?.addEventListener('click', async () => {
    if (!ctxTargetNode) return;
    const node = ctxTargetNode;
    const isDir = node.isDirectory;
    const canCheckpoint = !isDir && Number(node.size || 0) <= 4 * 1024 * 1024;
    const ok = await window.showConfirmDialog(
      `Delete ${isDir ? 'folder' : 'file'}`,
      `"${node.name}" will be deleted${isDir ? ' with everything inside it' : ''}.${canCheckpoint ? ' A checkpoint is kept so you can restore it.' : ' This deletion cannot be restored by VoidCode.'}`,
      'Delete'
    );
    if (ok === true) {
      sendHost('delete_entry', { path: node.path });
    }
  });

  installTreeKeyboard();
}

// Main.js calls this once. The explorer answers its own host actions now; the
// Rest still come from app.js's handler factory.
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;

  hostEvents.on('tree_refreshed', (payload) => {
    if (payload.tree) renderFileTree(payload.tree);
  });
  hostEvents.on('directory_children_response', (payload) => {
    renderDirectoryChildren(
      payload.path,
      payload.children || [],
      payload.error,
      payload.offset || 0,
      !!payload.hasMore,
      payload.total || 0
    );
  });
  hostEvents.on('all_files_response', (payload) => {
    setWorkspaceFiles(payload.files || [], !!payload.truncated);
    globalThis.renderQuickOpenList?.(control('quick-open-input')?.value ?? '');
  });

  Object.assign(globalThis, {
    renderFileTree,
    renderDirectoryChildren,
    highlightActiveTreeNode,
    setupExplorer,
    renameSelectedTreeNode,
    getWorkspaceFileIndex: getWorkspaceFiles
  });

  return {
    renderFileTree,
    renderDirectoryChildren,
    highlightActiveTreeNode,
    setupExplorer,
    renameSelectedTreeNode,
    getWorkspaceFileIndex: getWorkspaceFiles
  };
}