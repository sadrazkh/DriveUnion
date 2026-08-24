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
 *
 * All three elements it binds to live above `main.app-content`, so a navigation no longer disturbs
 * any of them: this is mounted once and survives every swap. The one thing a swap has to be told is
 * that the drawer should close behind it — see the controller returned below.
 */
export interface NavToggle {
  /** Closes the drawer, taking focus out of it first if that is where the reader left it. */
  close(): void;
}

export function mountNavToggle(): NavToggle {
  const shell = document.querySelector<HTMLElement>('[data-shell]');
  const toggle = document.querySelector<HTMLButtonElement>('[data-nav-toggle]');
  const scrim = document.querySelector<HTMLElement>('[data-nav-scrim]');

  // The public download page has no shell and no drawer. A controller that does nothing is the
  // honest answer there; the alternative is every caller asking whether the panel exists.
  if (!shell || !toggle) return { close: () => {} };

  const sidebar = document.querySelector<HTMLElement>('.app-sidebar');

  const set = (open: boolean, moveFocus = false) => {
    // Focus is moved before the attribute flips on the way out, because the closed sidebar is
    // `visibility: hidden` and a browser will not leave focus on a hidden element — it drops it on
    // the body, and the next Tab starts the page again from the top.
    if (!open && moveFocus && sidebar?.contains(document.activeElement)) toggle.focus();

    shell.dataset.nav = open ? 'open' : 'closed';
    toggle.setAttribute('aria-expanded', String(open));

    // The sidebar is before the header in the document, so Tab from the button that just opened the
    // menu lands in the search box — behind the scrim, on a page the reader cannot see. Moving
    // focus to the first item makes the next Tab continue down the menu. Not a focus trap: Tab past
    // the last item leaves for the page behind, which is what Escape and the scrim are also for.
    // a/button and not `.nav-item`: two of the slots are <span aria-disabled> placeholders for
    // screens that have no controller yet, and focus() on one of those does nothing at all.
    if (open && moveFocus) sidebar?.querySelector<HTMLElement>('a.nav-item, button.nav-item')?.focus();
  };

  set(false);

  toggle.addEventListener('click', () => set(shell.dataset.nav !== 'open', true));
  scrim?.addEventListener('click', () => set(false, true));

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && shell.dataset.nav === 'open') set(false, true);
  });

  // Above 900px the media query stops applying and the sidebar is a column again; leaving the
  // attribute at "open" would then reopen it as an overlay the next time the window narrowed.
  matchMedia('(min-width: 901px)').addEventListener('change', (event) => {
    if (event.matches) set(false);
  });

  // Navigation no longer reloads the page, so nothing closes the drawer on the way through a link
  // any more: on a phone the menu would stay open on top of the screen it was used to reach, and
  // the reader would have to dismiss the thing they just successfully used.
  return { close: () => set(false, true) };
}
