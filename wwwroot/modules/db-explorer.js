// Built-In SQLite Database Explorer modal UI.
// Auto-discovers .db/.sqlite/.sqlite3 files in the workspace, displays tables/columns/row-counts,
// runs SQL queries in a crisp scrollable table grid, and offers 1-click "Attach Schema to VoidAgent".
import { html, setHtml } from '../core/markdown.js';

let hostRequest = (action, data, timeoutMs) => globalThis.VoidBridge.request(action, data, timeoutMs);
let modalEl = null;
let activeDbPath = '';
let lastSchemaSummary = '';

function ensureModal() {
  if (modalEl && document.body.contains(modalEl)) return modalEl;

  const overlay = document.createElement('div');
  overlay.id = 'db-explorer-modal';
  overlay.className = 'modal-overlay';
  overlay.style.cssText = 'display:none;z-index:2600;';

  const card = document.createElement('div');
  card.className = 'glass glass--float';
  card.setAttribute('role', 'dialog');
  card.setAttribute('aria-modal', 'true');
  card.setAttribute('aria-label', 'SQLite Database Explorer');
  card.style.cssText = 'width:min(980px,95vw);max-height:88vh;display:flex;flex-direction:column;border-radius:16px;padding:18px;gap:10px;';

  setHtml(card, html`
    <div style="display:flex;align-items:center;justify-content:space-between;gap:12px;">
      <div>
        <div style="font-size:16px;font-weight:700;">SQLite Database Explorer</div>
        <div style="font-size:12px;opacity:0.75;">Inspect local .db / .sqlite schemas, run SQL queries, and attach table schemas directly to VoidAgent.</div>
      </div>
      <div style="display:flex;gap:6px;align-items:center;">
        <button type="button" class="btn btn--quiet btn--xs" id="btn-db-attach-ai" style="white-space:nowrap;flex-shrink:0;">Attach Schema to VoidAgent</button>
        <button type="button" class="icon-btn icon-btn--sm" id="btn-db-close" aria-label="Close Database Explorer">&times;</button>
      </div>
    </div>

    <div style="display:flex;gap:8px;align-items:center;">
      <select id="db-discovered-select" aria-label="Discovered databases" style="width:240px;">
        <option value="">Scanning workspace...</option>
      </select>
      <input type="text" id="db-path-input" class="text-input" placeholder="Path to .sqlite / .db file (relative or absolute)" style="flex:1;" />
      <button type="button" class="btn btn--quiet btn--sm" id="btn-db-load" style="white-space:nowrap;flex-shrink:0;">Load Schema</button>
    </div>

    <div style="display:grid;grid-template-columns:250px 1fr;gap:12px;min-height:360px;max-height:60vh;">
      <div id="db-tables-list" style="overflow-y:auto;border:1px solid var(--border-subtle,rgba(255,255,255,0.1));border-radius:10px;padding:8px;background:var(--surface-1,rgba(255,255,255,0.02));display:flex;flex-direction:column;gap:6px;">
        <div style="font-size:12px;opacity:0.65;padding:8px;">Select or enter a SQLite database file to inspect tables.</div>
      </div>

      <div style="display:flex;flex-direction:column;gap:8px;min-width:0;">
        <div style="display:flex;gap:8px;align-items:flex-start;">
          <textarea id="db-sql-input" rows="3" class="text-input" spellcheck="false" placeholder="SELECT * FROM users LIMIT 50;" style="flex:1;font-family:monospace;font-size:12px;resize:vertical;"></textarea>
          <button type="button" class="btn btn--primary btn--sm" id="btn-db-run-sql" style="white-space:nowrap;flex-shrink:0;">Run SQL</button>
        </div>
        <div id="db-query-meta" style="font-size:11px;font-family:monospace;opacity:0.8;">Ready</div>
        <div id="db-query-grid" style="flex:1;overflow:auto;border:1px solid var(--border-subtle,rgba(255,255,255,0.1));border-radius:10px;background:rgba(0,0,0,0.22);min-height:200px;"></div>
      </div>
    </div>
  `);

  overlay.appendChild(card);
  document.body.appendChild(overlay);
  modalEl = overlay;

  overlay.addEventListener('click', (e) => {
    if (e.target === overlay) closeDbExplorerModal();
  });
  document.getElementById('btn-db-close')?.addEventListener('click', closeDbExplorerModal);
  document.getElementById('btn-db-load')?.addEventListener('click', () => {
    const p = (/** @type {HTMLInputElement | null} */ (document.getElementById('db-path-input'))?.value || '').trim();
    if (p) loadSchema(p);
  });
  document.getElementById('db-discovered-select')?.addEventListener('change', (e) => {
    const val = /** @type {HTMLSelectElement} */ (e.target).value;
    if (val) {
      const inp = /** @type {HTMLInputElement | null} */ (document.getElementById('db-path-input'));
      if (inp) inp.value = val;
      loadSchema(val);
    }
  });
  document.getElementById('btn-db-run-sql')?.addEventListener('click', runQuery);
  document.getElementById('db-sql-input')?.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
      e.preventDefault();
      runQuery();
    }
  });
  document.getElementById('btn-db-attach-ai')?.addEventListener('click', attachSchemaToAgent);
  globalThis.setupCustomSelects?.();

  return overlay;
}

