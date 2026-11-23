// The top bar's operator control collapses to a dot below 1460px, so its tooltip has to
// Carry the name and the hub label has to stay in sync. That is a top-bar concern, so it lives here
// Rather than in app.js.
export function init() {
  if (typeof window === 'undefined') return;

  // `globalThis` is typed loosely enough to carry the classic script's global, which `window` is not.
  globalThis.setTopbarOperator = (name, hubText) => {
    document.getElementById('topbar-operator-name').textContent = name;
    document.getElementById('btn-operator-pill').title = `Operator: ${name} - profile`;
    document.getElementById('hub-operator-label').textContent = hubText;
  };
}