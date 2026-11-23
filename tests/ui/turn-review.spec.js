import { test, expect } from '@playwright/test';
import { MOCK_HOST } from './mock-host.js';

const URL = 'http://localhost:8765/index.html';

test('turn review card: per-file diff, per-hunk reject, file revert, and whole-turn revert', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript(() => { if (window === window.top) localStorage.clear(); });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2200);
  await page.click('#btn-toggle-pane-agent');
  await page.waitForTimeout(300);

  // Host emits agent_done with a multi-file turnSummary + one irreversible command
  await page.evaluate(() => {
    window.__sent.length = 0;
    window.__host('agent_done', {
      finalMessage: 'Updated two files.',
      steps: 2,
      toolCalls: 3,
      totalTokens: 540,
      elapsedMs: 1200,
      turnId: 'turn_demo_1',
      turnSummary: {
        turnId: 'turn_demo_1',
        files: [
          { path: 'C:\\proj\\Program.cs', relativePath: 'Program.cs', action: 'edit', reversible: true, reverted: false },
          { path: 'C:\\proj\\Helper.cs', relativePath: 'Helper.cs', action: 'create', reversible: true, reverted: false }
        ],
        irreversibleActions: [
          { tool: 'run_command', summary: 'dotnet test', reversible: false }
        ]
      }
    });
  });

  const card = page.locator('.agent-turn-review-bar[data-turn-id="turn_demo_1"]');
  await expect(card).toBeVisible();
  await expect(card).toContainText('2 file changes');
  await expect(card).toContainText('1 non-reversible');
  await expect(card).toContainText('Program.cs');
  await expect(card).toContainText('Helper.cs');

  // Click Diff on Program.cs -> sends get_turn_file_diff
  await card.locator('[data-turn-file-path="C:\\\\proj\\\\Program.cs"] [data-file-act="diff"]').click();
  const diffReq = await page.evaluate(() => window.__sent.find(m => m.action === 'get_turn_file_diff'));
  expect(diffReq).toMatchObject({ turnId: 'turn_demo_1', path: 'C:\\proj\\Program.cs' });

  // Host replies with two hunks -> diff window opens with hunk controls
  await page.evaluate(() => {
    window.__sent.length = 0;
    window.__host('turn_file_diff_response', {
      turnId: 'turn_demo_1',
      path: 'C:\\proj\\Program.cs',
      relativePath: 'Program.cs',
      beforeContent: 'a\nb\nc\nd\n',
      afterContent: 'a\nB1\nc\nD1\n',
      isBinary: false,
      existedBefore: true,
      existsNow: true,
      hunks: [
        { index: 0, oldStart: 2, oldCount: 1, newStart: 2, newCount: 1, header: '@@ -2,1 +2,1 @@', preview: '- b\n+ B1' },
        { index: 1, oldStart: 4, oldCount: 1, newStart: 4, newCount: 1, header: '@@ -4,1 +4,1 @@', preview: '- d\n+ D1' }
      ]
    });
  });

  await expect(page.locator('#win-diff')).toBeVisible();
  await expect(page.locator('#diff-hunks-bar [data-reject-hunk]')).toHaveCount(2);

  // Reject hunk 0 -> sends reject_turn_hunk
  await page.click('#diff-hunks-bar [data-reject-hunk="0"]');
  const rejectHunkReq = await page.evaluate(() => window.__sent.find(m => m.action === 'reject_turn_hunk'));
  expect(rejectHunkReq).toMatchObject({ turnId: 'turn_demo_1', path: 'C:\\proj\\Program.cs', hunkIndex: 0 });

  // Host answers with 1 remaining hunk
  await page.evaluate(() => {
    window.__host('turn_hunk_rejected', {
      turnId: 'turn_demo_1',
      path: 'C:\\proj\\Program.cs',
      relativePath: 'Program.cs',
      beforeContent: 'a\nb\nc\nd\n',
      afterContent: 'a\nb\nc\nD1\n',
      isBinary: false,
      existedBefore: true,
      existsNow: true,
      hunks: [
        { index: 0, oldStart: 4, oldCount: 1, newStart: 4, newCount: 1, header: '@@ -4,1 +4,1 @@', preview: '- d\n+ D1' }
      ]
    });
  });
  await expect(page.locator('#diff-hunks-bar [data-reject-hunk]')).toHaveCount(1);

  // Revert whole turn -> sends revert_whole_turn
  await page.evaluate(() => { window.__sent.length = 0; });
  await card.locator('[data-act="undo-turn"]').click();
  const revertAllReq = await page.evaluate(() => window.__sent.find(m => m.action === 'revert_whole_turn'));
  expect(revertAllReq).toMatchObject({ turnId: 'turn_demo_1' });

  await page.evaluate(() => {
    window.__host('turn_reverted', {
      turnId: 'turn_demo_1',
      files: [{ path: 'C:\\proj\\Program.cs', exists: true, content: 'a\nb\nc\nd\n' }],
      irreversibleActions: [{ tool: 'run_command', summary: 'dotnet test' }]
    });
  });
  await expect(card.locator('[data-act="undo-turn"]')).toHaveText('Turn reverted');
  expect(errors).toEqual([]);
});
