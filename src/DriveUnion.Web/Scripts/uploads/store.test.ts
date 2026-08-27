import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createUploadStore, type UploadConfig, type UploadStore } from './store';

/**
 * The queue, taken away from the network and from the phone and asked what it does about both.
 *
 * <p>What this file is really about is the one decision M4 turned on: a transfer that stopped
 * because the request never came back is not the same thing as a transfer somebody stopped, and it
 * is not the same thing as a transfer the server refused. The three have to behave differently when
 * the app comes back to the foreground, and there is no browser here to switch apps in — so the
 * events are checked by name and the behaviour is driven through <c>resumeStalled</c>, which is what
 * those events call.</p>
 *
 * <p>The transport is faked rather than mocked at the module boundary, because the thing worth
 * testing is exactly the difference between «no answer» and «an answer that says no», and that
 * difference only exists at the transport.</p>
 */

// ── the browser, as much of it as the queue asks about ────────────────────────────────────────────

/** Every listener the store registered, by event name. Also the record of *which* events it chose. */
let listeners: Map<string, ((event: unknown) => void)[]>;

function define(name: string, value: unknown) {
  Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
}

function fire(type: string) {
  for (const listener of listeners.get(type) ?? []) listener({});
}

function subscribing() {
  return {
    addEventListener(type: string, listener: (event: unknown) => void) {
      listeners.set(type, [...(listeners.get(type) ?? []), listener]);
    },
  };
}

// ── the transport ─────────────────────────────────────────────────────────────────────────────────

/**
 * What the next chunk meets.
 *
 * <p><c>'gone'</c> is the phone: the request produces no response at all, which is what a suspended
 * tab, a lift and a dropped connection look like from inside <c>putChunk</c>. Everything else is an
 * answer, and an answer — even a refusal — is the server having spoken.</p>
 */
type ChunkOutcome = 'ok' | 'gone' | { status: number; body: string };

/** Queued outcomes; anything past the end of the list succeeds. */
let outcomes: ChunkOutcome[] = [];

/** Every `Content-Range` that was actually put on the wire, so a resume can be shown to skip. */
let ranges: string[] = [];

/**
 * What each session has committed — the server's count, which is the one a resume believes.
 *
 * <p>Per session rather than one number, because the concurrency test runs three files at once and a
 * shared count would have file two starting where file one finished.</p>
 */
let confirmedOnServer: Map<string, number>;

/**
 * How long a chunk spends on the wire.
 *
 * <p>Zero for every test about what the queue decides, where an instant transport keeps the
 * arithmetic of fake time down to the store's own waits. Set only by the test about how many files
 * move at once, which cannot ask that question of a transport that finishes before it is asked.</p>
 */
let chunkDelayMs = 0;

interface XhrHandlers {
  onload: (() => void) | null;
  onerror: (() => void) | null;
  ontimeout: (() => void) | null;
  onabort: (() => void) | null;
}

class FakeXhr implements XhrHandlers {
  upload: { onprogress: ((event: { loaded: number }) => void) | null } = { onprogress: null };

  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  ontimeout: (() => void) | null = null;
  onabort: (() => void) | null = null;

  status = 0;
  statusText = '';
  responseText = '';

  private range = '';
  private session = '';

  /** `/api/uploads/session-1/chunk` — the session is what the answer has to be counted against. */
  open(_method: string, url: string) {
    this.session = url.split('/').at(-2) ?? '';
  }

  setRequestHeader(name: string, value: string) {
    if (name === 'Content-Range') this.range = value;
  }

  getResponseHeader() {
    return null;
  }

  abort() {
    this.onabort?.();
  }

  send(body: Blob) {
    ranges.push(this.range);

    const outcome = outcomes.shift() ?? 'ok';
    const deliver = () => this.answer(body, outcome);

    if (chunkDelayMs > 0) setTimeout(deliver, chunkDelayMs);
    else deliver();
  }

  private answer(body: Blob, outcome: ChunkOutcome) {
    if (outcome === 'gone') {
      this.onerror?.();
      return;
    }

    this.upload.onprogress?.({ loaded: body.size });

    if (outcome === 'ok') {
      // `bytes 1024-2047/3000` — what this session has once it commits this one.
      const committed = Number(this.range.split('-')[1].split('/')[0]) + 1;

      confirmedOnServer.set(this.session, committed);

      this.status = 200;
      this.responseText = JSON.stringify({
        bytesConfirmed: committed,
        status: 'InProgress',
        failureReason: null,
      });
    } else {
      this.status = outcome.status;
      this.statusText = 'Refused';
      this.responseText = outcome.body;
    }

    this.onload?.();
  }
}

