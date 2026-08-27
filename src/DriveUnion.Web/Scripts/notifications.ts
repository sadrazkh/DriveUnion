import { serviceWorkerReady } from './serviceWorker';

/**
 * The subscribe control on the notifications screen.
 *
 * Not a Vue island, for the reason `copyLink.ts` is not one: the card is already the finished
 * markup, server-rendered, with every sentence it can say drawn and hidden. What this adds is the
 * one question a server cannot answer — which of those states this particular browser is in — and
 * the two calls that follow a press.
 *
 * ── The four states, and why the button is hidden until one is chosen ────────────────────────────
 *
 * A control that appears and then fails when pressed is worse than one that was never drawn: the
 * reader believes the thing is on. Three of the four reasons it may not work are only knowable
 * here:
 *
 *   - **No push at all.** No `PushManager`, no `Notification`, or a browser with service workers
 *     switched off. Also every browser over plain http.
 *   - **iOS, not yet installed.** Safari refuses `Notification.requestPermission()` outright unless
 *     the page is running as a home-screen web app. It does not prompt and refuse — the call
 *     rejects, or resolves 'denied' and burns the one chance the origin gets. So on iOS the button
 *     is not drawn until `navigator.standalone` says the app is installed, and the screen explains
 *     the Share → Add to Home Screen gesture instead.
 *   - **Already refused.** `Notification.permission === 'denied'` cannot be undone from a page. Only
 *     the reader can, in the browser's own settings, and a button that silently does nothing is how
 *     somebody concludes the product is broken.
 *
 * The fourth — the operator has configured no VAPID keys — is knowable on the server, and the view
 * draws no mount point at all in that case.
 */

/** What the card can say about itself. Exactly one is ever shown. */
type Explanation = 'home-screen' | 'unsupported' | 'blocked';

interface Card {
  readonly root: HTMLElement;
  readonly status: HTMLElement | null;
  readonly enable: HTMLButtonElement | null;
  readonly disable: HTMLButtonElement | null;
}

/**
 * The four sentences a press can end in.
 *
 * Read off the mount point rather than written here. Every user-visible string in this product is an
 * entry in `UiText` and is chosen by `PanelCulture`; a bundle is compiled once and cannot ask which
 * language a request was in, so the view renders these into `data-*` and this reads them back. A
 * literal here would be a sentence the English panel or the Persian one could not say.
 */
interface Words {
  readonly on: string;
  readonly off: string;
  readonly refused: string;
  readonly failed: string;
  readonly tooMany: string;
}

function readWords(el: HTMLElement): Words {
  return {
    on: el.dataset.textOn ?? '',
    off: el.dataset.textOff ?? '',
    refused: el.dataset.textRefused ?? '',
    failed: el.dataset.textFailed ?? '',
    tooMany: el.dataset.textTooMany ?? '',
  };
}

export function mountNotifications(el: HTMLElement): void {
  const card: Card = {
    root: el,
    status: el.querySelector<HTMLElement>('[data-notifications-status]'),
    enable: el.querySelector<HTMLButtonElement>('[data-notifications-enable]'),
    disable: el.querySelector<HTMLButtonElement>('[data-notifications-disable]'),
  };

  // Everything after this point is asynchronous and the card is inside the swapped region, so the
  // reader may have navigated away before any of it lands. Writing to a detached node is harmless;
  // the checks that matter are the ones on `subscribe`, which must not register a device after the
  // reader has left the screen they pressed it on.
  void start(card, readWords(el));
}

async function start(card: Card, words: Words): Promise<void> {
  if (!supported()) {
    explain(card, 'unsupported');

    return;
  }

  // The iOS gate, and it is asked of standalone-ness rather than of the user agent. `navigator.
  // standalone` is Safari's own answer and is undefined everywhere else; the display-mode query is
  // the standard one every other browser answers. A desktop browser is neither installed nor
  // standalone and is perfectly able to subscribe, so the gate is only applied where the platform
  // actually imposes it — which is Safari, which is exactly what `navigator.standalone` being
  // present means.
  if (isSafariHomeScreenPending()) {
    explain(card, 'home-screen');

    return;
  }

  if (Notification.permission === 'denied') {
    explain(card, 'blocked');

    return;
  }

  const registration = await serviceWorkerReady();

  // No worker means no push, whatever the browser claims: a subscription belongs to a registration.
  // This is also the private-window and the plain-http case, which `supported()` cannot see.
  if (registration === null) {
    explain(card, 'unsupported');

    return;
  }

  const existing = await registration.pushManager.getSubscription();

  show(card, existing !== null, words);

  card.enable?.addEventListener('click', () => {
    void enable(card, registration, words);
  });

  card.disable?.addEventListener('click', () => {
    void disable(card, registration, words);
  });
}

