import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

// A non-responding host must show a visible error, not a silent no-op.
test('a non-responding host surfaces a timeout error', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  // The bridge reports a timeout as an error object, never null.
  const res = await page.evaluate(() => window.VoidBridge.request('nobody_home', {}, 120));
  expect(res).toMatchObject({ ok: false, error: 'timeout', action: 'nobody_home' });

  // The console panel shows the failure instead of swallowing it. made VoidShell
  // Lazy, so ask for it first - window.VoidConsole is only the facade until the scripts land.
  await page.evaluate(() => window.ensureVoidShell());
  await page.evaluate(() => window.VoidShell.core.request('nobody_home', {}, 120));
  await expect(page.locator('#vc-log')).toContainText(/no response from host/i);
});
