import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

const TREE = {
  name: 'proj',
  path: 'C:\\proj',
  isDirectory: true,
  children: [
    { name: 'Program.cs', path: 'C:\\proj\\Program.cs', isDirectory: false }
  ]
};

test('Block 5 extracted modules: jumpToProblem unopened file, codebase search searchId, split editor, utility windows, and composer chips', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript(() => { if (window === window.top) localStorage.clear(); });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2200);

  await page.evaluate(([t]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree: t, sessions: [], checkpoints: [] }), [TREE]);
  await page.waitForTimeout(400);

  // 1. jumpToProblem on an unopened file routes via navigateToFileLocation (no pendingLocation ReferenceError)
  await page.evaluate(() => {
    window.__sent.length = 0;
    window.jumpToProblem('C:\\proj\\Program.cs', 3, 5);
  });
  const readReq = await page.evaluate(() => window.__sent.find(m => m.action === 'read_file'));
  expect(readReq).toMatchObject({ path: 'C:\\proj\\Program.cs' });

  await page.evaluate(() => {
    window.__host('read_file_response', {
      path: 'C:\\proj\\Program.cs',
      content: 'line1\nline2\n    targetToken();\nline4\n',
      metadata: { encoding: 'utf-8', eol: 'LF' }
    });
  });
  await page.waitForTimeout(800);
  const pos = await page.evaluate(() => window.getMonacoEditor()?.getPosition());
  expect(pos).toMatchObject({ lineNumber: 3, column: 5 });

  // 2. Codebase search response with matching searchId renders without ReferenceError
  await page.evaluate(() => {
    window.__sent.length = 0;
    window.openCodebaseSearch();
  });
  await page.fill('#codebase-search-input', 'targetToken');
  await page.waitForTimeout(350);
  const searchReq = await page.evaluate(() => window.__sent.find(m => m.action === 'search_codebase'));
  expect(searchReq).toBeTruthy();
  await page.evaluate((sid) => {
    window.__host('search_codebase_response', {
      searchId: sid,
      results: [{ file: 'C:\\proj\\Program.cs', symbol: 'targetToken', kind: 'func', startLine: 3, startColumn: 5 }]
    });
  }, searchReq.searchId);
  await expect(page.locator('#codebase-results-list .symbol-item')).toHaveCount(1);
  await page.keyboard.press('Escape');

  // 3. Utility window (Playbooks) + Split Editor + Agent attachment chip
  await page.evaluate(() => window.openUtility('win-prompts'));
  await expect(page.locator('#win-prompts')).toBeVisible();
  await expect(page.locator('#playbooks-list .win-item').first()).toBeVisible();
  await page.evaluate(() => window.toggleMiniWindow('win-prompts', false));

  await page.evaluate(() => window.toggleSplitEditor());
  await expect(page.locator('#editor-pane-secondary')).toBeVisible();
  expect(await page.evaluate(() => window.getIsSplitEditorActive())).toBe(true);
  await page.evaluate(() => window.closeSplitEditor());
  expect(await page.evaluate(() => window.getIsSplitEditorActive())).toBe(false);

  await page.evaluate(() => window.attachFileToPrompt('C:\\proj\\Program.cs'));
  await expect(page.locator('#attached-context-bar .attached-chip')).toHaveCount(1);
  expect(await page.evaluate(() => window.getAttachedContextItems().length)).toBe(1);

  expect(errors).toEqual([]);
});
