import type { SavedFile } from './library';

/**
 * Handing a download to the browser, so it survives the tab.
 *
 * <p>The page side of <c>wwwroot/sw-download.js</c>. Background Fetch is the only web API that keeps
 * bytes moving after the page is gone: the browser takes the request, shows its own progress
 * notification, and wakes the service worker when it is done.</p>
 *
 * <p><b>Chromium only, and that is the platform this product's owner cares about least.</b> Safari
 * has no BackgroundFetchManager and there is no sign of one, so on an iPhone every path here answers
 * «no» and the save behaves as it did before — running while the app is in front, resuming by itself
 * when it comes back. That is not a fallback so much as the whole of what iOS permits.</p>
 *
 * <p>The two spellings below — the registration id and the staging filename — are the worker's, said
 * again on this side because a classic worker cannot import a module and a module cannot import a
 * classic worker. `background.test.ts` reads the shipped .js and asserts they still agree; two
 * spellings that drifted would be a download nothing recognises when it lands.</p>
 */

/**
 * How long to wait for a worker that is installing before giving up on it.
 *
 * <p>Long enough for an ordinary activation and short enough that a reader who pressed a button gets
 * an answer. What happens after it is the in-page save, which works.</p>
 */
const ReadyTimeoutMs = 4000;

/** Marks a registration as this feature's, so another library's fetches are left alone. */
const IdPrefix = 'du1-save:';

/**
 * Everything about the film, carried on the id.
 *
 * <p>It has to be. The point of the feature is that the page is gone by the time the download
 * finishes, so a variable, a MessageChannel and a record written in advance are all gone with it.
 * The id is the one thing the browser hands back.</p>
 */
function idFor(entry: SavedFile): string {
  const meta = {
    key: entry.key,
    name: entry.name,
    type: entry.type,
    bytes: entry.bytes,
    savedAt: entry.savedAt,
    watchUrl: entry.watchUrl,
  };

  return IdPrefix + encodeURIComponent(JSON.stringify(meta));
}

/** Where the worker leaves the raw bytes, which is not where a player looks. */
export function stagedNameFor(key: string): string {
  return `${encodeURIComponent(key).replace(/[^A-Za-z0-9._-]/g, '_')}.bin.raw`;
}

/** Whether this browser can keep a download running with the tab shut. */
export function canBackground(): boolean {
  // `globalThis` rather than `self`: it is the spelling that means the same thing in a window, in a
  // worker and under a test runner, and this file is read by all three.
  return 'BackgroundFetchManager' in globalThis
    && typeof navigator !== 'undefined'
    && 'serviceWorker' in navigator;
}

/**
 * Hands the download over. True when the browser took it.
 *
 * <p>Durability is asked for here rather than in the worker, and not by choice: <c>storage.persist</c>
 * is exposed to windows only, so the one side that can ask is the side that is about to stop
 * running. Without it the browser may clear the origin's storage under pressure, which for a film
 * somebody backgrounded specifically so it would be there later is the one moment it must not.</p>
 */
export async function startBackground(entry: SavedFile): Promise<boolean> {
  if (!canBackground()) return false;

  try {
    if (typeof navigator.storage?.persist === 'function') await navigator.storage.persist();
  } catch {
    // Refused. The download is still worth starting; it is only less durable.
  }

  try {
    // `ready` is a promise that never rejects and never resolves when nothing is registered, so
    // awaiting it bare is a button that hangs for ever — measured at 45 seconds and still going, on
    // a page whose worker had been unregistered. `getRegistration` answers promptly either way, and
    // the race covers the other case: a worker that is registered but still installing.
    if (!(await navigator.serviceWorker.getRegistration())) return false;

    const registration = await Promise.race([
      navigator.serviceWorker.ready,
      new Promise<null>((resolve) => setTimeout(() => resolve(null), ReadyTimeoutMs)),
    ]);

    if (!registration) return false;

    // Typed loosely because Background Fetch is not in the DOM types this project builds against,
    // and declaring the whole interface for one call would be more surface than the call.
    const manager = (registration as unknown as {
      backgroundFetch?: {
        fetch: (id: string, requests: string[], options: Record<string, unknown>) => Promise<unknown>;
      };
    }).backgroundFetch;

    if (!manager) return false;

    await manager.fetch(idFor(entry), [entry.key], {
      // What the browser puts in its own notification. It is the reader's own language because it is
      // the file's name — nothing here is translated, and nothing here should be: a worker is
      // compiled once with no culture and a title built there would be in whichever language the
      // build machine happened to have.
      title: entry.name,

      // Lets the browser draw a real bar instead of a spinner, and lets it refuse up front rather
      // than part-way if the download is larger than it will take.
      downloadTotal: entry.bytes,
    });

    return true;
  } catch {
    // Already registered under this id, refused for size, or no worker yet. The ordinary in-page
    // save is still there and is what the caller falls back to.
    return false;
  }
}

/**
 * The raw bytes a background download left, or null.
 *
 * <p>Read straight from OPFS rather than asked of the worker. Both sides see the same directory, and
 * a round trip through <c>postMessage</c> to fetch a file the page can already open would be a
 * second way for this to fail.</p>
 */
export async function stagedBytes(key: string): Promise<File | null> {
  if (typeof navigator === 'undefined' || navigator.storage?.getDirectory === undefined) return null;

  try {
    const root = await navigator.storage.getDirectory();
    const dir = await root.getDirectoryHandle('offline');
    const handle = await dir.getFileHandle(stagedNameFor(key));

    return await handle.getFile();
  } catch {
    return null;
  }
}
