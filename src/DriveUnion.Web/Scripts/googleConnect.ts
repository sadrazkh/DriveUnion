/**
 * «افزودن اکانت با OAuth» in a popup instead of taking the whole panel to Google and back.
 *
 * Not a Vue island, and not a fetch: the control is a server-rendered `<form method="post">` with an
 * antiforgery token in it, and the classic technique keeps it that way. Open an empty window with a
 * name, point the form's `target` at that name, and let the browser submit — the POST, its token and
 * the session cookie are the form's own, so nothing here needs a GET endpoint that would have to be
 * exempt from CSRF to exist.
 *
 * Every branch below ends at a flow that works. `window.open` returning null is a blocked popup, and
 * the answer to that is the same-tab post the panel has always done, not a button that does nothing.
 * With no bundle at all this file never runs and the form is still the M1 flow, unchanged.
 */
const WINDOW_NAME = 'duGoogleConnect';

/** Both halves of the conversation agree on this string, so a stray message from an extension or an
 *  embedded frame cannot be mistaken for the end of a consent flow. */
const MESSAGE_SOURCE = 'drive-union-google-connect';

/** Roughly what Google's consent screen wants; narrower and the account chooser scrolls. */
const POPUP_WIDTH = 520;
const POPUP_HEIGHT = 680;

export function mountGoogleConnect(): void {
  mountRedirectUriCopy();
  mountSetupJump();

  const form = document.querySelector<HTMLFormElement>('[data-google-connect]');
  const flag = form?.querySelector<HTMLInputElement>('[data-google-connect-popup]');
  if (!form || !flag) return;

  const status = document.querySelector<HTMLElement>('[data-google-connect-status]');
  let watch = 0;
  let settled = false;

  /**
   * The flow ended: reload the accounts list.
   *
   * A reload rather than a partial update because the callback has already written its sentence into
   * TempData, and this GET is what renders it — so the page lands in exactly the state the
   * no-JavaScript round trip would have left it in, with one code path instead of two.
   */
  const finish = (): void => {
    if (settled) return;
    settled = true;
    window.clearInterval(watch);
    window.location.reload();
  };

  window.addEventListener('message', (event: MessageEvent) => {
    // The origin check comes first and is not a formality: any page on the internet can postMessage
    // to this window, and without this line one of them could make the panel reload on command.
    if (event.origin !== window.location.origin) return;

    const data: unknown = event.data;
    if (typeof data !== 'object' || data === null) return;
    if ((data as { source?: unknown }).source !== MESSAGE_SOURCE) return;

    finish();
  });

  form.addEventListener('submit', () => {
    // Opened from inside the submit handler on purpose: a window opened outside a user gesture is
    // precisely what popup blockers exist to stop.
    const child = window.open('', WINDOW_NAME, features());

    if (!child) {
      // Removed rather than blanked, and reset every time: a target left over from an earlier click
      // would aim this submission at a window that is no longer there.
      form.removeAttribute('target');
      flag.value = 'false';
      return;
    }

    form.target = WINDOW_NAME;
    flag.value = 'true';
    child.focus();

    if (status) {
      status.textContent = 'پنجره‌ی ورود به گوگل باز شد. اگر آن را نمی‌بینید، پشت این صفحه است.';
      status.hidden = false;
    }

    window.clearInterval(watch);
    watch = window.setInterval(() => {
      if (!child.closed) return;

      // The window went away without saying anything: the operator closed it at Google's screen, or
      // a deployment put the redirect URI on a different host so the message was never delivered.
      // Reloading is right in both cases — it is the same GET the flow ends on either way.
      finish();
    }, 400);
  });
}

/**
 * The primary action while there is no OAuth client: a link to the setup panel, made to arrive.
 *
 * Without this file the anchor is still a working route — the browser's own fragment navigation
 * scrolls the panel into view and it is already unfolded, because it renders unfolded whenever it
 * is incomplete. What is added here is the last two feet of the journey: unfold the panel even if
 * the operator folded it earlier in this visit, and put the caret in the field they came to fill.
 *
 * The default is prevented rather than left to run alongside, because a fragment jump and a smooth
 * scroll to the same element at the same time is one visible jerk followed by a slide.
 */
function mountSetupJump(): void {
  const jump = document.querySelector<HTMLAnchorElement>('[data-setup-jump]');
  if (!jump) return;

  // From the href itself, so the markup keeps deciding where this goes and there is no second copy
  // of the id in here to drift from it.
  const panel = jump.hash.length > 1 ? document.getElementById(jump.hash.slice(1)) : null;
  if (!(panel instanceof HTMLDetailsElement)) return;

  jump.addEventListener('click', (event: MouseEvent) => {
    // A modified click is the operator asking for a new tab or a saved link; that is the browser's
    // to answer, not ours.
    if (event.defaultPrevented || event.button !== 0) return;
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;

    event.preventDefault();
    panel.open = true;

    panel.scrollIntoView({
      block: 'start',
      behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth',
    });

    // preventScroll, because focus would otherwise scroll the field to the top of the viewport and
    // undo the scroll above — the operator would land past the instructions they were sent to read.
    panel.querySelector<HTMLInputElement>('[data-setup-focus]')?.focus({ preventScroll: true });
  });
}

/**
 * One click for the redirect URI the operator has to paste into Google Cloud.
 *
 * The button is rendered hidden and shown here, not the other way round: `navigator.clipboard` is
 * absent on an insecure origin and can be refused by permissions policy, and a copy button that
 * silently does nothing is worse than no button — the operator would believe they had copied it and
 * paste whatever was there before. Without this the `<code>` still carries `user-select: all`, so a
 * single click selects the whole URI and Ctrl+C finishes the job.
 */
function mountRedirectUriCopy(): void {
  const button = document.querySelector<HTMLButtonElement>('[data-copy]');
  const source = document.querySelector<HTMLElement>('[data-copy-value]');
  if (!button || !source || !navigator.clipboard) return;

  const label = button.textContent ?? '';
  let restore = 0;

  button.hidden = false;

  button.addEventListener('click', () => {
    const value = source.textContent?.trim() ?? '';
    if (value.length === 0) return;

    void navigator.clipboard.writeText(value).then(
      () => {
        button.textContent = 'رونوشت شد';
        window.clearTimeout(restore);
        restore = window.setTimeout(() => {
          button.textContent = label;
        }, 1600);
      },
      () => {
        // Denied at the browser, which is a real outcome and not an error worth a console entry.
        // Selecting the text is the fallback the markup already supports, so say that instead of
        // leaving a button that appears to have worked.
        button.textContent = 'اجازه داده نشد — متن را انتخاب و کپی کنید';
      },
    );
  });
}

/** Centred on the window the operator is looking at, which on a second monitor is not the screen. */
function features(): string {
  const hostWidth = window.outerWidth || window.screen.width;
  const hostHeight = window.outerHeight || window.screen.height;

  const width = Math.min(POPUP_WIDTH, hostWidth);
  const height = Math.min(POPUP_HEIGHT, hostHeight);

  const left = Math.round((window.screenLeft ?? window.screenX) + (hostWidth - width) / 2);
  const top = Math.round((window.screenTop ?? window.screenY) + (hostHeight - height) / 2);

  // No `noopener` and no `noreferrer`: either one nulls the child's window.opener, which is the
  // channel the callback page reports back over.
  return `popup=yes,width=${width},height=${height},left=${left},top=${top}`;
}