/**
 * Asking, and then telling the server.
 *
 * `Notification.requestPermission()` has to be reached from the click that called this — it is
 * gesture-gated, and a call made after an `await` of something else is a call the browser refuses
 * without prompting. So it is the first thing here and nothing is awaited before it.
 */
async function enable(
  card: Card,
  registration: ServiceWorkerRegistration,
  words: Words,
): Promise<void> {
  const key = card.root.dataset.applicationServerKey;
  if (!key) return;

  busy(card, true);

  try {
    const permission = await Notification.requestPermission();

    if (permission !== 'granted') {
      // 'denied' or 'default' — the second is the reader dismissing the prompt without answering,
      // which is not a refusal and leaves the button where it is.
      say(card, words.refused);

      if (permission === 'denied') explain(card, 'blocked');

      return;
    }

    const subscription = await registration.pushManager.subscribe({
      // Required, and there is no other value. A subscription that is not userVisibleOnly is one
      // this product would have to promise never to use for anything the reader cannot see, and no
      // browser this panel runs on offers it in the first place.
      userVisibleOnly: true,
      applicationServerKey: keyBytes(key),
    });

    const stored = await post(card, card.root.dataset.subscribeUrl, {
      endpoint: subscription.endpoint,
      p256dh: encode(subscription.getKey('p256dh')),
      auth: encode(subscription.getKey('auth')),
    });

    if (stored === 429) {
      // The server kept nothing, so the browser must not keep a subscription either — an endpoint
      // the server has never heard of is one nothing will ever send to, and the reader would be
      // looking at a control that says it is on.
      await subscription.unsubscribe();
      say(card, words.tooMany);

      return;
    }

    if (stored !== 200) {
      await subscription.unsubscribe();
      say(card, words.failed);

      return;
    }

    show(card, true, words);
  } catch {
    // A rejected requestPermission (iOS, outside a gesture or outside a home-screen app), a
    // pushManager that refused the key, a network that was not there. All of them leave the reader
    // where they started, which is the honest outcome.
    say(card, words.failed);
  } finally {
    busy(card, false);
  }
}

/**
 * Giving it up, on the browser and on the server, in that order.
 *
 * The browser first: if the page is closed between the two calls, what is left is a row for an
 * endpoint that no longer exists — which the push service answers with a 410 and the server deletes
 * on its next send. The other order leaves a live endpoint the server has forgotten, which nothing
 * ever cleans up and which keeps the reader subscribed to a service that will not write to them.
 */
async function disable(
  card: Card,
  registration: ServiceWorkerRegistration,
  words: Words,
): Promise<void> {
  busy(card, true);

  try {
    const subscription = await registration.pushManager.getSubscription();

    if (subscription !== null) {
      const endpoint = subscription.endpoint;

      await subscription.unsubscribe();
      await post(card, card.root.dataset.unsubscribeUrl, { endpoint });
    }

    show(card, false, words);
  } catch {
    say(card, words.failed);
  } finally {
    busy(card, false);
  }
}

/** The status code, or 0 when the request never happened. Nothing here reads a body. */
async function post(card: Card, url: string | undefined, body: unknown): Promise<number> {
  if (!url) return 0;

  const header = card.root.dataset.antiforgeryHeader;
  const token = card.root.dataset.antiforgeryToken;

  const headers: Record<string, string> = { 'Content-Type': 'application/json' };

  // The token is minted per response and written into the mount point by Razor, exactly as the
  // upload queue's is. A bundle compiled once cannot know it, and a POST without it is a 400 that
  // reads like a broken feature.
  if (header && token) headers[header] = token;

  try {
    const response = await fetch(url, {
      method: 'POST',
      headers,
      body: JSON.stringify(body),

      // The cookie is the whole of the authentication here. Same-origin is the default for a
      // relative URL and is written down because this is the one line that would silently turn
      // every call into a 401 if somebody made the URL absolute.
      credentials: 'same-origin',
    });

    return response.status;
  } catch {
    return 0;
  }
}

