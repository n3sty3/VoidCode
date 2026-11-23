import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

// The adaptive layout table. The bands are ≥1600 (both panes may be open), 1100-1599
// (one secondary pane at a time - covered by layout.spec.js at 1440px) and ≤1099, where the side panes
// Are drawers over the editor. The audit's complaint was that at 920px the explorer stayed docked at
// ~210px of the window; these tests pin that it is now a drawer and that it dismisses itself.
const URL = 'http://localhost:8765/index.html';

async function boot(page, width) {
  await page.setViewportSize({ width, height: 860 });
  // Layout lives in localStorage per origin, so a sibling spec that opened a pane would otherwise decide
  // This test's starting state.
  await page.addInitScript(() => { try { window.localStorage.clear(); } catch { /* storage blocked */ } });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);
  await page.waitForTimeout(500);
}

const position = (page, selector) =>
  page.evaluate((sel) => getComputedStyle(document.querySelector(sel)).position, selector);

test('920px: the explorer is a drawer over the editor, not a column', async ({ page }) => {
  await boot(page, 920);

  await expect(page.locator('#sidebar-explorer')).toBeVisible();
  expect(await position(page, '#sidebar-explorer')).toBe('fixed');

  // Out of the flex row: the editor sits at the same offset whether the drawer is open or closed.
  const leftWithDrawer = await page.evaluate(() =>
    Math.round(document.getElementById('editor-pane-primary').getBoundingClientRect().left));
  await page.click('#btn-toggle-pane-files'); // rail toggle closes it
  await expect(page.locator('#sidebar-explorer')).toBeHidden();
  const leftWithout = await page.evaluate(() =>
    Math.round(document.getElementById('editor-pane-primary').getBoundingClientRect().left));
  expect(leftWithDrawer).toBe(leftWithout);

  // Nothing to drag in drawer mode.
  await page.click('#btn-toggle-pane-files');
  await expect(page.locator('#resizer-left')).toBeHidden();
  await expect(page.locator('body')).toHaveClass(/is-drawer-open/);
});

test('920px: clicking the editor dismisses the drawer', async ({ page }) => {
  await boot(page, 920);
  await expect(page.locator('#sidebar-explorer')).toBeVisible();

  // Click the editor well clear of the drawer (320px wide at 920px), which is what dismisses it.
  await page.mouse.click(760, 420);

  await expect(page.locator('#sidebar-explorer')).toBeHidden();
  await expect(page.locator('body')).not.toHaveClass(/is-drawer-open/);
});

test('920px: the agent pane is a drawer as well, and opening it closes the explorer', async ({ page }) => {
  await boot(page, 920);

  await page.click('#btn-toggle-pane-agent');
  await expect(page.locator('#sidebar-agent')).toBeVisible();
  expect(await position(page, '#sidebar-agent')).toBe('fixed');
  await expect(page.locator('#sidebar-explorer')).toBeHidden();
  await expect(page.locator('#resizer-right')).toBeHidden();
});

test('1440px: the middle band keeps the pane docked', async ({ page }) => {
  await boot(page, 1440);

  await expect(page.locator('#sidebar-explorer')).toBeVisible();
  expect(await position(page, '#sidebar-explorer')).not.toBe('fixed');
  await expect(page.locator('#resizer-left')).toBeVisible();
  await expect(page.locator('body')).not.toHaveClass(/is-drawer-open/);
});

test('1600px: both side panes start open and do not exclude each other', async ({ page }) => {
  await boot(page, 1600);

  // ApplyInitialPaneLayout opens the agent pane at this width: ">= 1600: all panes".
  await expect(page.locator('#sidebar-explorer')).toBeVisible();
  await expect(page.locator('#sidebar-agent')).toBeVisible();

  // Toggling one must not close the other above the middle band.
  await page.click('#btn-toggle-pane-agent');
  await expect(page.locator('#sidebar-agent')).toBeHidden();
  await expect(page.locator('#sidebar-explorer')).toBeVisible();
  await page.click('#btn-toggle-pane-agent');
  await expect(page.locator('#sidebar-agent')).toBeVisible();
  await expect(page.locator('#sidebar-explorer')).toBeVisible();
});