import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';
const SETTINGS = {
  Model: 'deepseek/deepseek-chat',
  Provider: 'openrouter',
  BaseUrl: 'https://openrouter.ai/api/v1',
  ApiKeyConfigured: true
};

// The settings modal's whole wiring (open/close with the revert baseline, the
// Tab strip, provider auto-fill, the API-key controls, Test Connection, persona/model/token pills and
// Save) moved into modules/settings.js, together with PERSONA_PRESETS, the clear-key flag and
// OpenSettingsToTab(). The chord recorder moved to modules/command-palette.js.
test('settings UI: tabs, provider auto-fill, pills, api key, save and revert-on-close', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  await page.evaluate(([tree, settings]) => window.__host('init_state_response',
    { settings, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE, SETTINGS]);
  await page.waitForTimeout(400);

  const modal = page.locator('#settings-modal');
  await expect(modal).toBeHidden();

  // The rail button opens it
  await page.click('#btn-settings-toggle');
  await expect(modal).toBeVisible();
  expect(await page.inputValue('#settings-model')).toBe('deepseek/deepseek-chat');

  // Provider auto-fill rewrites the base URL and the model
  await page.selectOption('#settings-provider', 'ollama');
  expect(await page.inputValue('#settings-base-url')).toBe('http://localhost:11434/v1');
  expect(await page.inputValue('#settings-model')).toBe('qwen2.5-coder:14b');

  // Model presets write through to the model field
  await page.selectOption('#settings-model-presets', 'deepseek/deepseek-chat');

  // The tab strip switches panes, and the arrow keys rove between tabs
  await page.click('#settings-tab-editor');
  await expect(page.locator('#tab-editor')).toBeVisible();
  await expect(page.locator('#settings-tab-editor')).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('#settings-tab-appearance')).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('#tab-appearance')).toBeVisible();
  await expect(page.locator('#tab-editor')).toBeHidden();

  // A persona pill loads its preset into the system prompt
  await page.click('#settings-tab-prompt');
  await page.click('[data-persona="systems"]');
  expect(await page.inputValue('#settings-system-prompt')).toContain('staff-level systems engineer');

  // A token pill drives the max-tokens input and the active marker (they live on the Editor tab)
  await page.click('#settings-tab-editor');
  await page.click('.token-pill[data-token-val="16384"]');
  expect(await page.inputValue('#settings-max-tokens')).toBe('16384');
  await expect(page.locator('.token-pill[data-token-val="16384"]')).toHaveClass(/is-active/);

  // The API key: show/hide flips the input type, clear marks the key for removal (Model endpoint tab)
  await page.click('#settings-tab-provider');
  await expect(page.locator('#settings-api-key')).toHaveAttribute('type', 'password');
  await page.click('#btn-toggle-key-vis');
  await expect(page.locator('#settings-api-key')).toHaveAttribute('type', 'text');
  await expect(page.locator('#btn-toggle-key-vis')).toHaveText('Hide');
  await page.click('#btn-clear-api-key');
  await expect(page.locator('#settings-api-key')).toHaveAttribute('placeholder', 'Saved key will be removed when settings are saved');
  await expect(page.locator('#btn-clear-api-key')).toBeHidden();

  // Test Connection sends the gathered settings plus the clear-key flag
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click('#btn-test-connection');
  await page.waitForTimeout(300);
  const test = await page.evaluate(() => window.__sent.find((m) => m.action === 'test_connection'));
  expect(test).toBeTruthy();
  expect(test.clearApiKey).toBe(true);
  expect(test.settings).toBeTruthy();

  // Save posts save_settings with the same flag
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click('#btn-save-settings');
  await page.waitForTimeout(300);
  const saved = await page.evaluate(() => window.__sent.find((m) => m.action === 'save_settings'));
  expect(saved).toBeTruthy();
  expect(saved.clearApiKey).toBe(true);

  // Closing reverts the fields to the baseline the modal opened with
  await page.fill('#settings-model', 'changed/by/hand');
  await page.click('#btn-close-settings');
  await expect(modal).toBeHidden();
  await page.click('#btn-settings-toggle');
  await expect(modal).toBeVisible();
  expect(await page.inputValue('#settings-model')).toBe('deepseek/deepseek-chat');

  expect(errors).toEqual([]);
});