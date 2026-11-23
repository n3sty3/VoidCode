#!/usr/bin/env node
/* check-css-vars.js - .
 *
 * Fails (exit 1) when a stylesheet uses var(--x) that is never defined. `--range-pct` is
 * exempt because it is set from JavaScript at runtime. Vendored CSS (wwwroot/vendor) is out
 * of scope - we do not own it.
 *
 * Usage: node scripts/check-css-vars.js
 * Also imported by scripts/metrics.js so the CLI and the ratchet agree.
 */
'use strict';

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const DEFAULT_ROOT = path.resolve(HERE, '..');
const EXEMPT = new Set(['--range-pct']);

function walkCss(dir, out) {
  out = out || [];
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return out; }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (e.name === 'vendor' || e.name === 'node_modules') continue;
      walkCss(full, out);
    } else if (e.isFile() && e.name.toLowerCase().endsWith('.css')) {
      out.push(full);
    }
  }
  return out;
}

// Returns the sorted names of every custom property that is referenced by var() but never defined.
export function findUndefinedCssVars(root = DEFAULT_ROOT) {
  const defined = new Set();
  const used = new Map();
  for (const file of walkCss(path.join(root, 'wwwroot'))) {
    const text = fs.readFileSync(file, 'utf8');
    for (const m of text.matchAll(/(--[A-Za-z0-9_-]+)\s*:/g)) defined.add(m[1]);
    for (const m of text.matchAll(/var\(\s*(--[A-Za-z0-9_-]+)/g)) {
      if (!used.has(m[1])) used.set(m[1], new Set());
      used.get(m[1]).add(path.relative(root, file).split(path.sep).join('/'));
    }
  }
  return [...used.keys()].filter((name) => !defined.has(name) && !EXEMPT.has(name)).sort();
}

const invokedDirectly = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (invokedDirectly) {
  const missing = findUndefinedCssVars();
  if (missing.length) {
    console.error(`Undefined CSS variables (${missing.length}):`);
    for (const name of missing) console.error('  ' + name);
    process.exit(1);
  }
  console.log('css vars: every var() reference is defined');
}
