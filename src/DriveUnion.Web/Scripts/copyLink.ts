/**
 * The two buttons beside a share link: «کپی», and «هم‌رسانی» where the browser has a share sheet.
 *
 * Not a Vue island, even though it is reached through the same `data-island` registry: the element
 * is already the finished button, server-rendered with its own label. Mounting an app here would
 * replace that markup with a copy of itself for no gain, and would take the button away from anyone
 * whose bundle has not arrived yet.
 */
export function mountCopyLink(el: HTMLElement): void {
  const found = resolve(el);
  if (!found) return;

  const { button, value } = found;

  mountShare(button, value);

  const original = button.textContent ?? '';
  const copied = document.documentElement.lang === 'en' ? 'Copied' : 'کپی شد';
  let restore = 0;

  button.addEventListener('click', () => {
    void write(value).then((ok) => {
      if (!ok) return;

      button.textContent = copied;
      window.clearTimeout(restore);
      restore = window.setTimeout(() => {
        button.textContent = original;
      }, 1600);
    });
  });
}

/**
 * Which element is the button, and what it copies.
 *
 * <b>Two views draw this control two ways, and for a long time only one of them worked.</b> The
 * share-link box is a bare button carrying the address; the API keys screen is a readout with a
 * button inside it, and mounts the island on the box. This function used to read `data-value` off
 * the mount point and return when it found none — so on the API keys screen no listener was ever
 * attached and both «کپی» buttons did nothing at all when pressed.
 *
 * That is worse than a dead control. An API secret is shown exactly once and the row keeps only its
 * SHA-256, so a customer who pressed Copy, saw a button that looked like every other button, and
 * navigated away had a key they could no longer read and could only revoke.
 *
 * Supporting both shapes rather than rewriting one of the views: they are genuinely different
 * controls — one is a button, one is a box with a readout in it — and the registry's own contract is
 * that a mount point's `data-*` attributes are its props. Unifying the markup would be tidier and is
 * not worth touching three views to get.
 */
function resolve(el: HTMLElement): { button: HTMLElement; value: string } | null {
  // The element is the button and carries the address itself.
  if (el.dataset.value) return { button: el, value: el.dataset.value };

  // The element is the box around a readout and a button.
  const value = el.dataset.copyValue;
  const button = el.querySelector<HTMLElement>('[data-copy-button]');

  return value && button ? { button, value } : null;
}

/**
 * The «هم‌رسانی» button beside the «کپی» one, which opens the phone's own share sheet.
 *
 * On a phone the clipboard is the wrong verb. Nobody wants a link on their clipboard; they want it
 * in a message to one particular person, and `navigator.share` is the one thing iOS gives a web app
 * in that direction. Beside the copy button and never instead of it — a browser with no sheet has
 * no share, and the copy button is what that reader has always used.
 *
 * Reached from here rather than registered as an island of its own: the two buttons are one control
 * with one value between them, and the share half has nothing to do without the URL the copy half
 * already carries. That is also why it takes no `data-value` of its own — a second copy of the
 * address in the markup is a second one to keep in step, and the two disagreeing would send somebody
 * a link to the wrong file.
 */
function mountShare(copy: HTMLElement, url: string): void {
  // A sibling, because the view draws both inside the one `.field` box the address sits in. Read
  // through the parent rather than from the document: a page can hold more than one of these.
  const button = copy.parentElement?.querySelector<HTMLButtonElement>('[data-share]');
  if (!button) return;

  // The whole of the decision. `navigator.share` is absent on every browser with no sheet to open
  // and absent outside a secure context, and it throws where it exists but is refused by permissions
  // policy — so the button is rendered hidden by the server and is only ever shown here. A control
  // that fails when pressed is worse than one that was never drawn: the reader believes they have
  // sent the link.
  if (typeof navigator.share !== 'function') return;

  // Built once, at mount, because none of it changes: a navigation replaces this whole region and
  // brings a fresh button with a fresh address on it.
  const payload: ShareData = { url };

  // The name of the file, which mail targets use as the subject line, and the sentence the
  // recipient reads above the link. Both are written by Razor into data-* rather than assembled
  // here — a bundle is compiled once and cannot ask which language the request was in, which is how
  // the redirect-URI copy button came to answer an English panel in Persian. Omitted rather than
  // sent empty: a share sheet handed `title: ''` offers an empty subject line.
  const title = button.dataset.shareTitle;
  const text = button.dataset.shareText;

  if (title) payload.title = title;
  if (text) payload.text = text;

  const label = button.textContent ?? '';
  const refused = button.dataset.shareRefused ?? label;

  button.hidden = false;

  button.addEventListener('click', () => {
    void navigator.share(payload).catch((reason: unknown) => {
      // The sheet opened and the sender closed it again without picking anybody. That is the
      // control working, so it says nothing at all — a «could not share» after a deliberate
      // dismissal is the panel calling the reader's own decision a failure.
      if (reason instanceof DOMException && reason.name === 'AbortError') return;

      // Everything else is a real refusal and is answered rather than swallowed with it. The label
      // stays as it is: the sentence is an instruction, and the button it points at is next to it.
      button.textContent = refused;
    });
  });
}

/**
 * `navigator.clipboard` needs a secure context. The panel is served over https and localhost counts
 * as one, so the fallback is for the case nobody plans: a deployment reached by IP or over plain
 * http, where the modern call is simply absent and a silent no-op would look like a broken button.
 */
async function write(value: string): Promise<boolean> {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(value);
      return true;
    } catch {
      // Denied by permission or by the context. Fall through rather than give up.
    }
  }

  const scratch = document.createElement('textarea');
  scratch.value = value;
  scratch.setAttribute('readonly', '');
  scratch.style.position = 'fixed';
  scratch.style.insetInlineStart = '-9999px';
  document.body.appendChild(scratch);

  try {
    scratch.select();
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    scratch.remove();
  }
}
