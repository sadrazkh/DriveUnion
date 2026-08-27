/*
 * The worker's push half, which M7 writes and M5 only makes room for.
 *
 * ── Why this is a file and not a paragraph in sw.js ──────────────────────────────────────────────
 *
 * Two service workers cannot hold one scope. Registering a second script for '/' does not add a
 * worker beside the first, it replaces it — so a push worker installed alongside sw.js would take
 * the offline shell down with it, and sw.js re-registering would take push back, whichever ran last
 * winning, once per page load, with nothing failing anywhere. That is the failure this arrangement
 * exists to make unavailable: there is one worker, at one address, and this file is how a second
 * concern gets into it.
 *
 * sw.js imports this with importScripts() while it is still evaluating, so anything registered here
 * is registered before the first event can arrive. It wraps the call, so a syntax error in this
 * file costs push and does not cost the app shell.
 *
 * ── The contract ─────────────────────────────────────────────────────────────────────────────────
 *
 * What belongs here:
 *
 *   self.addEventListener('push', (event) => { … })
 *   self.addEventListener('notificationclick', (event) => { … })
 *
 * and nothing else. In particular:
 *
 *   - No `fetch` listener. sw.js owns fetch. Two listeners race for respondWith and the winner is
 *     decided by registration order, so a fetch handler here is the caching rules being overridden
 *     by accident on some page loads and not others.
 *   - No `install` or `activate` listener that calls skipWaiting(), claim(), or touches
 *     caches.keys(). sw.js deletes every cache that is not its own on activate; a second activate
 *     handler here writing one would have it deleted, sometimes, depending on which listener ran
 *     first. If a subscription needs to survive an update, it belongs in IndexedDB or on the
 *     server, not in a Cache.
 *   - Nothing that reads or stores a customer's file names, workspace names, or anything from
 *     /d/{slug}. A push payload is decrypted on this device and a notification is drawn from it;
 *     what must not happen is that payload being written anywhere it outlives the notification. The
 *     panel's whole claim is that the server holds no readable copy, and a phone quietly
 *     accumulating file names in a worker's storage is that claim with an exception in it.
 *
 * The page side is the other half of the same rule, and it is the easier one to get wrong:
 * subscribing needs a ServiceWorkerRegistration, and the obvious way to get one — calling
 * navigator.serviceWorker.register() — is the thing that replaces the worker. Scripts/serviceWorker.ts
 * exports serviceWorkerReady() for exactly this, and says so at length.
 *
 * ── What this file does, now that it is written ──────────────────────────────────────────────────
 *
 * Two handlers and nothing else. The contract above is kept to the letter: no fetch listener, no
 * install or activate listener, no cache of any kind, and nothing written anywhere at all — the
 * payload is read, drawn, and dropped when the handler returns. The one thing this file stores is
 * the notification itself, which the operating system owns and the reader dismisses.
 */

'use strict';

/**
 * The payload's shape, which is PushDispatcher's PushPayload on the other side of the wire.
 *
 *   t — the title
 *   b — the body
 *   u — a path in this panel, never an absolute address
 *   g — the tag: a second notification with the same one replaces the first rather than stacking
 *
 * One letter each because a push record is 4096 bytes and every byte spent on "title" is a byte of
 * Persian that cannot be spent. There is deliberately no field for a file name, a workspace name, a
 * slug or an id — see PushDispatcher for the argument, which is the same one that keeps the panel's
 * pages out of the cache in sw.js.
 */

/**
 * Whether a payload's `u` is a path in this panel.
 *
 * One leading slash and not two. `//evil.example` is a protocol-relative URL: it starts with a
 * slash, so a bare `startsWith('/')` accepts it, and `clients.openWindow()` resolves it against the
 * current scheme and opens somebody else's site. That is a redirect out of the app arriving through
 * a notification — and although a payload can only come from a VAPID-signed sender, the shape of
 * what arrives is not something to take on trust when the check costs one character.
 *
 * A relative path with no leading slash is refused too, because the address this resolves against is
 * the worker's scope rather than the page the reader is on — so `files` would mean one thing on
 * install and another after a scope change, which is a link that quietly starts pointing elsewhere.
 */
