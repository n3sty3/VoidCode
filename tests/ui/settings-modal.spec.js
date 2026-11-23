import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

// Settings was the last draggable "window" in the app - a centred card with a
// `data-drag-window` header and `cursor: grab`, translated by translate3d on drag. Because
// ClampMiniWindow only ran for `.mini-window`, a drag could park the card half off-screen while the
// Scrim stayed where it was. These tests pin the modal behaviour, and they also pin the other half of the
// Audit item: the seven utility surfaces are ONE tabbed, clamped, non-modal window (that consolidation
// Predates this slice; it had no tests of its own).
const URL = 'http://localhost:8765/index.html';

async function boot(page, viewport = { width: 1440, height: 900 }) {
  await page.setViewportSize(viewport);
  await page.addInitScript(() => { try { window.localStorage.clear(); } catch { /* storage blocked */ } });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);
  await page.waitForTimeout(500);
}

const cardRect = (page) => page.evaluate(() => {
  const r = document.getElementById('win-settings').getBoundingClientRect();
  return { left: Math.round(r.left), top: Math.round(r.top), right: Math.round(r.right), bottom: Math.round(r.bottom), width: Math.round(r.width), height: Math.round(r.height) };
});

// The card animates in (`rise`: opacity + translateY(8px) -> 0), so a measurement taken while it is still
// Running reads an 8px offset - which is exactly what the first version of this spec reported as "the card
// Is not centred" and "the header moved".
async function openSettings(page) {
  await page.click('#btn-settings-toggle');
  await expect(page.locator('#win-settings')).toBeVisible();
  await page.evaluate(async () => {
    const card = document.getElementById('win-settings');
    await Promise.all(card.getAnimations({ subtree: true }).map((animation) => animation.finished));
  });
}

test('settings is a centred modal dialog, not a movable window', async ({ page }) => {
  await boot(page);
  await openSettings(page);

  const dialog = await page.evaluate(() => {
    const card = document.getElementById('win-settings');
    const header = card.querySelector('.modal-header');
    return {
      role: card.getAttribute('role'),
      ariaModal: card.getAttribute('aria-modal'),
      labelledBy: card.getAttribute('aria-labelledby'),
      describedBy: card.getAttribute('aria-describedby'),
      title: document.getElementById('settings-title').textContent.trim(),
      subtitleExists: !!document.getElementById(card.getAttribute('aria-describedby')),
      headerCursor: getComputedStyle(header).cursor,
      dragHandles: document.querySelectorAll('[data-drag-window="win-settings"]').length
    };
  });
  expect(dialog).toMatchObject({
    role: 'dialog',
    ariaModal: 'true', // the card is inside .modal-overlay, so this only makes the intent explicit
    title: 'Settings',
    headerCursor: 'auto', // was `grab`, which advertised a drag that no longer exists
    dragHandles: 0
  });
  expect(dialog.labelledBy).toBe('settings-title');
  expect(dialog.describedBy).toBe('settings-subtitle');
  expect(dialog.subtitleExists).toBe(true);

  const rect = await cardRect(page);
  const viewport = page.viewportSize();
  expect(Math.abs((rect.left + rect.right) / 2 - viewport.width / 2)).toBeLessThanOrEqual(2);
  expect(Math.abs((rect.top + rect.bottom) / 2 - viewport.height / 2)).toBeLessThanOrEqual(2);
  expect(rect.left).toBeGreaterThanOrEqual(0);
  expect(rect.top).toBeGreaterThanOrEqual(0);
  expect(rect.right).toBeLessThanOrEqual(viewport.width);
  expect(rect.bottom).toBeLessThanOrEqual(viewport.height);
});

