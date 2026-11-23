import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';
const SHELL_SCRIPTS = [
  'console/registry.js',
  'console/commands.js',
  'console/parser.js',
  'console/core.js',
  'console/exec.js',
  'console/ui.js'
];

// VoidShell is the fallback console and it used to cost 20 script requests on every
// Start (icon pack, registry, 14 command files, parser, core, executor, UI). Now the icon pack is
// Modules/icons.js, the commands are one file, and nothing from console/ is requested until the
// Console view is actually in front. window.VoidConsole stays the call site for app.js,
// Modules/terminal.js and modules/git.js: it is a facade that queues until the scripts land.
const consoleScripts = (page) => page.evaluate(() =>
  Array.from(document.querySelectorAll('script[src]'))
    .map((s) => s.getAttribute('src'))
    .filter((src) => src.includes('console/')));
const logText = (page) => page.evaluate(() => document.getElementById('vc-log').textContent);

async function boot(page, width = 1440) {
  await page.setViewportSize({ width, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);
  await page.waitForTimeout(300);
}

test('shell: nothing from console/ at startup, and revealing the panel loads it once', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await boot(page);

  // The panel starts collapsed at this width, so the shell must not have been requested
  expect(await consoleScripts(page)).toEqual([]);
  expect(await page.evaluate(() => typeof window.VoidShell)).toBe('undefined');
  expect(await page.evaluate(() => window.VoidConsole && window.VoidConsole.lazy)).toBe(true);

  // The icon pack is NOT part of the shell - it moved to modules/icons.js and paints the rail now
  expect(await page.evaluate(() => window.VoidIcons.theme())).toBeTruthy();
  const railIcon = await page.evaluate(() =>
    document.querySelector('#activity-rail [data-icon] img')?.getAttribute('src') || '');
  expect(railIcon).toMatch(/^icons\/voidicons-[a-z-]+\/svg\/[a-z-]+\/(ui|muted)\/[a-z-]+\.svg$/);

  // Expanding the terminal is the signal: six scripts, in the registry-first order
  await page.evaluate(() => window.setTerminalCollapsed(false));
  await expect.poll(() => page.evaluate(() => typeof window.VoidShell?.core)).toBe('object');
  expect(await consoleScripts(page)).toEqual(SHELL_SCRIPTS);

  // The real UI replaced the facade and booted itself: banner, prompt, focused input
  expect(await page.evaluate(() => window.VoidConsole.lazy)).toBeFalsy();
  await expect(page.locator('#vc-log')).toContainText('void shell');
  await expect(page.locator('#vc-prompt')).toHaveText('void \u276f .');
  expect(await page.evaluate(() => document.activeElement?.id)).toBe('vc-input');

  // The merged command file registered all 14 built-ins
  const commands = await page.evaluate(() => window.VoidShell.list().map((c) => c.name));
  expect(commands).toEqual(['cat', 'cd', 'clear', 'echo', 'exit', 'help', 'history', 'ls', 'mkdir', 'open', 'pwd', 'rm', 'theme', 'touch']);

  // A second expand is a no-op - the loader is memoised
  await page.evaluate(() => window.setTerminalCollapsed(false));
  await page.waitForTimeout(300);
  expect(await consoleScripts(page)).toEqual(SHELL_SCRIPTS);

  expect(errors).toEqual([]);
});
test('shell: calls made through the facade before the scripts land are queued in order', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await boot(page);
  expect(await page.evaluate(() => typeof window.VoidShell)).toBe('undefined');

  // Exactly what modules/git.js does when there is no PTY session: print the command, then run it
  await page.evaluate(() => {
    window.VoidConsole.print('void \u276f echo hi', 'cmd');
    window.VoidConsole.execute('echo hi');
  });
  await expect.poll(() => page.evaluate(() => typeof window.VoidShell?.core)).toBe('object');
  await page.waitForTimeout(400);

  const first = await page.evaluate(() =>
    Array.from(document.querySelectorAll('#vc-log .vc-line')).map((l) => l.textContent));
  const printed = first.indexOf('void \u276f echo hi');
  const echoed = first.indexOf('hi');
  expect(printed).toBeGreaterThanOrEqual(0);
  expect(echoed).toBeGreaterThan(printed); // the queue kept print before execute

  // A built-in that only touches the shell: help lists the merged command set
  await page.evaluate(() => window.VoidConsole.execute('help'));
  await page.waitForTimeout(300);
  expect(await logText(page)).toContain('void shell - built-in commands');
  expect(await logText(page)).toContain('mkdir');

  // Pwd goes through the workspace path the shell resolved for itself
  await page.evaluate(() => window.VoidConsole.setWorkspace('C:\\proj'));
  await page.evaluate(() => window.VoidConsole.execute('pwd'));
  await page.waitForTimeout(300);
  expect((await logText(page)).trimEnd()).toContain('C:\\proj');

  // Clear empties the log through setHtml (the sanitised sink)
  await page.evaluate(() => window.VoidConsole.execute('clear'));
  await page.waitForTimeout(300);
  expect(await page.evaluate(() => document.getElementById('vc-log').childElementCount)).toBe(0);

  expect(errors).toEqual([]);
});

test('shell: an expanded panel at a wide viewport loads the console without any user action', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  // 1600px is the width where the bottom panel starts expanded (modules/layout.js), so the shell is
  // Genuinely in front and the layout seam must have loaded it during boot
  await boot(page, 1600);
  await expect.poll(() => page.evaluate(() => typeof window.VoidShell?.core)).toBe('object');
  expect(await consoleScripts(page)).toEqual(SHELL_SCRIPTS);
  await expect(page.locator('#vc-log')).toContainText('void shell');

  expect(errors).toEqual([]);
});