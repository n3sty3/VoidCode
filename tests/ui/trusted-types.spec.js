import { test, expect } from '@playwright/test';
import { MOCK_HOST, TREE, CODE, withReportOnlyCsp } from './mock-host.js';

// Trusted Types, report-only first.
//
// Index.html is served with
//   Content-Security-Policy-Report-Only: require-trusted-types-for 'script'; trusted-types
//   Voidcode-html dompurify
// And core/markdown.js creates the voidcode-html policy that setHtml() routes through. Done
// When the UI harness reports 0 violations while the app is driven through its HTML sinks.
//
// Enforcing is NOT switched on here on purpose: with an enforcing policy Monaco never
// Initializes ("This document requires 'TrustedHTML' assignment. The action has been
// Blocked."), which is the plan's condition for staying report-only.
const APP_URL = 'http://localhost:8765/index.html';

async function boot(page) {
  await withReportOnlyCsp(page);
  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(APP_URL);
  await page.waitForTimeout(2500);
}

// Drives the surfaces that build HTML through setHtml(): the file tree, the Problems list, the
// Branch picker, the quick-open palette, the chat transcript and the markdown renderer.
async function driveSmokeFlow(page) {
  await page.evaluate(([tree]) => window.__host('init_state_response',
    { settings: {}, workspace: 'C:\\proj', tree, sessions: [], checkpoints: [] }), [TREE]);
  await page.evaluate(([content]) => window.__host('read_file_response',
    { path: 'C:\\proj\\Program.cs', content, metadata: { encoding: 'utf-8', eol: 'LF' } }), [CODE]);
  await page.waitForTimeout(1500);

  await page.evaluate(() => window.__host('lsp_diagnostics', {
    path: 'C:\\proj\\Program.cs',
    diagnostics: [{ startLine: 5, startCol: 7, severity: 1, message: 'Fixture error', source: 'csp' }]
  }));
  await page.evaluate(() => { window.openBranchPicker(); window.switchSidebarView('search'); });
  await page.keyboard.press('Control+Shift+P');
  await page.waitForTimeout(300);
  await page.keyboard.press('Escape');

  await page.evaluate(() => window.__host('agent_stream_start', {}));
  await page.evaluate(() => window.__host('agent_stream_delta', { delta: '## heading\n\n- item' }));
  await page.evaluate(() => window.__host('agent_stream_end', { fullContent: '## heading\n\n- item\n\n`code`' }));
  await page.waitForTimeout(1500);
}

test('report-only Trusted Types: the app still renders and the smoke flow reports 0 violations', async ({ page }) => {
  const pageErrors = [];
  page.on('pageerror', (e) => pageErrors.push(String(e)));

  await boot(page);
  await driveSmokeFlow(page);

  // The policy really is being delivered (the harness mirrors Services/CspPolicy.cs)
  const header = await page.evaluate(async () => {
    const response = await fetch('/index.html', { cache: 'no-store' });
    return response.headers.get('content-security-policy-report-only');
  });
  expect(header).toContain("require-trusted-types-for 'script'");

  const state = await page.evaluate(() => ({
    hasTrustedTypes: !!window.trustedTypes,
    violations: window.__cspViolations.slice(),
    monacoModels: window.monaco ? window.monaco.editor.getModels().length : -1,
    problemsRendered: document.querySelectorAll('#problems-list .problem-item').length,
    chatMessages: document.querySelectorAll('#agent-chat-container .chat-msg').length
  }));

  // The app worked: Monaco loaded, the Problems list and the agent transcript rendered
  expect(state.hasTrustedTypes).toBe(true);
  expect(state.monacoModels).toBeGreaterThan(0);
  expect(state.problemsRendered).toBeGreaterThan(0);
  expect(state.chatMessages).toBeGreaterThan(0);

  // Nothing in app.js / modules/* / core/* violates the policy
  const firstParty = state.violations.filter((v) => !v.source.includes('/vendor/'));
  expect(firstParty).toEqual([]);

  // The vendored Monaco bundle creates its own Trusted Types policy per feature and loader.js
  // Sets a script src, so report-only mode reports those. Two consequences: they are the
  // Measured reason the policy is not promoted to enforcing (under enforcement Monaco never
  // Initializes), and their presence is what proves the policy is really live in this run -
  // Without the header this count would be 0 too.
  const vendor = state.violations.filter((v) => v.source.includes('/vendor/'));
  expect(vendor.length).toBeGreaterThan(0);
  console.log(`[CSP] report-only violations from vendored bundles (reported, never blocked): ${vendor.length} - ${[...new Set(vendor.map((v) => `${v.directive}@${v.source.split('/').pop()}`))].join(', ')}`);

  expect(pageErrors).toEqual([]);
});

test('control: the violation reporter fires, so the assertion above is meaningful', async ({ page }) => {
  await boot(page);

  const violations = await page.evaluate(async () => {
    // A policy name outside the allowlist is reported even in report-only mode (report-only
    // Never blocks - the app keeps working).
    try { window.trustedTypes.createPolicy('not-in-the-allowlist', { createHTML: (s) => s }); } catch { /* reported, not thrown */ }
    await new Promise((resolve) => setTimeout(resolve, 200));
    return window.__cspViolations.slice();
  });

  expect(violations.length).toBeGreaterThan(0);
  expect(violations[0].directive).toContain('trusted-types');
  // The harness also forwards each violation to the host log
  const sent = await page.evaluate(() => window.__sent.filter((m) => m.action === 'js_log' && String(m.context).startsWith('csp')));
  expect(sent.length).toBeGreaterThan(0);
});
