import { test, expect } from '@playwright/test';
import { MOCK_HOST, BRIDGE_NONCE } from './mock-host.js';

// The bridge nonce must not be reachable from the URL, the window object, the
// DOM, web storage or a nested frame. It is delivered exactly once, by the host, over the
// Handshake message. Done when: location.href has no nonce, and a hostile iframe cannot post
// A valid message.
const APP_URL = 'http://localhost:8765/index.html';

async function boot(page) {
  await page.setViewportSize({ width: 1440, height: 860 });
  await page.addInitScript({ content: MOCK_HOST });
  await page.goto(APP_URL);
  await page.waitForTimeout(2500);
}

async function nestedAppFrame(page) {
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) {
    const child = page.frames().find((f) => f !== page.mainFrame() && /index\.html/.test(f.url()));
    if (child) return child;
    await page.waitForTimeout(200);
  }
  throw new Error('the nested index.html frame never appeared');
}

test('the nonce is nowhere the page can leak it, and every message carries it', async ({ page }) => {
  await boot(page);

  // The address bar and the document carry no nonce
  expect(page.url()).not.toContain('nonce');
  expect(new URL(page.url()).search).toBe('');
  expect(await page.evaluate(() => window.location.search)).toBe('');
  expect(await page.evaluate(() => /nonce/i.test(document.documentElement.outerHTML))).toBe(false);

  // Nor do the window object or any web storage key/value (the nonce lives in a bridge closure)
  expect(await page.evaluate(() => Object.keys(window).filter((k) => /nonce/i.test(k)))).toEqual([]);
  const storageLeak = await page.evaluate((nonce) => {
    const dump = [];
    for (const store of [window.localStorage, window.sessionStorage]) {
      for (let i = 0; i < store.length; i += 1) {
        const key = store.key(i);
        dump.push(key, store.getItem(key));
      }
    }
    return dump.filter((v) => v && String(v).includes(nonce));
  }, BRIDGE_NONCE);
  expect(storageLeak).toEqual([]);

  // The handshake is the only source, and it stamped everything already sent
  const sent = await page.evaluate(() => window.__sent);
  expect(sent.length).toBeGreaterThan(0);
  expect(sent.filter((m) => m.bridgeNonce !== BRIDGE_NONCE)).toEqual([]);
});

test('a nested frame cannot obtain a nonce, so it cannot post a valid message', async ({ page }) => {
  await boot(page);

  await page.evaluate(() => {
    const frame = document.createElement('iframe');
    frame.id = 'nested-app';
    frame.src = '/index.html';
    document.body.appendChild(frame);
  });

  const child = await nestedAppFrame(page);

  // Production only ever hands the nonce to the top-level document, so the nested copy of the
  // App is left waiting: its handshake never resolves and nothing it sends reaches the host.
  const childState = await child.evaluate(async () => {
    window.__sent.length = 0;
    window.VoidBridge.send('hostile_ping', { id: 'hostile_1' });
    const settled = await Promise.race([
      window.VoidBridge.ready.then(() => 'handshake'),
      new Promise((resolve) => setTimeout(() => resolve('none'), 500))
    ]);
    return { settled, posted: window.__sent.length, search: window.location.search, nonceOnWindow: Object.keys(window).filter((k) => /nonce/i.test(k)) };
  });

  expect(childState.settled).toBe('none');
  expect(childState.posted).toBe(0);
  expect(childState.search).toBe('');
  expect(childState.nonceOnWindow).toEqual([]);

  // While the top-level document is still talking to the host with the handshake nonce
  const top = await page.evaluate((nonce) => ({
    posted: window.__sent.length,
    allStamped: window.__sent.every((m) => m.bridgeNonce === nonce)
  }), BRIDGE_NONCE);
  expect(top.posted).toBeGreaterThan(0);
  expect(top.allStamped).toBe(true);
});
