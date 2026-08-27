/*
 * Drive Union's service worker: the app shell offline, and nothing else.
 *
 * ── Why this file is hand-written and not a Vite entry ───────────────────────────────────────────
 *
 * A worker's scope is the directory it is served from, so a worker at /build/sw-a1b2c3.js can only
 * control /build/ — and vite.config.ts sets base: '/build/' and hashes every output. That much a
 * second input with a fixed entryFileNames could work around. Two other facts it cannot:
 *
 *   - iOS has no module service worker. register(url, { type: 'module' }) is Chromium-only, and a
 *     Rollup build with two entries emits ES modules and hoists whatever they share into a third
 *     chunk. On the one platform this whole plan exists for, such a worker fails to parse and there
 *     is simply no worker — no error, no offline page, and nothing anywhere saying why.
 *   - A bundled worker can import page code. One transitive import that touches `document` throws
 *     during install and loses the worker the same silent way. A file no bundler reads cannot
 *     acquire that import by accident.
 *
 * wwwroot already holds hand-written source served straight to the browser — css/app.css,
 * css/tokens.css — so this is that shape rather than a new one. Being outside the bundle is also
 * what keeps the URL stable: /sw.js is the same address on every deploy, which is what lets a
 * browser recognise an update to this worker instead of installing a second one beside it.
 *
 * ── What is cached, and what is refused ──────────────────────────────────────────────────────────
 *
 * The shell and its static assets: the hashed bundle, the stylesheets, the font, the icons, and one
 * offline page. Nothing else, and the "nothing else" is the product decision rather than a scope cut.
 * This is a product whose claim is that the server holds no readable copy of a customer's files;
 * writing their file names and workspace names onto a phone's disk would be a second claim, quietly
 * contradicting the first. So no page of the panel is ever stored, no answer from /api/ is, and
 * /d/{slug} is not touched at all — see NeverOurs below for why that one is stronger than "not
 * stored".
 */

'use strict';

/**
 * The one cache, and the only lever that empties it.
 *
 * Bump this when what is stored changes shape, or when a file whose URL carries no hash of its own
 * changes — the font and the icons are the only two, and they are stale-while-revalidate below
 * precisely so that forgetting is not fatal. Activation deletes every cache that is not this one,
 * so a bump is an atomic swap rather than a merge: the offline page and the stylesheet it links are
 * re-fetched together and cannot end up a deploy apart.
 */
const CACHE = 'driveunion-shell-v1';

/** The one document this worker stores. Everything else it holds is an asset. */
const OFFLINE = '/offline';

/**
 * Addresses this worker has no code path for at all.
 *
 * Not "does not cache" — does not run. /d/{slug} is the address revocation is about: the whole
 * point of revoking a link is that it stops working at once, and a worker that can answer for that
 * path is a worker that can be wrong about it on somebody's phone for as long as a cache entry
 * lives. The cheapest way to guarantee it is never wrong is to have nothing there to be wrong.
 *
 * /api/ is the same decision for the other half: it carries the upload session and the panel's own
 * JSON, which is the customer's catalogue. It is also the transport — /api/uploads/{id} is polled
 * against the server's own byte count while a 96 GB file is in flight — and a worker between a
 * resumable upload and its server is a place for that to go wrong for no benefit whatsoever.
 */
const NeverOurs = ['/d/', '/api/'];

/**
 * Everything this worker is allowed to store, by path prefix.
 *
 * An allowlist and deliberately not a denylist. A denylist is a list somebody has to remember to
 * add to: the next screen at a new address would be cached by default, and "the catalogue is not on
 * the phone" would stop being true without a line changing. Here a new address is refused by
 * default and caching it is an edit to this line, which is a decision somebody has to make on
 * purpose.
 */
const Static = ['/build/', '/css/', '/fonts/', '/icons/'];

