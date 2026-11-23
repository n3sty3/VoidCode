import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

// Transparency and contrast, measured on the tokens rather than eyeballed.
//
// Before this slice "reduced transparency" only removed the blur: the fills stayed rgba(...,0.62), the
// Measured --field-opacity stayed 0.84, and the blur kill listed .glass* only, so #win-settings kept its
// Url(#lens) blur(30px). prefers-contrast: more had no rule at all. Both are now real, and the tests below
// Assert the *pairs* - translucent when the preference is off, opaque when it is on - so a ruleset that
// Merely paints everything opaque forever cannot pass.
const URL = 'http://localhost:8765/index.html';

async function boot(page) {
  await page.setViewportSize({ width: 1440, height: 900 });
  // Clear once per tab, not once per navigation: the persistence tests reload the page on purpose, and a
  // Blanket clear in the init script would wipe the very value they are checking survived.
  await page.addInitScript(() => {
    try {
      if (!sessionStorage.getItem('voidcode_test_cleared')) {
        localStorage.clear();
        sessionStorage.setItem('voidcode_test_cleared', '1');
      }
    } catch { /* storage blocked */ }
  });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);
  await page.waitForTimeout(500);
}

// Playwright's emulateMedia has no prefers-reduced-transparency option, so the OS preference is set over
// CDP. It is a real Chromium media feature - the test asserts matchMedia agrees before trusting anything.
async function emulateTransparency(page, value) {
  const client = await page.context().newCDPSession(page);
  await client.send('Emulation.setEmulatedMedia', {
    features: [{ name: 'prefers-reduced-transparency', value }]
  });
  await page.waitForTimeout(200);
}

const surfaces = (page) => page.evaluate(() => {
  const read = (selector) => {
    const el = document.querySelector(selector);
    if (!el) return null;
    const cs = getComputedStyle(el);
    const alpha = /^rgba?\(([^)]+)\)$/.exec(cs.backgroundColor)?.[1].split(',').map((part) => Number(part.trim()));
    return {
      opaque: !alpha || alpha.length < 4 || alpha[3] === 1,
      blurred: cs.backdropFilter !== 'none' && cs.backdropFilter !== ''
    };
  };
  const root = getComputedStyle(document.documentElement);
  return {
    topbar: read('#topbar'),
    explorer: read('#sidebar-explorer'),
    agent: read('#sidebar-agent'),
    settingsWindow: read('#win-settings'),
    fieldOpacity: root.getPropertyValue('--field-opacity').trim(),
    glassFill: root.getPropertyValue('--glass-fill').trim(),
    reduced: document.documentElement.getAttribute('data-reduce-transparency')
  };
});
test('the OS reduced-transparency preference makes the surfaces opaque, and off gives the glass back', async ({ page }) => {
  await boot(page);

  await emulateTransparency(page, 'no-preference');
  const glass = await surfaces(page);
  expect(glass.topbar, 'glass mode keeps a translucent top bar').toMatchObject({ opaque: false, blurred: true });
  expect(glass.explorer.opaque).toBe(false);
  expect(glass.settingsWindow.blurred, 'the settings window blurs in glass mode').toBe(true);
  expect(Number(glass.fieldOpacity)).toBeLessThan(1);

  await emulateTransparency(page, 'reduce');
  expect(await page.evaluate(() => matchMedia('(prefers-reduced-transparency: reduce)').matches)).toBe(true);
  const solid = await surfaces(page);
  for (const key of ['topbar', 'explorer', 'agent', 'settingsWindow']) {
    expect(solid[key], key).toEqual({ opaque: true, blurred: false });
  }
  expect(solid.fieldOpacity).toBe('1');
  // The fill token is the theme's own ink, not a hard-coded grey, so reduced transparency still themes.
  expect(solid.glassFill).toBe(await page.evaluate(() =>
    getComputedStyle(document.documentElement).getPropertyValue('--ink').trim()));
});

