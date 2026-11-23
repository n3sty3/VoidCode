#!/usr/bin/env node
/* metrics.js - VoidCode repository quality and ratchet metrics.
 *
 * Usage:
 *   node scripts/metrics.js          print the metric table
 *   node scripts/metrics.js --json   print raw JSON
 *   node scripts/metrics.js --check  exit 1 if any metric is worse than scripts/baselines.json
 *
 * "Ratchets only go down" (): a lower number is better for every
 * metric here except the test counts, which may only rise.
 */
'use strict';

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { findUndefinedCssVars } from './check-css-vars.js';
import { execFileSync } from 'node:child_process';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', '.vs', 'dist', 'publish', '.idea']);
const abs = (p) => path.join(ROOT, p);
const exists = (p) => fs.existsSync(abs(p));
const read = (p) => fs.readFileSync(abs(p), 'utf8');
const rel = (f) => path.relative(ROOT, f).split(path.sep).join('/');

function walk(dir, exts, out) {
  out = out || [];
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return out; }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (SKIP_DIRS.has(e.name)) continue;
      walk(full, exts, out);
    } else if (e.isFile() && exts.has(path.extname(e.name).toLowerCase())) {
      out.push(full);
    }
  }
  return out;
}

// Editor-style line count: newline-terminated file does not gain a phantom last line.
function lines(text) {
  if (!text.length) return 0;
  const n = text.split(/\r\n|\r|\n/).length;
  return /(?:\r\n|\r|\n)$/.test(text) ? n - 1 : n;
}
const lineCount = (p) => (exists(p) ? lines(read(p)) : 0);

// Styles.css is a manifest now, so the stylesheet metric has to sum the split files
// (manifest + every file under wwwroot/styles/) instead of reading one file. Counting only the
// Manifest would have made the ratchet measure 22 lines and silently stop working.
function stylesLines() {
  const files = [abs('wwwroot/styles.css'), ...walk(abs('wwwroot/styles'), new Set(['.css']))];
  return files.reduce((sum, file) => sum + (fs.existsSync(file) ? lines(fs.readFileSync(file, 'utf8')) : 0), 0);
}

// The type-check count is a ratchet. tsc writes one "error TS1234" line per diagnostic
// And exits 1 when there is any, so counting them is enough; when typescript is not installed (a
// Checkout without `npm ci`) the count reads 0 with a warning rather than failing the whole metric run.
function jsTypeErrors() {
  const tsc = abs('node_modules/typescript/bin/tsc');
  if (!fs.existsSync(tsc)) {
    console.warn('[metrics] typescript is not installed - jsTypeErrors reported as 0 (run npm ci)');
    return 0;
  }
  try {
    execFileSync(process.execPath, [tsc, '--noEmit', '--pretty', 'false'], { cwd: ROOT, encoding: 'utf8' });
    return 0;
  } catch (err) {
    const output = `${err.stdout || ''}${err.stderr || ''}`;
    const count = (output.match(/error TS\d+/g) || []).length;
    return count || 1;
  }
}

function countIn(files, re) {
  let total = 0; const per = {};
  for (const f of files) {
    const m = fs.readFileSync(f, 'utf8').match(re);
    if (m && m.length) { total += m.length; per[rel(f)] = (per[rel(f)] || 0) + m.length; }
  }
  return { total, per };
}

const EMPTY_CATCH = /\bcatch\s*(?:\([^)]*\))?\s*\{\s*\}/g;

// The fallback shell (wwwroot/console) is excluded from the JS counts to match the
// InnerHTML breakdown (app.js + modules only).
const isShellOrVendor = (relPath) => /^wwwroot\/(vendor|console)\//.test(relPath);

// SetHtml() lives here and is the ONE sanctioned place the app assigns innerHTML, so the
// Helper file is not counted as an unsafe innerHTML site.
const INNERHTML_EXEMPT = new Set(['wwwroot/core/markdown.js']);

