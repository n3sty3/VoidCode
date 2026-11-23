import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

// Settings information architecture. The audit asked for a searchable left nav and flagged a
// Contradiction: Appearance "persists immediately while offering Cancel/Save". The second half was already
// Fixed when this slice started - app.js has `persistImmediately = !btn.closest('#settings-modal')` on the
// Theme chips and an `id === 'slider-...'` guard on both sliders, and closeSettings restores the baseline - so
// What is asserted here is that the contract holds, not that a new one was invented. The first half is new:
// The rail filters on label + data-keywords and the arrow walk skips what the filter hid.
const URL = 'http://localhost:8765/index.html';

async function boot(page) {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => { try { window.localStorage.clear(); } catch { /* storage blocked */ } });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);
  await page.waitForTimeout(500);
}

async function openSettings(page) {
  await page.click('#btn-settings-toggle');
  await expect(page.locator('#win-settings')).toBeVisible();
  await page.evaluate(async () => {
    const card = document.getElementById('win-settings');
    await Promise.all(card.getAnimations({ subtree: true }).map((animation) => animation.finished));
  });
}

const railState = (page) => page.evaluate(() => ({
  visible: Array.from(document.querySelectorAll('#settings-modal .settings-tab-btn'))
    .filter((btn) => btn.getBoundingClientRect().height > 0)
    .map((btn) => btn.id),
  status: document.getElementById('settings-search-status').textContent.trim()
}));

test('the rail filters sections by label and by keyword', async ({ page }) => {
  await boot(page);
  await openSettings(page);
  expect((await railState(page)).visible).toHaveLength(5);

  await page.fill('#settings-search', 'shortcut');
  await expect.poll(async () => (await railState(page)).visible).toEqual(['settings-tab-keybindings']);
  expect((await railState(page)).status).toBe('1 section match');

  // "terminal" appears in no label - it is a keyword on "Editor and limits", which is the whole point of
  // Matching keywords instead of labels.
  await page.fill('#settings-search', 'terminal');
  await expect.poll(async () => (await railState(page)).visible).toEqual(['settings-tab-editor']);
});

test('Enter opens the first match and Escape clears the filter without closing the dialog', async ({ page }) => {
  await boot(page);
  await openSettings(page);
  await page.fill('#settings-search', 'them');
  await page.press('#settings-search', 'Enter');

  const opened = await page.evaluate(() => ({
    selected: document.querySelector('#settings-modal .settings-tab-btn[aria-selected="true"]').id,
    pane: Array.from(document.querySelectorAll('#settings-modal .settings-tab-pane'))
      .find((pane) => pane.style.display !== 'none')?.id
  }));
  expect(opened.selected).toBe('settings-tab-appearance');
  expect(opened.pane).toBe('tab-appearance');

  await page.focus('#settings-search');
  await page.press('#settings-search', 'Escape');
  await expect.poll(async () => (await railState(page)).visible).toHaveLength(5);
  expect(await page.evaluate(() =>
    getComputedStyle(document.getElementById('settings-modal')).display)).toBe('flex');
});

test('a filter with no match says so and the strip still recovers', async ({ page }) => {
  await boot(page);
  await openSettings(page);
  await page.fill('#settings-search', 'zzzznothing');
  await expect.poll(async () => (await railState(page)).status).toBe('No sections match');
  expect((await railState(page)).visible).toEqual([]);

  await page.press('#settings-search', 'Escape');
  await expect.poll(async () => (await railState(page)).visible).toHaveLength(5);
  expect((await railState(page)).status).toBe('');
});

test('the arrow walk skips the sections the filter hid', async ({ page }) => {
  await boot(page);
  await openSettings(page);
  await page.fill('#settings-search', 'key'); // matches Model endpoint (api key) and Keyboard shortcuts
  expect((await railState(page)).visible).toEqual(['settings-tab-provider', 'settings-tab-keybindings']);

  await page.focus('#settings-tab-provider');
  await page.keyboard.press('ArrowRight');
  // Straight from "Model endpoint" to "Keyboard shortcuts": the three hidden tabs are not in the walk.
  expect(await page.evaluate(() => document.activeElement.id)).toBe('settings-tab-keybindings');
  await page.keyboard.press('ArrowRight');
  expect(await page.evaluate(() => document.activeElement.id)).toBe('settings-tab-provider');
});

test('the filter is cleared the next time the dialog opens', async ({ page }) => {
  await boot(page);
  await openSettings(page);
  await page.fill('#settings-search', 'shortcut');
  await expect.poll(async () => (await railState(page)).visible).toHaveLength(1);

  await page.click('#btn-close-settings');
  await expect(page.locator('#settings-modal')).toBeHidden();
  await openSettings(page);

  expect((await railState(page)).visible).toHaveLength(5);
  expect(await page.evaluate(() => document.getElementById('settings-search').value)).toBe('');
});

test('the search field sits in the rail, above the strip, left of the pane', async ({ page }) => {
  await boot(page);
  await openSettings(page);

  const layout = await page.evaluate(() => {
    const rect = (selector) => document.querySelector(selector).getBoundingClientRect();
    return {
      rail: rect('#win-settings .settings-rail'),
      search: rect('#settings-search'),
      strip: rect('#settings-tabs-nav'),
      body: rect('#win-settings .modal-body')
    };
  });
  // The grid area moved from the strip to its wrapper, so a regression here means the rail collapsed or the
  // Body slid under it.
  expect(Math.round(layout.search.top)).toBeGreaterThanOrEqual(Math.round(layout.rail.top));
  expect(Math.round(layout.search.bottom)).toBeLessThanOrEqual(Math.round(layout.strip.top));
  expect(Math.round(layout.rail.right)).toBeLessThanOrEqual(Math.round(layout.body.left) + 1);
  expect(Math.round(layout.search.width)).toBeGreaterThan(120);
});

test('appearance previews inside the dialog and Cancel puts the old theme back', async ({ page }) => {
  await boot(page);
  await openSettings(page);
  await page.click('#settings-tab-appearance');

  const before = await page.evaluate(() => ({
    theme: document.documentElement.getAttribute('data-theme'),
    picked: document.querySelector('#settings-modal [data-theme-pick].is-active')?.getAttribute('data-theme-pick')
  }));

  // A different chip than the one currently active, whatever that is.
  const other = await page.evaluate((current) => {
    const chip = Array.from(document.querySelectorAll('#settings-modal [data-theme-pick]'))
      .find((btn) => btn.getAttribute('data-theme-pick') !== current);
    chip.click();
    return chip.getAttribute('data-theme-pick');
  }, before.picked);
  expect(other).not.toBe(before.picked);
  expect(await page.evaluate(() => document.documentElement.getAttribute('data-theme'))).toBe(other);
  // The whole point: inside the modal a theme is a preview, so nothing is persisted yet.
  expect(await page.evaluate(() =>
    window.__sent.filter((m) => m.action === 'save_settings').length)).toBe(0);

  await page.click('#btn-cancel-settings');
  await expect(page.locator('#settings-modal')).toBeHidden();
  expect(await page.evaluate(() => document.documentElement.getAttribute('data-theme'))).toBe(before.theme);
});