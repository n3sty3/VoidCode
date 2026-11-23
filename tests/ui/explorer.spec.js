import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

const TREE = {
  name: 'proj',
  path: 'C:\\proj',
  isDirectory: true,
  children: [
    { name: 'Program.cs', path: 'C:\\proj\\Program.cs', isDirectory: false, size: 120 },
    { name: 'src', path: 'C:\\proj\\src', isDirectory: true, hasChildren: true }
  ]
};

// The explorer is the first feature It owns the tree,
// Its state (selection, expanded paths, per-directory views) and the tree_refreshed /
// Directory_children_response / all_files_response host actions, and app.js wires it with a single
// SetupExplorer() call.
test('explorer: render, lazy expansion, context menu, new file and F2 rename', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);
  await page.waitForTimeout(600);

  // The tree renders both rows, with the file glyph the explorer owns now
  await expect(page.locator('#file-tree-container .tree-node')).toHaveCount(2);
  await expect(page.locator('#file-tree-container')).toContainText('Program.cs');
  expect(await page.locator('#file-tree-container .file-glyph').count()).toBeGreaterThan(0);

  const fileRow = page.locator('#file-tree-container .tree-node', { hasText: 'Program.cs' }).first();
  const folderRow = page.locator('#file-tree-container .tree-node', { hasText: 'src' }).first();

  // Clicking a file asks the host to read it
  await page.evaluate(() => { window.__sent.length = 0; });
  await fileRow.click();
  const read = await page.evaluate(() => window.__sent.find((m) => m.action === 'read_file'));
  expect(read).toBeTruthy();
  expect(read.path).toBe('C:\\proj\\Program.cs');

  // Clicking a folder expands it lazily and asks for one level
  await page.evaluate(() => { window.__sent.length = 0; });
  await folderRow.click();
  const request = await page.evaluate(() => window.__sent.find((m) => m.action === 'get_directory_children'));
  expect(request).toMatchObject({ path: 'C:\\proj\\src', offset: 0 });
  await expect(folderRow).toHaveAttribute('aria-expanded', 'true');
  await expect(folderRow).toHaveAttribute('aria-busy', 'true');
  await expect(page.locator('#file-tree-container')).toContainText('Loading folder');

  // The response renders into the expanded node
  await page.evaluate(() => window.__host('directory_children_response', {
    path: 'C:\\proj\\src',
    children: [{ name: 'app.js', path: 'C:\\proj\\src\\app.js', isDirectory: false }],
    offset: 0,
    hasMore: false,
    total: 1
  }));
  await page.waitForTimeout(300);
  await expect(page.locator('#file-tree-container')).toContainText('app.js');
  await expect(folderRow).not.toHaveAttribute('aria-busy', 'true');

  // All_files_response fills the index: the store slice and the legacy global agree
  await page.evaluate(() => window.__host('all_files_response', {
    files: [{ path: 'C:\\proj\\Program.cs', relativePath: 'Program.cs' }],
    truncated: true
  }));
  await page.waitForTimeout(300);
  const index = await page.evaluate(() => ({
    slice: window.VoidState.get('workspaceFiles').length,
    legacyGlobal: window.allWorkspaceFiles.length,
    truncated: window.allWorkspaceFilesTruncated
  }));
  expect(index.slice).toBe(1);
  expect(index.legacyGlobal).toBe(1);
  expect(index.truncated).toBe(true);

  // Right-click opens the menu, and an action sends the host message for the clicked node
  await fileRow.click({ button: 'right' });
  await expect(page.locator('#tree-context-menu')).toBeVisible();
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click('#ctx-inspect-file');
  const inspect = await page.evaluate(() => window.__sent.find((m) => m.action === 'inspect_file'));
  expect(inspect).toBeTruthy();
  expect(inspect.path).toBe('C:\\proj\\Program.cs');
  await expect(page.locator('#tree-context-menu')).toBeHidden();

  // Shift+F10 on a row opens the same menu, Escape closes it and restores focus
  await folderRow.focus();
  await page.keyboard.press('Shift+F10');
  await expect(page.locator('#tree-context-menu')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.locator('#tree-context-menu')).toBeHidden();

  // The + button creates at the workspace root
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click('#btn-new-file');
  await expect(page.locator('#input-dialog-modal')).toBeVisible();
  await page.fill('#input-dialog-field', 'new-file.cs');
  await page.click('#input-dialog-confirm');
  await page.waitForTimeout(300);
  const created = await page.evaluate(() => window.__sent.find((m) => m.action === 'create_entry'));
  expect(created).toMatchObject({ path: 'C:\\proj\\new-file.cs', isDir: false });

  // F2 renames the selected row - app.js delegates to the module that owns the selection
  await fileRow.click();
  await fileRow.focus();
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.keyboard.press('F2');
  await expect(page.locator('#input-dialog-modal')).toBeVisible();
  await page.fill('#input-dialog-field', 'renamed.cs');
  await page.click('#input-dialog-confirm');
  await page.waitForTimeout(300);
  const renamed = await page.evaluate(() => window.__sent.find((m) => m.action === 'rename_entry'));
  expect(renamed).toBeTruthy();
  expect(renamed).toMatchObject({ path: 'C:\\proj\\Program.cs', newName: 'renamed.cs' });

  expect(errors).toEqual([]);
});