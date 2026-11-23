import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE, CODE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';
const CS_PATH = 'C:\\proj\\Program.cs';
const MD_PATH = 'C:\\proj\\notes.md';
const HELPER_PATH = 'C:\\proj\\Helper.cs';

// The LSP client lifecycle and the Monaco providers moved to
// Modules/lsp.js. The host here is the mock, so there is no language server - the two tests assert
// The seam instead: what the app sends on open/change/save/close, and that the providers are
// Registered and wired (a definition/hover request goes out, the mocked answer comes back and the
// Editor reacts).
const sentActions = (page) => page.evaluate(() => window.__sent.map((m) => m.action));
const sentOf = (page, action) => page.evaluate((a) => window.__sent.filter((m) => m.action === a), action);
const clearSent = (page) => page.evaluate(() => { window.__sent.length = 0; });
const lastRequestId = (page, action) => page.evaluate((a) => {
  const hits = window.__sent.filter((m) => m.action === a);
  return hits.length ? hits[hits.length - 1].id : null;
}, action);
const respond = (page, action, reqId, result) => page.evaluate(([a, id, r]) =>
  window.__host(a, { reqId: id, result: r }), [action, reqId, result]);

async function bootWorkspace(page) {
  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);
}

async function openFile(page, path, content) {
  await page.evaluate(([p, c]) => window.__host('read_file_response',
    { path: p, content: c, metadata: { encoding: 'utf-8', eol: 'LF' } }), [path, content]);
  await page.waitForTimeout(600);
}

