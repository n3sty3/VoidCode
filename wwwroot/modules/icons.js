// The themed icon pack (VoidIcons), 
//
// It sat in console/ because the fallback shell was the first thing that needed an icon, but the
// Activity rail and the status bar's LSP badge use it too - so it cannot load lazily with the shell.
// And main.js calls init() before the classic scripts, which is the same
// Moment the old <script> used to run.
//
// Rule, unchanged: the THEME picks the palette (one folder per theme), the STATE picks the variant
// (muted = inactive, ui = active/emphasis, accent = highlighted). Never CSS filters: the pack is
// Baked, so filters would corrupt it.

const PACK = {
  harbor: 'harbor-glass',
  obsidian: 'obsidian-frost',
  crimson: 'crimson-dusk',
  emerald: 'emerald-matrix',
  nebula: 'violet-nebula'
};

function theme() {
  return document.documentElement.getAttribute('data-theme') || 'harbor';
}

function base() {
  const pack = PACK[theme()] || PACK.harbor;
  return 'icons/voidicons-' + pack + '/svg/' + pack + '/';
}

function src(name, variant) {
  return base() + (variant || 'ui') + '/' + name + '.svg';
}

function render(host) {
  const name = host.getAttribute('data-icon');
  if (!name) return;

  const variant = host.getAttribute('data-variant') || 'ui';
  const url = src(name, variant);

  let img = host.querySelector('img');
  if (!img) {
    img = document.createElement('img');
    img.alt = '';
    img.draggable = false;
    img.setAttribute('aria-hidden', 'true');
    host.appendChild(img);
  }

  if (img.getAttribute('src') !== url) img.setAttribute('src', url);
  host.setAttribute('data-icon-variant', variant);
}

function apply(root) {
  const scope = root && root.querySelectorAll ? root : document;
  scope.querySelectorAll('[data-icon]').forEach(render);
  if (scope !== document && scope.hasAttribute && scope.hasAttribute('data-icon')) render(scope);
}

function refresh() { apply(document); syncStates(); }

/* Rail buttons carry their state in a class, but the pack encodes state in the
   variant folder (muted = inactive, ui = active). Keep the two in sync. */
function syncStates() {
  document.querySelectorAll('#activity-rail .rail-btn').forEach(function (btn) {
    const host = btn.querySelector('[data-icon]');
    if (!host) return;
    const want = btn.classList.contains('is-active') ? 'ui' : 'muted';
    if (host.getAttribute('data-variant') !== want) {
      host.setAttribute('data-variant', want);
      render(host);
    }
  });
}

function boot() {
  refresh();
  const rail = document.getElementById('activity-rail');
  if (rail) {
    new MutationObserver(syncStates).observe(rail, {
      subtree: true,
      attributes: true,
      attributeFilter: ['class']
    });
  }
}

// Published as window.VoidIcons for the classic scripts (status-bar.js calls apply() when the LSP
// Badge changes its icon).
export function init() {
  window.VoidIcons = { apply, refresh, syncStates, src, theme };

  // Main.js runs before DOMContentLoaded, so the old "refresh once the DOM is there" timing holds.
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();

  // Switching theme repoints every icon: the pack is per-theme, not tinted
  new MutationObserver(refresh).observe(document.documentElement, {
    attributes: true,
    attributeFilter: ['data-theme']
  });

  return window.VoidIcons;
}