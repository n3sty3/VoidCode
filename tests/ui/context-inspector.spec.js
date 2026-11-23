import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

test('context inspector modal lists token breakdown, compaction warning, and removes attachments', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript(() => { if (window === window.top) localStorage.clear(); });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2200);

  await page.evaluate(() => {
    window.__sent.length = 0;
    window.openContextInspector();
  });

  await expect(page.locator('#context-inspector-modal')).toBeVisible();
  const req = await page.evaluate(() => window.__sent.find(m => m.action === 'inspect_agent_context'));
  expect(req).toBeTruthy();

  await page.evaluate(() => {
    window.__host('agent_context_response', {
      model: 'claude-sonnet-4-20250514',
      provider: 'anthropic',
      contextWindow: 200000,
      inputBudget: 191808,
      totalTokens: 162000,
      percentUsed: 84.5,
      willCompact: true,
      items: [
        { id: 'system', category: 'system', label: 'System prompt', detail: 'Act mode (workspace_auto)', tokens: 420, removable: false },
        { id: 'project-rules', category: 'rules', label: 'Project rules', detail: 'AGENTS.md', tokens: 680, removable: false },
        { id: 'att_1', category: 'attachment', label: 'Program.cs', detail: 'C:\\proj\\Program.cs', tokens: 1200, removable: true },
        { id: 'history', category: 'history', label: 'Conversation history', detail: '24 messages', tokens: 159700, removable: true }
      ]
    });
  });

  await expect(page.locator('#context-inspector-summary')).toContainText('claude-sonnet-4-20250514');
  await expect(page.locator('#context-inspector-summary')).toContainText('Auto-compaction will run');
  await expect(page.locator('#context-inspector-list [data-context-item-id]')).toHaveCount(4);

  // Removing an attachment triggers a refresh inspect_agent_context call
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click('[data-context-remove="att_1"]');
  const refreshed = await page.evaluate(() => window.__sent.find(m => m.action === 'inspect_agent_context'));
  expect(refreshed).toBeTruthy();

  // Escape closes the modal cleanly
  await page.keyboard.press('Escape');
  await expect(page.locator('#context-inspector-modal')).toBeHidden();
  expect(errors).toEqual([]);
});
