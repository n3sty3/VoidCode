import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

// Workspace Trust.
//
// The host asks about folders it has never opened and pushes the resulting state; the UI shows
// The prompt, then a "Restricted" badge while the folder is untrusted and locks the agent to
// Review. Done when: the UI test shows the prompt and the Restricted badge.
const APP_URL = 'http://localhost:8765/index.html';
const FOLDER = 'C:\\proj\\untrusted-app';

async function boot(page) {
  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(APP_URL);
  await page.waitForTimeout(2500);
}

async function sendTrust(page, action, payload) {
  await page.evaluate(([a, p]) => window.__host(a, p), [action, payload]);
  await page.waitForTimeout(250);
}

test('an unknown folder asks first, and trusting it lifts restricted mode', async ({ page }) => {
  await boot(page);

  // The page asks the host for the state of the open folder once it has loaded
  const asked = await page.evaluate(() => window.__sent.filter((m) => m.action === 'get_workspace_trust').length);
  expect(asked).toBeGreaterThan(0);

  await sendTrust(page, 'trust_prompt', { path: FOLDER });
  await expect(page.locator('#trust-modal')).toBeVisible();
  await expect(page.locator('#trust-modal-path')).toHaveText(FOLDER);

  await page.click('#btn-trust-folder');
  const decision = await page.evaluate(() => window.__sent.filter((m) => m.action === 'set_workspace_trust'));
  expect(decision).toHaveLength(1);
  expect(decision[0]).toMatchObject({ path: FOLDER, trusted: true });

  // The host confirms, the prompt goes away and restricted mode is off
  await sendTrust(page, 'trust_state', { path: FOLDER, state: 'trusted', restricted: false });
  await expect(page.locator('#trust-modal')).toBeHidden();
  await expect(page.locator('#workspace-trust-badge')).toBeHidden();
  await expect(page.locator('#btn-agent-authority-mode')).toBeEnabled();
});

test('keeping the folder restricted shows the badge and locks the agent to Review', async ({ page }) => {
  await boot(page);

  await sendTrust(page, 'trust_prompt', { path: FOLDER });
  await expect(page.locator('#trust-modal')).toBeVisible();

  await page.click('#btn-distrust-folder');
  const decision = await page.evaluate(() => window.__sent.filter((m) => m.action === 'set_workspace_trust'));
  expect(decision[0]).toMatchObject({ path: FOLDER, trusted: false });

  await sendTrust(page, 'trust_state', { path: FOLDER, state: 'restricted', restricted: true });
  await expect(page.locator('#trust-modal')).toBeHidden();

  const badge = page.locator('#workspace-trust-badge');
  await expect(badge).toBeVisible();
  await expect(badge).toHaveText('Restricted');

  const authority = page.locator('#btn-agent-authority-mode');
  await expect(authority).toBeDisabled();
  await expect(authority).toHaveText('Review (restricted)');

  // And auto-approve is switched off and locked so the agent cannot run anything unreviewed
  await expect(page.locator('#settings-auto-approve')).toBeDisabled();
  expect(await page.evaluate(() => document.getElementById('settings-auto-approve').checked)).toBe(false);
});