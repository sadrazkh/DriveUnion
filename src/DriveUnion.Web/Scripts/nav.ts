/**
 * The collapsible sidebar below 900px.
 *
 * Not a Vue island: the sidebar is server-rendered Razor and this only flips one attribute on the
 * shell. Wrapping that in a component would mean re-rendering the navigation in JavaScript to
 * change a class name.
 *
 * Progressive enhancement is the point of the ordering here. The stylesheet leaves the sidebar as
 * a plain block at the top of the page while the shell has no `data-nav` attribute, so a checkout
 * with no bundle — or a bundle that failed to load — is still navigable on a phone. This function
 * is what sets `data-nav="closed"` and thereby opts into the off-canvas behaviour.
 */
export function mountNavToggle(): void {
  const shell = document.querySelector<HTMLElement>('[data-shell]');
  const toggle = document.querySelector<HTMLButtonElement>('[data-nav-toggle]');
  const scrim = document.querySelector<HTMLElement>('[data-nav-scrim]');
  if (!shell || !toggle) return;

  const set = (open: boolean) => {
    shell.dataset.nav = open ? 'open' : 'closed';
    toggle.setAttribute('aria-expanded', String(open));
  };

  set(false);

  toggle.addEventListener('click', () => set(shell.dataset.nav !== 'open'));
  scrim?.addEventListener('click', () => set(false));

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && shell.dataset.nav === 'open') set(false);
  });

  // Above 900px the media query stops applying and the sidebar is a column again; leaving the
  // attribute at "open" would then reopen it as an overlay the next time the window narrowed.
  matchMedia('(min-width: 901px)').addEventListener('change', (event) => {
    if (event.matches) set(false);
  });
}
