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
 * Empty on purpose. A worker that imports a file which does not exist is a worker that fails to
 * install, so this ships now, saying what it is for, rather than being created by whoever needs it.
 */

'use strict';
