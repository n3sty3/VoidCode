// The top bar's operator control collapses to a dot below 1460px, so its tooltip has to
// Carry the name and the hub label has to stay in sync. Also adapts native window chrome and
// Shell profile labels when running on macOS.
export function init() {
  if (typeof window === 'undefined') return;

  const isMac = /mac/i.test(navigator.platform || navigator.userAgent || '');
  document.documentElement.dataset.platform = isMac ? 'macos' : 'windows';

  if (isMac) {
    const adaptMacChrome = () => {
      // macOS Photino hosts a native titled NSWindow with traffic lights; hide duplicate Windows caption buttons.
      document.querySelectorAll('.win-caption-group').forEach((el) => {
        /** @type {HTMLElement} */ (el).style.display = 'none';
      });
      const shellChip = document.getElementById('console-chip-shell');
      if (shellChip && shellChip.textContent === 'powershell') {
        shellChip.textContent = 'zsh';
      }
      const profileMenu = document.getElementById('terminal-profile-menu');
      if (profileMenu) {
        const psItem = profileMenu.querySelector('[data-shell-profile="powershell"]');
        if (psItem) {
          psItem.setAttribute('data-shell-profile', 'zsh');
          const lbl = psItem.querySelector('.ctx-label');
          const hint = psItem.querySelector('.ctx-hint');
          if (lbl) lbl.textContent = 'Zsh (macOS Default)';
          if (hint) hint.textContent = 'zsh';
        }
        const bashItem = profileMenu.querySelector('[data-shell-profile="bash"]');
        if (bashItem) {
          const lbl = bashItem.querySelector('.ctx-label');
          if (lbl) lbl.textContent = 'Bash';
        }
        profileMenu.querySelectorAll('[data-shell-profile="cmd"], [data-shell-profile="wsl"]').forEach((el) => {
          /** @type {HTMLElement} */ (el).style.display = 'none';
        });
      }
    };
    if (document.readyState === 'loading') {
      document.addEventListener('DOMContentLoaded', adaptMacChrome, { once: true });
    } else {
      adaptMacChrome();
    }
  }

  // `globalThis` is typed loosely enough to carry the classic script's global, which `window` is not.
  globalThis.setTopbarOperator = (name, hubText) => {
    document.getElementById('topbar-operator-name').textContent = name;
    document.getElementById('btn-operator-pill').title = `Operator: ${name} - profile`;
    document.getElementById('hub-operator-label').textContent = hubText;
  };
}