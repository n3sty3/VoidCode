// Sandboxed iframe runtime bridge for VoidCode Live Web Preview.
// Runs ONLY inside the opaque `sandbox="allow-scripts"` preview frame, forwarding console
// errors/warnings and uncaught exceptions back to the preview console drawer via postMessage.
(function initVoidPreviewBridge() {
  if (window.parent === window) return;

  function formatArgs(args) {
    return Array.prototype.map.call(args, function (x) {
      if (typeof x === 'string') return x;
      if (x && typeof x.stack === 'string') return x.stack;
      try { return JSON.stringify(x); } catch (_err) { return String(x); }
    }).join(' ');
  }

  function postLog(level, text) {
    try {
      window.parent.postMessage({ __voidPreviewLog: true, level: level, text: String(text || '') }, '*');
    } catch (_err) {
      // Ignore cross-frame messaging errors when preview tab closes mid-load.
    }
  }

  ['error', 'warn'].forEach(function (level) {
    var orig = console[level];
    console[level] = function () {
      postLog(level, formatArgs(arguments));
      if (typeof orig === 'function') orig.apply(console, arguments);
    };
  });

  window.addEventListener('error', function (ev) {
    var loc = ev.lineno ? ' (L' + ev.lineno + ')' : '';
    postLog('error', (ev.message || 'Runtime error') + loc);
  });

  window.addEventListener('unhandledrejection', function (ev) {
    postLog('error', 'Unhandled promise rejection: ' + String(ev.reason || ''));
  });

  function runPreviewInlineScripts() {
    var nodes = document.querySelectorAll('script[type="text/void-preview-inline"]');
    nodes.forEach(function (node) {
      var code = node.textContent || '';
      if (!code.trim()) return;
      try {
        var fn = new Function(code);
        fn();
      } catch (err) {
        postLog('error', err && err.stack ? err.stack : String(err));
      }
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', runPreviewInlineScripts, { once: true });
  } else {
    runPreviewInlineScripts();
  }
}());
