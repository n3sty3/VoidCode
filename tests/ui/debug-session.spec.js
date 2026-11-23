import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

const TREE = {
  name: 'proj',
  path: 'C:\\proj',
  isDirectory: true,
  children: [{ name: 'tool.js', path: 'C:\\proj\\tool.js', isDirectory: false }]
};

// It owns the breakpoint cache (app.js used to
// Write it from its bridge switch) and installs its debug_unsupported subscription from init().
// The unsupported-runtime path is already covered by debug.spec.js; this walks a real session.
test('debug session: start, breakpoints, paused frames, resume, stop', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  // Open a .js file: the Node/V8 path is the one that is genuinely supported
  await page.evaluate(([t]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree: t, sessions: [], checkpoints: [] }), [TREE]);
  await page.evaluate(() => window.__host('read_file_response',
    { path: 'C:\\proj\\tool.js', content: 'function main() {}\nmain();\n', metadata: { encoding: 'utf-8', eol: 'LF' } }));
  await page.waitForTimeout(1500);

  const startBtn = page.locator('#btn-sidebar-debug-start');
  await expect(startBtn).toBeEnabled();
  await expect(startBtn).toHaveAttribute('title', 'Start debugging (F5)');

  // Start -> debug_start for the active file (the controls live in the Debug sidebar view)
  await page.click('#tab-nav-debug');
  await page.waitForTimeout(400);
  await page.evaluate(() => { window.__sent.length = 0; });
  await startBtn.click();
  await page.waitForTimeout(400);
  const startMsg = await page.evaluate(() => window.__sent.find((m) => m.action === 'debug_start'));
  expect(startMsg).toBeTruthy();
  expect(startMsg.file).toBe('C:\\proj\\tool.js');
  await expect(page.locator('#debug-target-file')).toHaveText('tool.js');
  await expect(page.locator('#debug-console-output')).toContainText('[Debugger] Launching');

  // Session started: toolbar in, start button out
  await page.evaluate(() => window.__host('debug_started', { target: 'tool.js' }));
  await page.waitForTimeout(300);
  await expect(page.locator('#debug-floating-toolbar')).toBeVisible();
  await expect(startBtn).toBeHidden();
  await expect(page.locator('#btn-sidebar-debug-stop')).toBeVisible();

  // Breakpoints arrive from the host: the cache lives in the module now
  await page.evaluate(() => window.__host('debug_breakpoints_updated',
    { file: 'C:\\proj\\tool.js', breakpoints: [{ line: 5, condition: null, enabled: true }] }));
  await page.waitForTimeout(300);
  await expect(page.locator('#debug-bp-count')).toHaveText('1');
  await expect(page.locator('#debug-breakpoints-list')).toContainText('Ln 5');
  await expect(page.locator('#debug-breakpoints-list')).toContainText('tool.js');

  // Paused: call stack + variables, and the sidebar switches to the debug view
  await page.evaluate(() => window.__host('debug_paused', {
    frames: [{ Name: 'main', FilePath: 'C:\\proj\\tool.js', Line: 3, InternalFrameId: 7 }],
    variables: [{ Name: 'x', Value: '5', Type: 'number', VariablesReference: 0 }]
  }));
  await page.waitForTimeout(400);
  await expect(page.locator('#debug-callstack-list .debug-stack-item')).toHaveCount(1);
  await expect(page.locator('#debug-callstack-list')).toContainText('main');
  await expect(page.locator('#debug-variables-list')).toContainText('x');
  await expect(page.locator('#debug-variables-list')).toContainText('5');

  // Resumed: continue hidden, pause shown
  await page.evaluate(() => window.__host('debug_resumed', {}));
  await page.waitForTimeout(200);
  await expect(page.locator('#btn-dbg-continue')).toBeHidden();
  await expect(page.locator('#btn-dbg-pause')).toBeVisible();

  // Clearing every breakpoint
  await page.evaluate(() => window.__host('debug_breakpoints_updated', {}));
  await page.waitForTimeout(200);
  await expect(page.locator('#debug-bp-count')).toHaveText('0');
  await expect(page.locator('#debug-breakpoints-list')).toContainText('No breakpoints');

  // Stop: back to the idle state
  await page.evaluate(() => window.__host('debug_stopped', {}));
  await page.waitForTimeout(300);
  await expect(page.locator('#debug-floating-toolbar')).toBeHidden();
  await expect(startBtn).toBeVisible();
  await expect(page.locator('#debug-callstack-list')).toContainText('Not running');

  expect(errors).toEqual([]);
});