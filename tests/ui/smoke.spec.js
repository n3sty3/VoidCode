import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE, CODE, pinGlassMode } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

// Boot smoke: the app must come up clean at four widths and match its snapshot.
for (const [w, h] of [[920, 700], [1280, 800], [1440, 860], [1600, 900]]) {
  test(`boots clean at ${w}px`, async ({ page }) => {
    const errors = [];
    page.on('pageerror', (e) => errors.push(String(e)));
    page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
    page.on('requestfailed', (r) => errors.push('FAILED ' + r.url()));

    await page.setViewportSize({ width: w, height: h });
    await page.addInitScript({ content: MOCK_HOST });
    // The snapshot is of the glass look, so the runner's reduced-transparency default
    // Must not decide it (see pinGlassMode).
    expect(await pinGlassMode(page)).toBe(true);
    await page.goto(URL);
    await page.waitForTimeout(2500);

    expect(errors).toEqual([]);
    await expect(page).toHaveScreenshot(`boot-${w}.png`, { maxDiffPixelRatio: 0.001 });
  });
}

test('open file, edit, save, diagnostics, palette', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  await page.evaluate(([t]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree: t, sessions: [], checkpoints: [] }), [TREE]);
  await page.evaluate(([c]) => window.__host('read_file_response',
    { path: 'C:\\proj\\Program.cs', content: c, metadata: { encoding: 'utf-8', eol: 'LF' } }), [CODE]);
  await page.waitForTimeout(2000);

  expect(await page.evaluate(() => window.monaco.editor.getModels()[0].getLanguageId())).toBe('csharp');

  // Type makes the tab dirty, Ctrl+S posts save_file
  await page.click('.monaco-editor .view-lines');
  await page.keyboard.type('x');
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.keyboard.press('Control+s');
  await page.waitForTimeout(500);
  expect(await page.evaluate(() => window.__sent.map((m) => m.action))).toContain('save_file');

  // 1 error + 1 warning -> Problems badge shows 2
  await page.evaluate(() => window.__host('lsp_diagnostics', { path: 'C:\\proj\\Program.cs',
    diagnostics: [
      { startLine: 5, startCol: 7, endLine: 5, endCol: 8, severity: 1, message: 'Fake error', code: 'CS0001', source: 'test' },
      { startLine: 6, startCol: 1, severity: 2, message: 'Fake warning', source: 'test' }] }));
  await expect(page.getByRole('tab', { name: /Problems/ })).toContainText('2');

  // Command palette opens in '>' mode with the full command list
  await page.keyboard.press('Control+Shift+P');
  await expect(page.locator('#quick-open-input')).toHaveValue('>');
  expect(await page.locator('#quick-open-list > *').count()).toBeGreaterThanOrEqual(60);
});

// Landed (core/modal-focus.js), so this is a real assertion now: it was written as
// Test.fail while the old trap's `offsetParent !== null` check silently never matched a fixed overlay.
test('settings dialog traps focus', async ({ page }) => {
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.keyboard.press('Control+,');
  await expect(page.locator('#settings-modal')).toBeVisible();
  let escaped = 0;
  for (let i = 0; i < 40; i++) {
    await page.keyboard.press('Tab');
    const inside = await page.evaluate(() =>
      document.querySelector('#win-settings')?.contains(document.activeElement));
    if (inside === false) escaped++;
  }
  expect(escaped).toBe(0);
});
