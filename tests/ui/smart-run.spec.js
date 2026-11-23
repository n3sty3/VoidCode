import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE, CODE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

// And it absorbed the terminal "fix with
// AI" banner cluster from app.js (the cluster reads the module's run state). This walks the whole
// F5 path: detected config -> Run -> captured error output -> banner -> AI prompt, and it is the
// Test that would have caught executeTerminalCommand being unreachable from the module.
test('smart run detects a config, runs it, and the AI banner uses the captured output', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  // An open tab is what enables Run, so seed a workspace + one file
  await page.evaluate(([t]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree: t, sessions: [], checkpoints: [] }), [TREE]);
  await page.evaluate(([c]) => window.__host('read_file_response',
    { path: 'C:\\proj\\Program.cs', content: c, metadata: { encoding: 'utf-8', eol: 'LF' } }), [CODE]);
  await page.waitForTimeout(1500);

  await page.evaluate(() => window.__host('run_config_detected', {
    config: {
      Command: 'dotnet run',
      Label: 'Run C#',
      RuntimeType: 'dotnet',
      Alternatives: [{ Label: 'Debug', Command: 'dotnet run --debug' }]
    }
  }));
  await page.waitForTimeout(300);

  await expect(page.locator('#smart-run-label')).toHaveText('Run C#');
  await expect(page.locator('#btn-smart-run')).toBeEnabled();
  const options = page.locator('#smart-run-alternatives .run-option-item');
  await expect(options).toHaveCount(1);
  await expect(options.first()).toContainText('Debug');

  // Run: the module must reach app.js's executeTerminalCommand through the published helper
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click('#btn-smart-run');
  await page.waitForTimeout(500);
  await expect(page.locator('#smart-run-label')).toHaveText('Stop');
  await expect(page.locator('#smart-run-group')).toHaveClass(/is-running/);

  // Captured error output, then the exit code: the banner is built from the module's own state
  await page.evaluate(() => window.__host('terminal_stream',
    { text: 'error CS0103: The name x does not exist\n', isError: true }));
  await page.evaluate(() => window.__host('terminal_command_done', { exitCode: 1 }));
  await page.waitForTimeout(300);

  const banner = page.locator('#terminal-ai-banner');
  await expect(banner).toBeVisible();
  await expect(page.locator('#terminal-ai-error-snippet')).toContainText('CS0103');
  await expect(page.locator('#terminal-ai-error-snippet')).toContainText('code 1');
  // The run finished, so the button goes back to the detected label
  await expect(page.locator('#smart-run-label')).toHaveText('Run C#');

  // The palette entry ("Explain Last Terminal Error with AI") builds the prompt from that state and
  // Sends it: the composer is cleared on send, so assert the outgoing frame instead
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.evaluate(() => window.openAgentWithTerminalError(false));
  await page.waitForTimeout(600);
  const startMsg = await page.evaluate(() => window.__sent.find((m) => m.action === 'start_agent'));
  expect(startMsg).toBeTruthy();
  expect(startMsg.prompt).toContain('dotnet run');
  expect(startMsg.prompt).toContain('exit code 1');
  expect(startMsg.prompt).toContain('CS0103');
  await expect(banner).toBeHidden();

  expect(errors).toEqual([]);
});