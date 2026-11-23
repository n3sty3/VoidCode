// Node unit test: the `html` tagged template escapes every interpolation by default,
// Raw() opts a value out, and setHtml() is the only innerHTML assignment path.
// Run with: node tests/html-template.test.js
import assert from 'node:assert';
import { escapeHtml, html, raw, setHtml } from '../wwwroot/core/markdown.js';

// Core/markdown.js is an ES module, so the helpers are imported directly instead
// Of being evaluated in a vm context.
assert.ok(html && raw && setHtml && escapeHtml, 'html/raw/setHtml/escapeHtml must all be defined');

// Every interpolation is escaped by default.
assert.equal(html`<p>${'<img src=x onerror=alert(1)>'}</p>`, '<p>&lt;img src=x onerror=alert(1)&gt;</p>');
// Quotes are escaped, so a value cannot break out of an attribute.
assert.equal(html`<a title="${'" onmouseover="alert(1)'}">x</a>`, '<a title="&quot; onmouseover=&quot;alert(1)">x</a>');
// A literal ${...} inside a value is inert text.
assert.equal(html`<div data-x="${'a${b}c'}">t</div>`, '<div data-x="a${b}c">t</div>');
// Raw() opts a value out of escaping (for HTML we built ourselves).
assert.equal(html`<b>${raw('<i>ok</i>')}</b>`, '<b><i>ok</i></b>');
// Null/undefined become empty; arrays are joined after escaping each item.
assert.equal(html`<p>${null}${undefined}</p>`, '<p></p>');
assert.equal(html`${['a', '<b>']}`, 'a&lt;b&gt;');
// A template with no interpolation is returned verbatim.
assert.equal(html`<p>plain</p>`, '<p>plain</p>');
// SetHtml is the single innerHTML assignment point.
const el = { innerHTML: 'stale' };
setHtml(el, html`<p>${'<x>'}</p>`);
assert.equal(el.innerHTML, '<p>&lt;x&gt;</p>');
setHtml(el, raw('<hr>'));
assert.equal(el.innerHTML, '<hr>');
setHtml(el, null);
assert.equal(el.innerHTML, '');
// EscapeHtml is unchanged.
assert.equal(escapeHtml(`<&>"'`), '&lt;&amp;&gt;&quot;&#039;');

console.log('PASS: html escapes interpolations by default; raw()/setHtml() behave');
