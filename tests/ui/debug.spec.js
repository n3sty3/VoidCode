import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE, CODE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

// .NET (.cs) debugging is not implemented, so the Debug entry point must be
// Disabled with a reason, and invoking it must not start a session.
test('Debug is disabled with a reason for an unsupported runtime (.cs)', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  await page.evaluate(([t]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree: t, sessions: [], checkpoints: [] }), [TREE]);
  await page.evaluate(([c]) => window.__host('read_file_response',
    { path: 'C:\\proj\\Program.cs', content: c, metadata: { encoding: 'utf-8', eol: 'LF' } }), [CODE]);
  await page.waitForTimeout(1500);

  const dbg = page.locator('#btn-sidebar-debug-start');
  await expect(dbg).toBeDisabled();
  await expect(dbg).toHaveAttribute('title', /not available yet/i);

  await page.evaluate(() => { window.__sent.length = 0; });
  await page.evaluate(() => window.startDebuggingActiveFile());
  await page.waitForTimeout(300);
  expect(await page.evaluate(() => window.__sent.map((m) => m.action))).not.toContain('debug_start');
});

// The host's debug_unsupported event must surface the reason instead of a fake session.
test('debug_unsupported from the host shows the reason', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  await page.evaluate(() => window.__host('debug_unsupported',
    { runtime: 'dotnet', reason: 'Debugging for C# is not available yet.' }));

  await expect(page.locator('#debug-console-output')).toContainText(/not available yet/i);
});
