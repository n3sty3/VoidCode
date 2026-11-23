#!/usr/bin/env node
// Host -> page payload audit.
//
// Why this exists: two shipping bugs came from the same place. `git_diff_response` built
// `new { result.Success, result.Path, result.Staged, ... }`, which serializes PascalCase keys while the
// Page reads `payload.path` / `payload.staged`. The UI harness could never see it - its mock host speaks
// CamelCase, so the page and the mock agreed and the app disagreed with both. This compares the sides
// Statically.
//
// Rules that keep it honest:
//   * `key = expr` owns the key `key`; its right-hand side is not a key (so `success = result.Success`
//     Does not report "Success");
//   * a positional member owns the last segment of its expression (`result.Success` -> "Success"),
//     Because that is what System.Text.Json writes for an anonymous type;
//   * a POCO payload (`new SearchWorkspaceResponse { ... }`) resolves to the type's
//     `[JsonPropertyName]` values, and a payload property without one is reported as a bug;
//   * string interpolations are stripped first (`$"{detected.Value.Command}"` is not a key).
//
// Known blind spots, on purpose: reads inside helper functions a handler calls, and keys the page never
// Touches (reported as info, not as a failure).
//
// Exit codes: 0 = clean, 1 = a payload would serialize with the wrong name.

import fs from 'node:fs';
import path from 'node:path';

const HANDLER_DIR = 'Bridge/Handlers';
const SERVICE_DIRS = ['Services', 'Agent'];

const read = (file) => fs.readFileSync(file, 'utf8');

/** Type -> (property -> wire name); `null` when the property has no JsonPropertyName. */
function loadJsonNames() {
  const map = new Map();
  for (const dir of SERVICE_DIRS) {
    if (!fs.existsSync(dir)) continue;
    for (const file of fs.readdirSync(dir).filter((f) => f.endsWith('.cs'))) {
      const text = read(path.join(dir, file));
      const classRe = /(?:public|internal)\s+(?:sealed\s+)?class\s+(\w+)([\s\S]*?)(?=\n\s*(?:public|internal)\s+(?:sealed\s+)?(?:class|record|static|interface)\b|$)/g;
      for (const m of text.matchAll(classRe)) {
        const props = new Map();
        for (const pm of m[2].matchAll(/\[JsonPropertyName\("([^"]+)"\)\][\s\S]{0,80}?\s(\w+)\s*\{/g)) props.set(pm[2], pm[1]);
        for (const pm of /(?:public|internal)\s+[\w?<>\[\], ]+\s+(\w+)\s*\{\s*get/g[Symbol.matchAll](m[2])) {
          if (!props.has(pm[1])) props.set(pm[1], null);
        }
        if (props.size) map.set(m[1], props);
      }
    }
  }
  return map;
}

function matchBrace(text, openIndex) {
  let depth = 0;
  for (let i = openIndex; i < text.length; i++) {
    if (text[i] === '{') depth++;
    else if (text[i] === '}') {
      depth--;
      if (depth === 0) return i;
    }
  }
  return text.length - 1;
}
const jsonNames = loadJsonNames();
const sentBy = new Map();
const unmapped = [];

function record(key, action) {
  if (!sentBy.has(key)) sentBy.set(key, new Set());
  sentBy.get(key).add(action);
}

function collectFromAnonymous(body, action) {
  const scrubbed = body
    .replace(/\$?"(?:[^"\\]|\\.)*"/g, '""')
    .replace(/(^|[,{\s])([A-Za-z_]\w*)\s*=\s*[^,{}]+/g, (whole, lead, name) => {
      record(name, action);
      return lead;
    });
  for (const m of scrubbed.matchAll(/(^|[,{\s])([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*(?=[,}])/g)) {
    const expr = m[2];
    if (expr === 'true' || expr === 'false' || expr === 'null') continue;
    record(expr.split('.').pop(), action);
  }
}

function collectFromPoco(typeName, body, action) {
  const props = jsonNames.get(typeName);
  for (const m of body.matchAll(/(?:^|[,{\s])([A-Za-z_]\w*)\s*=/g)) {
    const prop = m[1];
    const wire = props?.get(prop);
    if (wire) record(wire, action);
    else if (props && wire === null) unmapped.push({ action, typeName, prop });
    else record(prop, action);
  }
}

for (const file of fs.readdirSync(HANDLER_DIR).filter((f) => f.endsWith('.cs'))) {
  const text = read(path.join(HANDLER_DIR, file));
  for (const m of text.matchAll(/_send\(\s*"([a-z0-9_]+)"\s*,\s*new\s+(\w+)?\s*\{/g)) {
    const action = m[1];
    const typeName = m[2] ?? '';
    const openIndex = m.index + m[0].length - 1;
    const body = text.slice(openIndex + 1, matchBrace(text, openIndex));
    if (jsonNames.has(typeName)) collectFromPoco(typeName, body, action);
    else collectFromAnonymous(body, action);
  }
}

const readKeys = new Set();
const walk = (dir) => {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full);
    else if (entry.name.endsWith('.js') && !full.includes('vendor')) {
      const text = read(full);
      for (const m of text.matchAll(/\b(?:payload|data|msg|message|event|ev|res)\??\.([A-Za-z_]\w*)/g)) readKeys.add(m[1]);
      for (const m of text.matchAll(/\b(?:payload|data|msg|message|event|ev|res)\[['"]([A-Za-z_]\w*)['"]\]/g)) readKeys.add(m[1]);
      for (const m of text.matchAll(/\{([^{}]+)\}\s*=\s*(?:payload|data|msg|message|event|ev|res)\b/g)) {
        for (const name of m[1].split(',')) {
          const key = name.split(':').pop().trim();
          if (/^[A-Za-z_]\w*$/.test(key)) readKeys.add(key);
        }
      }
    }
  }
};
walk('wwwroot');

const camelOf = (k) => k.charAt(0).toLowerCase() + k.slice(1);
const suspects = [...sentBy.entries()].filter(([key]) => !readKeys.has(key));
const casing = suspects.filter(([key]) => key[0] === key[0].toUpperCase() && readKeys.has(camelOf(key)));

console.log(`payload keys sent: ${sentBy.size}; keys the page reads: ${readKeys.size}`);
console.log(`payload properties without [JsonPropertyName]: ${unmapped.length}`);
for (const u of unmapped) console.log(`  ${u.typeName}.${u.prop}  (${u.action})`);

console.log(`\nPascalCase keys whose camelCase twin the page reads: ${casing.length}`);
for (const [key, actions] of casing) console.log(`  ${key} -> ${camelOf(key)}  (${[...actions].join(', ')})`);

console.log(`\nSent but never read (info): ${suspects.length}`);
for (const [key, actions] of suspects) console.log(`  ${key.padEnd(24)} <- ${[...actions].slice(0, 3).join(', ')}`);

process.exit(unmapped.length || casing.length ? 1 : 0);