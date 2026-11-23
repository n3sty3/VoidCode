import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';
const FILES = [
  { name: 'Program.cs', path: 'C:\\proj\\Program.cs', relativePath: 'Program.cs' },
  { name: 'README.md', path: 'C:\\proj\\README.md', relativePath: 'README.md' }
];

// The command registry, the keybinding dispatch and the palette/quick-open UI
// Moved to modules/command-palette.js - the module is now the only global shortcut dispatcher.
test('command palette: registry dispatch, rebinding, filtering and quick open', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  // A user rebinding, loaded from storage at module init: the palette moves off Ctrl+Shift+P
  // (addInitScript also runs in sandboxed preview iframes, which have no localStorage)
  await page.addInitScript(() => {
    try {
      localStorage.setItem('voidcode_keybindings_v1', JSON.stringify({ 'navigation.commandPalette': 'Ctrl+Alt+P' }));
    } catch { /* sandboxed frame */ }
  });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);

  const palette = page.locator('#quick-open-modal');
  const input = page.locator('#quick-open-input');

  // F1 always opens the palette, in '>' mode, with the whole registry listed
  await expect(palette).toBeHidden();
  await page.keyboard.press('F1');
  await expect(palette).toBeVisible();
  await expect(input).toHaveValue('>');
  const commandCount = await page.locator('#quick-open-list .quick-open-item').count();
  expect(commandCount).toBeGreaterThanOrEqual(60);
  await expect(page.locator('#quick-open-list')).toContainText('Command Palette');

  // Escape closes it (the module's keydown handler owns this now)
  await page.keyboard.press('Escape');
  await expect(palette).toBeHidden();

  // The rebinding is honoured by the capture-phase dispatcher, and the default is gone
  await page.keyboard.press('Control+Shift+P');
  await page.waitForTimeout(300);
  await expect(palette).toBeHidden();
  await page.keyboard.press('Control+Alt+P');
  await expect(palette).toBeVisible();
  await expect(input).toHaveValue('>');

  // Filtering, then Enter runs the selected command
  await page.keyboard.type('open settings');
  await expect(page.locator('#quick-open-list .quick-open-item').first()).toContainText('Preferences: Open Settings');
  await page.keyboard.press('Enter');
  await expect(page.locator('#settings-modal')).toBeVisible();
  await expect(palette).toBeHidden();
  await page.locator('#btn-close-settings').click();
  await page.waitForTimeout(300);

  // The keybindings table is the moved renderer, and it lists the whole registry
  await page.keyboard.press('Control+Alt+P');
  await page.keyboard.type('keyboard');
  await expect(page.locator('#quick-open-list .quick-open-item').first()).toContainText('Keyboard Shortcuts');
  await page.keyboard.press('Enter');
  await expect(page.locator('#settings-modal')).toBeVisible();
  await page.waitForTimeout(400);
  expect(await page.locator('#keybindings-list .keybinding-row').count()).toBeGreaterThanOrEqual(60);

  // Quick open lists the workspace index and opens the chosen file through read_file
  await page.evaluate(([files]) => window.__host('all_files_response', { files, truncated: false }), [FILES]);
  await page.locator('#btn-close-settings').click();
  await page.waitForTimeout(300);
  await page.keyboard.press('Control+Alt+P');
  await expect(palette).toBeVisible();
  await page.fill('#quick-open-input', '');
  await page.keyboard.type('readme');
  await expect(page.locator('#quick-open-list')).toContainText('README.md');
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(400);
  expect(await page.evaluate(() => window.__sent.map((m) => m.action))).toContain('read_file');

  expect(errors).toEqual([]);
});