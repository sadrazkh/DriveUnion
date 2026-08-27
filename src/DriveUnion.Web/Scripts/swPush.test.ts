import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * The push half of the service worker: what it draws, and what it refuses to keep.
 *
 * The file under test is wwwroot/sw-push.js itself, read off disk and evaluated with a stand-in for
 * the globals a worker has. It is deliberately the shipped bytes rather than a module imported
 * through the bundler — the file is outside the bundle on purpose, and a test that pulled it through
 * Vite would be testing something that is never served. The same bargain Scripts/sw.test.ts makes.
 *
 * The assertions that matter here are the negative ones. A notification that fails to appear is a
 * missing feature somebody reports; a worker that writes a payload into a cache or into IndexedDB is
 * a customer's news accumulating on a phone in a product sold on the server holding no readable
 * copy, and nothing anywhere reports that. So: the happy path is checked once, and every way the
 * payload could outlive the notification is checked from both ends.
 */

const Origin = 'https://panel.driveunion.test';

interface Shown {
  title: string;
  options: Record<string, unknown>;
}

interface FakeClient {
  url: string;
  focused: boolean;
  navigated: string | null;
  focus: () => Promise<void>;
  navigate?: (url: string) => Promise<void>;
}

interface Worker {
  /** Runs a listener and settles everything it started through waitUntil. */
  fire: (type: string, event: Record<string, unknown>) => Promise<void>;

  /** Every notification drawn, in order. */
  shown: Shown[];

  /** Every notification closed. */
  closed: number;

  /** Windows the worker can see, and what it did to them. */
  clients: FakeClient[];

  /** Addresses opened in a new window. */
  opened: string[];

  /** Anything the worker asked the platform to store. Nothing ever should. */
  storage: string[];

  /** The listener names the file registered. */
  listeners: string[];
}

/**
 * The worker's push half, evaluated in a scope we control.
 *
 * `new Function` rather than an import: this is a classic script that registers its listeners as a
 * side effect of being evaluated, which is exactly how a browser runs it. Naming the globals as
 * parameters shadows Node's own — and it is also what makes «did this file touch caches» a question
 * with an answer, because the stand-ins record every call.
 */
function boot(): Worker {
  const source = readFileSync(resolve(import.meta.dirname, '../wwwroot/sw-push.js'), 'utf8');

  const handlers = new Map<string, (event: unknown) => void>();
  const shown: Shown[] = [];
  const storage: string[] = [];
  const opened: string[] = [];
  const clients: FakeClient[] = [];
  let closed = 0;

  const scope = {
    location: new URL(`${Origin}/sw.js`),
    addEventListener: (type: string, handler: (event: unknown) => void) => {
      handlers.set(type, handler);
    },
    registration: {
      showNotification: async (title: string, options: Record<string, unknown>) => {
        shown.push({ title, options });
      },
    },
    clients: {
      matchAll: async () => clients,
      openWindow: async (url: string) => {
        opened.push(url);
      },
    },
  };

  // Every way a worker can write something down. Each one records rather than working, so that
  // «nothing outlives the notification» is a thing this file can assert rather than read.
  const caches = {
    open: async (name: string) => {
      storage.push(`caches.open(${name})`);

      return { put: async () => undefined, add: async () => undefined };
    },
    keys: async () => {
      storage.push('caches.keys');

      return [];
    },
  };

  const indexedDB = {
    open: (name: string) => {
      storage.push(`indexedDB.open(${name})`);

      return {};
    },
  };

  const localStorage = {
    setItem: (key: string) => {
      storage.push(`localStorage.setItem(${key})`);
    },
  };

  // eslint-disable-next-line @typescript-eslint/no-implied-eval
  new Function('self', 'caches', 'indexedDB', 'localStorage', source)(
    scope,
    caches,
    indexedDB,
    localStorage);

  const fire = async (type: string, event: Record<string, unknown>): Promise<void> => {
    const waited: unknown[] = [];
    const handler = handlers.get(type);

    expect(handler, `sw-push.js registers no ${type} listener`).toBeDefined();

    handler!({
      ...event,
      waitUntil: (promise: unknown) => waited.push(promise),
      notification: {
        ...(event.notification as Record<string, unknown> | undefined),
        close: () => {
          closed += 1;
        },
      },
    });

    await Promise.allSettled(waited);
  };

  return {
    fire,
    shown,
    storage,
    opened,
    clients,
    listeners: [...handlers.keys()],
    get closed() {
      return closed;
    },
  };
}