async function discoverDatabases() {
  const sel = /** @type {HTMLSelectElement | null} */ (document.getElementById('db-discovered-select'));
  if (!sel) return;
  try {
    const res = await hostRequest('db_discover', {}, 6000);
    const dbs = res?.ok && Array.isArray(res.data) ? res.data : [];
    setHtml(sel, '');
    if (dbs.length === 0) {
      const opt = document.createElement('option');
      opt.value = '';
      opt.textContent = 'No .db/.sqlite found in workspace';
      sel.appendChild(opt);
      return;
    }
    dbs.forEach((db, idx) => {
      const opt = document.createElement('option');
      opt.value = db.path;
      const kb = (db.sizeBytes / 1024).toFixed(1);
      opt.textContent = `${db.relativePath} (${kb} KB)`;
      sel.appendChild(opt);
      if (idx === 0 && !activeDbPath) {
        const inp = /** @type {HTMLInputElement | null} */ (document.getElementById('db-path-input'));
        if (inp) inp.value = db.path;
        loadSchema(db.path);
      }
    });
    if (activeDbPath) sel.value = activeDbPath;
    globalThis.syncAllCustomSelects?.();
  } catch (err) {
    globalThis.logSwallowed?.(err, 'db-explorer.discover');
  }
}

async function loadSchema(path) {
  activeDbPath = path;
  const listEl = document.getElementById('db-tables-list');
  if (!listEl) return;
  setHtml(listEl, html`<div style="font-size:12px;opacity:0.7;padding:8px;">Loading schema...</div>`);

  try {
    const res = await hostRequest('db_schema', { path }, 8000);
    const report = res?.ok ? res.data : null;
    if (!report || !report.ok) {
      lastSchemaSummary = '';
      setHtml(listEl, html`<div style="font-size:12px;color:#ff8a8a;padding:8px;">${report?.error || res?.error || 'Could not inspect database.'}</div>`);
      return;
    }

    lastSchemaSummary = report.schemaSummary || '';
    const tables = Array.isArray(report.tables) ? report.tables : [];
    setHtml(listEl, '');

    if (tables.length === 0) {
      setHtml(listEl, html`<div style="font-size:12px;opacity:0.7;padding:8px;">No tables found in this database.</div>`);
      return;
    }

    for (const tbl of tables) {
      const item = document.createElement('div');
      item.style.cssText = 'padding:8px;border-radius:8px;border:1px solid var(--border-subtle,rgba(255,255,255,0.08));background:rgba(255,255,255,0.02);cursor:pointer;';
      const cols = (tbl.columns || []).map((c) => `${c.name}${c.primaryKey ? ' 🔑' : ''}`).join(', ');
      setHtml(item, html`
        <div style="display:flex;justify-content:space-between;align-items:center;gap:6px;">
          <strong style="font-size:12px;font-family:monospace;">${tbl.name}</strong>
          <span style="font-size:10px;opacity:0.7;">${tbl.rowCount} rows</span>
        </div>
        <div style="font-size:10px;opacity:0.65;margin-top:3px;word-break:break-word;">${cols}</div>
      `);
      item.addEventListener('click', () => {
        const sqlBox = /** @type {HTMLTextAreaElement | null} */ (document.getElementById('db-sql-input'));
        if (sqlBox) {
          const safe = `"${tbl.name.replace(/"/g, '""')}"`;
          sqlBox.value = `SELECT * FROM ${safe} LIMIT 100;`;
          runQuery();
        }
      });
      listEl.appendChild(item);
    }
  } catch (err) {
    globalThis.logSwallowed?.(err, 'db-explorer.loadSchema');
  }
}