test('the in-app switch does the same thing, and turns back off', async ({ page }) => {
  await boot(page);
  await emulateTransparency(page, 'no-preference');
  await page.click('#btn-settings-toggle');
  await page.click('#settings-tab-appearance');
  // The switch is a styled checkbox inside a <label>; clicking the label is the real user gesture.
  await page.click('label[for="readability-reduce"]');
  await expect.poll(async () => (await surfaces(page)).reduced).toBe('on');
  const solid = await surfaces(page);
  for (const key of ['topbar', 'explorer', 'agent', 'settingsWindow']) {
    expect(solid[key], key).toEqual({ opaque: true, blurred: false });
  }

  await page.click('label[for="readability-reduce"]');
  await expect.poll(async () => (await surfaces(page)).reduced).toBe('off');
  const back = await surfaces(page);
  expect(back.topbar.opaque).toBe(false);
  expect(back.settingsWindow.blurred).toBe(true);
});

test('the choice is remembered across a reload', async ({ page }) => {
  await boot(page);
  await page.click('#btn-settings-toggle');
  await page.click('#settings-tab-appearance');
  await page.click('label[for="readability-reduce"]');
  await expect.poll(async () => (await surfaces(page)).reduced).toBe('on');

  await page.reload();
  await page.waitForTimeout(2500);
  expect(await page.evaluate(() =>
    document.documentElement.getAttribute('data-reduce-transparency'))).toBe('on');
});

test('prefers-contrast: more lifts the text tokens, and off leaves them alone', async ({ page }) => {
  await boot(page);
  const tokens = () => page.evaluate(() => {
    const root = getComputedStyle(document.documentElement);
    return {
      text: root.getPropertyValue('--text').trim(),
      faint: root.getPropertyValue('--text-faint').trim(),
      hairline: root.getPropertyValue('--hairline').trim()
    };
  });

  await page.emulateMedia({ contrast: 'no-preference' });
  expect((await tokens()).text).not.toBe('#ffffff');

  await page.emulateMedia({ contrast: 'more' });
  expect(await page.evaluate(() => matchMedia('(prefers-contrast: more)').matches)).toBe(true);
  const boosted = await tokens();
  expect(boosted.text).toBe('#ffffff');
  expect(boosted.faint).toBe('#c4cad4');
  expect(boosted.hairline).toBe('rgba(255, 255, 255, 0.30)');
});

test('the High contrast switch matches the OS preference and survives a reload', async ({ page }) => {
  await boot(page);
  await page.click('#btn-settings-toggle');
  await page.click('#settings-tab-appearance');
  await page.click('label[for="readability-contrast"]');
  await expect.poll(async () => page.evaluate(() =>
    document.documentElement.getAttribute('data-contrast'))).toBe('on');
  expect(await page.evaluate(() =>
    getComputedStyle(document.documentElement).getPropertyValue('--text').trim())).toBe('#ffffff');

  await page.reload();
  await page.waitForTimeout(2500);
  expect(await page.evaluate(() =>
    document.documentElement.getAttribute('data-contrast'))).toBe('on');
});

test('forced colours paint the shell in Canvas colours and drop every blur', async ({ page }) => {
  await boot(page);
  await page.emulateMedia({ forcedColors: 'active' });
  await expect.poll(async () => page.evaluate(() => matchMedia('(forced-colors: active)').matches)).toBe(true);

  const forced = await page.evaluate(() => {
    const card = getComputedStyle(document.querySelector('#win-settings'));
    const overlay = getComputedStyle(document.querySelector('#settings-modal'));
    return {
      cardBackground: card.backgroundColor,
      cardBlur: card.backdropFilter,
      overlayBackground: overlay.backgroundColor,
      overlayBlur: overlay.backdropFilter
    };
  });
  // Rgb(255, 255, 255) is Canvas in the default (light) forced-colours palette.
  expect(forced.cardBackground).toBe('rgb(255, 255, 255)');
  expect(forced.overlayBackground).toBe('rgb(255, 255, 255)');
  expect(forced.cardBlur).toBe('none');
  expect(forced.overlayBlur).toBe('none');
});