/** A push event carrying a payload this server would really have sent. */
function push(payload: unknown): Record<string, unknown> {
  return {
    data: {
      json: () => {
        if (payload === undefined) throw new SyntaxError('no payload');

        return payload;
      },
    },
  };
}

function window(url: string, canNavigate = true): FakeClient {
  const client: FakeClient = {
    url,
    focused: false,
    navigated: null,
    focus: async () => {
      client.focused = true;
    },
  };

  if (canNavigate) {
    client.navigate = async (to: string) => {
      client.navigated = to;
    };
  }

  return client;
}

// ------------------------------------------------------------------ the contract with sw.js

describe('what this file is allowed to add to the worker', () => {
  it('registers a push and a notificationclick listener and nothing else', () => {
    // sw.js owns fetch, install and activate. A second listener for any of them races the first —
    // and which one wins depends on registration order, so the caching rules would be overridden by
    // accident on some page loads and not others.
    expect(boot().listeners.sort()).toEqual(['notificationclick', 'push']);
  });

  it('writes nothing to any storage the platform offers', async () => {
    const worker = boot();

    await worker.fire('push', push({ t: 'title', b: 'body', u: '/files', g: 'tag' }));
    await worker.fire('notificationclick', { notification: { data: { url: '/files' } } });

    // The whole product claim, at its narrowest point. A payload is decrypted on this device and
    // drawn; what must not happen is it being written anywhere it outlives the notification.
    expect(worker.storage).toEqual([]);
  });
});

// ------------------------------------------------------------------ drawing a notification

describe('a push that arrives', () => {
  it('draws the title, the body and the tag the server sent', async () => {
    const worker = boot();

    await worker.fire('push', push({ t: 'حذف تمام شد', b: '۷ فایل به زباله‌دان رفت.', u: '/trash', g: 'deletioncompleted' }));

    expect(worker.shown).toHaveLength(1);
    expect(worker.shown[0].title).toBe('حذف تمام شد');
    expect(worker.shown[0].options.body).toBe('۷ فایل به زباله‌دان رفت.');

    // Same tag, same entry: five link-uploads finishing while a phone is asleep leave one
    // notification rather than five identical ones to swipe away.
    expect(worker.shown[0].options.tag).toBe('deletioncompleted');

    // The path travels on the notification. A click handler runs in a worker that may have been
    // terminated and restarted since, so there is no variable from the push still standing.
    expect(worker.shown[0].options.data).toEqual({ url: '/trash' });
  });

  it('shows something even when the payload will not parse', async () => {
    const worker = boot();

    await worker.fire('push', push(undefined));

    // A worker that receives a push and draws nothing is a "silent push". Browsers answer a run of
    // them with their own "this site has been updated in the background" notice — and iOS revokes
    // the permission outright, which would take the feature away from the one platform it was
    // built for.
    expect(worker.shown).toHaveLength(1);
    expect(worker.shown[0].title).toBeTruthy();
  });

  it('refuses a url that is not a path in this panel', async () => {
    const worker = boot();

    // The endpoint is not a secret — only a VAPID-signed sender is accepted, but the shape of what
    // arrives is still not something to take on trust. An absolute URL here would be a notification
    // that opens somewhere else entirely when it is tapped.
    const refused = [
      'https://evil.example/steal',
      '//evil.example',
      'javascript:alert(1)',

      // The one that got through the first guard, and the reason this check resolves the URL
      // instead of matching its prefix: a URL parser normalises a backslash to a forward slash in
      // the authority position for http and https, so this begins with exactly one slash, passes
      // «starts with / and not with //», and resolves to https://evil.example. Verified in a
      // browser rather than reasoned about.
      String.raw`/\evil.example`,
      String.raw`/\/evil.example`,
      String.raw`/\\evil.example`,
    ];

    for (const url of refused) {
      await worker.fire('push', push({ t: 'x', b: '', u: url, g: 'g' }));
    }

    expect(worker.shown.map((each) => each.options.data))
      .toEqual(refused.map(() => ({ url: '/' })));
  });

  it('still allows the addresses the panel actually sends', async () => {
    const worker = boot();

    // The other half, and it is not decoration: a guard that refused everything would pass every
    // assertion above and quietly send every notification in the product to the dashboard.
    for (const url of ['/files', '/trash', '/operator/abuse', '/files?folder=1']) {
      await worker.fire('push', push({ t: 'x', b: '', u: url, g: 'g' }));
    }

    expect(worker.shown.map((each) => each.options.data)).toEqual([
      { url: '/files' },
      { url: '/trash' },
      { url: '/operator/abuse' },
      { url: '/files?folder=1' },
    ]);
  });

  it('ignores fields that are not the shape the server sends', async () => {
    const worker = boot();

    await worker.fire('push', push({ t: 'title', b: { nested: true }, u: 42, g: null }));

    expect(worker.shown[0].options.body).toBe('');
    expect(worker.shown[0].options.data).toEqual({ url: '/' });
    expect(typeof worker.shown[0].options.tag).toBe('string');
  });

  it('does not insist on being read', async () => {
    const worker = boot();

    await worker.fire('push', push({ t: 'x', b: '', u: '/', g: 'g' }));

    // requireInteraction keeps a notification on screen until it is dismissed, and renotify makes a
    // replacement buzz again. Nothing this product sends is worth either: a link-upload finished, a
    // deletion finished, somebody filed a report.
    expect(worker.shown[0].options.requireInteraction).toBeUndefined();
    expect(worker.shown[0].options.renotify).toBeUndefined();
  });
});

