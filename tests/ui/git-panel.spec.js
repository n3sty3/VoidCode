import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

// It owns the git status that app.js's agent
// Context and terminal.js's console prompt used to read as a global - both now go through
// GetGitStatus(). This drives the panel through the bridge.
test('git panel: status, stage/unstage, diff, and the accessor used by other modules', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);

  await page.click('#tab-nav-git'); // the panel lives in the Source control view
  await page.waitForTimeout(400);

  await page.evaluate(() => window.__host('git_status_response', {
    isAvailable: true,
    isRepo: true,
    branch: 'main',
    stagedChanges: [{ path: 'src/a.js', status: 'M' }],
    workingChanges: [{ path: 'src/b.js', status: 'M' }]
  }));
  await page.waitForTimeout(400);

  await expect(page.locator('#git-branch-label')).toHaveText('main');
  await expect(page.locator('#status-branch-name')).toHaveText('main');
  await expect(page.locator('#git-badge-count')).toHaveText('2');
  await expect(page.locator('#git-staged-count')).toHaveText('1');
  await expect(page.locator('#git-working-count')).toHaveText('1');
  await expect(page.locator('#git-staged-list .git-file-row')).toHaveCount(1);
  await expect(page.locator('#git-working-list .git-file-row')).toHaveCount(1);

  // The accessor other modules use
  expect(await page.evaluate(() => window.getGitStatus()?.branch)).toBe('main');
  await page.evaluate(() => window.updateConsoleStatus?.());
  await expect(page.locator('#console-chip-branch')).toHaveText('main');

  // Stage / unstage round trip
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.locator('#git-working-list .btn-stage-file').first().click();
  await page.locator('#git-staged-list .btn-unstage-file').first().click();
  await page.waitForTimeout(300);
  const sent = await page.evaluate(() => window.__sent);
  expect(sent.find((m) => m.action === 'git_stage_file')?.path).toBe('src/b.js');
  expect(sent.find((m) => m.action === 'git_unstage_file')?.path).toBe('src/a.js');

  // HEAD diff mounts the monaco diff editor and closes again
  await page.evaluate(() => window.__host('git_diff_response', {
    success: true, path: 'src/b.js', staged: false, headContent: 'let a = 1;\n', currentContent: 'let a = 2;\n'
  }));
  await page.waitForTimeout(800);
  await expect(page.locator('#monaco-diff-mount')).toBeVisible();
  await page.evaluate(() => window.closeDiffEditor());
  await page.waitForTimeout(300);
  await expect(page.locator('#monaco-diff-mount')).toBeHidden();

  // A clean tree
  await page.evaluate(() => window.__host('git_status_response',
    { isAvailable: true, isRepo: true, branch: 'main', stagedChanges: [], workingChanges: [] }));
  await page.waitForTimeout(300);
  await expect(page.locator('#git-badge-count')).toHaveText('0');
  await expect(page.locator('#git-working-list')).toContainText('No changes');

  // Not a repository: one honest card with the init action
  await page.evaluate(() => window.__host('git_status_response',
    { isAvailable: true, isRepo: false, message: 'Workspace is not a Git repository.' }));
  await page.waitForTimeout(300);
  await expect(page.locator('#git-notice-banner')).toBeVisible();
  await expect(page.locator('#git-notice-banner')).toContainText('Workspace is not a Git repository.');
  await expect(page.locator('#btn-git-init-repo')).toHaveCount(1);

  // Git missing entirely: badge hidden, blocked card shown
  await page.evaluate(() => window.__host('git_status_response',
    { isAvailable: false, message: 'Git was not found on this system.' }));
  await page.waitForTimeout(300);
  await expect(page.locator('#git-badge-count')).toBeHidden();
  await expect(page.locator('#git-notice-banner')).toContainText('Git is not available');
  await expect(page.locator('#btn-git-install')).toHaveCount(1);

  expect(errors).toEqual([]);
});