/** Sessions opened, so «a resume opens a second session» would be visible rather than silent. */
let sessionsOpened = 0;

/** Progress reads, which is how a resume finds out what the server actually has. */
let progressReads = 0;

/** Set to make the two `fetch` calls fail the way a phone makes them fail: no answer at all. */
let fetchIsGone = false;

function fakeFetch(url: string, init?: { method?: string }): Promise<Response> {
  if (fetchIsGone) return Promise.reject(new TypeError('Load failed'));

  if (init?.method === 'POST') {
    sessionsOpened++;

    return Promise.resolve(
      new Response(
        JSON.stringify({ id: `session-${sessionsOpened}`, chunkSize: 1024 }),
        { status: 200 }));
  }

  progressReads++;

  return Promise.resolve(
    new Response(
      JSON.stringify({
        bytesConfirmed: confirmedOnServer.get(url.split('/').at(-1) ?? '') ?? 0,
        status: 'InProgress',
        failureReason: null,
      }),
      { status: 200 }));
}

// ── the harness ───────────────────────────────────────────────────────────────────────────────────

const config = (): UploadConfig => ({
  beginUrl: '/api/uploads',
  antiforgeryHeader: 'X-CSRF',
  antiforgeryToken: 'token',
  lang: 'en',
});

/** 3000 bytes against a 1024-byte chunk: three chunks, so «continues from the middle» is expressible. */
const FileBytes = 3000;

const holiday = () => new File([new Uint8Array(FileBytes)], 'holiday.mp4', { type: 'video/mp4' });

/**
 * Runs the queue forward.
 *
 * <p>Timers are faked because the chunk retries wait one second and then two, and the wake sweep
 * waits half a second and then eight — four real pauses per test is four seconds of nothing.</p>
 */
const settle = (ms = 0) => vi.advanceTimersByTimeAsync(ms);

/** Long enough for three chunk attempts and both waits between them. */
const ThroughEveryAttempt = 4000;

/** Long enough for both sweeps booked by a wake. */
const ThroughBothSweeps = 9000;

let store: UploadStore;

beforeEach(() => {
  vi.useFakeTimers();

  listeners = new Map();
  outcomes = [];
  ranges = [];
  confirmedOnServer = new Map();
  chunkDelayMs = 0;
  sessionsOpened = 0;
  progressReads = 0;
  fetchIsGone = false;

  const stored = new Map<string, string>();

  define('localStorage', {
    getItem: (key: string) => stored.get(key) ?? null,
    setItem: (key: string, value: string) => stored.set(key, value),
  });

  define('document', { visibilityState: 'visible', ...subscribing() });
  define('window', subscribing());
  define('navigator', { onLine: true });
  define('XMLHttpRequest', FakeXhr);
  define('fetch', fakeFetch);

  store = createUploadStore(config);
});

afterEach(() => vi.useRealTimers());

/** Queues one file and lets it get as far as it is going to get. */
async function upload(ms = ThroughEveryAttempt) {
  store.add([holiday()]);
  await settle(ms);

  return store.items.value[0];
}

// ── which events mean «the app is back» ───────────────────────────────────────────────────────────

