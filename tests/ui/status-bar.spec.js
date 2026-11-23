import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE, CODE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';
const CS_PATH = 'C:\\proj\\Program.cs';

// The status bar's slot painters (setStatusSlot/setActivityStatus), the
// Language-server slot (updateLspStatus with its lastLspStatus/notifiedLsp state) and the boot-time
// Slot clearing moved to modules/status-bar.js; modules/editor-tabs.js imports the painters now.
test('status bar: slots speak only when true, LSP states, activity line', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);

  // With no tab open the editor slots stay silent (hidden), not "Unknown"
  await expect(page.locator('#status-eol')).toBeHidden();
  await expect(page.locator('#status-encoding')).toBeHidden();

  // Opening a file fills them
  await page.evaluate(([path, content]) => window.__host('read_file_response',
    { path, content, metadata: { encoding: 'utf-8', eol: 'LF' } }), [CS_PATH, CODE]);
  await page.waitForTimeout(600);
  await expect(page.locator('#status-lang')).toContainText('C#');
  await expect(page.locator('#status-eol')).toHaveText('EOL: LF');
  await expect(page.locator('#status-encoding')).toHaveText('Encoding: utf-8');
  await expect(page.locator('#status-cursor')).toHaveText('Ln 1, Col 1');

  // The LSP slot reports a healthy server, and clicking it explains the detail
  await page.evaluate(() => window.__host('lsp_status',
    { language: 'C#', status: 'ready', detail: 'OmniSharp is running.' }));
  await page.waitForTimeout(300);
  const lsp = page.locator('#status-lsp');
  await expect(lsp).toContainText('C#: Ready');
  await expect(lsp).toHaveClass(/is-ready/);
  await page.evaluate(() => { document.getElementById('toast-container').textContent = ''; });
  await lsp.click();
  await expect(page.locator('#toast-container .toast-item')).toContainText('OmniSharp is running.');

  // A missing server is attention-worthy: glyph + class + exactly one notification per language
  await page.evaluate(() => window.__host('lsp_status',
    { language: 'Rust', status: 'missing', detail: 'rust-analyzer was not found.' }));
  await page.waitForTimeout(300);
  await expect(lsp).toContainText('Rust: Missing');
  await expect(lsp).toHaveClass(/is-missing/);
  await expect(lsp).toHaveClass(/is-attention/);
  await expect(lsp.locator('.icon')).toHaveCount(1);
  await expect(page.locator('#toast-container .toast-item.error')).toHaveCount(1);

  // The same language+status is not announced twice
  await page.evaluate(() => window.__host('lsp_status',
    { language: 'Rust', status: 'missing', detail: 'rust-analyzer was not found.' }));
  await page.waitForTimeout(300);
  await expect(page.locator('#toast-container .toast-item.error')).toHaveCount(1);

  // The activity line: setActivityStatus() targets a #status-activity slot that index.html does not
  // Have, so (verbatim from app.js) the text lands on the footer's title attribute - recorded as a
  // Found gap in PROGRESS rather than fixed inside a move.
  await page.evaluate(() => window.VoidState.get('activeTab').model.setValue('// changed\n'));
  await page.evaluate(([path]) => window.__host('save_file_response', { path }), [CS_PATH]);
  await page.waitForTimeout(400);
  await expect(page.locator('#statusbar')).toHaveAttribute('title', 'Saved Program.cs');

  // Closing the last tab silences the editor slots again
  await page.locator('#tabs-list .tab-item.active .tab-close-btn').click();
  await page.waitForTimeout(500);
  await expect(page.locator('#status-eol')).toBeHidden();
  await expect(page.locator('#status-lang')).toBeHidden();

  expect(errors).toEqual([]);
});