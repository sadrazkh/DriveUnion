import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * What the service worker does and — far more to the point — what it refuses to do.
 *
 * The file under test is wwwroot/sw.js itself, read off disk and evaluated with a stand-in for the
 * three globals a worker has: `self`, `caches` and `fetch`. It is deliberately the shipped bytes
 * rather than a module imported through the bundler, because being outside the bundle is the whole
 * design — a test that pulled it through Vite would be testing a file that is never served.
 *
 * The assertions that matter here are the negative ones. A worker that caches too little is a
 * missing feature somebody notices; a worker that caches too much is a customer's file names
 * written to a phone in a product sold on the server holding no readable copy, and a revoked
 * /d/{slug} still answering from a pocket a week after it was revoked. Neither of those fails
 * anywhere. So: the happy paths are checked once each, and every path that must not be touched is
 * checked from both ends — nothing answered, and nothing stored.
 */

const Origin = 'https://panel.driveunion.test';

/** A stand-in Response. Only the four members the worker actually reads. */
interface FakeResponse {
  ok: boolean;
  type: string;
  body: string;
  clone: () => FakeResponse;
}

interface FakeRequestInit {
  method?: string;
  mode?: string;
}

class FakeRequest {
  public readonly url: string;

  public readonly method: string;

  public readonly mode: string;

  public constructor(url: string, init: FakeRequestInit = {}) {
    this.url = new URL(url, `${Origin}/`).href;
    this.method = init.method ?? 'GET';
    this.mode = init.mode ?? 'no-cors';
  }
}

function response(body: string, over: Partial<FakeResponse> = {}): FakeResponse {
  const made: FakeResponse = {
    ok: true,
    type: 'basic',
    body,
    clone: () => made,
    ...over,
  };

  return made;
}

interface Worker {
  /** Runs a listener and settles everything it started — install, activate and waitUntil alike. */
  fire: (type: string, event: Record<string, unknown>) => Promise<void>;

  /** A fetch event, and whatever the worker chose to answer it with. `undefined` is "not ours". */
  request: (url: string, init?: FakeRequestInit) => Promise<FakeResponse | undefined>;

  /** Every address the worker asked the network for, in order. */
  network: string[];

  /** Every address the worker has written to a cache, by cache name. */
  stored: () => Map<string, string[]>;

  /** Cache names the worker has opened, so a deletion can be seen to have happened. */
  caches: Map<string, Map<string, FakeResponse>>;

  /** Takes the network away, which is the state this whole file exists for. */
  disconnect: () => void;

  imported: string[];
}

/**
 * The worker, evaluated in a scope we control.
 *
 * `new Function` rather than an import: sw.js is a classic script that registers its listeners as a
 * side effect of being evaluated, which is exactly how a browser runs it, and naming the globals as
 * parameters shadows Node's own — `Request` in particular, whose Node implementation refuses a
 * relative URL that a real worker resolves against its own address.
 */
function boot(options: { offline?: boolean; missing?: string[] } = {}): Worker {
  const source = readFileSync(resolve(import.meta.dirname, '../wwwroot/sw.js'), 'utf8');

  const listeners = new Map<string, (event: unknown) => void>();
  const stores = new Map<string, Map<string, FakeResponse>>();
  const network: string[] = [];
  const imported: string[] = [];
  let offline = options.offline === true;

  const key = (request: unknown): string =>
    typeof request === 'string'
      ? new URL(request, `${Origin}/`).href
      : (request as FakeRequest).url;

  const open = (name: string): Map<string, FakeResponse> => {
    const existing = stores.get(name);
    if (existing !== undefined) return existing;

    const made = new Map<string, FakeResponse>();
    stores.set(name, made);

    return made;
  };

  const fetched = async (request: unknown): Promise<FakeResponse> => {
    const url = key(request);
    network.push(url);

    if (offline) throw new TypeError('Failed to fetch');
    if (options.missing?.includes(new URL(url).pathname) === true) {
      return response(url, { ok: false });
    }

    return response(url);
  };

  const cacheApi = {
    open: async (name: string) => ({
      put: async (request: unknown, value: FakeResponse) => {
        open(name).set(key(request), value);
      },
      add: async (request: unknown) => {
        open(name).set(key(request), await fetched(request));
      },
    }),
    match: async (request: unknown, options?: { cacheName?: string }) =>
      open(options?.cacheName ?? 'default').get(key(request)),
    keys: async () => [...stores.keys()],
    delete: async (name: string) => stores.delete(name),
  };

  const scope = {
    location: new URL(`${Origin}/sw.js`),
    addEventListener: (type: string, handler: (event: unknown) => void) => {
      listeners.set(type, handler);
    },
    skipWaiting: () => undefined,
    clients: { claim: async () => undefined },
  };

  const fakeResponse = { error: () => response('network error', { ok: false, type: 'error' }) };

  // eslint-disable-next-line @typescript-eslint/no-implied-eval
  new Function('self', 'caches', 'fetch', 'importScripts', 'Request', 'Response', source)(
    scope,
    cacheApi,
    fetched,
    (url: string) => imported.push(url),
    FakeRequest,
    fakeResponse);

  const fire = async (type: string, event: Record<string, unknown>): Promise<void> => {
    const waited: unknown[] = [];
    const handler = listeners.get(type);

    expect(handler, `sw.js registers no ${type} listener`).toBeDefined();

    handler!({ ...event, waitUntil: (promise: unknown) => waited.push(promise) });

    // allSettled: an install whose fetch failed is a state this worker has to survive, and it is
    // one of the cases below. A browser rejects the install event and does not activate; here the
    // assertions are about what is on disk afterwards, which is nothing.
    await Promise.allSettled(waited);
  };

  return {
    fire,
    network,
    imported,
    caches: stores,
    disconnect: () => {
      offline = true;
    },
    stored: () => new Map([...stores].map(([name, entries]) => [name, [...entries.keys()]])),
    request: async (url: string, init: FakeRequestInit = {}) => {
      const request = new FakeRequest(url, init);
      let answer: Promise<FakeResponse> | undefined;

      await fire('fetch', {
        request,
        respondWith: (promise: Promise<FakeResponse>) => {
          answer = promise;
        },
      });

      return answer === undefined ? undefined : await answer;
    },
  };
}