async function runQuery() {
  const path = (/** @type {HTMLInputElement | null} */ (document.getElementById('db-path-input'))?.value || activeDbPath || '').trim();
  const sql = (/** @type {HTMLTextAreaElement | null} */ (document.getElementById('db-sql-input'))?.value || '').trim();
  const meta = document.getElementById('db-query-meta');
  const grid = document.getElementById('db-query-grid');
  if (!meta || !grid) return;

  if (!path) {
    globalThis.showToast?.('Select or enter a SQLite database path first', 'info');
    return;
  }
  if (!sql) {
    globalThis.showToast?.('Enter a SQL query to run', 'info');
    return;
  }

  meta.textContent = 'Running query...';
  try {
    const res = await hostRequest('db_query', { path, sql, maxRows: 200 }, 12000);
    const data = res?.ok ? res.data : null;
    if (!data || !data.ok) {
      meta.textContent = `Error (${data?.elapsedMs || 0}ms)`;
      setHtml(grid, html`<div style="padding:12px;color:#ff8a8a;font-family:monospace;font-size:12px;">${data?.error || res?.error || 'Query failed.'}</div>`);
      return;
    }

    const cols = Array.isArray(data.columns) ? data.columns : [];
    const rows = Array.isArray(data.rows) ? data.rows : [];
    meta.textContent = cols.length > 0
      ? `${rows.length} row${rows.length === 1 ? '' : 's'}${data.truncated ? ' (capped at 200)' : ''} • ${data.elapsedMs}ms`
      : `Done (${data.rowsAffected} rows affected) • ${data.elapsedMs}ms`;

    setHtml(grid, '');
    if (cols.length === 0) {
      setHtml(grid, html`<div style="padding:12px;font-size:12px;opacity:0.8;">Statement executed successfully (${data.rowsAffected} rows affected).</div>`);
      return;
    }

    const table = document.createElement('table');
    table.style.cssText = 'width:100%;border-collapse:collapse;font-family:monospace;font-size:11px;';
    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');
    for (const c of cols) {
      const th = document.createElement('th');
      th.style.cssText = 'text-align:left;padding:6px 8px;border-bottom:1px solid var(--border-subtle,rgba(255,255,255,0.12));background:rgba(255,255,255,0.04);position:sticky;top:0;white-space:nowrap;';
      th.textContent = c;
      headRow.appendChild(th);
    }
    thead.appendChild(headRow);
    table.appendChild(thead);

    const tbody = document.createElement('tbody');
    for (const r of rows) {
      const tr = document.createElement('tr');
      for (const cell of r) {
        const td = document.createElement('td');
        td.style.cssText = 'padding:5px 8px;border-bottom:1px solid rgba(255,255,255,0.05);max-width:280px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
        td.textContent = cell === null || cell === undefined ? 'NULL' : String(cell);
        if (cell === null || cell === undefined) td.style.opacity = '0.45';
        td.title = cell === null || cell === undefined ? 'NULL' : String(cell);
        tr.appendChild(td);
      }
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    grid.appendChild(table);
  } catch (err) {
    globalThis.logSwallowed?.(err, 'db-explorer.runQuery');
  }
}

function attachSchemaToAgent() {
  if (!lastSchemaSummary) {
    globalThis.showToast?.('Load a database schema first', 'info');
    return;
  }
  globalThis.attachContextItem?.({
    id: `db_schema_${Date.now()}`,
    type: 'selection',
    label: `DB Schema (${activeDbPath.split(/[\\/]/).pop()})`,
    content: lastSchemaSummary
  });
  closeDbExplorerModal();
  globalThis.toggleWorkspacePane?.('agent', true);
  globalThis.showToast?.('Database schema attached to VoidAgent', 'success');
}

export function openDbExplorerModal(initialPath = '') {
  const el = ensureModal();
  el.style.display = 'flex';
  const clean = typeof initialPath === 'string' ? initialPath.trim() : '';
  if (clean) {
    activeDbPath = clean;
    const inp = /** @type {HTMLInputElement | null} */ (document.getElementById('db-path-input'));
    if (inp) inp.value = clean;
    loadSchema(clean);
  }
  discoverDatabases();
}

export function closeDbExplorerModal() {
  if (modalEl) modalEl.style.display = 'none';
}

export function init(ctx = {}) {
  if (typeof ctx.requestHost === 'function') hostRequest = ctx.requestHost;
  Object.assign(globalThis, {
    openDbExplorerModal,
    closeDbExplorerModal
  });
  return {
    openDbExplorerModal,
    closeDbExplorerModal
  };
}
