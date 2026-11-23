import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

// This guards the two things that
// Conversion could silently break - the imported html/setHtml helpers (escaping is no longer a
// Global fallback) and the globalThis shims that the app.js calls.
test('codebase results render escaped, navigate on click, and close', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  // OpenCodebaseSearch is the shim app.js binds to Ctrl+T
  await page.evaluate(() => window.openCodebaseSearch());
  await expect(page.locator('#codebase-modal')).toBeVisible();

  // The shape the host sends back for search_codebase
  await page.evaluate(() => window.renderCodebaseSearchResults([
    { kind: 'class', symbol: '<img src=x onerror=alert(1)>', file: 'C:\\proj\\A.cs', startLine: 12 },
    { kind: 'chunk', symbol: '', file: 'C:\\proj\\B.md', startLine: 3, detail: 'notes' }
  ]));

  const items = page.locator('#codebase-results-list .symbol-item');
  await expect(items).toHaveCount(2);
  // Html`` escaped the payload, so it is text and no element was created from it
  await expect(items.first()).toContainText('<img src=x onerror=alert(1)>');
  expect(await page.locator('#codebase-results-list img').count()).toBe(0);
  // An empty symbol falls back to the file name; the row shows file:line
  await expect(items.nth(1)).toContainText('B.md');
  await expect(items.nth(1)).toContainText('C:\\proj\\B.md:3');

  await page.evaluate(() => { window.__sent.length = 0; });
  await items.first().click();
  await page.waitForTimeout(300);

  // NavigateToFileLocation is still in app.js: it asks the host for the file
  expect(await page.evaluate(() => window.__sent.map((m) => m.action))).toContain('read_file');
  await expect(page.locator('#codebase-modal')).toBeHidden();
  expect(errors).toEqual([]);
});