/*
 * ── The seam M7 (Web Push) adds to ───────────────────────────────────────────────────────────────
 *
 * M7 needs a `push` and a `notificationclick` handler on *this* worker. Two workers cannot hold one
 * scope: registering a second script for '/' does not add a worker beside this one, it replaces it.
 * A push worker registered alongside would take the offline shell with it, and this one
 * re-registering would take push back — whichever ran last winning, once per page load, silently.
 *
 * The contract, in three parts:
 *
 *   1. The handlers go in wwwroot/sw-push.js, which is imported here rather than written here so
 *      that M7 owns a file of its own and never edits the caching below. importScripts is
 *      synchronous and runs while this script is still being evaluated, which is the requirement:
 *      a `push` listener added later, from a promise, is a listener that is not registered when the
 *      first push arrives and the browser shows its own "this site has been updated in the
 *      background" notice instead.
 *   2. Nothing in sw-push.js may add a `fetch` listener. Two fetch listeners race, the first to
 *      call respondWith wins, and which one that is depends on registration order — so fetch lives
 *      here and only here.
 *   3. The page side never calls register() again. Scripts/serviceWorker.ts exports
 *      serviceWorkerReady(), which is how M7 reaches registration.pushManager; the reasoning is
 *      written there too, because that is the other place the mistake is available to make.
 *
 * Wrapped, because importScripts throws on a 404 or on a syntax error and an unhandled throw here
 * fails this script's evaluation outright: no worker, no offline page, no cached shell, and nothing
 * anywhere reporting it. Push is the smaller half, so push is the half that gets lost.
 */
try {
  importScripts('/sw-push.js');
} catch {
  // Deliberately silent. There is no reader in a worker, and the failure is already visible as
  // notifications that never arrive — which is a great deal better than an app that never loads.
}

self.addEventListener('install', (event) => {
  // The offline page is fetched rather than assembled here: it is Razor, its words come from UiText
  // and its language from the culture cookie, which this request carries because a Request built
  // from a same-origin URL sends credentials by default. A page written out in this file would be a
  // second place the product's Persian is spelled, and the only one nothing renders in both.
  //
  // cache: 'reload' so what is stored is what the server says now rather than a copy the HTTP cache
  // has been holding since before this deploy.
  event.waitUntil(
    caches.open(CACHE).then((cache) => cache.add(new Request(OFFLINE, { cache: 'reload' }))));

  // Taking over immediately is safe *here* and would not be everywhere. The usual objection is that
  // a new worker inherits pages rendered by the old one and starts answering them with the new
  // one's assets. Every asset this worker caches carries its own content hash in its URL — Vite's
  // in the filename, asp-append-version's in a ?v= — so a page that was rendered a deploy ago asks
  // for the files it was rendered with, by name, and gets those or a network fetch. There is no
  // version of "this week's CSS against last week's HTML" available to go wrong.
  self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    // Every cache but the current one. This is the whole of the version story: the name is the
    // version, so a bump above empties everything in one step and the shell is rebuilt whole.
    for (const name of await caches.keys()) {
      if (name !== CACHE) await caches.delete(name);
    }

    // Pages that were already open when this worker installed. Without it the first visit after an
    // install is a visit with no worker, and somebody who loads the panel and then walks into a
    // tunnel gets the browser's error page from an app that has an offline page sitting on disk.
    await self.clients.claim();
  })());
});

self.addEventListener('fetch', (event) => {
  const request = event.request;

  // Returning without calling respondWith is not the same as answering with fetch(request). A
  // request this worker never touches is made by the browser itself and keeps its range headers,
  // its streaming, its upload progress events and its credentials exactly as the platform gives
  // them. The 96 GB upload and the streamed download both live on that, so everything this worker
  // has no opinion about is left alone rather than proxied through it.
  if (request.method !== 'GET') return;

  let url;
  try {
    url = new URL(request.url);
  } catch {
    return;
  }

  if (url.origin !== self.location.origin) return;
  if (NeverOurs.some((prefix) => url.pathname.startsWith(prefix))) return;

  if (request.mode === 'navigate') {
    event.respondWith(navigation(request));
    return;
  }

  // Scripts/navigate.ts fetches the next page itself and swaps main.app-content, and its fetch is
  // not a navigation — mode is 'cors', not 'navigate' — so it falls through to here, matches no
  // static prefix, and is left entirely alone. That is the intended behaviour and not an accident
  // of the ordering: answering it from a cache would swap a stored page into a live shell, and
  // answering it with the offline page would put a "you are offline" card inside a panel that still
  // has its sidebar. What happens instead when the network is gone is that its fetch fails, it
  // hands the address back to the browser exactly as it was written to, and the real navigation
  // that follows is answered below.
  if (!Static.some((prefix) => url.pathname.startsWith(prefix))) return;

  event.respondWith(contentAddressed(url) ? cacheFirst(request) : staleWhileRevalidate(event));
});

