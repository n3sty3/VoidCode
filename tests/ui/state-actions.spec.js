import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

// Every failure state ends in one primary action. Before this slice the two that mattered
// Most were dead ends: an agent error rendered the message and nothing else, and a host timeout printed a
// Console line with no way to try again. Git's "not installed" card already had its two fixes, and the
// No-workspace empty state its three, so those are pinned here rather than rebuilt.
const URL = 'http://localhost:8765/index.html';

async function boot(page) {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => { try { window.localStorage.clear(); } catch { /* storage blocked */ } });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: { ApiKeyConfigured: false, Provider: 'openrouter' }, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);
  await page.waitForTimeout(500);
}

const cardState = (page) => page.evaluate(() => {
  const card = document.querySelector('#agent-chat-container .agent-error-state');
  if (!card) return null;
  return {
    role: card.getAttribute('role'),
    title: card.querySelector('p')?.textContent.trim(),
    detail: card.querySelector('.empty-hint')?.textContent.trim(),
    action: card.querySelector('button')?.textContent.trim()
  };
});

// The agent pane starts closed at 1440px (one secondary pane at a time), so anything chat-shaped has to
// Open it first - otherwise every locator is "present but hidden".
async function showAgentPane(page) {
  await page.click('#btn-toggle-pane-agent');
  await expect(page.locator('#agent-chat-container')).toBeVisible();
}

async function showGitPane(page) {
  await page.click('#tab-nav-git');
  await expect(page.locator('#view-git')).toBeVisible();
}

test('a configuration failure offers Settings, and the button opens the right section', async ({ page }) => {
  await boot(page);
  await showAgentPane(page);
  await page.evaluate(() => window.__host('agent_error',
    { message: 'API Error (401 Unauthorized): no api key configured' }));

  await expect.poll(async () => (await cardState(page))?.action).toBe('Open settings');
  expect(await cardState(page)).toMatchObject({ role: 'alert', title: 'Agent run failed' });

  await page.click('#agent-chat-container .agent-error-state button');
  await expect(page.locator('#settings-modal')).toBeVisible();
  expect(await page.evaluate(() => document.querySelector('#settings-modal .settings-tab-btn[aria-selected="true"]').id))
    .toBe('settings-tab-provider');
  // The card went away with the click, so a stale error is not left behind in the transcript.
  expect(await cardState(page)).toBeNull();
});

test('a transport failure offers Retry, which re-runs the last turn', async ({ page }) => {
  await boot(page);
  await showAgentPane(page);

  // A real turn first: Retry regenerates the last prompt, and there is nothing to regenerate without one.
  await page.fill('#agent-input-text', 'rename the parser');
  await page.click('#btn-send-agent');
  await expect.poll(async () => page.evaluate(() =>
    window.__sent.filter((m) => m.action === 'start_agent').length)).toBe(1);

  await page.evaluate(() => window.__host('agent_error',
    { message: 'Network error calling LLM API: connection reset by peer' }));
  await expect.poll(async () => (await cardState(page))?.action).toBe('Retry');

  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click('#agent-chat-container .agent-error-state button');
  await expect.poll(async () => page.evaluate(() =>
    window.__sent.filter((m) => m.action === 'start_agent').length)).toBe(1);
});

test('git is unavailable: the card names both fixes and Retry asks the host again', async ({ page }) => {
  await boot(page);
  await showGitPane(page);
  await page.evaluate(() => window.__host('git_status_response',
    { isAvailable: false, message: 'git.exe was not found on PATH' }));
  await expect(page.locator('.git-blocked')).toBeVisible();

  const card = await page.evaluate(() => ({
    heading: document.querySelector('.git-blocked h4')?.textContent.trim(),
    actions: Array.from(document.querySelectorAll('.git-blocked-actions button')).map((b) => b.textContent.trim())
  }));
  expect(card.heading).toBe('Git is not available');
  expect(card.actions).toEqual(['Install Git', 'Retry detection']);

  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click('#btn-git-retry');
  // The card's "Retry detection" goes through refreshGitStatus, which asks for git_get_status (the
  // Response action is git_status_response - the pair is not symmetric, which is exactly why the retry is
  // Worth re-asking instead of guessing).
  await expect.poll(async () => page.evaluate(() =>
    window.__sent.filter((m) => m.action === 'git_get_status').length)).toBeGreaterThan(0);

  await page.click('#btn-git-install');
  // Outgoing frames are flattened by core/bridge.js (`{action, ...data, bridgeNonce}`), so the url is a
  // Top-level field - not under `payload`, which is the shape of *incoming* messages.
  expect(await page.evaluate(() =>
    window.__sent.find((m) => m.action === 'open_external_url')?.url)).toBe('https://git-scm.com/download/win');
});

test('a missing language server is fixable from the status slot', async ({ page }) => {
  await boot(page);
  await page.evaluate(() => window.__host('lsp_status',
    { language: 'C#', status: 'missing', detail: 'OmniSharp is not installed.' }));
  await expect(page.locator('#status-lsp')).toHaveClass(/is-attention/);

  await page.click('#status-lsp');
  await expect(page.locator('#settings-modal')).toBeVisible();
  expect(await page.evaluate(() => document.querySelector('#settings-modal .settings-tab-btn[aria-selected="true"]').id))
    .toBe('settings-tab-editor');
});

test('the no-workspace empty state still offers its way in', async ({ page }) => {
  await boot(page);
  const actions = await page.evaluate(() => ({
    choose: document.getElementById('btn-empty-choose-project')?.textContent.trim(),
    open: document.getElementById('btn-empty-open-file')?.textContent.trim(),
    recent: document.getElementById('btn-empty-customize')?.textContent.trim()
  }));
  expect(actions).toEqual({ choose: 'Choose project', open: 'Open file', recent: 'Recent projects' });

  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click('#btn-empty-choose-project');
  expect(await page.evaluate(() => window.__sent.map((m) => m.action))).toContain('open_workspace_dialog');
});