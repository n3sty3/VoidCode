import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE, CODE } from './mock-host.js';

// XSS regression harness.
//
// One payload is fed into every host-supplied string the UI renders. moved every
// Renderer onto setHtml() + html``/raw(), so the angle brackets must reach the DOM as TEXT.
// If a site regresses to raw innerHTML the <img> element is built (and onerror would fire,
// Were it not for the document CSP) - which is exactly what the assertions below catch. The
// Control test at the bottom proves the harness is sensitive to that difference.
const URL = 'http://localhost:8765/index.html';

const XSS = '<img src=x onerror=window.__xss=1>.txt';
const XSS_MARKUP = '<img src=x onerror=window.__xss=1>';
const PROGRAM = 'C:\\proj\\Program.cs';

async function boot(page) {
  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(URL);
  await page.waitForTimeout(2500);
}

function treeWith(fileName) {
  return {
    ...TREE,
    children: [...TREE.children, { name: fileName, path: `C:\\proj\\${fileName}`, isDirectory: false }]
  };
}

// Drives the payload through all five host-supplied strings the plan lists: a file name in
// The tree, a Problems message, a git branch name, a search result, and an agent message.
async function feedHostStrings(page, fileName, message) {
  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [treeWith(fileName)]);

  await page.evaluate(([path, content]) => window.__host('read_file_response',
    { path, content, metadata: { encoding: 'utf-8', eol: 'LF' } }), [PROGRAM, CODE]);
  await page.waitForTimeout(400);

  await page.evaluate(([msg, path]) => window.__host('lsp_diagnostics', {
    path,
    diagnostics: [{
      startLine: 5, startCol: 7, endLine: 5, endCol: 8,
      severity: 1, message: msg, code: 'XSS1', source: 'xss'
    }]
  }), [message, PROGRAM]);

  await page.evaluate(([branch]) => window.__host('git_branches_response',
    { current: branch, branches: [branch, 'main'] }), [fileName]);

  await page.evaluate(([file, preview]) => window.__host('search_workspace_response', {
    totalMatches: 1,
    fileCount: 1,
    query: 'xss',
    files: [{ relativePath: file, fullPath: file, matches: [{ line: 3, column: 1, length: 4, preview }] }]
  }), [fileName, message]);

  await page.evaluate(() => window.__host('agent_stream_start', {}));
  await page.evaluate(([markup]) => window.__host('agent_stream_delta', { delta: markup }), [message]);
  await page.evaluate(([markup]) => window.__host('agent_stream_end', { fullContent: markup }), [message]);

  await page.waitForTimeout(1200); // the Problems refresh is debounced (~350 ms)
}

test('host-supplied markup is escaped on every surface: no execution, no bridge traffic', async ({ page }) => {
  await boot(page);

  // Open the panes the fixtures target the way a user would, so the injection lands in
  // Visible DOM rather than only in hidden containers.
  await page.evaluate(() => { window.switchSidebarView('search'); window.openBranchPicker(); });
  await page.waitForTimeout(300);

  // Pass 1 - benign strings record the bridge traffic this fixture sequence legitimately
  // Produces while the workspace is populated. Pass 2 repeats it with the payload, so any
  // EXTRA action means injected markup managed to reach sendHost(). Comparing the two runs
  // Keeps the check exact without hand-maintaining an allowlist of every host action.
  await page.evaluate(() => { window.__sent.length = 0; });
  await feedHostStrings(page, 'benign.txt', 'benign message');
  const benign = await page.evaluate(() => [...new Set(window.__sent.map((m) => m.action))]);

  await page.evaluate(() => { window.__sent.length = 0; });
  await feedHostStrings(page, XSS, XSS_MARKUP);
  const sent = await page.evaluate(() => window.__sent.map((m) => m.action));

  // A) the payload never executed
  expect(await page.evaluate(() => window.__xss)).toBeUndefined();

  // B) no injected event handler survived anywhere (DOMPurify strips it; escaping avoids it)
  expect(await page.locator('[onerror]').count()).toBe(0);

  // C) the escaping surfaces render the payload as text, so nothing was parsed out of it
  for (const sel of ['#file-tree-container', '#problems-list', '#branch-results-list', '#search-results-tree']) {
    expect(await page.locator(`${sel} img`).count(), `${sel} must not build an element from the payload`).toBe(0);
  }
  await expect(page.locator('#file-tree-container')).toContainText(XSS);
  await expect(page.locator('#problems-list')).toContainText(XSS_MARKUP);
  await expect(page.locator('#branch-results-list')).toContainText(XSS);
  await expect(page.locator('#search-results-tree')).toContainText(XSS_MARKUP);

  // D) nothing the payload could have triggered was posted to the host
  expect([...new Set(sent)].filter((a) => !benign.includes(a)), 'payload-derived bridge traffic').toEqual([]);
});

// Sensitivity control: proves the assertions above are meaningful. Written through a raw
// InnerHTML sink the payload IS parsed into a real element - exactly the shape 
// Removed - so the "nothing was built from the payload" assertions fail if any site is
// Bypassed.
//
// Window.__xss is deliberately not asserted here: index.html already ships a CSP without
// 'unsafe-inline' for scripts, so an inline onerror handler is blocked as a second layer.
// That CSP is defence in depth, not a substitute - the structural assertion is what catches
// A renderer that regresses to innerHTML.
test('control: a raw innerHTML sink does build an element from the payload', async ({ page }) => {
  await boot(page);
  expect(await page.evaluate(() => window.__xss)).toBeUndefined();
  expect(await page.locator('img[src="x"]').count()).toBe(0);

  await page.evaluate(([markup]) => {
    const sink = document.createElement('div');
    sink.id = 'xss-control-sink';
    // eslint-disable-next-line no-unsanitized/property -- deliberate: this is the control
    sink.innerHTML = markup; // the exact shape removed from the app
    document.body.appendChild(sink);
  }, [XSS_MARKUP]);

  await expect(page.locator('#xss-control-sink img[src="x"]')).toHaveCount(1);
});
