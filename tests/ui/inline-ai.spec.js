import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

const TREE = {
  name: 'proj',
  path: 'C:\\proj',
  isDirectory: true,
  children: [{ name: 'tool.js', path: 'C:\\proj\\tool.js', isDirectory: false }]
};

const CODE_JS = 'const x = 1;\nconst y = 2;\n';

// (it keeps its own inlineAi* state). This
// Drives the Ctrl+K flow through the published entry points and the bridge responses.
test('inline AI assist: open, request, preview, accept and discard', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  await page.evaluate(([t]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree: t, sessions: [], checkpoints: [] }), [TREE]);
  await page.evaluate(([c]) => window.__host('read_file_response',
    { path: 'C:\\proj\\tool.js', content: c, metadata: { encoding: 'utf-8', eol: 'LF' } }), [CODE_JS]);
  await page.waitForTimeout(2000);

  // Select the first line, then open the widget the way Ctrl+K does
  await page.evaluate(() => {
    const editor = window.getMonacoEditor();
    editor.setSelection(new window.monaco.Selection(1, 1, 1, 14));
    window.openInlineAiAssist();
  });
  await page.waitForTimeout(400);

  await expect(page.locator('#inline-ai-widget')).toBeVisible();
  await expect(page.locator('#inline-ai-context-pill')).toHaveText('Line 1');

  // Submit -> inline_assist_start carries the selection and the file
  await page.fill('#inline-ai-input', 'make it better');
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click('#btn-inline-ai-submit');
  await page.waitForTimeout(400);
  const req = await page.evaluate(() => window.__sent.find((m) => m.action === 'inline_assist_start'));
  expect(req).toBeTruthy();
  expect(req.instruction).toBe('make it better');
  expect(req.selectedCode).toBe('const x = 1;');
  expect(req.fileContent).toContain('const y = 2;');
  expect(req.relativePath).toBe('C:\\proj\\tool.js');

  // Status + preview
  await page.evaluate(() => window.__host('inline_assist_status', { status: 'Thinking…' }));
  await expect(page.locator('#inline-ai-status-text')).toHaveText('Thinking…');

  await page.evaluate(() => window.__host('inline_assist_complete',
    { replacement: 'const x = 1;\nconst y = 3;' }));
  await page.waitForTimeout(400);
  await expect(page.locator('#inline-ai-diff-banner')).toBeVisible();
  await expect(page.locator('#inline-ai-status-row')).toBeHidden();
  await expect(page.locator('#inline-diff-stats')).toContainText('2 new vs 1 orig');
  expect(await page.evaluate(() => window.getMonacoEditor().getModel().getValue()))
    .toContain('const y = 3;');

  // Accept keeps the proposed edit and marks the tab dirty
  await page.click('#btn-inline-ai-accept');
  await page.waitForTimeout(300);
  await expect(page.locator('#inline-ai-widget')).toBeHidden();
  expect(await page.evaluate(() => window.VoidState.get('activeTab').dirty)).toBe(true);
  expect(await page.evaluate(() => window.getMonacoEditor().getModel().getValue()))
    .toContain('const y = 3;');

  // Discardundoes the preview edit
  await page.evaluate(() => {
    const editor = window.getMonacoEditor();
    editor.setSelection(new window.monaco.Selection(1, 1, 1, 14));
    window.openInlineAiAssist();
  });
  await page.waitForTimeout(300);
  await page.evaluate(() => window.__host('inline_assist_complete', { replacement: 'const z = 9;' }));
  await page.waitForTimeout(300);
  expect(await page.evaluate(() => window.getMonacoEditor().getModel().getValue())).toContain('const z = 9;');
  // Discard undoes the preview edit. Note: monaco groups the two preview pushes into one undo
  // Element, so this can land on the original text as well - the invariant that matters is that the
  // Discarded proposal is gone again.
  await page.click('#btn-inline-ai-discard');
  await page.waitForTimeout(300);
  await expect(page.locator('#inline-ai-widget')).toBeHidden();
  const afterDiscard = await page.evaluate(() => window.getMonacoEditor().getModel().getValue());
  expect(afterDiscard).not.toContain('const z = 9;');
  expect(afterDiscard).toContain('const y =');

  expect(errors).toEqual([]);
});