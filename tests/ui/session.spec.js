import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE, CODE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';
const WS = 'C:\\proj';
const CS_PATH = 'C:\\proj\\Program.cs';
// SessionKey() = 'voidcode_session_v1:' + normalizePath(ws), and normalizePath lowercases.
const SESSION_KEY = 'voidcode_session_v1:c:\\proj';

function watchErrors(page) {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  return errors;
}

async function boot(page, extra = {}) {
  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.evaluate(([tree, extraInit]) => window.__host('init_state_response',
    Object.assign({ settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }, extraInit)),
  [TREE, extra]);
}

async function openFile(page, path, content) {
  await page.evaluate(([p, c]) => window.__host('read_file_response',
    { path: p, content: c, metadata: { encoding: 'utf-8', eol: 'LF' } }), [path, content]);
  await page.waitForTimeout(600);
}

const sentActions = (page) => page.evaluate(() => window.__sent.map((m) => m.action));

// Session restore, crash recovery, safe close and external file changes moved to
// Modules/session.js. app.js keeps only the reload wrapper, which now feeds the module through two
// Seams: setRecoveryPayload() for the host's recovery payload and handlePendingCloseSaveResult() for
// Each save result. These tests exercise both seams and the flows behind them.
test('session: a dirty tab is persisted, and closing saves before confirming', async ({ page }) => {
  const errors = watchErrors(page);
  await boot(page);
  await openFile(page, CS_PATH, CODE);

  // Editing marks the tab dirty, and the 2s autosave tick writes the session + the recovery buffer
  await page.evaluate(() => window.VoidState.get('activeTab').model.setValue('// dirty\n'));
  await page.waitForTimeout(2400);

  const session = await page.evaluate(([key]) => JSON.parse(localStorage.getItem(key) || 'null'), [SESSION_KEY]);
  expect(session).toBeTruthy();
  expect(session.tabs).toEqual([CS_PATH]);
  expect(session.active).toBe(CS_PATH);

  const recovery = await page.evaluate(() =>
    window.__sent.filter((m) => m.action === 'save_recovery_state').pop() || null);
  expect(recovery).toBeTruthy();
  expect(recovery.state.buffers).toHaveLength(1);
  expect(recovery.state.buffers[0]).toMatchObject({ path: CS_PATH, content: '// dirty\n' });

  // The host asks to close; the dirty tab means we ask first
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.evaluate(() => window.__host('request_close', {}));
  await expect(page.locator('#input-dialog-modal')).toBeVisible();
  await expect(page.locator('#input-dialog-title')).toContainText('Unsaved changes');
  await expect(page.locator('#confirm-dialog-msg')).toContainText('1 file has unsaved changes');
  expect(await sentActions(page)).toContain('close_ack');

  // "Save all and close" sends the save and waits for the host's answer before confirming the close
  await page.click('#input-dialog-confirm');
  await page.waitForTimeout(300);
  const afterConfirm = await sentActions(page);
  expect(afterConfirm).toContain('save_file');
  expect(afterConfirm).not.toContain('confirm_close');

  await page.evaluate(([path]) => window.__host('save_file_response', { path }), [CS_PATH]);
  await page.waitForTimeout(400);
  expect(await sentActions(page)).toContain('confirm_close');
  await expect(page.locator('#input-dialog-modal')).toBeHidden();

  expect(errors).toEqual([]);
});

test('session: an unclean shutdown prompts once and restores the recovered buffers', async ({ page }) => {
  const errors = watchErrors(page);
  await boot(page, {
    recovery: {
      workspace: WS,
      activePath: CS_PATH,
      buffers: [{ path: CS_PATH, content: '// recovered\n', cursorLine: 2, cursorColumn: 3 }]
    }
  });

  // RestoreSessionForWorkspace() runs 350ms after init_state_response and asks before applying
  await expect(page.locator('#input-dialog-modal')).toBeVisible({ timeout: 5000 });
  await expect(page.locator('#input-dialog-title')).toContainText('Recover unsaved changes');
  await expect(page.locator('#input-dialog-alt')).toHaveText('Discard');
  await expect(page.locator('#input-dialog-confirm')).toHaveText('Restore');

  // "Restore" reopens the file through the host and keeps the recovered content
  await page.click('#input-dialog-confirm');
  await page.waitForTimeout(300);
  const asked = await page.evaluate(() =>
    window.__sent.filter((m) => m.action === 'read_file').map((m) => m.path));
  expect(asked).toContain(CS_PATH);
  expect(await sentActions(page)).not.toContain('clear_recovery_state');

  await page.evaluate(([path, content]) => window.__host('read_file_response', { path, content }), [CS_PATH, CODE]);
  await page.waitForTimeout(900);

  await expect(page.locator('#tabs-list .tab-item')).toHaveCount(1);
  const tab = await page.evaluate(() => {
    const active = window.VoidState.get('activeTab');
    return { path: active.path, value: active.model.getValue(), dirty: active.dirty };
  });
  expect(tab.path).toBe(CS_PATH);
  expect(tab.value).toBe('// recovered\n');
  expect(tab.dirty).toBe(true);
  await expect(page.locator('#toast-container .toast-item')).toContainText(
    'Recovered 1 unsaved file from the previous session.');

  expect(errors).toEqual([]);
});

test('session: external file changes reload a clean tab and prompt on a dirty one', async ({ page }) => {
  const errors = watchErrors(page);
  await boot(page);
  await openFile(page, CS_PATH, CODE);

  const value = () => page.evaluate(() => window.VoidState.get('activeTab').model.getValue());
  const dirty = () => page.evaluate(() => window.VoidState.get('activeTab').dirty);

  // A clean tab just reloads from disk, keeping the tab (and its state) in place
  await page.evaluate(([path]) => window.__host('file_changed',
    { path, content: '// from disk\n' }), [CS_PATH]);
  await page.waitForTimeout(500);
  expect(await value()).toBe('// from disk\n');
  expect(await dirty()).toBe(false);
  await expect(page.locator('#input-dialog-modal')).toBeHidden();
  await expect(page.locator('#tabs-list .tab-item')).toHaveCount(1);

  // An unchanged file is ignored: the guard compares against the current model value
  await page.evaluate(([path]) => window.__host('file_changed',
    { path, content: '// from disk\n' }), [CS_PATH]);
  await page.waitForTimeout(300);
  await expect(page.locator('#input-dialog-modal')).toBeHidden();

  // With unsaved edits the change conflicts, so we ask before throwing the edits away
  await page.evaluate(() => window.VoidState.get('activeTab').model.setValue('// mine\n'));
  await page.waitForTimeout(300);
  await page.evaluate(([path]) => window.__host('file_changed',
    { path, content: '// from disk 2\n' }), [CS_PATH]);
  await expect(page.locator('#input-dialog-modal')).toBeVisible();
  await expect(page.locator('#input-dialog-title')).toContainText('File changed on disk');
  await expect(page.locator('#input-dialog-confirm')).toHaveText('Reload from disk');

  await page.click('#input-dialog-confirm');
  await page.waitForTimeout(400);
  expect(await value()).toBe('// from disk 2\n');
  expect(await dirty()).toBe(false);
  await expect(page.locator('#input-dialog-modal')).toBeHidden();

  expect(errors).toEqual([]);
});