function isOurs(url) {
  if (typeof url !== 'string') return false;

  // Resolved and compared by origin rather than matched by prefix, and that is not fussiness.
  //
  // The obvious guard — starts with '/' and not with '//' — is wrong, and wrong in the direction
  // that matters. A URL parser normalises a backslash to a forward slash in the authority position
  // for http and https, so «/\evil.example» begins with exactly one slash, passes any prefix test
  // written by hand, and resolves to https://evil.example. Measured in a browser, not reasoned
  // about: the string below is the one that got through.
  //
  // What that would buy an attacker is the whole point of the check. A push payload arrives from
  // this product's own server, so this is not the first line of defence — but a notification is a
  // thing a person taps without reading, on a lock screen, believing it came from the app whose
  // icon is on it. Sending that tap to somebody else's origin is as good a phishing primitive as
  // exists, and it would survive in a file that is imported by a worker and never rendered.
  //
  // The parser is the only thing that knows every one of these normalisations. Let it decide, then
  // compare what it produced.
  try {
    return new URL(url, self.location.origin).origin === self.location.origin;
  } catch {
    return false;
  }
}

/** Shown when a push arrives carrying nothing this worker can read. */
const Fallback = {
  // Deliberately not a sentence in either language. This is the one string in the product that
  // cannot come from UiText — a worker is compiled once and has no culture — so rather than pick a
  // language for somebody, it says the product's name, which is the same in neither and reads as
  // itself in both. Anything more would be Persian shipped to an English reader or the reverse.
  t: 'Drive Union',
  b: '',
  u: '/',
  g: 'driveunion',
};

self.addEventListener('push', (event) => {
  // Every push shows something, without exception, and that is a platform rule rather than a
  // preference: a service worker that receives a push and draws no notification is a "silent push",
  // and browsers answer a run of them by showing their own "this site has been updated in the
  // background" notice — or, on iOS, by revoking the permission outright. So a payload that will
  // not parse still draws the fallback above rather than returning.
  let payload = Fallback;

  try {
    const parsed = event.data?.json();

    // A push from anywhere but this server — the endpoint is not a secret, only a signed sender is
    // accepted — could be any shape at all. Read defensively and take nothing on trust.
    if (parsed && typeof parsed.t === 'string') {
      payload = {
        t: parsed.t,
        b: typeof parsed.b === 'string' ? parsed.b : '',
        u: isOurs(parsed.u) ? parsed.u : '/',
        g: typeof parsed.g === 'string' ? parsed.g : Fallback.g,
      };
    }
  } catch {
    // Not JSON, or no payload at all. The fallback stands.
  }

  event.waitUntil(self.registration.showNotification(payload.t, {
    body: payload.b,

    // Same tag, same entry: five link-uploads finishing while a phone is asleep leave one
    // notification saying a link-upload finished, not five identical ones to swipe away.
    tag: payload.g,

    // The path travels on the notification rather than being looked up again on click. A click
    // handler runs in a worker that may have been terminated and restarted since — there is no
    // variable from this scope still standing — and `data` is the one thing the platform keeps
    // beside the notification for it.
    data: { url: payload.u },

    icon: '/icons/icon-192.png',

    // The small glyph Android draws in the status bar. Ignored everywhere else, and pointing it at
    // the maskable art is what stops Android cropping the padded icon into a grey square.
    badge: '/icons/icon-maskable-512.png',

    // Not renotify, and not requireInteraction. Both are ways of making a notification harder to
    // ignore, and nothing this product sends is worth that: a link-upload finished, a deletion
    // finished, somebody filed a report. The one that is genuinely urgent — the abuse report — is
    // urgent to an operator who will act on it, not to a phone that needs to insist.
  }));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();

  // Checked again here, and not because the push handler's check is in doubt. This is the line that
  // actually navigates, and a notification drawn by an older version of this file — one that is
  // sitting in a notification centre right now, from before this check existed — arrives at exactly
  // this point. The guard belongs where the address is used.
  const wanted = isOurs(event.notification.data?.url) ? event.notification.data.url : '/';

  event.waitUntil((async () => {
    const open = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });

    // An already-open window is focused and navigated rather than a second one opened. The panel is
    // an app shell that never reloads — Scripts/navigate.ts swaps main.app-content and the shell
    // stays up — so opening a new window would abandon whatever is in the upload queue of the one
    // that is already there, which is the 96 GB transfer this whole architecture exists for.
    for (const client of open) {
      if (!('focus' in client)) continue;

      await client.focus();

      // navigate() is a no-op that rejects on some platforms (iOS among them), so its failure is
      // caught and the window is left where it is. A focused panel one press from the answer is a
      // great deal better than a rejected promise nobody sees.
      if ('navigate' in client) {
        try {
          await client.navigate(wanted);
        } catch {
          // Left where it was, focused.
        }
      }

      return;
    }

    // Nothing open. The address is one leading slash and no more — see isOurs — so it resolves
    // against this worker's own origin and cannot be talked into opening somewhere else.
    await self.clients.openWindow(wanted);
  })());
});