/**
 * Whether this browser has the three things a subscription needs.
 *
 * All three, and not one: Safari had `Notification` for years before it had `PushManager`, and a
 * feature test that stopped at the first would draw a button that throws on press.
 */
function supported(): boolean {
  return 'serviceWorker' in navigator
    && 'PushManager' in window
    && 'Notification' in window;
}

/**
 * Safari, not yet added to the home screen.
 *
 * `navigator.standalone` is Safari's own property and is absent in every other browser, so its
 * presence is the platform test and its value is the installed test. Both matter: a desktop Chrome
 * is not standalone and can subscribe perfectly well, and gating on `display-mode: standalone`
 * alone would tell every desktop reader in the product to add the panel to their home screen.
 *
 * The media query is checked as well because iPadOS reports itself as a desktop and has no
 * `navigator.standalone` on some versions, while still being installed — so an installed app that
 * answers the standard query is let through whatever Safari's own property says.
 */
function isSafariHomeScreenPending(): boolean {
  const safari = navigator as Navigator & { standalone?: boolean };

  if (safari.standalone === undefined) return false;
  if (safari.standalone) return false;

  return !window.matchMedia('(display-mode: standalone)').matches;
}

/** Reveals one explanation and hides the buttons. There is no state where both are right. */
function explain(card: Card, which: Explanation): void {
  card.root
    .querySelectorAll<HTMLElement>('[data-notifications-state]')
    .forEach((block) => {
      block.hidden = block.dataset.notificationsState !== which;
    });

  if (card.enable) card.enable.hidden = true;
  if (card.disable) card.disable.hidden = true;
}

/** The ordinary case: one button, and a sentence saying which way round it is. */
function show(card: Card, subscribed: boolean, words: Words): void {
  card.root
    .querySelectorAll<HTMLElement>('[data-notifications-state]')
    .forEach((block) => {
      block.hidden = true;
    });

  if (card.enable) card.enable.hidden = subscribed;
  if (card.disable) card.disable.hidden = !subscribed;

  say(card, subscribed ? words.on : words.off);
}

function say(card: Card, sentence: string): void {
  if (card.status) card.status.textContent = sentence;
}

/**
 * Both buttons disabled while a press is in flight.
 *
 * Permission prompts are modal on some platforms and not on others; on the ones where they are not,
 * a second press starts a second `subscribe()` against the same registration and the two race to
 * report an endpoint to the server.
 */
function busy(card: Card, working: boolean): void {
  if (card.enable) card.enable.disabled = working;
  if (card.disable) card.disable.disabled = working;
}

/**
 * base64url text to the bytes `applicationServerKey` wants.
 *
 * `atob` reads standard base64 with padding and the server sends unpadded base64url — the encoding
 * a JWT and a push key both use. The two differ in exactly two characters and in the padding, and
 * handing `atob` the wrong alphabet throws `InvalidCharacterError` from inside `subscribe()`, which
 * is a button that fails on press for a reason nothing prints.
 */
function keyBytes(key: string): Uint8Array<ArrayBuffer> {
  const padded = key.replace(/-/g, '+').replace(/_/g, '/');
  const binary = atob(padded.padEnd(padded.length + ((4 - (padded.length % 4)) % 4), '='));

  // Built over an ArrayBuffer this function owns rather than through Uint8Array.from, whose result
  // is typed over ArrayBufferLike — which includes SharedArrayBuffer and is therefore not a
  // BufferSource that subscribe() will take. The narrower type is the whole reason for the loop.
  const bytes = new Uint8Array(new ArrayBuffer(binary.length));

  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);

  return bytes;
}

/**
 * The device's own keys, as unpadded base64url — which is what the server decodes and what
 * `PushSubscription.toJSON()` would have produced.
 *
 * Read through `getKey()` rather than `toJSON()` because the latter is typed as returning optional
 * members and is absent on older WebKit; these two are the only fields this product wants and
 * asking for them by name is what makes a missing one a null here rather than an undefined field on
 * the request.
 */
function encode(key: ArrayBuffer | null): string {
  if (key === null) return '';

  let binary = '';
  for (const byte of new Uint8Array(key)) binary += String.fromCharCode(byte);

  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
