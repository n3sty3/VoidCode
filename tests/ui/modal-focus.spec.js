import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

// Focus management for every dialog. Two things are asserted here that a keyboard user
// Notices immediately and a mouse user never does: Tab stays inside the open dialog, and the rest of the
// Window stops being reachable (inert) while it is open. The focus that opened the dialog comes back on
// Close, so the user is not dumped at the top of the document.
const URL = 'http://localhost:8765/index.html';
const SETTINGS = {
  Model: 'deepseek/deepseek-chat',
  Provider: 'openrouter',
  BaseUrl: 'https://openrouter.ai/api/v1',
  ApiKeyConfigured: true
};

async function boot(page) {
  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.evaluate(([tree, settings]) => window.__host('init_state_response',
    { settings, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE, SETTINGS]);
  await page.waitForTimeout(400);
}

/** Presses Tab `times` times and returns the 1-based presses after which focus left the dialog. */
async function tabEscapes(page, dialogId, times) {
  const escapes = [];
  for (let press = 1; press <= times; press++) {
    await page.keyboard.press('Tab');
    const inside = await page.evaluate((id) => {
      const dialog = document.getElementById(id);
      const active = document.activeElement;
      if (!dialog || !active) return true;
      return dialog.contains(active) || active === document.body;
    }, dialogId);
    if (!inside) escapes.push(press);
  }
  return escapes;
}

test('settings: Tab x40 never leaves the dialog', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  await boot(page);

  await page.click('#btn-settings-toggle');
  await expect(page.locator('#settings-modal')).toBeVisible();

  const escapes = await tabEscapes(page, 'win-settings', 40);
  expect(escapes, `focus left the settings dialog after Tab presses ${escapes.join(', ')}`).toEqual([]);
  expect(errors).toEqual([]);
});

test('settings: the rest of the window is inert while it is open', async ({ page }) => {
  await boot(page);
  await page.click('#btn-settings-toggle');
  await expect(page.locator('#settings-modal')).toBeVisible();

  // The rail button that opened the dialog must not be reachable while it is open.
  const reachable = await page.evaluate(() => {
    const toggle = document.getElementById('btn-settings-toggle');
    return toggle ? toggle.closest('[inert]') !== null : null;
  });
  expect(reachable).toBe(true);

  // ...and it becomes reachable again once the dialog closes.
  await page.click('#btn-close-settings');
  await expect(page.locator('#settings-modal')).toBeHidden();
  await expect.poll(() => page.evaluate(() => document.querySelectorAll('[inert]').length)).toBe(0);
});

test('settings: opening focuses the first control, closing returns focus to the opener', async ({ page }) => {
  await boot(page);
  await page.click('#btn-settings-toggle');
  await expect(page.locator('#settings-modal')).toBeVisible();

  await expect.poll(() => page.evaluate(() =>
    document.getElementById('win-settings')?.contains(document.activeElement) ?? false)).toBe(true);

  await page.click('#btn-close-settings');
  await expect(page.locator('#settings-modal')).toBeHidden();
  await expect.poll(() => page.evaluate(() => document.activeElement?.id ?? '')).toBe('btn-settings-toggle');
});

test('command palette: Tab x40 never leaves the palette', async ({ page }) => {
  await boot(page);

  await page.keyboard.press('Control+p');
  await expect(page.locator('#quick-open-modal')).toBeVisible();
  await page.keyboard.type('>');

  const escapes = await tabEscapes(page, 'quick-open-modal', 40);
  expect(escapes, `focus left the palette after Tab presses ${escapes.join(', ')}`).toEqual([]);
});