/** An installed and activated worker, which is the only state a fetch is ever seen in. */
async function installed(options: { offline?: boolean } = {}): Promise<Worker> {
  const worker = boot(options);

  await worker.fire('install', {});
  await worker.fire('activate', {});

  worker.network.length = 0;

  return worker;
}

// ------------------------------------------------------------------ what must never be cached

describe('the addresses the worker has no code path for', () => {
  it('does not answer or store a share link, because a revoked link must die at once', async () => {
    const worker = await installed();

    const page = await worker.request(`${Origin}/d/kx91mzq4`, { mode: 'navigate' });
    const file = await worker.request(`${Origin}/d/kx91mzq4/file`);
    const preview = await worker.request(`${Origin}/d/kx91mzq4/preview`);

    // Not answered at all: the browser makes these requests itself, so what a /d/ address does is
    // decided by the server every single time. A worker that answered them — even by fetching and
    // returning — would be a worker that could be made to answer them from disk by one later edit.
    expect([page, file, preview]).toEqual([undefined, undefined, undefined]);

    expect(worker.network).toEqual([]);
    expect([...worker.caches.values()].flatMap((entries) => [...entries.keys()]))
      .not.toContain(`${Origin}/d/kx91mzq4`);
  });

  it('does not answer or store anything from the API, which is the catalogue and the transport', async () => {
    const worker = await installed();

    const begin = await worker.request(`${Origin}/api/uploads`, { method: 'POST' });
    const session = await worker.request(`${Origin}/api/uploads/9f2c`);
    const files = await worker.request(`${Origin}/api/files?folder=root`);
    const v1 = await worker.request(`${Origin}/api/v1/files`);

    expect([begin, session, files, v1]).toEqual([undefined, undefined, undefined, undefined]);
    expect(worker.network).toEqual([]);
  });

  it('does not store a page of the panel, which is rendered for whoever asked', async () => {
    const worker = await installed();

    for (const path of ['/files', '/links', '/trash', '/keys', '/plans', '/operator/tenants']) {
      const answer = await worker.request(Origin + path, { mode: 'navigate' });

      // Answered — that is what makes an offline page possible — and answered from the network,
      // with no branch anywhere that could write the reply down.
      expect(answer?.body, `${path} is answered from the network`).toBe(Origin + path);
    }

    const written = [...worker.stored().values()].flat();

    expect(written).toEqual([`${Origin}/offline`]);
  });

  it('does not store the sign-in page or anything under Identity', async () => {
    const worker = await installed();

    await worker.request(`${Origin}/Identity/Account/Login`, { mode: 'navigate' });

    expect([...worker.stored().values()].flat()).toEqual([`${Origin}/offline`]);
  });

  it('leaves every request that is not a same-origin GET to the browser', async () => {
    const worker = await installed();

    // The sign-out and the language switch are POSTs; the upload's own transport is a cross-origin
    // PUT to Google. A worker between any of those and its server is a place for a 96 GB transfer
    // to go wrong for no benefit at all.
    const signOut = await worker.request(`${Origin}/Identity/Account/Logout`, { method: 'POST' });
    const culture = await worker.request(`${Origin}/Culture/Set`, { method: 'POST' });
    const resumable = await worker.request('https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable');

    expect([signOut, culture, resumable]).toEqual([undefined, undefined, undefined]);
    expect(worker.network).toEqual([]);
  });

  it('leaves the swap fetch alone, so navigate.ts still decides what a click does', async () => {
    const worker = await installed();

    // Scripts/navigate.ts fetches the next page itself and replaces main.app-content. Its fetch is
    // not a navigation, so it must fall straight through: answering it from a cache would swap a
    // stored page into a live shell, and answering it with the offline notice would draw «no
    // connection» inside a panel that still has its sidebar and its running upload.
    const swap = await worker.request(`${Origin}/files?q=report`, { mode: 'cors' });

    expect(swap).toBeUndefined();
    expect(worker.network).toEqual([]);
  });
});

