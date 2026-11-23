import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

// And it took over the pty_output/pty_exit
// Bridge cases plus the terminal readers app.js used to own. app.js no longer touches
// TerminalSessions at all - it goes through sendToActiveTerminal/killActiveTerminal/getActiveTerminalText.
test('terminal: tabs, pty output, send helper, exit banner and kill', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  const before = await page.locator('#terminal-tabs-list .term-tab-label').count();

  // The panel starts collapsed, so the tab strip has to be revealed first
  await page.evaluate(() => window.setTerminalCollapsed?.(false));
  await page.waitForTimeout(500);

  // The strip's add button only becomes visible once a second session exists, so this drives the
  // Same entry point the button and the Ctrl+Shift+` command call.
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.evaluate(() => window.addTerminalTab('powershell'));
  await page.waitForTimeout(600);
  const after = await page.locator('#terminal-tabs-list .term-tab-label').count();
  expect(after).toBeGreaterThan(before);
  const init = await page.evaluate(() => window.__sent.find((m) => m.action === 'pty_init'));
  expect(init).toBeTruthy();

  // The send helper types into the live session (app.js's console + smart run use it)
  const sentInfo = await page.evaluate(() => {
    window.__sent.length = 0;
    const ok = window.sendToActiveTerminal('echo hi', true);
    const frame = window.__sent.find((m) => m.action === 'pty_input');
    return { ok, data: frame && frame.data };
  });
  expect(sentInfo.ok).toBe(true);
  expect(sentInfo.data).toBe('echo hi\r');

  // Pty_output goes through the module and lands in the xterm buffer; getActiveTerminalText reads it
  const activeId = await page.evaluate(() => window.getActiveTerminalSession()?.id);
  expect(activeId).toBeTruthy();
  await page.evaluate(([id]) => window.handlePtyOutput({ id, data: 'HELLO-PTY\r\n' }), [activeId]);
  await page.waitForTimeout(400);
  expect(await page.evaluate(() => window.getActiveTerminalText())).toContain('HELLO-PTY');

  // Pty_exit prints the marker and hands the exit code to the run/AI path (modules/run-debug.js)
  await page.evaluate(([id]) => window.handlePtyExit({ id, exitCode: 3 }), [activeId]);
  await page.waitForTimeout(400);
  // The xterm buffer wraps at the harness pane width, so compare with whitespace removed
  const bufferFlat = await page.evaluate(() => window.getActiveTerminalText().replace(/\s+/g, ''));
  expect(bufferFlat).toContain('Processexitedwithcode3');
  await expect(page.locator('#terminal-ai-banner')).toBeVisible();
  await expect(page.locator('#terminal-ai-error-snippet')).toContainText('code 3');

  // Kill goes through the module's own session id
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.evaluate(() => window.killActiveTerminal());
  await page.waitForTimeout(300);
  const kill = await page.evaluate(() => window.__sent.find((m) => m.action === 'kill_terminal'));
  expect(kill).toBeTruthy();
  expect(kill.id).toBe(activeId);

  // A theme change restains the PTY palette through the module export
  const themed = await page.evaluate(() => {
    window.applyThemeAndOptics('emerald', 18, 60);
    return { theme: document.documentElement.getAttribute('data-theme'), configured: typeof window.refreshTerminalThemes };
  });
  expect(themed.theme).toBe('emerald');
  expect(themed.configured).toBe('function');

  expect(errors).toEqual([]);
});