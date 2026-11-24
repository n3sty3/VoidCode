import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE, CODE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

const TEST_CS = `using Xunit;

namespace Demo.Tests;

public class CalculatorTests
{
    [Fact]
    public void AddsNumbers()
    {
        Assert.Equal(4, 2 + 2);
    }

    [Theory]
    [InlineData(1, 2)]
    public void MultipliesNumbers(int a, int b)
    {
        Assert.Equal(a * b, b * a);
    }
}
`;

const SAMPLE_HTML = `<!DOCTYPE html>
<html>
<head><title>Preview Demo</title></head>
<body>
  <h1>Hello VoidCode Preview</h1>
  <script>console.error('Boom from preview runtime');</script>
</body>
</html>`;

async function bootWithWorkspace(page) {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => {
    if (m.type() === 'error' && !m.text().includes('Boom from preview runtime')) errors.push(m.text());
  });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript(() => { if (window === window.top) localStorage.clear(); });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2200);
  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);
  await page.waitForTimeout(300);
  return errors;
}

test('native tool tabs: SQLite, API Client, Git Graph and docked Settings live in editor tab bar', async ({ page }) => {
  const errors = await bootWithWorkspace(page);

  // Open a regular file tab first
  await page.evaluate(([code]) => window.__host('read_file_response',
    { path: 'C:\\proj\\Program.cs', content: code, metadata: { encoding: 'utf-8', eol: 'LF' } }), [CODE]);
  await page.waitForTimeout(400);
  await expect(page.locator('#tabs-list .tab-item')).toHaveCount(1);

  // Open API Client as native tab (not blocking overlay)
  await page.evaluate(() => window.openApiClientModal());
  await expect(page.locator('#editor-tool-pane')).toBeVisible();
  await expect(page.locator('#api-client-modal')).toBeVisible();
  await expect(page.locator('#tabs-list .tab-item--tool')).toHaveCount(1);
  await expect(page.locator('#tabs-list .tab-item.active')).toContainText('API Client');

  // Switch back to code tab -> Monaco returns, tool pane hides
  await page.locator('#tabs-list .tab-item', { hasText: 'Program.cs' }).click();
  await expect(page.locator('#monaco-mount')).toBeVisible();
  await expect(page.locator('#editor-tool-pane')).toBeHidden();

  // Dock Settings as a native tab and confirm surrounding IDE is NOT inert
  await page.keyboard.press('Control+,');
  await expect(page.locator('#settings-modal')).toBeVisible();
  await page.click('#btn-dock-settings-tab');
  await expect(page.locator('#editor-tool-pane #settings-modal')).toBeVisible();
  await expect(page.locator('#tabs-list .tab-item.active')).toContainText('Settings');
  const tabsInert = await page.evaluate(() => !!document.getElementById('tabs-list')?.closest('[inert]'));
  expect(tabsInert).toBe(false);

  // Close docked Settings tab -> modal cleanly returns to original hidden host
  await page.locator('#tabs-list .tab-item.active .tab-close-btn').click();
  await expect(page.locator('#editor-tool-pane #settings-modal')).toHaveCount(0);
  await expect(page.locator('#settings-modal')).toBeHidden();

  // Close API Client tab
  await page.locator('#tabs-list .tab-item', { hasText: 'API Client' }).locator('.tab-close-btn').click();
  await expect(page.locator('#tabs-list .tab-item')).toHaveCount(1);

  expect(errors).toEqual([]);
});

test('live web preview tab: renders active HTML, switches viewports, captures console errors and forwards to AI', async ({ page }) => {
  const errors = await bootWithWorkspace(page);

  await page.evaluate(([doc]) => window.__host('read_file_response',
    { path: 'C:\\proj\\index.html', content: doc, metadata: { encoding: 'utf-8', eol: 'LF' } }), [SAMPLE_HTML]);
  await page.waitForTimeout(400);

  // Click the Smart Context Action "Live preview" button
  await page.locator('#editor-context-actions button', { hasText: 'Live preview' }).click();
  await expect(page.locator('#web-preview-tab')).toBeVisible();
  await expect(page.locator('#tabs-list .tab-item.active')).toContainText('Preview');

  // Switch viewports
  await page.click('[data-wp-vp="mobile"]');
  await expect(page.locator('#wp-stage')).toHaveAttribute('data-viewport', 'mobile');
  await page.click('[data-wp-vp="tablet"]');
  await expect(page.locator('#wp-stage')).toHaveAttribute('data-viewport', 'tablet');

  // Runtime console.error from preview iframe is captured into badge + console drawer
  await expect(page.locator('#wp-err-badge')).toBeVisible({ timeout: 4000 });
  await page.click('#btn-wp-console');
  await expect(page.locator('#wp-console-drawer')).toBeVisible();
  await expect(page.locator('#wp-console-list')).toContainText('Boom from preview runtime');

  // Click Fix in AI -> populates VoidAgent composer
  await page.click('#btn-wp-send-ai');
  await expect(page.locator('#agent-input-text')).toHaveValue(/Diagnose and fix the runtime errors/i);

  expect(errors).toEqual([]);
});

test('in-editor gutter test runner: detects C# tests, runs single test without breakpoint, updates pass/fail badge and opens popover', async ({ page }) => {
  const errors = await bootWithWorkspace(page);

  await page.evaluate(([src]) => window.__host('read_file_response',
    { path: 'C:\\proj\\CalculatorTests.cs', content: src, metadata: { encoding: 'utf-8', eol: 'LF' } }), [TEST_CS]);
  await page.waitForTimeout(600);

  // Context action pill shows 2 detected tests
  await expect(page.locator('#btn-run-file-tests')).toContainText('Run 2 tests');

  // Clicking line 8 (AddsNumbers) runs single test via run_tests { filter } and does NOT send debug_toggle_breakpoint
  await page.evaluate(() => {
    window.__sent.length = 0;
    window.toggleEditorBreakpoint(8);
  });
  const sentActions = await page.evaluate(() => window.__sent.slice());
  expect(sentActions.some((m) => m.action === 'run_tests' && m.filter === 'CalculatorTests.AddsNumbers')).toBe(true);
  expect(sentActions.some((m) => m.action === 'debug_toggle_breakpoint')).toBe(false);

  // Host completes with 1 failed test -> clicking line 8 now opens result popover with Fix with AI
  await page.evaluate(() => window.__host('test_run_completed', {
    runId: 'r1',
    runner: 'dotnet test',
    total: 1,
    passed: 0,
    failed: 1,
    durationMs: 420,
    tests: [{ name: 'Demo.Tests.CalculatorTests.AddsNumbers', status: 'failed', duration: '12ms', error: 'Expected 5, got 4' }]
  }));
  await page.waitForTimeout(200);
  await page.evaluate(() => window.toggleEditorBreakpoint(8));
  await expect(page.locator('#test-gutter-popover')).toBeVisible();
  await expect(page.locator('#test-gutter-popover')).toContainText('FAILED');
  await expect(page.locator('#test-gutter-popover')).toContainText('Expected 5, got 4');

  // Click Fix with AI inside popover
  await page.click('#test-gutter-popover [data-tg-act="fix-ai"]');
  await expect(page.locator('#agent-input-text')).toHaveValue(/AddsNumbers/);

  expect(errors).toEqual([]);
});
