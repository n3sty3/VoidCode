import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

// And the per-turn state it resets
// (currentTurnEdits / lastRunMode) moved into the store because a strict module cannot assign
// Another script's `let`. This drives the helpers through their published shims.
test('agent chat helpers: actions, edit, footer, patch preview, regenerate', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.click('#btn-toggle-pane-agent'); // the agent pane starts closed
  await page.waitForTimeout(400);

  // Build one user + one assistant turn the way the stream does, then run the helpers
  await page.evaluate(() => {
    const container = document.getElementById('agent-chat-container');

    const user = document.createElement('div');
    user.className = 'chat-msg user';
    const userContent = document.createElement('div');
    userContent.className = 'msg-content';
    userContent.textContent = 'please fix the build';
    user.appendChild(userContent);
    container.appendChild(user);

    const assistant = document.createElement('div');
    assistant.className = 'chat-msg assistant';
    const assistantContent = document.createElement('div');
    assistantContent.className = 'msg-content';
    const body = document.createElement('div');
    body.className = 'stream-markdown-body';
    body.textContent = 'done';
    assistantContent.appendChild(body);
    assistant.appendChild(assistantContent);
    container.appendChild(assistant);

    window.addUserMessageActions(user);
    window.addAssistantMessageActions(assistant);
    window.addAssistantMessageActions(assistant); // idempotent: no second bar
  });

  await expect(page.locator('.chat-msg.user .msg-actions--user')).toHaveCount(1);
  await expect(page.locator('.chat-msg.assistant .msg-actions')).toHaveCount(1);
  await expect(page.locator('.chat-msg.assistant .msg-actions')).toContainText('Regenerate');

  // Edit puts the user's text back in the composer
  await page.click('.chat-msg.user .msg-action-btn');
  await expect(page.locator('#agent-input-text')).toHaveValue('please fix the build');

  // The telemetry footer and the patch preview are pure builders
  const built = await page.evaluate(() => {
    window.renderRunFooter({ steps: 2, toolCalls: 1, totalTokens: 1500, elapsedMs: 2100 });
    return {
      footer: document.querySelector('.agent-run-footer')?.textContent,
      preview: window.buildPatchPreview({ search: 'a\nb', replace: 'c' })
    };
  });
  expect(built.footer).toContain('2 steps');
  expect(built.footer).toContain('1 tool call');
  expect(built.footer).toContain('1.5k tok');
  expect(built.footer).toContain('2.1s');
  expect(built.preview).toBe('- a\n- b\n+ c');

  // Regenerate re-sends the last user prompt and resets the per-turn state that now lives in the store
  const regen = await page.evaluate(() => {
    window.VoidState.set('chatHistory', [
      { role: 'user', content: 'please fix the build' },
      { role: 'assistant', content: 'done' }
    ]);
    window.VoidState.set('currentTurnEdits', [{ path: 'stale' }]);
    window.VoidState.set('currentAgentMode', 'plan');
    window.__sent.length = 0;
    window.regenerateLastAnswer();
    return {
      sent: window.__sent.find((m) => m.action === 'start_agent'),
      edits: window.VoidState.get('currentTurnEdits'),
      lastMode: window.VoidState.get('lastRunMode'),
      assistants: document.querySelectorAll('#agent-chat-container > .chat-msg.assistant').length
    };
  });
  expect(regen.sent).toBeTruthy();
  expect(regen.sent.prompt).toBe('please fix the build');
  expect(regen.sent.mode).toBe('plan');
  expect(regen.edits).toEqual([]);
  expect(regen.lastMode).toBe('plan');
  expect(regen.assistants).toBe(0);

  expect(errors).toEqual([]);
});