// Focus management for every dialog.
//
// What was wrong: app.js had a Tab trap that looked for an open dialog with
// `m.style.display !== 'none' && m.offsetParent !== null`. `.modal-overlay` is `position: fixed`, and a
// Fixed element's `offsetParent` is always null, so the trap never found a dialog and never ran - a
// Keyboard user tabbed straight out of Settings into the app behind it.
//
// What this module does instead, for any visible dialog:
//   * remembers what had focus before, focuses the first control inside, and restores focus on close;
//   * marks everything outside the dialog's ancestor chain `inert`, so the window behind cannot be
//     Clicked or tabbed into;
//   * cycles Tab/Shift+Tab inside the dialog.
//
// Detection watches the DOM instead of each opener: dialogs are opened by toggling a class or an inline
// Style from a dozen places (app.js and every module), and a MutationObserver catches all of them
// Without editing any of them.

const DIALOG_SELECTOR = '[role="dialog"]';
// A dialog only counts as modal when it sits in a modal surface or says so itself. The inline-AI widget
// Is a `role="dialog"` popover that must NOT make the whole window inert.
const MODAL_CONTAINER_SELECTOR = '.modal-overlay, .stage-overlay';

const FOCUSABLE = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled]):not([type="hidden"])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])'
].join(', ');

function isVisible(el) {
  if (!el || !el.isConnected) return false;
  const style = getComputedStyle(el);
  if (style.display === 'none' || style.visibility === 'hidden') return false;
  return el.getClientRects().length > 0; // works for position:fixed, unlike offsetParent
}

function isModal(dialog) {
  if (!isVisible(dialog)) return false;
  if (dialog.closest('.editor-tool-pane, .is-tab-docked')) return false;
  if (dialog.getAttribute('aria-modal') === 'true') return true;
  return dialog.closest(MODAL_CONTAINER_SELECTOR) !== null && isVisible(dialog.parentElement);
}

function topModal() {
  const modals = Array.from(document.querySelectorAll(DIALOG_SELECTOR)).filter(isModal);
  return modals.length ? modals[modals.length - 1] : null;
}

function focusables(root) {
  return Array.from(root.querySelectorAll(FOCUSABLE)).filter((el) => isVisible(el) && el.tabIndex >= 0);
}

/** Marks every sibling on the path from `dialog` to <body> inert; returns a restore function. */
function inertOutside(dialog) {
  const touched = [];
  let node = dialog;
  while (node && node !== document.body && node.parentElement) {
    for (const sibling of Array.from(node.parentElement.children)) {
      if (sibling === node || sibling.inert) continue;
      if (sibling.tagName === 'SCRIPT' || sibling.tagName === 'STYLE') continue;
      sibling.inert = true;
      touched.push(sibling);
    }
    node = node.parentElement;
  }
  return () => {
    for (const el of touched) el.inert = false;
  };
}

let active = null;
let opener = null;
let releaseInert = null;

function activate(dialog) {
  if (active === dialog) return;
  if (active) deactivate();

  active = dialog;
  opener = document.activeElement instanceof HTMLElement && !dialog.contains(document.activeElement)
    ? document.activeElement
    : null;
  releaseInert = inertOutside(dialog);

  const first = focusables(dialog)[0];
  if (first) {
    first.focus({ preventScroll: true });
  } else {
    if (!dialog.hasAttribute('tabindex')) dialog.setAttribute('tabindex', '-1');
    dialog.focus({ preventScroll: true });
  }
}

function deactivate() {
  if (!active) return;
  const restoreTo = opener;
  active = null;
  opener = null;
  releaseInert?.();
  releaseInert = null;
  if (restoreTo && restoreTo.isConnected) restoreTo.focus({ preventScroll: true });
}

/** Re-evaluates which dialog is open. Idempotent; safe to call from anywhere. */
export function syncModalFocus() {
  const dialog = topModal();
  if (dialog) activate(dialog);
  else deactivate();
}

export function installModalFocus() {
  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Tab' || !active) return;
    const items = focusables(active);
    if (!items.length) {
      event.preventDefault();
      return;
    }
    const first = items[0];
    const last = items[items.length - 1];
    const current = document.activeElement;
    if (event.shiftKey && (current === first || !active.contains(current))) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && (current === last || !active.contains(current))) {
      event.preventDefault();
      first.focus();
    }
  }, true);

  // Escape keeps working the way each dialog already implements it; this listener only makes sure a
  // Dialog that hides itself leaves the document in a usable state (it does, via the observer below).

  document.addEventListener('focusin', (event) => {
    if (!active || active.contains(event.target)) return;
    const items = focusables(active);
    (items[0] ?? active).focus({ preventScroll: true });
  });

  new MutationObserver(() => syncModalFocus()).observe(document.body, {
    subtree: true,
    attributes: true,
    attributeFilter: ['style', 'class', 'hidden', 'open', 'aria-modal']
  });

  syncModalFocus();
}