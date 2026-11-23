import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

test('environment health modal renders capabilities and runs managed LSP install flow', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript(() => { if (window === window.top) localStorage.clear(); });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2200);

  await page.evaluate(() => {
    window.__sent.length = 0;
    window.openHealthModal();
  });

  await expect(page.locator('#health-modal')).toBeVisible();
  expect(await page.evaluate(() => window.__sent.some(m => m.action === 'get_capabilities'))).toBe(true);

  await page.evaluate(() => {
    window.__host('capabilities_response', {
      debug: { node: true, dotnet: false, python: false },
      lsp: [
        { language: 'typescript', serverName: 'typescript-language-server', found: false, path: null, installCommand: 'npm install --prefix tools typescript typescript-language-server', prerequisite: 'npm', prerequisiteFound: true },
        { language: 'python', serverName: 'pyright', found: true, path: 'C:\\tools\\pyright-langserver.cmd', installCommand: 'npm install pyright', prerequisite: 'npm', prerequisiteFound: true }
      ],
      git: { name: 'Git', found: false, path: null, installHint: 'winget install Git.Git' },
      ripgrep: { name: 'ripgrep (rg)', found: true, path: 'C:\\tools\\rg.exe', installHint: '' },
      runtimes: [{ name: 'Node.js', found: true, path: 'C:\\Program Files\\nodejs\\node.exe' }],
      shells: [{ name: 'Windows PowerShell', found: true, path: 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe' }],
      provider: { configured: true, provider: 'anthropic', model: 'claude-sonnet-4-20250514', contextWindow: 200000, toolsSupported: true },
      workspaceTrust: 'trusted'
    });
  });

  const body = page.locator('#health-modal-body');
  await expect(body).toContainText('claude-sonnet-4-20250514');
  await expect(body).toContainText('Fast (rg)');
  await expect(body).toContainText('Unsupported');

  // Click Install on missing typescript LSP -> confirm modal -> sends install_lsp_server
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click('[data-install-lsp="typescript"]');
  await page.click('#input-dialog-confirm');

  const installMsg = await page.evaluate(() => window.__sent.find(m => m.action === 'install_lsp_server'));
  expect(installMsg).toMatchObject({ language: 'typescript' });

  // Stream install output and completion -> sends lsp_restart
  await page.evaluate(() => {
    window.__host('lsp_install_progress', { language: 'typescript', line: 'added 2 packages in 1s' });
    window.__host('lsp_install_finished', { language: 'typescript', success: true, message: 'Installed typescript-language-server cleanly.' });
  });

  await expect(page.locator('#health-install-log')).toContainText('added 2 packages in 1s');
  expect(await page.evaluate(() => window.__sent.some(m => m.action === 'lsp_restart'))).toBe(true);

  await page.keyboard.press('Escape');
  await expect(page.locator('#health-modal')).toBeHidden();
  expect(errors).toEqual([]);
});
