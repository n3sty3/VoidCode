import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

// It owns the streaming state that used
// To sit in app.js, imports the markup helpers, and imports the per-message helpers from
// Modules/agent-chat.js (a real inter-module import, no globals). This drives the bridge events the
// Host sends during a run.
test('agent stream renders reasoning, content, and grouped tool cards', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.click('#btn-toggle-pane-agent'); // the agent pane starts closed
  await page.waitForTimeout(400);

  // Stream start -> reasoning delta -> content deltas
  await page.evaluate(() => window.__host('agent_stream_start', {}));
  await page.evaluate(() => window.__host('agent_reasoning_delta', { delta: 'thinking hard' }));
  await page.evaluate(() => window.__host('agent_stream_delta', { delta: 'Hello ' }));
  await page.evaluate(() => window.__host('agent_stream_delta', { delta: 'world' }));
  await page.waitForTimeout(300);

  await expect(page.locator('.chat-msg.assistant .msg-content.is-streaming')).toHaveCount(1);
  await expect(page.locator('.reasoning-box')).toBeVisible();
  await expect(page.locator('.reasoning-content')).toHaveText('thinking hard');
  await expect(page.locator('.stream-markdown-body')).toContainText('Hello world');

  // Finalize: markdown is rendered through the imported helpers and the action bar appears
  await page.evaluate(() => window.__host('agent_stream_end', { fullContent: 'Hello **world**' }));
  await page.waitForTimeout(300);
  await expect(page.locator('.chat-msg.assistant .msg-content.is-streaming')).toHaveCount(0);
  await expect(page.locator('.stream-markdown-body strong')).toHaveText('world');
  await expect(page.locator('.chat-msg.assistant .msg-actions')).toHaveCount(1);

  // Two tool cards collapse into one run group
  await page.evaluate(() => window.__host('agent_tool_start',
    { id: 't1', tool: 'read_file', args: '{"path":"src/Program.cs"}' }));
  await page.evaluate(() => window.__host('agent_tool_start',
    { id: 't2', tool: 'patch_file', args: '{"path":"src/Program.cs","search":"a\\nb","replace":"c"}' }));
  await page.waitForTimeout(300);

  await expect(page.locator('.tool-run-group')).toHaveCount(1);
  await expect(page.locator('.tool-card')).toHaveCount(2);
  await expect(page.locator('.tool-run-label')).toContainText('read 1, 1 edit');
  await expect(page.locator('#tool-card-t1 .tool-verb')).toHaveText('Read');
  await expect(page.locator('#tool-card-t1 .tool-target')).toHaveText('src/Program.cs');

  // Approval prompt: Approve resolves back to the host
  await page.evaluate(() => window.__host('agent_tool_approval_needed', { id: 't2' }));
  await page.waitForTimeout(200);
  const approve = page.locator('#tool-card-t2 .approval-v2-bar button', { hasText: /^Approve$/ });
  await expect(approve).toBeVisible();
  await page.evaluate(() => { window.__sent.length = 0; });
  await approve.click();
  await page.waitForTimeout(300);
  const resolved = await page.evaluate(() => window.__sent.find((m) => m.action === 'resolve_tool_approval'));
  expect(resolved).toBeTruthy();
  expect(resolved.id).toBe('t2');
  expect(resolved.approved).toBe(true);

  // Finish: pill, duration, patch preview from the imported builder, and checkpoint buttons
  await page.evaluate(() => window.__host('agent_tool_finish',
    { id: 't2', tool: 'patch_file', output: 'patched ok', checkpointId: 'cp1' }));
  await page.waitForTimeout(300);
  await expect(page.locator('#tool-card-t2 .tool-badge-pill')).toHaveText('Done');
  await expect(page.locator('#tool-card-t2 .tool-duration')).not.toBeEmpty();
  await expect(page.locator('#tool-card-t2 .tool-card-details')).toContainText('- a');
  await expect(page.locator('#tool-card-t2 .tool-card-details')).toContainText('+ c');
  await expect(page.locator('#tool-card-t2 .btn-undo-checkpoint')).toHaveCount(2);

  expect(errors).toEqual([]);
});