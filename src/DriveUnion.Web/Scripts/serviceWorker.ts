/**
 * The one registration.
 *
 * The worker itself is wwwroot/sw.js — hand-written, outside the bundle, and served unhashed from
 * the root because a worker's scope is the directory it is served from. This file is only the half
 * that runs on the page: it registers that worker once, and it hands the registration to anything
 * that needs the worker rather than letting each caller find its own.
 *
 * That second half is not a convenience. Two service workers cannot hold one scope, and
 * `navigator.serviceWorker.register()` for a different script at '/' does not add a worker beside
 * the one that is there — it replaces it. So the failure available here is a quiet one: a later
 * feature registers its own worker, the shell's offline story stops working, this file registers
 * again on the next page load, that feature stops working, and neither of them ever throws.
 */

/**
 * The worker's address, which must stay at the root and must stay unhashed.
 *
 * A worker controls the directory it is served from and everything under it. At /build/sw-a1b2c3.js
 * — which is what putting it through Vite would produce — its scope would be /build/, so it would
 * control the stylesheets and none of the pages, and the offline page would never be reached. See
 * the comment at the top of wwwroot/sw.js for why it is not a Vite input.
 */
const WorkerUrl = '/sw.js';

let pending: Promise<ServiceWorkerRegistration | null> | null = null;

/**
 * Registers the worker. Called once, from main.ts, and safe to call again.
 *
 * Nothing waits for it: an install fetches the offline page, and the page this is running on has
 * better uses for that connection. A failure is not reported to the reader either — a browser with
 * no service workers, a private window, or a panel served over plain http is a panel that works in
 * every respect except being available offline, which is not something to interrupt somebody about.
 */
export function registerServiceWorker(): void {
  void serviceWorkerReady();
}

/**
 * The registration, for anything that needs the worker itself.
 *
 * <b>This is the seam.</b> M7 (Web Push) subscribes with
 * `(await serviceWorkerReady())?.pushManager.subscribe(…)`, from a user gesture, and adds its
 * `push` and `notificationclick` handlers in wwwroot/sw-push.js — which wwwroot/sw.js already
 * imports. Neither half of M7 touches the caching rules, and neither half calls `register()`.
 *
 * Calling `navigator.serviceWorker.register()` a second time is the mistake this exists to remove.
 * With the same script URL it is merely redundant; with any other it silently replaces the worker
 * holding this scope, and the two features take it in turns to work depending on which ran last.
 *
 * `navigator.serviceWorker.ready` is not the same thing and is not a substitute: it never settles
 * where there is no controller, so a caller awaiting it on a browser without service workers waits
 * for ever with nothing to log. This resolves to null instead, so the caller can say "not here" —
 * which on iOS is the ordinary case, since push is only offered to a web app that has been added to
 * the home screen.
 */
export function serviceWorkerReady(): Promise<ServiceWorkerRegistration | null> {
  pending ??= register();

  return pending;
}

async function register(): Promise<ServiceWorkerRegistration | null> {
  // Absent on http, in some private windows, and in browsers that have it disabled. The panel is
  // built to work without a bundle at all, so it certainly works without this.
  if (!('serviceWorker' in navigator)) return null;

  try {
    return await navigator.serviceWorker.register(WorkerUrl, {
      // Stated rather than inferred. The default is the script's own directory, which is the same
      // '/' here — writing it down is what makes moving the file a visible change instead of a
      // silent loss of every page from the worker's scope.
      scope: '/',

      // The worker script and everything it importScripts() are fetched from the network on an
      // update check, never from the HTTP cache. The default ('imports') lets sw-push.js come back
      // from cache, which is M7 shipping a push handler that some devices do not see for as long as
      // a cached copy lives.
      updateViaCache: 'none',
    });
  } catch {
    // A worker that would not install. The page is already rendered and working; the only thing
    // lost is being usable offline, and there is nothing the reader could do about it.
    return null;
  }
}
