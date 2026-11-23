import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

// The accessibility pass, measured instead of eyeballed. Each test corresponds to one
// Line of the audit, and each one failed against the app as it was before this slice - the hub tiles
// Were <div role="button">, the run-config chevron was 20x20, the panel dividers were 8-10px of hit
// Area, the visible half of a select had no name and offered no Home/End, and the native <select> it
// Hides stayed in the tab order as a 1x1 invisible stop.
const URL = 'http://localhost:8765/index.html';

async function boot(page) {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => { try { window.localStorage.clear(); } catch { /* storage blocked */ } });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);
  await page.waitForTimeout(500);
}

// A target counts as big enough when a 23x23 box centred on it lands on the control. That is one pixel
// Inside 24 so a decimal offset cannot flip the answer, and it means the *hit area* is measured, not the
// Visible box, which is why a control with a transparent wrapper still passes.
//
// [role="separator"] is excluded on purpose: the pane dividers are 8-10px wide, and widening their hit
// Area with a ::before covered the right edge of every file-tree row and the corners of the pane-head
// Buttons (measured, then reverted - see the comment in layout.css). They are exempt under the
// "equivalent" clause instead, and the next test proves the equivalent input exists.
async function smallTargets(page, root = 'body') {
  return page.evaluate((scope) => {
    const host = document.querySelector(scope) || document.body;
    const selector = 'button, [role="button"], a[href], select, input:not([type="hidden"]), textarea,'
      + ' [role="tab"], [role="option"]';
    const offenders = [];
    for (const el of host.querySelectorAll(selector)) {
      if (el.closest('[aria-hidden="true"]') || el.tabIndex < 0) continue;
      const box = el.getBoundingClientRect();
      const style = getComputedStyle(el);
      if (style.display === 'none' || style.visibility === 'hidden' || !box.width || !box.height) continue;
      // Offscreen controls cannot be probed with elementFromPoint; they are not what the audit measured.
      if (box.top < 0 || box.left < 0 || box.bottom > innerHeight || box.right > innerWidth) continue;
      const cx = box.left + box.width / 2;
      const cy = box.top + box.height / 2;
      const corners = [[-11.5, -11.5], [11.5, -11.5], [-11.5, 11.5], [11.5, 11.5]];
      const covered = corners.every(([dx, dy]) => {
        const hit = document.elementFromPoint(cx + dx, cy + dy);
        return !!hit && (hit === el || el.contains(hit) || hit.contains(el));
      });
      if (!covered) {
        offenders.push(`${el.tagName.toLowerCase()}#${el.id || '-'} ${Math.round(box.width)}x${Math.round(box.height)}`);
      }
    }
    return offenders;
  }, root);
}

test('every interactive target reaches 24px', async ({ page }) => {
  await boot(page);
  expect(await smallTargets(page), 'shell').toEqual([]);

  await page.click('#btn-settings-toggle');
  await page.waitForTimeout(300);
  // Scoped to the dialog: the shell behind the overlay is not reachable while it is open, and every
  // ElementFromPoint probe up there returns the overlay.
  expect(await smallTargets(page, '#settings-modal'), 'settings dialog').toEqual([]);
});

test('the hub action tiles are real buttons and Enter activates them', async ({ page }) => {
  await boot(page);
  await page.evaluate(() => window.openProjectHub());
  await expect(page.locator('#project-hub')).toBeVisible();

  // <div role="button" tabindex="0"> was the old shape, with a hand-written Enter/Space handler.
  const tags = await page.evaluate(() => ['btn-hub-open-pc', 'btn-hub-new-project', 'btn-hub-customize']
    .map((id) => document.getElementById(id).tagName));
  expect(tags).toEqual(['BUTTON', 'BUTTON', 'BUTTON']);

  // A <button> does not stretch like a <div>: the column is a flex container so it does, and this keeps
  // The conversion from quietly shrinking the tiles to their text.
  const geometry = await page.evaluate(() => {
    const tile = document.getElementById('btn-hub-open-pc').getBoundingClientRect();
    const column = document.querySelector('.hub-actions-col').getBoundingClientRect();
    return { width: Math.round(tile.width), column: Math.round(column.width - 24), height: Math.round(tile.height) };
  });
  expect(geometry.height).toBe(32);
  expect(Math.abs(geometry.width - geometry.column)).toBeLessThanOrEqual(1);

  await page.evaluate(() => { window.__sent.length = 0; });
  await page.focus('#btn-hub-open-pc');
  await page.keyboard.press('Enter');
  // Exactly one message: the platform fires click on Enter, so a keydown shim would double it.
  await expect.poll(async () => page.evaluate(() =>
    window.__sent.filter((m) => m.action === 'open_workspace_dialog').length)).toBe(1);
});

