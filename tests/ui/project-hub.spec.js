import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';
const RECENTS = ['C:\\proj', 'C:\\other\\second'];

// The project hub (open/close/render + all of its listeners) moved to
// Modules/project-hub.js. app.js still calls it from the host handlers and the empty state.
test('project hub: recents render, select, new project, customize, close', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  await page.evaluate(([tree, recents]) => window.__host('init_state_response',
    { settings: { RecentWorkspaces: recents }, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }),
  [TREE, RECENTS]);
  await page.waitForTimeout(400);

  const hub = page.locator('#project-hub');
  await expect(hub).toBeHidden();

  // The top-bar chip opens it, and the recents come from appSettings.RecentWorkspaces
  await page.click('#btn-open-hub');
  await expect(hub).toBeVisible();
  const rows = page.locator('#hub-recents-list .recent-project-row');
  await expect(rows).toHaveCount(2);
  await expect(rows.first()).toContainText('proj');
  await expect(rows.first()).toHaveClass(/is-active/);
  await expect(rows.first()).toContainText('Active');
  await expect(rows.nth(1)).toContainText('second');
  await expect(rows.nth(1)).not.toHaveClass(/is-active/);

  // Picking another workspace sends select_workspace_path and closes the hub
  await page.evaluate(() => { window.__sent.length = 0; });
  await rows.nth(1).click();
  await expect(hub).toBeHidden();
  const select = await page.evaluate(() => window.__sent.find((m) => m.action === 'select_workspace_path'));
  expect(select).toBeTruthy();
  expect(select.path).toBe('C:\\other\\second');

  // New project: closes the hub, asks for a name, creates the folder
  await page.click('#btn-open-hub');
  await expect(hub).toBeVisible();
  await page.click('#btn-hub-new-project');
  await expect(hub).toBeHidden();
  await expect(page.locator('#input-dialog-modal')).toBeVisible();
  await page.fill('#input-dialog-field', 'demo');
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click('#input-dialog-confirm');
  await page.waitForTimeout(300);
  const created = await page.evaluate(() => window.__sent.find((m) => m.action === 'create_project_folder'));
  expect(created).toMatchObject({ name: 'demo' });

  // Customize closes the hub and opens the settings modal instead
  await page.click('#btn-open-hub');
  await page.click('#btn-hub-customize');
  await expect(hub).toBeHidden();
  await expect(page.locator('#settings-modal')).toBeVisible();
  await page.click('#btn-close-settings');
  await page.waitForTimeout(300);

  // The resume button and a click on the overlay both close it
  await page.click('#btn-open-hub');
  await page.click('#btn-hub-resume');
  await expect(hub).toBeHidden();
  await page.click('#btn-open-hub');
  await expect(hub).toBeVisible();
  await page.evaluate(() => document.getElementById('project-hub').click());
  await expect(hub).toBeHidden();

  expect(errors).toEqual([]);
});