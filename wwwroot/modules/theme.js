// Themes & glass optics: applyThemeAndOptics (theme attr, blur, field opacity, persistence).
//
// Main.js calls init(), and the module keeps
// Publishing window.applyThemeAndOptics as the temporary shim for the classic scripts that still
// Call it (app.js, console/core.js, modules/settings.js).
//
// Collaborators: host messages go out through VoidBridge.send (app.js's sendHost is a script-scoped
// Const, so a module cannot reach it) and the settings come from the state store.
// GatherSettingsFromModal is read off the global instead of imported because modules/settings.js
// Imports applyThemeAndOptics from here - an import back would close a cycle. settings.js's init()
// Publishes the shim before app.js loads, so the read is always live.
let hostSend = (action, data) => globalThis.VoidBridge.send(action, data);

function sendHost(action, data = {}) {
  return hostSend(action, data);
}

function gatherSettingsFromModal() {
  return typeof globalThis.gatherSettingsFromModal === 'function' ? globalThis.gatherSettingsFromModal() : {};
}

// Themes & Glass Optics Customization (synced between floating window & settings modal)
// One typed lookup instead of a cast per call site - the controls this module reads
// By id are inputs, and tsc only knows getElementById returns a plain HTMLElement.
/**
 * @param {string} id
 * @returns {HTMLInputElement | null}
 */
function control(id) {
  return /** @type {HTMLInputElement | null} */ (document.getElementById(id));
}

export function applyThemeAndOptics(themeName, blurPx, fieldPct, persist = false) {
  // The settings slice comes from the store, not from app.js's global.
  const settings = window.VoidState.get('settings') || {};
  const theme = themeName || settings.Theme || 'harbor';
  const blur = blurPx ?? settings.GlassBlur ?? 22;
  const field = fieldPct ?? settings.FieldOpacity ?? 55;

  document.documentElement.setAttribute('data-theme', theme);
  document.documentElement.style.setProperty('--blur', `${blur}px`);
  // Blur at 0 means no backdrop pass at all, not a 0px blur (which still costs GPU)
  document.documentElement.setAttribute('data-glass', Number(blur) > 0 ? 'on' : 'off');
  const effectiveOpacity = field <= 0 ? 0 : Math.min(0.95, 0.38 + (field / 100) * 0.62);
  document.documentElement.style.setProperty('--field-opacity', String(effectiveOpacity.toFixed(2)));

  // The PTY palette is derived from these tokens — restain it with the shell
  globalThis.refreshTerminalThemes?.();

  document.querySelectorAll('[data-theme-pick]').forEach(btn => {
    btn.classList.toggle('is-active', btn.getAttribute('data-theme-pick') === theme);
  });

  const themeBadge = control('lbl-active-theme');
  if (themeBadge) themeBadge.textContent = theme.charAt(0).toUpperCase() + theme.slice(1);

  ['slider-glass-blur', 'settings-slider-blur'].forEach(id => {
    const el = control(id);
    if (el) { el.value = blur; globalThis.updateSliderFill?.(el); }
  });
  ['lbl-blur-val', 'settings-lbl-blur'].forEach(id => {
    const el = control(id);
    if (el) el.textContent = `${blur}px`;
  });

  ['slider-field-opacity', 'settings-slider-field'].forEach(id => {
    const el = control(id);
    if (el) { el.value = field; globalThis.updateSliderFill?.(el); }
  });
  ['lbl-field-val', 'settings-lbl-field'].forEach(id => {
    const el = control(id);
    if (el) el.textContent = `${field}%`;
  });

  settings.Theme = theme;
  settings.GlassBlur = blur;
  settings.FieldOpacity = field;

  sendHost('set_theme_icon', { theme });

  if (persist) {
    const merged = { ...settings, ...gatherSettingsFromModal(), Theme: theme, GlassBlur: blur, FieldOpacity: field };
    sendHost('save_settings', { settings: merged });
  }
}

// Main.js calls this once. ctx.sendHost lets a caller (or a test) substitute the
// Host transport; everything else is looked up at call time.
export function init(ctx = {}) {
  if (typeof ctx.sendHost === 'function') hostSend = ctx.sendHost;
  globalThis.applyThemeAndOptics = applyThemeAndOptics;
  return { applyThemeAndOptics };
}