test('dragging the header leaves the card where it is', async ({ page }) => {
  await boot(page);
  await openSettings(page);
  const before = await cardRect(page);

  const header = await page.locator('#win-settings .modal-header').boundingBox();
  await page.mouse.move(header.x + 40, header.y + 20);
  await page.mouse.down();
  await page.mouse.move(header.x + 240, header.y + 140, { steps: 8 });
  await page.mouse.up();
  await page.waitForTimeout(200);

  expect(await cardRect(page)).toEqual(before);
  // The utility window drag system is untouched: it keys off [data-drag-window], which Settings no longer has.
  expect(await page.evaluate(() => getComputedStyle(document.getElementById('win-settings')).transform)).toBe('none');
});
test('the body scrolls while the header and the footer stay put', async ({ page }) => {
  await boot(page, { width: 1200, height: 520 });
  await openSettings(page);

  const before = await page.evaluate(() => {
    const body = document.querySelector('#win-settings .modal-body');
    const head = document.querySelector('#win-settings .modal-header').getBoundingClientRect();
    const foot = document.querySelector('#win-settings .modal-footer').getBoundingClientRect();
    return {
      overflowing: body.scrollHeight > body.clientHeight + 8,
      headTop: Math.round(head.top),
      footBottom: Math.round(foot.bottom),
      cardBottom: Math.round(document.getElementById('win-settings').getBoundingClientRect().bottom)
    };
  });
  expect(before.overflowing, 'the tab body is the only scrolling region').toBe(true);
  expect(before.footBottom).toBeLessThanOrEqual(before.cardBottom + 1);

  await page.evaluate(() => {
    const body = document.querySelector('#win-settings .modal-body');
    body.scrollTop = body.scrollHeight;
  });
  await page.waitForTimeout(150);

  const after = await page.evaluate(() => {
    const head = document.querySelector('#win-settings .modal-header').getBoundingClientRect();
    const foot = document.querySelector('#win-settings .modal-footer').getBoundingClientRect();
    return { headTop: Math.round(head.top), footBottom: Math.round(foot.bottom) };
  });
  expect(after.headTop).toBe(before.headTop);
  expect(after.footBottom).toBe(before.footBottom);
});

test('Escape closes the dialog and gives focus back to the button that opened it', async ({ page }) => {
  await boot(page);
  await openSettings(page);
  await page.focus('#btn-close-settings'); // a real control inside the dialog, so the restore is meaningful

  await page.keyboard.press('Escape');
  await expect.poll(async () => page.evaluate(() =>
    getComputedStyle(document.getElementById('settings-modal')).display)).toBe('none');
  await expect.poll(async () => page.evaluate(() => document.activeElement.id)).toBe('btn-settings-toggle');
});

test('the seven workspace tools are one tabbed, non-modal window', async ({ page }) => {
  await boot(page);
  await page.evaluate(() => window.toggleMiniWindow('win-preview', true));
  await expect(page.locator('#win-preview')).toBeVisible();

  const tabs = await page.evaluate(() => {
    const nav = document.querySelector('#win-preview .utility-tabs');
    return {
      role: nav ? nav.getAttribute('role') : null,
      labels: Array.from(document.querySelectorAll('#win-preview .utility-tab')).map((t) => t.textContent.trim()),
      modalAttr: document.getElementById('win-preview').getAttribute('aria-modal'),
      dialogRole: document.getElementById('win-preview').getAttribute('role')
    };
  });
  expect(tabs.role).toBe('tablist');
  expect(tabs.labels).toEqual(['Preview', 'Sessions', 'Timeline', 'Playbooks']);
  // One window with tabs, and explicitly NOT modal: the editor has to stay usable while it is open.
  expect(tabs.dialogRole).toBe('dialog');
  expect(tabs.modalAttr).toBeNull();

  // Switching tools moves the same window instead of stacking a second one.
  await page.evaluate(() => window.toggleMiniWindow('win-history', true));
  await expect(page.locator('#win-preview')).toBeHidden();
  await expect(page.locator('#win-history')).toBeVisible();
  expect(await page.evaluate(() =>
    document.querySelectorAll('.mini-window:not([style*="display: none"])').length)).toBe(1);

  // Non-modal for real: the shell behind it still takes clicks and nothing is inert.
  expect(await page.evaluate(() => document.body.inert === true)).toBe(false);
  await openSettings(page);
});

test('a utility window cannot be dragged out of the viewport', async ({ page }) => {
  await boot(page);
  await page.evaluate(() => window.toggleMiniWindow('win-preview', true));
  await expect(page.locator('#win-preview')).toBeVisible();

  const header = await page.locator('#win-preview .mini-win-head').boundingBox();
  await page.mouse.move(header.x + 60, header.y + 12);
  await page.mouse.down();
  await page.mouse.move(header.x + 3000, header.y + 2000, { steps: 10 });
  await page.mouse.up();
  await page.waitForTimeout(250);

  const landed = await page.evaluate(() => {
    const r = document.getElementById('win-preview').getBoundingClientRect();
    return { left: Math.round(r.left), top: Math.round(r.top), right: Math.round(r.right), bottom: Math.round(r.bottom) };
  });
  const viewport = page.viewportSize();
  expect(landed.left).toBeGreaterThanOrEqual(0);
  expect(landed.top).toBeGreaterThanOrEqual(0);
  expect(landed.right).toBeLessThanOrEqual(viewport.width);
  expect(landed.bottom).toBeLessThanOrEqual(viewport.height);
});