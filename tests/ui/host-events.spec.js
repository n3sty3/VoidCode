import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

// The 86-case switch in app.js's handleHostMessage is a Map in
// Core/host-events.js. app.js registers its handler factory at load, dispatch is a lookup, and an
// Action nobody owns is reported once instead of falling through a switch with no default.
// This test also pins the subscription fix: the bridge used to capture handleHostMessage itself, so
// The reliability wrapper that intercepts request_close/file_changed never ran.
test('host events: registry answers, unknown actions reported once, request_close reaches the wrapper', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  // Every case label app.js used to answer from is a registered action now
  const registry = await page.evaluate(() => ({
    count: window.VoidHostEvents.actions().length,
    allHandled: window.VoidHostEvents.actions().every((action) => window.VoidHostEvents.has(action)),
    known: ['init_state_response', 'tree_refreshed', 'pty_output', 'agent_done', 'toast', 'inline_assist_cancelled', 'shell_spawn_response']
      .filter((action) => window.VoidHostEvents.has(action)).length
  }));
  // 85 original app.js cases + 12 new handlers added in Blocks 2-4 (turn-review 5, context-inspector 1, health 3, workflow-tools 3)
  expect(registry.count).toBe(97);
  expect(registry.allHandled).toBe(true);
  expect(registry.known).toBe(7);

  // A known action still runs end to end through the map
  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);
  await page.waitForTimeout(800);
  await expect(page.locator('#file-tree-container')).toContainText('Program.cs');
  await page.evaluate(([tree]) => window.__host('tree_refreshed', { tree }), [TREE]);
  await page.waitForTimeout(300);
  await expect(page.locator('#file-tree-container')).toContainText('notes.md');

  // An unknown action is reported once and does not disturb the next message
  const before = await page.evaluate(() => window.VoidHostEvents.unknownActions().length);
  await page.evaluate(() => window.__host('not_a_real_action', { x: 1 }));
  await page.evaluate(() => window.__host('not_a_real_action', { x: 2 }));
  await page.waitForTimeout(200);
  const unknowns = await page.evaluate(() => window.VoidHostEvents.unknownActions());
  expect(unknowns).toContain('not_a_real_action');
  expect(unknowns.length).toBe(before + 1);
  await page.evaluate(() => window.__host('toast', { message: 'still alive', type: 'info' }));
  await page.waitForTimeout(300);
  await expect(page.locator('body')).toContainText('still alive');

  // The reliability wrapper is on the subscription path: nothing is dirty, so the host is told to
  // Close immediately. Before the thunk fix this produced no frame at all.
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.evaluate(() => window.__host('request_close', {}));
  await page.waitForTimeout(500);
  const closeFrames = await page.evaluate(() => window.__sent.filter((m) => m.action === 'confirm_close').length);
  expect(closeFrames).toBe(1);

  expect(errors).toEqual([]);
});