/**
 * Whether this URL names one particular version of a file.
 *
 * Vite writes the hash into the name (/build/assets/main-B7xK2p.js) and asp-append-version writes
 * it into a ?v=. Either way the address changes when the bytes do, which is what makes cache-first
 * safe: this week's HTML cannot ask for last week's file, because last week's file has a different
 * address. A stylesheet linked without asp-append-version has no such address and falls to
 * stale-while-revalidate below rather than being pinned on a phone for ever.
 */
function contentAddressed(url) {
  return url.pathname.startsWith('/build/') || url.searchParams.has('v');
}

/**
 * A page, from the network, or the offline notice.
 *
 * Network-only, with no branch that could store the response, and that is the product decision
 * rather than a simplification. Every page of the panel is rendered for whoever asked: the sidebar
 * carries their email, the file table carries their file names, the shell carries an antiforgery
 * token minted for their session. There is no subset of the panel's HTML that is safe to write to a
 * phone's disk, so none of it is written.
 */
async function navigation(request) {
  try {
    return await fetch(request);
  } catch {
    const offline = await caches.match(OFFLINE, { cacheName: CACHE });

    // Response.error() is the browser's own network failure, which is the honest answer when the
    // offline page was never stored — a worker installed seconds ago, or an install whose fetch
    // failed. Inventing a page here would mean writing one in this file.
    if (offline === undefined) return Response.error();

    // The offline page's own 200 rather than a 503, and it is a trade rather than an oversight.
    // Changing the status means constructing a new Response, which means deciding its headers —
    // and a response out of the Cache API still carries the Content-Encoding of a body the Cache
    // API has already decoded. Copying those headers hands the browser a gzip header over plain
    // text; hand-picking them is a second list to keep true. A more honest status code is not worth
    // an offline page that renders as binary.
    return offline;
  }
}

/** An address that names its own version: the copy on disk is the copy that was asked for. */
async function cacheFirst(request) {
  const cached = await caches.match(request, { cacheName: CACHE });
  if (cached !== undefined) return cached;

  const response = await fetch(request);
  await store(request, response);

  return response;
}

/**
 * An address that does not name its own version — the font, the icons.
 *
 * Answered from disk at once and corrected behind that, so a changed file is right on the next load
 * rather than on the next time somebody remembers to bump CACHE. Cache-first here would be one line
 * shorter and would pin a font on a phone until a human intervened, which is a decision that
 * depends on somebody remembering, which is a decision that will be got wrong.
 */
async function staleWhileRevalidate(event) {
  const request = event.request;
  const cached = await caches.match(request, { cacheName: CACHE });

  const update = fetch(request).then(async (response) => {
    await store(request, response);

    return response;
  });

  // Nothing on disk yet, so the network is the answer — and its failure is this handler's failure,
  // which is the same network error the browser would have shown without a worker.
  if (cached === undefined) return update;

  // waitUntil and not a bare promise. respondWith has already been settled by the return below, and
  // a worker with nothing left to do is terminated: unattended, this update would land or not land
  // depending on how fast the phone is.
  event.waitUntil(update.catch(() => undefined));

  return cached;
}

/**
 * Writes one asset, or declines to.
 *
 * `basic` is a same-origin response this worker can actually read. An opaque one — a redirect that
 * left the origin, a captive portal's sign-in page answering for a stylesheet — stored under an
 * asset's address is that asset broken until the cache is emptied, and nothing reports it. Neither
 * is a 404 page: the panel is meant to degrade to a server-rendered page when the bundle is
 * missing, and a stored 404 makes "missing" permanent.
 */
async function store(request, response) {
  if (!response.ok || response.type !== 'basic') return;

  const cache = await caches.open(CACHE);

  // Cloned before the response is handed back, because a body can only be read once and the caller
  // is about to give this one to the browser.
  await cache.put(request, response.clone());
}
