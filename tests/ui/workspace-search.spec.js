import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

// This drives the real request/response pair
// Through the bridge switch and pins what the conversion touched: the imported html/setHtml
// Helpers, the globalThis shims, and handleSearchCancelled - which because the
// Module now owns activeWorkspaceSearchId.
test('workspace search renders escaped results and owns the cancel branch', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  // The sidebar starts on Files; the search input only exists in the Search view
  await page.click('#tab-nav-search');
  await page.fill('#global-search-input', 'Console');
  await page.press('#global-search-input', 'Enter');
  await page.waitForTimeout(400);

  // The recorded frame is flat: { action, ...payload, bridgeNonce }
  const req = await page.evaluate(() => window.__sent.find((m) => m.action === 'search_workspace'));
  expect(req).toBeTruthy();
  const searchId = req.searchId;
  expect(req.query).toBe('Console');
  expect(req.isRegex).toBe(false);

  await page.evaluate(([sid]) => window.__host('search_workspace_response', {
    searchId: sid,
    query: 'Console',
    totalMatches: 2,
    fileCount: 1,
    files: [{
      relativePath: 'src/Program.cs',
      fullPath: 'C:\\proj\\src\\Program.cs',
      matches: [
        { line: 6, column: 7, length: 7, preview: '<img src=x onerror=alert(1)>' },
        { line: 9, column: 1, length: 7, preview: 'Console.WriteLine(x);' }
      ]
    }]
  }), [searchId]);
  await page.waitForTimeout(300);

  await expect(page.locator('#search-status-bar')).toHaveText('Found 2 matches across 1 file');
  const header = page.locator('.search-file-header').first();
  await expect(header).toContainText('Program.cs');
  await expect(header).toContainText('2');

  const rows = page.locator('.search-match-row');
  await expect(rows).toHaveCount(2);
  // Html`` escaped the preview, so no element was created from it
  await expect(rows.first()).toContainText('<img src=x onerror=alert(1)>');
  expect(await page.locator('#search-results-tree img').count()).toBe(0);

  // The group header still collapses and re-expands
  await header.click();
  await expect(page.locator('.search-file-matches').first()).toBeHidden();
  await header.click();
  await expect(page.locator('.search-file-matches').first()).toBeVisible();

  // A stale search id is ignored by the module's own guard
  await page.evaluate(([sid]) => window.__host('search_workspace_response', {
    searchId: sid + '-stale', query: 'nope', totalMatches: 0, fileCount: 0, files: []
  }), [searchId]);
  await page.waitForTimeout(200);
  await expect(page.locator('#search-status-bar')).toHaveText('Found 2 matches across 1 file');

  // HandleSearchCancelled lives in the module now (it reads the module's search id)
  await page.evaluate(([sid]) => window.__host('search_workspace_cancelled', { searchId: sid }), [searchId]);
  await page.waitForTimeout(200);
  await expect(page.locator('#search-status-bar')).toHaveText('Search cancelled.');

  // Opening a match asks the host for the file
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.locator('.search-match-row').first().click();
  await page.waitForTimeout(300);
  const sent = await page.evaluate(() => window.__sent);
  expect(sent.map((m) => m.action)).toContain('read_file');
  expect(sent.find((m) => m.action === 'read_file').path).toBe('C:\\proj\\src\\Program.cs');

  expect(errors).toEqual([]);
});