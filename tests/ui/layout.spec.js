import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

// Pane visibility/persistence, the terminal collapse state, the splitters and
// The sidebar switcher moved to modules/layout.js. Everything is tested at 1440px on purpose: below
// 1600px opening one side pane must close the other, which is the rule most likely to break.
test('layout: pane toggles, mutual exclusion, persistence, sidebar views and splitters', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);
  await page.waitForTimeout(500);

  const explorer = page.locator('#sidebar-explorer');
  const agent = page.locator('#sidebar-agent');

  // At 1440px the agent pane starts closed and the explorer is open
  await expect(explorer).toBeVisible();
  await expect(agent).toBeHidden();

  // Opening the agent pane closes the explorer (the "keep a usable editor width" rule)
  await page.click('#btn-toggle-pane-agent');
  await expect(agent).toBeVisible();
  await expect(explorer).toBeHidden();
  await expect(page.locator('#btn-toggle-pane-agent')).toHaveAttribute('aria-pressed', 'true');

  // And opening the explorer again closes the agent
  await page.click('#btn-toggle-pane-files');
  await expect(explorer).toBeVisible();
  await expect(agent).toBeHidden();

  // The per-pane close button and the toggle button agree
  await page.click('#btn-close-pane-files');
  await expect(explorer).toBeHidden();
  await page.click('#btn-toggle-pane-files');
  await expect(explorer).toBeVisible();

  // The layout is persisted per workspace
  const stored = await page.evaluate(() => JSON.parse(localStorage.getItem('voidcode_panes_v2::c:\\proj') || 'null'));
  expect(stored).toBeTruthy();
  expect(stored.files).toBe(true);
  expect(stored.agent).toBe(false);

  // The terminal collapses through the toggle and reports its state on the button
  const toggle = page.locator('#btn-toggle-terminal');
  const before = await toggle.getAttribute('aria-expanded');
  await page.click('#btn-toggle-terminal');
  await expect(toggle).toHaveAttribute('aria-expanded', before === 'true' ? 'false' : 'true');

  // The sidebar switcher shows one view at a time and remembers it in the store
  await page.click('#tab-nav-git');
  await expect(page.locator('#view-git')).toBeVisible();
  await expect(page.locator('#view-files')).toBeHidden();
  expect(await page.evaluate(() => window.VoidState.get('activeSidebarView'))).toBe('git');
  await page.click('#tab-nav-files');
  await expect(page.locator('#view-files')).toBeVisible();
  await expect(page.locator('#view-git')).toBeHidden();
  expect(await page.evaluate(() => window.VoidState.get('activeSidebarView'))).toBe('files');

  // The splitter has a keyboard alternative and it resizes the pane by one step
  const widthBefore = await page.evaluate(() => document.getElementById('sidebar-explorer').getBoundingClientRect().width);
  await page.locator('#resizer-left').focus();
  await page.keyboard.press('ArrowRight');
  const widthAfter = await page.evaluate(() => document.getElementById('sidebar-explorer').getBoundingClientRect().width);
  expect(Math.round(widthAfter - widthBefore)).toBe(16);

  expect(errors).toEqual([]);
});