describe('coming back to the app', () => {
  /**
   * The three subscriptions, checked by name.
   *
   * There is no browser here to background, so what this can hold is the wiring — and the wiring is
   * exactly what a later edit removes without failing anything else. A queue that resumes correctly
   * and is never told the app came back is a queue that never resumes.
   */
  it('listens for the three events a phone announces itself with', () => {
    expect([...listeners.keys()].sort()).toEqual(['online', 'pageshow', 'visibilitychange']);
  });

  it('picks up a transfer the connection took away, from where the server got to', async () => {
    // One chunk committed, then nothing answers at all — three attempts, and the phone is asleep.
    outcomes = ['ok', 'gone', 'gone', 'gone'];

    const item = await upload();

    expect(item.status).toBe('interrupted');
    expect(item.confirmed).toBe(1024);

    fire('visibilitychange');
    await settle(ThroughBothSweeps);

    expect(item.status).toBe('done');

    // The session is the same one, and the resume asked the server what it had rather than
    // continuing from the client's own idea of it.
    expect(sessionsOpened).toBe(1);
    expect(progressReads).toBe(2);

    // 1024 was committed before the connection went; the three attempts that answered nothing asked
    // for the same range each time, and the resume carried on from there.
    expect(ranges).toEqual([
      'bytes 0-1023/3000',
      'bytes 1024-2047/3000',
      'bytes 1024-2047/3000',
      'bytes 1024-2047/3000',
      'bytes 1024-2047/3000',
      'bytes 2048-2999/3000',
    ]);
  });

  /**
   * The half that would make this feature worse than not having it.
   *
   * Somebody who pressed Pause and then took a phone call has decided something, and finding the
   * upload running again on their mobile data when they came back would be the product overruling
   * them. Only <c>pause</c> writes «paused», so the sweep has one word to avoid.
   */
  it('does not restart a transfer the customer paused', async () => {
    outcomes = ['ok', 'gone', 'gone', 'gone'];

    const item = await upload();
    expect(item.status).toBe('interrupted');

    // Pausing an interrupted file is «stop picking this up», and it has to be honoured by the very
    // next thing that would have picked it up.
    store.pause(item.id);
    expect(item.status).toBe('paused');

    fire('visibilitychange');
    await settle(ThroughBothSweeps);

    expect(item.status).toBe('paused');
    expect(item.confirmed).toBe(1024);
  });

  /**
   * And the other half: a refusal that was actually given.
   *
   * A plan ceiling or a full workspace says the same thing every time it is asked, so resuming it on
   * every app switch is a request that cannot succeed, repeated for as long as the tab is open.
   */
  it('does not resume a file the server refused', async () => {
    outcomes = [{ status: 400, body: '{"error":"tenant_quota_exceeded","capBytes":10,"usedBytes":10}' }];

    const item = await upload();

    expect(item.status).toBe('failed');
    expect(item.error).toContain('out of space');

    fire('visibilitychange');
    await settle(ThroughBothSweeps);

    expect(item.status).toBe('failed');
    expect(ranges).toHaveLength(1);
  });

  it('resumes when the network comes back, without waiting to be switched away from', async () => {
    outcomes = ['gone', 'gone', 'gone'];

    const item = await upload();
    expect(item.status).toBe('interrupted');

    fire('online');
    await settle(ThroughBothSweeps);

    expect(item.status).toBe('done');
  });

  /**
   * A hidden page is a frozen page on the phone this is for, and a resume issued into one is a chunk
   * attempt spent on nothing. `online` can arrive while the app is in the background.
   */
  it('leaves everything alone while the app is not in front', async () => {
    outcomes = ['gone', 'gone', 'gone'];

    const item = await upload();

    define('document', { visibilityState: 'hidden', ...subscribing() });

    store.resumeStalled();
    await settle(ThroughBothSweeps);

    expect(item.status).toBe('interrupted');
  });

  it('leaves everything alone while the device says it has no network', async () => {
    outcomes = ['gone', 'gone', 'gone'];

    const item = await upload();

    define('navigator', { onLine: false });

    store.resumeStalled();
    await settle(ThroughBothSweeps);

    expect(item.status).toBe('interrupted');
  });

  /**
   * Three files stalled together come back three files at a time and go out two at a time, because
   * that is what the queue was already told to do. Resuming deliberately does not have a second,
   * private answer to «how much of this connection may we use».
   */
  it('does not put more back on the wire than the queue is allowed to move', async () => {
    outcomes = Array<ChunkOutcome>(9).fill('gone');

    store.setConcurrency(1);
    store.add([holiday(), holiday(), holiday()]);
    await settle(ThroughEveryAttempt * 3);

    expect(store.items.value.every((i) => i.status === 'interrupted')).toBe(true);

    // Now the connection is back but slow, which is the state a phone actually comes back in and
    // the only one in which «how many at once» is a question with an observable answer.
    chunkDelayMs = 60_000;

    fire('visibilitychange');
    await settle(600);

    expect(store.items.value.filter((i) => i.status === 'uploading')).toHaveLength(1);
    expect(store.items.value.filter((i) => i.status === 'queued')).toHaveLength(2);
  });

  /**
   * A wake books two passes and three events book the same two, not six. The second pass is the one
   * that matters on a phone: a chunk iOS killed cannot report it until the page runs again, so the
   * file is still «uploading» when the first pass looks at it.
   */
  it('sweeps again later, for what had not finished failing yet', async () => {
    outcomes = ['gone', 'gone', 'gone'];

    store.add([holiday()]);

    // The order a phone actually produces: the app is back before the chunk that iOS killed has
    // said so, because it cannot say so until this page is running again.
    fire('visibilitychange');
    fire('pageshow');
    fire('online');

    const item = store.items.value[0];

    // The first pass, at half a second, has nothing to find: the file is between attempts.
    await settle(600);
    expect(item.status).toBe('uploading');

    // The third attempt gives up shortly after — too late for the pass that already ran.
    await settle(ThroughEveryAttempt);
    expect(item.status).toBe('interrupted');

    // Nothing else is going to happen: no event is coming, because the app is already in front.
    // Without a second pass this is where the transfer stays until somebody switches away and back.
    await settle(3000);
    expect(item.status).toBe('interrupted');

    await settle(ThroughBothSweeps);
    expect(item.status).toBe('done');
  });
});

