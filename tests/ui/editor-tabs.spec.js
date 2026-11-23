import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE, CODE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';
const CS_PATH = 'C:\\proj\\Program.cs';
const MD_PATH = 'C:\\proj\\README.md';

// The tab lifecycle moved to modules/editor-tabs.js - opening a file into a
// Tab, activating it, dirty tracking, the tab bar, closing (with the unsaved-changes prompt) and the
// Read_file_response / save_file_response handlers. app.js calls its entry points by name.
test('editor tabs: open, activate, dirty, save, close and empty state', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);

  // One read_file_response opens one tab and makes it active
  await page.evaluate(([path, content]) => window.__host('read_file_response',
    { path, content, metadata: { encoding: 'utf-8', eol: 'LF' } }), [CS_PATH, CODE]);
  await page.waitForTimeout(600);
  await expect(page.locator('#tabs-list .tab-item')).toHaveCount(1);
  await expect(page.locator('#tabs-list .tab-item')).toContainText('Program.cs');
  await expect(page.locator('#tabs-list .tab-item')).toHaveClass(/active/);
  expect(await page.evaluate(() => window.VoidState.get('activeTab').path)).toBe(CS_PATH);
  await expect(page.locator('#monaco-mount')).toBeVisible();

  // Opening the same file again re-uses the tab instead of duplicating it
  await page.evaluate(([path, content]) => window.__host('read_file_response',
    { path, content }), [CS_PATH, CODE]);
  await page.waitForTimeout(400);
  await expect(page.locator('#tabs-list .tab-item')).toHaveCount(1);

  // A second file gets its own tab, which becomes active
  await page.evaluate(([path]) => window.__host('read_file_response',
    { path, content: '# readme\n' }), [MD_PATH]);
  await page.waitForTimeout(600);
  await expect(page.locator('#tabs-list .tab-item')).toHaveCount(2);
  expect(await page.evaluate(() => window.VoidState.get('activeTab').path)).toBe(MD_PATH);
  await expect(page.locator('#tabs-list .tab-item.active')).toContainText('README.md');

  // Clicking the first tab activates it again
  await page.locator('#tabs-list .tab-item', { hasText: 'Program.cs' }).click();
  await page.waitForTimeout(400);
  expect(await page.evaluate(() => window.VoidState.get('activeTab').path)).toBe(CS_PATH);
  await expect(page.locator('#tabs-list .tab-item.active')).toContainText('Program.cs');

  // Editing the model marks the tab dirty (bindTabModel) and shows the dot
  await page.evaluate(() => window.VoidState.get('activeTab').model.setValue('// changed\n'));
  await page.waitForTimeout(400);
  expect(await page.evaluate(() => window.VoidState.get('activeTab').dirty)).toBe(true);
  await expect(page.locator('#tabs-list .tab-item.active .tab-dirty-dot')).toHaveCount(1);

  // Save_file_response clears the dirty flag and refreshes the tab bar
  await page.evaluate(([path]) => window.__host('save_file_response', { path }), [CS_PATH]);
  await page.waitForTimeout(600);
  expect(await page.evaluate(() => window.VoidState.get('activeTab').dirty)).toBe(false);
  await expect(page.locator('#tabs-list .tab-dirty-dot')).toHaveCount(0);

  // Closing a dirty tab asks first; "Discard" closes it without sending a save
  await page.evaluate(() => window.VoidState.get('activeTab').model.setValue('// dirty again\n'));
  await page.waitForTimeout(300);
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.locator('#tabs-list .tab-item.active .tab-close-btn').click();
  await expect(page.locator('#input-dialog-modal')).toBeVisible();
  await expect(page.locator('#input-dialog-title')).toContainText('Unsaved changes');
  await page.click('#input-dialog-alt');
  await page.waitForTimeout(500);
  await expect(page.locator('#tabs-list .tab-item')).toHaveCount(1);
  expect(await page.evaluate(() => window.VoidState.get('activeTab').path)).toBe(MD_PATH);
  expect(await page.evaluate(() => window.__sent.map((m) => m.action))).not.toContain('save_file');

  // Closing the last tab returns the editor to its empty state
  await page.locator('#tabs-list .tab-item.active .tab-close-btn').click();
  await page.waitForTimeout(500);
  await expect(page.locator('#tabs-list .tab-item')).toHaveCount(0);
  expect(await page.evaluate(() => window.VoidState.get('activeTab'))).toBe(null);
  await expect(page.locator('#no-file-state')).toBeVisible();
  await expect(page.locator('#monaco-mount')).toBeHidden();
  // The language slot is reset (closeTab writes the label, then clears the slot underneath it)
  await expect(page.locator('#status-lang')).not.toContainText('C#');

  expect(errors).toEqual([]);
});