// ------------------------------------------------------------------ a tap

describe('a notification that is tapped', () => {
  it('focuses the window that is already open rather than opening a second one', async () => {
    const worker = boot();
    const open = window(`${Origin}/files`);
    worker.clients.push(open);

    await worker.fire('notificationclick', { notification: { data: { url: '/trash' } } });

    // The panel is an app shell that never reloads — navigate.ts swaps main.app-content and the
    // shell stays up — so a second window would abandon whatever is in the upload queue of the
    // first, which is the 96 GB transfer the whole architecture exists for.
    expect(open.focused).toBe(true);
    expect(open.navigated).toBe('/trash');
    expect(worker.opened).toEqual([]);
    expect(worker.closed).toBe(1);
  });

  it('leaves a focused window where it is when the platform will not navigate it', async () => {
    const worker = boot();
    const open = window(`${Origin}/files`, false);
    worker.clients.push(open);

    await worker.fire('notificationclick', { notification: { data: { url: '/trash' } } });

    // iOS has no client.navigate. A focused panel one press from the answer is a great deal better
    // than a rejected promise nobody sees.
    expect(open.focused).toBe(true);
    expect(worker.opened).toEqual([]);
  });

  it('opens a window when there is none', async () => {
    const worker = boot();

    await worker.fire('notificationclick', { notification: { data: { url: '/operator/abuse' } } });

    expect(worker.opened).toEqual(['/operator/abuse']);
  });

  it('opens the panel when the notification carries no address', async () => {
    const worker = boot();

    await worker.fire('notificationclick', { notification: {} });

    expect(worker.opened).toEqual(['/']);
  });

  it('refuses an address on an old notification that the push handler would refuse today', async () => {
    const worker = boot();

    // A notification drawn by an earlier version of this file — one sitting in a notification
    // centre right now — arrives at the click handler with whatever data it was given. The guard
    // belongs where the address is used, not only where it was read.
    await worker.fire('notificationclick', { notification: { data: { url: '//evil.example' } } });

    // And the backslash variant, which is the one that would actually still be sitting in somebody's
    // notification centre: it passed the guard this file shipped with before the check was changed
    // to resolve the address instead of matching its prefix.
    await worker.fire('notificationclick', {
      notification: { data: { url: String.raw`/\evil.example` } },
    });

    expect(worker.opened).toEqual(['/', '/']);
  });
});
