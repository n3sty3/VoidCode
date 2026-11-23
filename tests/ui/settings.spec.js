import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

// The last converted file besides app.js.
// It owns the autosave mode, the readability switches and the Workspace Trust subscription (that
// Part is covered by workspace-trust.spec.js), while the tab list it saves stays in app.js.
// App.js reaches it only through the five entry points published by init().
test('settings module: render, round-trip, autosave cycle and readability switches', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  // --- auto save: the mode table and the current mode live in the module now ---
  const autosave = page.locator('#status-autosave');
  await expect(autosave).toHaveText('Auto save: Off');
  const labelFor = async () => (await autosave.textContent()).trim();
  await autosave.click();
  expect(await labelFor()).toBe('Auto save: After delay');
  expect(await page.evaluate(() => localStorage.getItem('voidcode_autosave_v1'))).toBe('delay');
  await autosave.click();
  expect(await labelFor()).toBe('Auto save: On focus loss');
  expect(await page.evaluate(() => localStorage.getItem('voidcode_autosave_v1'))).toBe('blur');
  await autosave.click();
  expect(await labelFor()).toBe('Auto save: Off');
  expect(await page.evaluate(() => localStorage.getItem('voidcode_autosave_v1'))).toBe('off');

  // --- readability switches: the well is built by setupReadabilityModes() into the Appearance tab ---
  await page.click('#btn-open-settings');
  await expect(page.locator('#settings-modal')).toBeVisible();
  await page.click('#settings-tab-appearance');
  const reduce = page.locator('#readability-reduce');
  await expect(reduce).toBeVisible();
  await reduce.check();
  expect(await page.evaluate(() => document.documentElement.getAttribute('data-reduce-transparency'))).toBe('on');
  expect(await page.evaluate(() => localStorage.getItem('voidcode_reduce_transparency'))).toBe('1');
  await page.locator('#readability-contrast').check();
  expect(await page.evaluate(() => document.documentElement.getAttribute('data-contrast'))).toBe('on');
  await reduce.uncheck();
  expect(await page.evaluate(() => document.documentElement.getAttribute('data-reduce-transparency'))).toBe('off');
  await page.click('#btn-close-settings');

  // --- render: applySettingsToUI paints the modal, the theme and the editor options ---
  const painted = await page.evaluate(() => {
    window.applySettingsToUI({
      Provider: 'openrouter',
      BaseUrl: 'https://example.test/v1',
      Model: 'openai/gpt-x',
      SystemPrompt: 'be brief',
      Temperature: 0.7,
      MaxTokens: 16384,
      MaxAgentTurns: 0,
      EditorFontSize: 16,
      EditorMinimap: false,
      EditorWordWrap: true,
      FormatOnSave: true,
      AutoApproveTools: true,
      ApiKeyConfigured: true,
      Theme: 'emerald',
      GlassBlur: 12,
      FieldOpacity: 40
    });
    const activePill = document.querySelector('.token-pill.is-active');
    const editor = window.getMonacoEditor?.();
    return {
      modelLabel: document.getElementById('active-model-label').textContent,
      provider: document.getElementById('settings-provider').value,
      baseUrl: document.getElementById('settings-base-url').value,
      temp: document.getElementById('lbl-temp-val').textContent,
      turns: document.getElementById('lbl-turns-val').textContent,
      tempField: document.getElementById('settings-temperature').value,
      pill: activePill && activePill.getAttribute('data-token-val'),
      theme: document.documentElement.getAttribute('data-theme'),
      authority: document.getElementById('btn-agent-authority-mode').textContent,
      apiPlaceholder: document.getElementById('settings-api-key').placeholder,
      clearKeyVisible: document.getElementById('btn-clear-api-key').style.display,
      hasEditor: !!editor,
      fontSize: editor ? editor.getOption(window.monaco.editor.EditorOption.fontSize) : null,
      wordWrap: editor ? editor.getOption(window.monaco.editor.EditorOption.wordWrap) : null,
      minimap: editor ? editor.getOption(window.monaco.editor.EditorOption.minimap) : null
    };
  });

  expect(painted.modelLabel).toBe('openai/gpt-x');
  expect(painted.provider).toBe('openrouter');
  expect(painted.baseUrl).toBe('https://example.test/v1');
  expect(painted.temp).toBe('0.7');
  expect(painted.tempField).toBe('0.7');
  expect(painted.turns).toBe('Unlimited');
  expect(painted.pill).toBe('16384');
  expect(painted.theme).toBe('emerald');
  expect(painted.authority).toBe('Auto-run');
  expect(painted.apiPlaceholder).toContain('Stored securely');
  expect(painted.clearKeyVisible).toBe('inline-flex');

  // The module reaches the editor through getMonacoEditor() instead of a script-scoped `let`
  expect(painted.hasEditor).toBe(true);
  expect(painted.fontSize).toBe(16);
  expect(painted.wordWrap).toBe('on');
  expect(painted.minimap.enabled).toBe(false);

  // --- round-trip: gatherSettingsFromModal() reads the modal back, theme and operator included ---
  const gathered = await page.evaluate(() => {
    document.getElementById('settings-model').value = 'round/trip';
    document.getElementById('settings-temperature').value = '0.4';
    return {
      s: window.gatherSettingsFromModal(),
      operator: window.getCurrentOperator()
    };
  });
  expect(gathered.s.Model).toBe('round/trip');
  expect(gathered.s.Temperature).toBe(0.4);
  expect(gathered.s.MaxTokens).toBe(16384);
  expect(gathered.s.MaxAgentTurns).toBe(0);
  expect(gathered.s.EditorFontSize).toBe(16);
  expect(gathered.s.Theme).toBe('emerald');
  expect(gathered.s.AutoApproveTools).toBe(true);
  expect(gathered.s.ActiveOperator).toBe(gathered.operator);

  expect(errors).toEqual([]);
});