import { defineConfig } from '@playwright/test';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));

// Serves wwwroot/ over http so the app boots without the WebView2 host; the mock
// Host (tests/ui/mock-host.js) is injected per-test via addInitScript.
//
// Playwright snapshot files are platform-specific (boot-920-win32.png / boot-920-linux.png).
// Screenshots are enforced only where a baseline for THIS platform is committed, so a fresh
// Platform (e.g. the Linux CI runner) does not fail the whole job before its baselines exist.
// Commit the generated `-linux` PNGs and enforcement turns on automatically.
const baseline = path.join(root, 'tests', 'ui', 'smoke.spec.js-snapshots', `boot-920-${process.platform}.png`);
const enforceSnapshots = existsSync(baseline);
if (!enforceSnapshots) {
  console.warn(`[playwright.config] no screenshot baseline for platform "${process.platform}" - snapshots ignored (all other assertions still run).`);
}

export default defineConfig({
  testDir: './tests/ui',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  ignoreSnapshots: !enforceSnapshots,
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    headless: true,
    baseURL: 'http://localhost:8765'
  },
  webServer: {
    command: 'npx http-server wwwroot -p 8765 -c-1',
    port: 8765,
    reuseExistingServer: true,
    timeout: 30_000
  }
});
