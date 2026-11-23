// Mock host bridge for the UI smoke tests.
// Inject with page.addInitScript({ content: MOCK_HOST }) BEFORE the page loads so
// Window.chrome.webview exists when core/bridge.js and app.js run.
//
// PostMessage records everything the app sends into window.__sent.
// Window.__host(action, payload) pushes a host->app message to every subscriber.
// The host no longer puts the nonce in the URL. It pushes a single handshake
// Message to the top-level document after navigation - exactly what
// CoreWebView2.PostWebMessageAsJson does on NavigationCompleted.
export const BRIDGE_NONCE = 'test-bridge-nonce-7f3c1a';

export const MOCK_HOST = `
window.__sent = [];
(function () {
  const NONCE = ${JSON.stringify(BRIDGE_NONCE)};
  const listeners = [];
  let delivered = false;
  window.chrome = { webview: {
    postMessage(m) { try { window.__sent.push(JSON.parse(m)); } catch (e) { /* ignore a non-JSON frame */ } },
    addEventListener(t, fn) { if (t === 'message') listeners.push(fn); },
    removeEventListener() {}
  }};
  window.__host = (action, payload) =>
    listeners.forEach(fn => fn({ data: JSON.stringify({ action, payload }), source: window.chrome.webview }));
  // The one handshake the real host posts after navigation (WebView2 sets event.source = window.chrome.webview).
  window.__handshake = () => {
    if (delivered) return;
    delivered = true;
    listeners.forEach(fn => fn({ data: JSON.stringify({ action: 'bridge_handshake', payload: { nonce: NONCE } }), source: window.chrome.webview }));
  };
  // Only the top-level document is handed the nonce - a nested frame loading the same page
  // Gets nothing, which is what the hostile-frame test asserts.
  window.addEventListener('load', () => { if (window.top === window) window.__handshake(); });
})();
`;

// Headless Chromium reports prefers-reduced-transparency: reduce by default (verified:
// MatchMedia is true before any emulation), and the reduced mode is deliberately fully opaque. The
// Screenshot baselines must keep covering the glass look - that is what the product shows to a user who
// Has not turned transparency down - so they pin the preference instead of inheriting the runner's.
// The reduced rendering is asserted in tests/ui/a11y-contrast.spec.js.
export async function pinGlassMode(page, value = 'no-preference') {
  const client = await page.context().newCDPSession(page);
  await client.send('Emulation.setEmulatedMedia', {
    features: [{ name: 'prefers-reduced-transparency', value }]
  });
  const matches = await page.evaluate(() =>
    matchMedia('(prefers-reduced-transparency: no-preference)').matches).catch(() => null);
  return matches;
}

// The report-only Trusted Types policy. The real host attaches this to the
// Document response (Services/CspPolicy.cs); the harness has no such hook, so the same header
// Is injected by route interception, exactly as it arrives in production.
export const CSP_REPORT_ONLY = "require-trusted-types-for 'script'; trusted-types voidcode-html dompurify";

export async function withReportOnlyCsp(page) {
  await page.route('**/index.html', async (route) => {
    const response = await route.fetch();
    await route.fulfill({
      response,
      headers: { ...response.headers(), 'content-security-policy-report-only': CSP_REPORT_ONLY }
    });
  });
}

export const TREE = {
  name: 'proj',
  path: 'C:\\proj',
  isDirectory: true,
  children: [
    { name: 'Program.cs', path: 'C:\\proj\\Program.cs', isDirectory: false },
    { name: 'notes.md', path: 'C:\\proj\\notes.md', isDirectory: false }
  ]
};

export const CODE =
  'using System;\nnamespace Demo {\n  public class Program {\n' +
  '    static void Main(string[] args) {\n      var x = 5;\n' +
  '      Console.WriteLine(x);\n    }\n  }\n}\n';