// ── what «stopped» is allowed to mean ─────────────────────────────────────────────────────────────

describe('telling a stall from a refusal', () => {
  it('treats a begin that never answered as a stall rather than a failure', async () => {
    fetchIsGone = true;

    const item = await upload();

    expect(item.status).toBe('interrupted');
    expect(item.error).toContain('come back');

    fetchIsGone = false;
    fire('visibilitychange');
    await settle(ThroughBothSweeps);

    expect(item.status).toBe('done');
  });

  /**
   * The sign-in page, which arrives as a 200 and a login form because both fetch and XHR follow the
   * redirect. It is an answer, so it fails and stays failed — resuming it would ask an expired
   * session to open an upload on every app switch for as long as the tab is open.
   */
  it('treats a 2xx that is not JSON as the session having ended', async () => {
    define('fetch', () =>
      Promise.resolve(new Response('<!doctype html><form action="/Identity/Account/Login">', {
        status: 200,
      })));

    const item = await upload();

    expect(item.status).toBe('failed');
    expect(item.error).toContain('session has ended');

    fire('visibilitychange');
    await settle(ThroughBothSweeps);

    expect(item.status).toBe('failed');
  });

  it('leaves an empty file refused where it was, rather than resuming it for ever', async () => {
    store.add([new File([], 'nothing.txt')]);
    await settle();

    const item = store.items.value[0];
    expect(item.status).toBe('failed');

    fire('visibilitychange');
    await settle(ThroughBothSweeps);

    expect(item.status).toBe('failed');
    expect(ranges).toHaveLength(0);
  });
});

// ── the screen ────────────────────────────────────────────────────────────────────────────────────

describe('keeping the screen awake', () => {
  function withWakeLock() {
    const sentinel = { released: false, release: vi.fn(() => Promise.resolve()) };
    const request = vi.fn(() => Promise.resolve(sentinel as unknown as WakeLockSentinel));

    define('navigator', { onLine: true, wakeLock: { request } });

    // Built after the navigator it will ask, because the store subscribes at construction.
    store = createUploadStore(config);

    return { request, sentinel };
  }

  it('is asked for while the queue is moving and given back when it empties', async () => {
    const lock = withWakeLock();

    await upload();

    expect(lock.request).toHaveBeenCalledWith('screen');
    expect(lock.sentinel.release).toHaveBeenCalled();
  });

  /**
   * The return to the foreground asks for the lock again, because the browser took the last one
   * away — but only if there is something to keep the screen on for. A phone that will not dim
   * because somebody opened the panel to look at a finished list is a battery complaint.
   */
  it('is not asked for on a return to an app with nothing moving', async () => {
    const lock = withWakeLock();

    fire('visibilitychange');
    fire('pageshow');
    await settle(ThroughBothSweeps);

    expect(lock.request).not.toHaveBeenCalled();
  });

  /**
   * The whole of the feature detection. Safari has had it since 16.4, and an older phone is most of
   * why this product has a mobile phase at all — a queue that threw here would upload nothing on
   * exactly the devices this work is for.
   */
  it('is not reached for at all on a browser that does not have it', async () => {
    const item = await upload();

    expect(item.status).toBe('done');
  });
});