test('settings tabs keep the tab semantics that make them reachable', async ({ page }) => {
  await boot(page);
  await page.click('#btn-settings-toggle');

  const tablist = await page.evaluate(() => Array.from(document.querySelectorAll('#settings-modal .settings-tab-btn'))
    .map((tab) => {
      const pane = document.getElementById(tab.getAttribute('aria-controls'));
      return {
        id: tab.id,
        role: tab.getAttribute('role'),
        selected: tab.getAttribute('aria-selected'),
        tabIndex: tab.tabIndex,
        paneRole: pane?.getAttribute('role'),
        paneLabelledBy: pane?.getAttribute('aria-labelledby')
      };
    }));
  expect(tablist.length).toBeGreaterThan(1);
  for (const [index, tab] of tablist.entries()) {
    expect(tab.role).toBe('tab');
    expect(tab.tabIndex).toBe(index === 0 ? 0 : -1); // roving tabindex: one stop for the whole strip
    expect(tab.selected).toBe(index === 0 ? 'true' : 'false');
    expect(tab.paneRole).toBe('tabpanel');
    expect(tab.paneLabelledBy).toBe(tab.id);
  }

  // Roving tabindex is only safe because the arrows move between tabs; without them four tabs would be
  // Keyboard-unreachable.
  await page.focus('#settings-tab-provider');
  await page.keyboard.press('ArrowRight');
  const afterArrow = await page.evaluate(() => ({
    focused: document.activeElement.id,
    selected: document.querySelector('#settings-modal .settings-tab-btn[aria-selected="true"]').id
  }));
  expect(afterArrow).toEqual({ focused: 'settings-tab-prompt', selected: 'settings-tab-prompt' });
});

test('the pane dividers are focusable separators that resize with the arrow keys', async ({ page }) => {
  await boot(page);
  const divider = await page.evaluate(() => {
    const el = document.getElementById('resizer-left');
    return {
      role: el.getAttribute('role'),
      orientation: el.getAttribute('aria-orientation'),
      min: el.getAttribute('aria-valuemin'),
      max: el.getAttribute('aria-valuemax'),
      tabIndex: el.tabIndex,
      label: el.getAttribute('aria-label')
    };
  });
  expect(divider).toMatchObject({ role: 'separator', orientation: 'vertical', tabIndex: 0 });
  expect(divider.label).toBeTruthy();
  expect(Number(divider.min)).toBeLessThan(Number(divider.max));

  const widthOf = () => page.evaluate(() =>
    Math.round(document.getElementById('sidebar-explorer').getBoundingClientRect().width));
  const before = await widthOf();
  await page.focus('#resizer-left');
  await page.keyboard.press('ArrowRight');
  expect(await widthOf()).toBe(before + 16);
});

test('the themed select is one named control, not an invisible second one', async ({ page }) => {
  await boot(page);
  await page.click('#btn-settings-toggle');

  const select = await page.evaluate(() => {
    const source = document.getElementById('settings-provider');
    const wrap = source.parentElement;
    const trigger = wrap.querySelector('.custom-select-trigger');
    const menu = wrap.querySelector('.custom-select-menu');
    return {
      sourceTabIndex: source.tabIndex,
      sourceAriaHidden: source.getAttribute('aria-hidden'),
      sourceOpacity: getComputedStyle(source).opacity,
      sourcePointerEvents: getComputedStyle(source).pointerEvents,
      triggerName: trigger.getAttribute('aria-label') || '',
      triggerExpanded: trigger.getAttribute('aria-expanded'),
      controlsMenu: trigger.getAttribute('aria-controls') === menu.id && !!menu.id,
      menuRole: menu.getAttribute('role')
    };
  });
  // The <label> is the only name this select has; the trigger used to copy aria-label only, so the
  // Control a user actually presses was announced as a nameless button.
  expect(select.triggerName.length).toBeGreaterThan(0);
  expect(select.controlsMenu).toBe(true);
  expect(select.menuRole).toBe('listbox');
  expect(select.triggerExpanded).toBe('false');
  // The native select is the value store, not a control: out of the tab order and out of the tree. Its
  // 1px CSS width is a lie Chromium tells (a <select> keeps a ~25px intrinsic minimum), so invisibility
  // Is what gets asserted rather than size.
  expect(select.sourceTabIndex).toBe(-1);
  expect(select.sourceAriaHidden).toBe('true');
  expect(select.sourceOpacity).toBe('0');
  expect(select.sourcePointerEvents).toBe('none');

  await page.focus('#settings-provider + .custom-select-trigger');
  await page.keyboard.press('Enter');
  await expect.poll(async () => page.evaluate(() =>
    document.querySelector('#settings-provider + .custom-select-trigger')
      .getAttribute('aria-expanded'))).toBe('true');

  const options = await page.evaluate(() => {
    const items = Array.from(document.querySelectorAll(
      '.custom-select-wrap.is-open .custom-select-option'));
    return { first: items[0].textContent.trim(), last: items[items.length - 1].textContent.trim(), total: items.length };
  });
  expect(options.total).toBeGreaterThan(2);

  // Home/End were the two listbox keys missing from the keydown map.
  await page.keyboard.press('End');
  expect(await page.evaluate(() => document.activeElement.textContent.trim())).toBe(options.last);
  await page.keyboard.press('Home');
  expect(await page.evaluate(() => document.activeElement.textContent.trim())).toBe(options.first);

  await page.keyboard.press('Escape');
  const afterOneEscape = await page.evaluate(() => ({
    onTrigger: document.activeElement.classList.contains('custom-select-trigger'),
    expanded: document.activeElement.getAttribute('aria-expanded'),
    dialogOpen: getComputedStyle(document.getElementById('settings-modal')).display !== 'none'
  }));
  // One Escape steps out one layer. Before this slice a capture-phase window handler closed the whole
  // Dialog and dropped focus on a rail button while the listbox was the thing the user was looking at.
  expect(afterOneEscape).toEqual({ onTrigger: true, expanded: 'false', dialogOpen: true });

  await page.keyboard.press('Escape');
  await expect.poll(async () => page.evaluate(() =>
    getComputedStyle(document.getElementById('settings-modal')).display)).toBe('none');
});