function scriptTags() {
  if (!exists('wwwroot/index.html')) return { total: 0, console: 0 };
  const tags = [...read('wwwroot/index.html').matchAll(/<script\b[^>]*>/gi)].map((m) => m[0]);
  return { total: tags.length, console: tags.filter((t) => /src\s*=\s*["'][^"']*console\//i.test(t)).length };
}

function uiTestCount() {
  const dir = abs('tests/ui');
  if (!fs.existsSync(dir)) return 0;
  let n = 0;
  for (const f of walk(dir, new Set(['.js']))) {
    // Line-anchored so comments like "flip to test() then" are not counted.
    n += (fs.readFileSync(f, 'utf8').match(/^[ \t]*test(?:\.(?:only|skip|fail|describe))?\s*\(/gm) || []).length;
  }
  return n;
}

function measure() {
  const csFiles = walk(ROOT, new Set(['.cs']));
  const jsFiles = walk(abs('wwwroot'), new Set(['.js'])).filter((f) => !isShellOrVendor(rel(f)));
  // Empty-catch coverage spans ALL first-party JS (incl. wwwroot/console and the node
  // Tooling), so the 0 baseline blocks a new empty catch anywhere - not just where innerHTML is
  // Counted. Only vendored code is excluded.
  const firstPartyJs = walk(ROOT, new Set(['.js'])).filter((f) => !rel(f).startsWith('wwwroot/vendor/'));
  const innerHtml = countIn(jsFiles.filter((f) => !INNERHTML_EXEMPT.has(rel(f))), /\.innerHTML\b/g);
  const st = scriptTags();
  const mw = exists('MainWindow.xaml.cs') ? read('MainWindow.xaml.cs') : '';
  const testsCs = walk(abs('tests'), new Set(['.cs']));
  // Reuse the standalone var-checker so metrics and the CLI always agree.
  const cssVarNames = findUndefinedCssVars(ROOT);

  return {
    appJsLines: lineCount('wwwroot/app.js'),
    stylesCssLines: stylesLines(),
    mainWindowLines: lineCount('MainWindow.xaml.cs'),
    agentEngineLines: lineCount('Services/AgentEngine.cs'),
    bridgeCaseBranches: (mw.match(/\bcase\s+"/g) || []).length,
    emptyCatchCSharp: countIn(csFiles, EMPTY_CATCH).total,
    emptyCatchJs: countIn(firstPartyJs, EMPTY_CATCH).total,
    innerHtmlTotal: innerHtml.total,
    innerHtmlPerFile: innerHtml.per,
    scriptTags: st.total,
    consoleScriptTags: st.console,
    undefinedCssVars: cssVarNames.length,
    undefinedCssVarNames: cssVarNames,
    dotnetTests: countIn(testsCs, /\[\s*(?:Fact|Theory)\b/g).total,
    jsTypeErrors: jsTypeErrors(),
    uiTests: uiTestCount()
  };
}

// Direction: 'down' = lower is better, 'up' = higher is better.
const METRICS = [
  ['appJsLines', 'wwwroot/app.js lines', 'down'],
  ['stylesCssLines', 'stylesheet lines (styles.css + styles/**)', 'down'],
  ['mainWindowLines', 'MainWindow.xaml.cs lines', 'down'],
  ['agentEngineLines', 'Services/AgentEngine.cs lines', 'down'],
  ['bridgeCaseBranches', 'bridge case branches (MainWindow)', 'down'],
  ['emptyCatchCSharp', 'empty catch blocks (C#)', 'down'],
  ['emptyCatchJs', 'empty catch blocks (JS)', 'down'],
  ['innerHtmlTotal', 'innerHTML sites (JS)', 'down'],
  ['scriptTags', 'script tags (index.html)', 'down'],
  ['consoleScriptTags', 'console/ script tags (index.html)', 'down'],
  ['undefinedCssVars', 'CSS vars used but undefined', 'down'],
  ['dotnetTests', '.NET tests', 'up'],
  ['jsTypeErrors', 'TypeScript errors (core + modules, checkJs)', 'down'],
  ['uiTests', 'UI tests', 'up']
];

function loadBaselines() {
  const p = abs('scripts/baselines.json');
  if (!fs.existsSync(p)) return null;
  try { return JSON.parse(fs.readFileSync(p, 'utf8')); } catch { return null; }
}

function main() {
  const args = process.argv.slice(2);
  const data = measure();

  if (args.includes('--json')) { console.log(JSON.stringify(data, null, 2)); return; }

  console.log('VOIDCODE metrics (' + new Date().toISOString().slice(0, 10) + ')');
  console.log('');
  for (const [key, label] of METRICS) {
    console.log('  ' + String(data[key]).padStart(6) + '  ' + label);
  }
  console.log('');
  console.log('  innerHTML by file:');
  const per = Object.entries(data.innerHtmlPerFile).sort((a, b) => b[1] - a[1]);
  if (!per.length) console.log('    (none)');
  for (const [f, n] of per) console.log('    ' + String(n).padStart(4) + '  ' + f);
  if (data.undefinedCssVarNames.length) {
    console.log('');
    console.log('  undefined CSS vars: ' + data.undefinedCssVarNames.join(', '));
  }

  if (args.includes('--check')) {
    const baseline = loadBaselines();
    if (!baseline) { console.error('no scripts/baselines.json to compare against'); process.exit(2); }
    const worse = [];
    for (const [key, , dir] of METRICS) {
      if (!(key in baseline)) continue;
      const now = data[key], was = baseline[key];
      if (dir === 'down' ? now > was : now < was) worse.push(key + ': ' + now + ' (baseline ' + was + ')');
    }
    console.log('');
    if (worse.length) {
      console.log('REGRESSED vs baseline:');
      for (const w of worse) console.log('  x ' + w);
      process.exit(1);
    }
    console.log('all metrics at or better than baseline');
  }
}

main();