// ------------------------------------------------------------------ what it does cache

describe('the shell', () => {
  it('precaches the offline page and nothing else', async () => {
    const worker = boot();

    await worker.fire('install', {});

    expect([...worker.stored().values()].flat()).toEqual([`${Origin}/offline`]);
  });

  it('answers a navigation with the offline page when the network is gone', async () => {
    const worker = await installed();

    worker.disconnect();

    // Every address a reader can reach by pressing something, including the one navigate.ts hands
    // back to the browser when its own fetch fails.
    for (const path of ['/', '/files', '/Identity/Account/Login']) {
      const answer = await worker.request(Origin + path, { mode: 'navigate' });

      expect(answer?.body, `${path} while offline`).toBe(`${Origin}/offline`);
    }
  });

  it('fails a navigation honestly when it has no offline page to give', async () => {
    // An install whose fetch failed, or a worker seconds old. Inventing a page here would mean
    // writing the product's Persian into a JavaScript file, which is the one thing the offline page
    // being a Razor view exists to avoid.
    const worker = boot({ offline: true });

    await worker.fire('install', {});
    await worker.fire('activate', {});

    const answer = await worker.request(`${Origin}/files`, { mode: 'navigate' });

    expect(answer?.ok).toBe(false);
  });

  it('serves a hashed bundle from disk on the second ask', async () => {
    const worker = await installed();
    const url = `${Origin}/build/assets/main-CXIz7Wx2.js`;

    expect((await worker.request(url))?.body).toBe(url);
    expect((await worker.request(url))?.body).toBe(url);

    // Once. The address names one particular build, so a copy on disk is the copy that was asked
    // for and there is nothing to revalidate.
    expect(worker.network).toEqual([url]);
  });

  it('treats a stylesheet as immutable only while it carries its own version', async () => {
    const worker = await installed();

    const versioned = `${Origin}/css/app.css?v=8Kd1`;
    await worker.request(versioned);
    await worker.request(versioned);

    expect(worker.network).toEqual([versioned]);

    // Without the ?v= there is no address to distinguish this week's file from last week's, so it
    // is answered from disk and corrected behind that rather than pinned there.
    const bare = `${Origin}/css/app.css`;
    await worker.request(bare);
    await worker.request(bare);

    expect(worker.network).toEqual([versioned, bare, bare]);
  });

  it('revalidates the font and the icons, whose addresses never change', async () => {
    const worker = await installed();
    const font = `${Origin}/fonts/Vazirmatn-Variable.woff2`;

    await worker.request(font);
    await worker.request(font);
    await worker.request(font);

    // Answered from disk from the second ask on, and asked of the network every time — which is
    // what stops a pinned font outliving the deploy that replaced it.
    expect(worker.network).toEqual([font, font, font]);
  });

  it('does not store a failed response under an asset address', async () => {
    const worker = boot({ missing: ['/build/assets/main-gone.js'] });
    await worker.fire('install', {});
    await worker.fire('activate', {});

    const url = `${Origin}/build/assets/main-gone.js`;
    await worker.request(url);

    expect(worker.caches.get('driveunion-shell-v1')?.has(url)).toBe(false);
  });
});

// ------------------------------------------------------------------ version, activation, the seam

describe('the version story', () => {
  it('deletes every cache that is not the current one', async () => {
    const worker = boot();

    // Last version's shell, as it would be found on a phone the morning after a bump. The name is
    // the version, so activation is an atomic swap: the offline page and the stylesheet it links
    // are re-fetched together and cannot end up a deploy apart.
    worker.caches.set(
      'driveunion-shell-v0',
      new Map([[`${Origin}/css/app.css?v=old`, response('last week')]]));

    await worker.fire('install', {});
    await worker.fire('activate', {});

    expect([...worker.caches.keys()]).toEqual(['driveunion-shell-v1']);
  });

  it('imports its seams, which are the only way a second concern gets in', async () => {
    const worker = boot();

    // Two workers cannot hold one scope, so a second concern adds itself to this one through a file
    // it owns rather than by registering a worker of its own. Push was the first; playing an
    // encrypted file without downloading it is the second. If either import goes, the contract
    // written at the top of that file has gone with it and nothing else would say so.
    //
    // An exact list rather than a `toContain`. A third seam is a decision — every one of them runs
    // inside the worker that serves the app shell — and it should have to be made here, in a diff,
    // rather than accumulate.
    expect(worker.imported).toEqual(['/sw-push.js', '/sw-media.js']);
  });
});
