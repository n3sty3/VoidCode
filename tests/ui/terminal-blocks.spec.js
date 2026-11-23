import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

test('terminal OSC 133 command blocks capture exit codes, render modal, and rerun commands', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript(() => { if (window === window.top) localStorage.clear(); });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2200);

  // Open the terminal panel and ensure an xterm session exists, then feed two OSC 133 / 633 command blocks
  await page.evaluate(() => {
    window.setTerminalCollapsed?.(false);
    let sess = window.getActiveTerminalSession?.();
    if (!sess) {
      window.addTerminalTab?.('powershell');
      sess = window.getActiveTerminalSession?.();
    }
    const id = sess ? sess.id : 'term-1';
    const okStream = '\x1b]633;E;dotnet test\x07\x1b]133;C\x07Passed! 284/284\r\n\x1b]133;D;0\x07';
    const failStream = '\x1b]633;E;npm run lint\x07\x1b]133;C\x07Found 1 error\r\n\x1b]133;D;1\x07';
    window.__host('pty_output', { id, data: okStream + failStream });
  });

  const blocks = await page.evaluate(() => window.getTerminalCommandBlocks());
  expect(blocks.length).toBe(2);
  expect(blocks[0].command).toBe('dotnet test');
  expect(blocks[0].exitCode).toBe(0);
  expect(blocks[1].command).toBe('npm run lint');
  expect(blocks[1].exitCode).toBe(1);

  await page.evaluate(() => {
    window.__sent.length = 0;
    window.openTerminalBlocksModal();
  });

  const modal = page.locator('#terminal-blocks-modal');
  await expect(modal).toBeVisible();
  await expect(modal).toContainText('OK (0)');
  await expect(modal).toContainText('Exit 1');
  await expect(modal).toContainText('dotnet test');

  // Rerun "dotnet test" -> closes modal and writes command + CR to active PTY
  await page.click('[data-rerun-command="dotnet test"]');
  await expect(modal).toBeHidden();
  const rerunFrame = await page.evaluate(() => window.__sent.find(m => m.action === 'pty_input' && m.data === 'dotnet test\r'));
  expect(rerunFrame).toBeTruthy();
  expect(errors).toEqual([]);
});