test('lsp document sync: did_open, debounced did_change versions, did_save, did_close', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await bootWorkspace(page);

  // Opening a C# file announces the document with its language and full content
  await openFile(page, CS_PATH, CODE);
  const opens = await sentOf(page, 'lsp_did_open');
  expect(opens).toHaveLength(1);
  expect(opens[0].path).toBe(CS_PATH);
  expect(opens[0].languageId).toBe('csharp');
  expect(opens[0].content).toBe(CODE);

  // Two edits inside the debounce window collapse into one change, versioned from 1
  await clearSent(page);
  await page.evaluate(() => {
    const tab = window.VoidState.get('activeTab');
    tab.model.setValue('class A {}\n');
    tab.model.setValue('class B {}\n');
  });
  await page.waitForTimeout(600);
  let changes = await sentOf(page, 'lsp_did_change');
  expect(changes).toHaveLength(1);
  expect(changes[0]).toMatchObject({ path: CS_PATH, version: 2, content: 'class B {}\n' });

  // The next edit is version 3 - the version map survives the debounce
  await clearSent(page);
  await page.evaluate(() => window.VoidState.get('activeTab').model.setValue('class C {}\n'));
  await page.waitForTimeout(600);
  changes = await sentOf(page, 'lsp_did_change');
  expect(changes).toHaveLength(1);
  expect(changes[0]).toMatchObject({ version: 3, content: 'class C {}\n' });

  // A save notification, then a close notification when the tab goes away
  await clearSent(page);
  await page.evaluate(([path]) => window.__host('save_file_response', { path }), [CS_PATH]);
  await page.waitForTimeout(500);
  const saves = await sentOf(page, 'lsp_did_save');
  expect(saves).toHaveLength(1);
  expect(saves[0].path).toBe(CS_PATH);

  await clearSent(page);
  await page.locator('#tabs-list .tab-item.active .tab-close-btn').click();
  await page.waitForTimeout(500);
  await expect(page.locator('#tabs-list .tab-item')).toHaveCount(0);
  const closes = await sentOf(page, 'lsp_did_close');
  expect(closes).toHaveLength(1);
  expect(closes[0].path).toBe(CS_PATH);

  // Markdown has no language server, so opening it stays silent
  await clearSent(page);
  await openFile(page, MD_PATH, '# readme\n');
  expect(await sentActions(page)).not.toContain('lsp_did_open');
  expect(await sentActions(page)).not.toContain('lsp_did_change');

  expect(errors).toEqual([]);
});
test('lsp providers: definition jumps in-file and re-opens another file, hover renders the answer', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await bootWorkspace(page);
  await openFile(page, CS_PATH, CODE);

  // Go-to-definition from a known position; the provider is what sends the request. This Monaco build
  // Has no editor.action.revealDefinition, but showDefinitionPreviewHover (the peek variant) drives the
  // Same provider - provideDefinition still runs, which is what the assertions below need.
  const triggerDefinition = () => page.evaluate(() => {
    const ed = window.getMonacoEditor();
    ed.focus();
    ed.setPosition({ lineNumber: 6, column: 15 });
    ed.getAction('editor.action.showDefinitionPreviewHover').run();
  });
  const definitionRequests = () => sentActions(page).then((a) => a.filter((x) => x === 'lsp_definition').length);

  await clearSent(page);
  await triggerDefinition();
  await expect.poll(definitionRequests).toBeGreaterThan(0);
  const definitionId = await lastRequestId(page, 'lsp_definition');
  expect(definitionId).toBeTruthy();

  // In-file answer: the provider moves the cursor itself (the getMonacoEditor() adaptation)
  await respond(page, 'lsp_definition_response', definitionId, { path: CS_PATH, line: 4, column: 18 });
  await expect.poll(() => page.evaluate(() => window.getMonacoEditor().getPosition().lineNumber)).toBe(4);
  expect(await page.evaluate(() => window.getMonacoEditor().getPosition().column)).toBe(18);
  expect(await page.evaluate(() => window.VoidState.get('activeTab').path)).toBe(CS_PATH);

  // The peek widget resolves the uri the provider returns, so Monaco needs a model for the target
  // File: open it first (which is also the realistic case - the server points at project files).
  // Escape dismisses the peek from the in-file answer. The file must be long enough for line 3 to
  // Exist - Monaco clamps a position past the end.
  await page.keyboard.press('Escape');
  await openFile(page, HELPER_PATH, '// helper\n\nclass Helper {}\n');
  await page.locator('#tabs-list .tab-item', { hasText: 'Program.cs' }).click();
  await page.waitForTimeout(400);
  expect(await page.evaluate(() => window.VoidState.get('activeTab').path)).toBe(CS_PATH);

  // Cross-file answer: the app asks the host for the file and remembers where to land
  await clearSent(page);
  await triggerDefinition();
  await expect.poll(definitionRequests).toBeGreaterThan(0);
  const crossId = await lastRequestId(page, 'lsp_definition');
  await respond(page, 'lsp_definition_response', crossId, { path: HELPER_PATH, line: 3, column: 5 });
  await expect.poll(() => sentActions(page).then((a) => a.filter((x) => x === 'read_file').length)).toBeGreaterThan(0);
  const readFiles = await sentOf(page, 'read_file');
  expect(readFiles.every((m) => m.path === HELPER_PATH)).toBe(true);

  // Once the host delivers it the other tab is active and the cursor lands on the remembered position
  await openFile(page, HELPER_PATH, '// helper\n\nclass Helper {}\n');
  await expect.poll(() => page.evaluate(() => window.VoidState.get('activeTab').path)).toBe(HELPER_PATH);
  await expect.poll(() => page.evaluate(() => window.getMonacoEditor().getPosition().lineNumber)).toBe(3);
  expect(await page.evaluate(() => window.getMonacoEditor().getPosition().column)).toBe(5);
  await page.keyboard.press('Escape');

  // Hover: the provider asks, the mocked answer comes back as plain content
  await clearSent(page);
  await page.evaluate(() => {
    const ed = window.getMonacoEditor();
    ed.focus();
    ed.setPosition({ lineNumber: 1, column: 8 });
    ed.getAction('editor.action.showHover').run();
  });
  await expect.poll(() => sentActions(page).then((a) => a.filter((x) => x === 'lsp_hover').length)).toBeGreaterThan(0);
  const hoverId = await lastRequestId(page, 'lsp_hover');
  await respond(page, 'lsp_hover_response', hoverId, { value: 'class Helper' });
  await expect(page.locator('.monaco-hover')).toContainText('class Helper');

  expect(errors).toEqual([]);
});