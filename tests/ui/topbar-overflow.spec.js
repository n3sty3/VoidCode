import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

// The top bar and rail never hide things silently.
//
// What was wrong: layout.css hid the operator pill at 1240px, the model chip at 1120px AND the "..." overflow
// At 980px, so a 920px window had no route to the six tool windows and no way to see which model was
// Selected. Now every control collapses to an icon (with its full name in the tooltip) and the overflow
// Is always present. The plan's done-when is the first assertion: the Agent toggle exists at every width.
const URL = 'http://localhost:8765/index.html';
const SETTINGS = {
  Model: 'deepseek/deepseek-chat',
  Provider: 'openrouter',
  BaseUrl: 'https://openrouter.ai/api/v1',
  ApiKeyConfigured: true
};
const WIDTHS = [920, 1280, 1440, 1600];

async function boot(page, width) {
  await page.setViewportSize({ width, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.evaluate(([tree, settings]) => window.__host('init_state_response',
    { settings, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE, SETTINGS]);
  await page.waitForTimeout(400);
}

for (const width of WIDTHS) {
  test(`top bar at ${width}px: agent toggle, model chip, operator pill and overflow all reachable`, async ({ page }) => {
    await boot(page, width);

    // The plan's done-when: the Agent toggle exists at every width.
    await expect(page.locator('#btn-toggle-pane-agent')).toBeVisible();

    // Nothing in the top bar may be display:none - collapsed, not hidden.
    for (const selector of ['#btn-open-settings', '#btn-operator-pill', '#btn-more-tools']) {
      const visible = await page.locator(selector).isVisible();
      expect(visible, `${selector} is not reachable at ${width}px`).toBe(true);
    }
  });
}

test('at 920px the tool windows are still reachable, through the overflow menu', async ({ page }) => {
  await boot(page, 920);

  // The dock itself is allowed to collapse; the menu that replaces it is not.
  await expect(page.locator('#top-tools-dock')).toBeHidden();
  await page.click('#btn-more-tools');
  const menu = page.locator('#top-tools-menu');
  await expect(menu).toBeVisible();
  expect(await menu.locator('[role="menuitem"]').count()).toBe(6);
  await expect(menu.locator('[role="menuitem"]', { hasText: 'Live preview' })).toBeVisible();
});

test('the collapsed chips still name themselves in their tooltips', async ({ page }) => {
  await boot(page, 920);

  // The model name is only visible here at this width, so the tooltip is not optional.
  const modelTitle = await page.getAttribute('#btn-open-settings', 'title');
  expect(modelTitle).toContain('deepseek/deepseek-chat');
  await expect(page.locator('#btn-open-settings')).toHaveClass(/is-ready/);

  const operatorTitle = await page.getAttribute('#btn-operator-pill', 'title');
  expect(operatorTitle).toContain('Operator:');
});

test('at 1600px the chips show their labels and the tool menu is still the route', async ({ page }) => {
  await boot(page, 1600);

  await expect(page.locator('#active-model-label')).toBeVisible();
  await expect(page.locator('#topbar-operator-name')).toBeVisible();
  await expect(page.locator('#btn-more-tools')).toBeVisible();
  // The six-window dock stays collapsed into the "..." menu at every width on purpose
  // (styles/components/icon-pack.css): one entry point, no label wall.
  await expect(page.locator('#top-tools-dock')).toBeHidden();
});