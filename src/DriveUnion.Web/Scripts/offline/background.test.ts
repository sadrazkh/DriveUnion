import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { beforeEach, describe, expect, it } from 'vitest';
import { canBackground, stagedNameFor, startBackground } from './background';
import type { SavedFile } from './library';

/**
 * The page half of Background Fetch, and the agreement it has to keep with the worker half.
 *
 * <p>The two cannot import each other — a classic service worker has no modules and a module cannot
 * pull in a classic worker — so the registration id and the staging filename are spelled twice. Two
 * spellings that drifted would be a download nothing recognises when it lands: the worker would
 * refuse an id it did not claim, or write bytes under a name the page never looks for, and in both
 * cases the customer's gigabytes are simply gone with nothing said.</p>
 */

const worker = readFileSync(
  resolve(import.meta.dirname, '../../wwwroot/sw-download.js'),
  'utf8',
);

function define(name: string, value: unknown) {
  Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
}

const film: SavedFile = {
  key: '/d/film2026/file',
  name: 'a-short-film.webm',
  type: 'video/webm',
  bytes: 4096,
  savedAt: 1_700_000_000_000,
  watchUrl: '/d/film2026/watch',
  written: 0,
};

let started: { id: string; requests: string[]; options: Record<string, unknown> }[];
let persisted: number;

beforeEach(() => {
  started = [];
  persisted = 0;

  define('BackgroundFetchManager', class {});
  define('navigator', {
    storage: {
      persist: async () => {
        persisted++;
        return true;
      },
    },
    serviceWorker: {
      getRegistration: async () => ({}),
      ready: Promise.resolve({
        backgroundFetch: {
          fetch: async (id: string, requests: string[], options: Record<string, unknown>) => {
            started.push({ id, requests, options });
            return {};
          },
        },
      }),
    },
  });
});

describe('the two spellings the worker also knows', () => {
  /**
   * <b>The agreement.</b> Read out of the shipped worker rather than restated, so a rename on that
   * side fails here instead of in a customer's storage.
   */
  it('uses the prefix the worker claims registrations by', async () => {
    const declared = /const IdPrefix = '([^']+)'/.exec(worker);

    expect(declared, 'the worker still declares IdPrefix as a literal').not.toBeNull();

    await startBackground(film);

    expect(started[0].id.startsWith(declared![1])).toBe(true);
  });

  it('stages under the name the worker writes to', () => {
    // The worker builds it as `${fileNameFor(key)}.raw`; this side says it in one piece. Both have
    // to produce the same string for the same key or the page looks in the wrong place.
    expect(stagedNameFor('/d/film2026/file')).toBe('_2Fd_2Ffilm2026_2Ffile.bin.raw');
    expect(worker).toContain(".raw");
  });

  /**
   * The whole film's details ride on the id, because by the time the download lands there is no page
   * left holding them. A round trip through the id has to come back unchanged.
   */
  it('carries the film through the id and back', async () => {
    await startBackground(film);

    const encoded = started[0].id.slice(started[0].id.indexOf(':') + 1);
    const meta = JSON.parse(decodeURIComponent(encoded));

    expect(meta).toEqual({
      key: film.key,
      name: film.name,
      type: film.type,
      bytes: film.bytes,
      savedAt: film.savedAt,
      watchUrl: film.watchUrl,
    });
  });
});

describe('handing the download over', () => {
  it('asks the browser for the request, the title and the size', async () => {
    expect(await startBackground(film)).toBe(true);

    expect(started[0].requests).toEqual(['/d/film2026/file']);

    // The size lets the browser draw a real bar rather than a spinner, and lets it refuse up front
    // rather than part-way.
    expect(started[0].options.downloadTotal).toBe(4096);
    expect(started[0].options.title).toBe('a-short-film.webm');
  });

  /**
   * Durability is asked for here and can only be asked for here: `storage.persist` is exposed to
   * windows only, so the one side able to ask is the side about to stop running.
   */
  it('asks for durable storage before handing over, because the worker cannot', async () => {
    await startBackground(film);

    expect(persisted).toBe(1);
  });

  it('answers no on a browser without it, rather than throwing at the caller', async () => {
    // Deleted rather than set to undefined: `in` is true for a property that exists and holds
    // undefined, so defining it away would have left the feature test passing on Safari.
    delete (globalThis as Record<string, unknown>).BackgroundFetchManager;
    // Safari. Every path has to say no so the ordinary in-page save carries on being what happens.
    define('navigator', { serviceWorker: {} });

    expect(canBackground()).toBe(false);
    expect(await startBackground(film)).toBe(false);
  });

  /**
   * <b>Measured, not imagined.</b> On a page whose worker had been unregistered this hung for
   * forty-five seconds and was still going: `serviceWorker.ready` neither rejects nor resolves when
   * nothing is registered. A control that never comes back is worse than one that says no.
   */
  it('answers no rather than hanging when no worker is registered', async () => {
    define('navigator', {
      storage: { persist: async () => true },
      serviceWorker: {
        getRegistration: async () => undefined,
        // Exactly what the browser gives: a promise that will never settle.
        ready: new Promise(() => {}),
      },
    });

    expect(await startBackground(film)).toBe(false);
  });

  it('answers no when the browser refuses the registration', async () => {
    define('navigator', {
      storage: { persist: async () => true },
      serviceWorker: {
        getRegistration: async () => ({}),
        ready: Promise.resolve({
          backgroundFetch: {
            fetch: async () => {
              throw new Error('already registered');
            },
          },
        }),
      },
    });

    // A duplicate id, a refusal for size, or no worker yet. The caller falls back to saving in the
    // page, which is worse and works.
    expect(await startBackground(film)).toBe(false);
  });
});
