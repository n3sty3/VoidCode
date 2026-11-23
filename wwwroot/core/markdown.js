// Shared, dependency-light helpers for turning untrusted text/Markdown into safe HTML.
// First step of modularizing the frontend: loaded as a classic script before app.js,
// So these stay global function declarations (no call sites change).

export function escapeHtml(str) {
  if (!str) return '';
  return str.replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
}

export function renderSafeMarkdown(markdownText) {
  const source = String(markdownText || '');
  if (!globalThis.marked || !globalThis.DOMPurify) {
    // Failing closed keeps untrusted model/file content as text if either vendor fails to load.
    return escapeHtml(source).replace(/\n/g, '<br>');
  }

  const rendered = marked.parse(source, { async: false });
  return DOMPurify.sanitize(rendered, {
    USE_PROFILES: { html: true },
    ALLOW_DATA_ATTR: false,
    FORBID_TAGS: [
      'style', 'form', 'input', 'button', 'textarea', 'select', 'option',
      'iframe', 'object', 'embed', 'svg', 'math', 'video', 'audio', 'canvas'
    ],
    FORBID_ATTR: ['style', 'srcdoc', 'form', 'autofocus']
  });
}

// ============================================================================
// The `html` tagged template.
//   Html`<p>${untrusted}</p>`  escapes every interpolation by default
//   Raw(str)                   marks a value as trusted (never escapes it)
//   SetHtml(el, tpl)           the ONLY place in the app that assigns innerHTML
// Kept in core/markdown.js (this file already owns safe-HTML helpers) because adding the
// Planned wwwroot/core/html.js would need a new index.html <script> and grow the
// ScriptTags ratchet. extracts these to core/html.js.
// ============================================================================
const HTML_RAW = Symbol('voidcode.raw');

export function raw(value) {
  return { [HTML_RAW]: true, value: value == null ? '' : String(value) };
}

function htmlValue(value) {
  if (value && value[HTML_RAW]) return value.value;
  if (value == null) return '';
  if (Array.isArray(value)) return value.map(htmlValue).join('');
  return escapeHtml(String(value));
}

export function html(strings, ...values) {
  let out = '';
  for (let i = 0; i < strings.length; i += 1) {
    out += strings[i];
    if (i < values.length) out += htmlValue(values[i]);
  }
  return out;
}

// ============================================================================
// Trusted Types (report-only first).
// Index.html is served with
//   Content-Security-Policy-Report-Only: require-trusted-types-for 'script';
//                                        Trusted-types voidcode-html dompurify
// So every string assignment to an HTML sink is checked. setHtml is the only such sink in
// First-party code, so one pass-through policy makes the whole app Trusted-Types-ready.
//
// Enforcing is deliberately NOT turned on: Monaco and xterm assign strings to innerHTML
// Internally, and that was measured - with an enforcing policy Monaco never initializes and
// The console fills with "This document requires 'TrustedHTML' assignment. The action has been
// Blocked." That is the plan's condition for staying report-only. `dompurify` is allowlisted
// Because the vendored sanitizer registers that policy itself.
// ============================================================================
const TT_POLICY_NAME = 'voidcode-html';
let sinkPolicy = null;
try {
  if (globalThis.trustedTypes && typeof globalThis.trustedTypes.createPolicy === 'function') {
    // One policy for the app's own trusted sinks: the HTML sink (setHtml) and the script-URL sink
    // Used by the module entry point to load the not-yet-converted classic scripts.
    sinkPolicy = globalThis.trustedTypes.createPolicy(TT_POLICY_NAME, {
      createHTML: (input) => String(input),
      createScriptURL: (input) => String(input)
    });
  }
} catch (error) {
  logSwallowed(error, 'markdown.trustedTypesPolicy');
}

function trustedHtml(value) {
  if (!sinkPolicy) return value;
  try { return sinkPolicy.createHTML(String(value)); }
  catch (error) { logSwallowed(error, 'markdown.trustedHtml'); return value; }
}

// Main.js assigns <script src> for the classic scripts that are not modules yet,
// And a script URL is a Trusted Types sink too. Routing it through the same policy keeps the
// Report-only policy clean and leaves nothing to fix when it becomes enforcing.
export function trustedScriptUrl(value) {
  if (!sinkPolicy) return value;
  try { return sinkPolicy.createScriptURL(String(value)); }
  catch (error) { logSwallowed(error, 'markdown.trustedScriptUrl'); return value; }
}

// Report-only never blocks, so a violation is a signal rather than a recovery path: keep a
// Bounded list for the UI harness and forward each one to the host log (a "csp." context on
// Js_log, which MainWindow routes to LoggingService instead of only the debugger).
globalThis.__cspViolations = [];
function reportCspViolation(event) {
  const violation = {
    directive: event.violatedDirective || '',
    source: event.sourceFile || '',
    line: event.lineNumber || 0,
    sample: String(event.sample || '').slice(0, 120)
  };
  if (globalThis.__cspViolations.length < 100) globalThis.__cspViolations.push(violation);
  try {
    const detail = `CSP ${violation.directive} at ${violation.source || 'inline'}:${violation.line} sample=${violation.sample}`;
    if (globalThis.VoidBridge && typeof globalThis.VoidBridge.send === 'function') {
      globalThis.VoidBridge.send('js_log', { level: 'warn', msg: detail, context: 'csp.violation' });
    }
  } catch (error) {
    logSwallowed(error, 'markdown.reportCspViolation');
  }
}

// There is no document when this file is loaded outside a browser (the node unit tests), so the
// Listener is attached only when there is one.
if (typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
  document.addEventListener('securitypolicyviolation', reportCspViolation);
}

export function setHtml(el, template) {
  if (!el) return el;
  const markup = template == null ? '' : ((template && template[HTML_RAW]) ? template.value : String(template));
  // eslint-disable-next-line no-unsanitized/property -- setHtml is the single sanctioned innerHTML sink
  el.innerHTML = trustedHtml(markup);
  return el;
}

// The classic scripts that have not been converted
// Yet still call these helpers as globals, so they are published here as the temporary shim the
// Plan allows. drops the shim as each consumer becomes a module.
Object.assign(globalThis, { escapeHtml, renderSafeMarkdown, raw, html, setHtml, trustedHtml });