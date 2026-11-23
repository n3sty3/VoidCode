import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE, CODE } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

test('workflow tools: merge conflicts, tasks modal, git blame, reopen closed tab, and dynamic DAP debug gating', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript(() => { if (window === window.top) localStorage.clear(); });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2200);

  // 1. Merge-conflict parser & resolver
  const conflictResult = await page.evaluate(() => {
    const sample = [
      'header',
      '<<<<<<< HEAD',
      'const x = 1;',
      '=======',
      'const x = 2;',
      '>>>>>>> feature',
      'footer'
    ].join('\n');
    const found = window.findMergeConflicts(sample);
    return {
      count: found.length,
      current: window.resolveMergeConflict(sample, 0, 'current'),
      incoming: window.resolveMergeConflict(sample, 0, 'incoming'),
      both: window.resolveMergeConflict(sample, 0, 'both')
    };
  });
  expect(conflictResult.count).toBe(1);
  expect(conflictResult.current).toBe('header\nconst x = 1;\nfooter');
  expect(conflictResult.incoming).toBe('header\nconst x = 2;\nfooter');
  expect(conflictResult.both).toBe('header\nconst x = 1;\nconst x = 2;\nfooter');

  // 2. Tasks modal: list -> run -> problem markers
  await page.evaluate(() => {
    window.__sent.length = 0;
    window.openTasksModal();
  });
  await expect(page.locator('#tasks-modal')).toBeVisible();
  expect(await page.evaluate(() => window.__sent.some(m => m.action === 'list_tasks'))).toBe(true);

  await page.evaluate(() => {
    window.__sent.length = 0;
    window.__host('tasks_list_response', {
      tasks: [{ label: 'build', command: 'dotnet', args: ['build'], sourceFile: '.voidcode/tasks.json' }]
    });
  });
  await expect(page.locator('#tasks-modal-list [data-run-task="build"]')).toBeVisible();
  await page.click('#tasks-modal-list [data-run-task="build"]');
  await expect(page.locator('#tasks-modal')).toBeHidden();
  expect(await page.evaluate(() => window.__sent.find(m => m.action === 'run_task'))).toMatchObject({ label: 'build' });

  // 3. Open Program.cs -> Git blame -> dynamic debug capability unlock -> close & reopen tab
  await page.evaluate(([t, c]) => {
    window.__host('init_state_response', { settings: {}, workspace: 'C:\\proj', tree: t, sessions: [], checkpoints: [] });
    window.__host('read_file_response', { path: 'C:\\proj\\Program.cs', content: c, metadata: { encoding: 'utf-8', eol: 'LF' } });
  }, [TREE, CODE]);
  await page.waitForTimeout(800);

  // Git blame sends git_get_blame and accepts git_blame_response cleanly
  await page.evaluate(() => {
    window.__sent.length = 0;
    window.toggleGitBlame();
  });
  expect(await page.evaluate(() => window.__sent.find(m => m.action === 'git_get_blame'))).toMatchObject({ path: 'C:\\proj\\Program.cs' });
  await page.evaluate(() => {
    window.__host('git_blame_response', {
      path: 'C:\\proj\\Program.cs',
      lines: [{ line: 1, shortHash: 'abc1234', author: 'Ada', date: '2026-11-23', summary: 'Initial' }]
    });
  });

  // Dynamic debug gating: .cs starts disabled, then unlocks when capabilities_response reports dotnet=true
  await expect(page.locator('#btn-sidebar-debug-start')).toBeDisabled();
  await page.evaluate(() => {
    window.__host('capabilities_response', { debug: { node: true, dotnet: true, python: false } });
  });
  await expect(page.locator('#btn-sidebar-debug-start')).toBeEnabled();

  // Reopen closed editor
  await page.evaluate(() => {
    window.__sent.length = 0;
    window.closeTab('C:\\proj\\Program.cs');
    window.reopenLastClosedTab();
  });
  expect(await page.evaluate(() => window.__sent.find(m => m.action === 'read_file'))).toMatchObject({ path: 'C:\\proj\\Program.cs' });

  expect(errors).toEqual([]);
});
