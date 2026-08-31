import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { beforeEach, describe, expect, it } from 'vitest';
import type { Bytes } from './crypto/format';
import { save, type SavedFile } from './offline/library';

/**
 * A save that keeps downloading after the tab is closed.
 *
 * <p>The file under test is <c>wwwroot/sw-download.js</c> itself, read off disk and evaluated
 * against stand-ins for the globals a worker has — the shipped bytes, not a module pulled through
 * Vite, because the file is outside the bundle on purpose and a bundled copy is one that is never
 * served. The same bargain <c>sw.test.ts</c>, <c>swPush.test.ts</c> and <c>swMedia.test.ts</c>
 * make.</p>
 *
 * <p><b>The assertions that matter are about what is left behind.</b> Everything this worker does
 * happens with no page open and no reader watching: a failure that writes bytes nobody can account
 * for, or a manifest entry pointing at a file that was never created, does not surface as an error
 * anywhere — it surfaces weeks later as storage that will not free itself, or as a row in the
 * offline list that offers to play something that is gone. So a success is checked once and every
 * ending that is not a success is checked from both sides: nothing recorded, and nothing on the
 * disk.</p>
 *
 * <p>The other half is the agreement tests. <c>sw-download.js</c> hand-copies the directory name and
 * the file-naming rule out of <c>Scripts/offline/library.ts</c>, because a classic worker cannot
 * import a TypeScript module — the same copy <c>sw-media.js</c> makes of the <c>du1</c> constants,
 * with the same failure if the two drift: the worker writes beside the manifest rather than into
 * it, nothing throws, and the page reports an honest, wrong «the download did not finish». They are
 * pinned here by running library.ts's real <c>save()</c> against the same fake disk and reading back
 * what it actually called things, rather than by spelling the rule a third time in a test where it
 * would agree with the worker by construction.</p>
 */

const Origin = 'https://panel.driveunion.test';

const source = readFileSync(
  resolve(import.meta.dirname, '../wwwroot/sw-download.js'),
  'utf8',
);

// ── OPFS, as much of it as the worker touches ─────────────────────────────────────────────────────
//
// Lifted from Scripts/offline/library.test.ts and kept deliberately identical in behaviour, because
// the two files write into one directory and a fake that behaved differently for each would be two
// disks rather than one.

class FakeFile {
  constructor(readonly bytes: Uint8Array, readonly name: string) {}

  text() {
    return Promise.resolve(new TextDecoder().decode(this.bytes));
  }

  get size() {
    return this.bytes.length;
  }
}

class FakeFileHandle {
  bytes = new Uint8Array(0);

  constructor(readonly name: string) {}

  getFile() {
    return Promise.resolve(new FakeFile(this.bytes, this.name));
  }

  createWritable(options?: { keepExistingData?: boolean }) {
    const chunks: Uint8Array[] = options?.keepExistingData ? [this.bytes.slice()] : [];

    return Promise.resolve({
      seek: async (_at: number) => {
        // Neither writer seeks anywhere but the end of what it kept, which is where writes land.
      },
      // A real FileSystemWritableFileStream takes a string as readily as it takes bytes, and the
      // manifest is written as one. A fake that only took bytes would store a run of zeros, which
      // parses as nothing and makes every read come back empty.
      write: async (chunk: Uint8Array | string) => {
        if (writeThrowsAfter !== null && this.name.endsWith('.raw')) {
          if (dataWrites >= writeThrowsAfter) throw new Error('quota');
          dataWrites++;
        }

        chunks.push(typeof chunk === 'string' ? new TextEncoder().encode(chunk) : chunk);
      },
      close: async () => {
        const total = chunks.reduce((n, c) => n + c.length, 0);
        const joined = new Uint8Array(total);
        let at = 0;

        for (const chunk of chunks) {
          joined.set(chunk, at);
          at += chunk.length;
        }

        this.bytes = joined;
      },
    });
  }
}

class FakeDirectory {
  readonly files = new Map<string, FakeFileHandle>();
  readonly directories = new Map<string, FakeDirectory>();

  async getDirectoryHandle(name: string, options?: { create?: boolean }) {
    const existing = this.directories.get(name);
    if (existing) return existing;
    if (!options?.create) throw new DOMException('not found');

    const made = new FakeDirectory();
    this.directories.set(name, made);

    return made;
  }

  async getFileHandle(name: string, options?: { create?: boolean }) {
    const existing = this.files.get(name);
    if (existing) return existing;
    if (!options?.create) throw new DOMException('not found');

    const made = new FakeFileHandle(name);
    this.files.set(name, made);

    return made;
  }

  async *keys() {
    for (const name of [...this.files.keys()]) yield name;
  }

  async removeEntry(name: string, options?: { recursive?: boolean }) {
    if (options?.recursive && this.directories.delete(name)) return;
    if (!this.files.delete(name)) throw new DOMException('not found');
  }
}

let root: FakeDirectory;

/** Set to make the writer fail after N chunks of film, which is what a full disk looks like. */
let writeThrowsAfter: number | null;
let dataWrites: number;

function define(name: string, value: unknown) {
  Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
}

// ── The worker, and the events the browser wakes it with ──────────────────────────────────────────

/** The metadata the page puts on a registration id, which is a SavedFile without its progress. */
interface Meta {
  key: string;
  name: string;
  type: string;
  bytes: number;
  savedAt: number;
  watchUrl: string;
}

interface Surface {
  IdPrefix: string;
  claims: (id: unknown) => boolean;
  idFor: (entry: Meta) => string;
  metaFrom: (id: unknown) => Meta | null;
  stagedNameFor: (key: string) => string;
  staged: () => Promise<SavedFile[]>;
  stagedFile: (key: string) => Promise<FakeFile | null>;
  discard: (key: string) => Promise<void>;
}

interface Harness {
  /** What the file installed on `self`. */
  download: Surface;

  /** Delivers one of the three events the way the browser would, and settles what it started. */
  fire: (type: string, registration: unknown) => Promise<void>;

  /** The `offline` directory, or undefined if the worker never made one. */
  offline: () => FakeDirectory | undefined;

  /** The manifest as it stands, parsed the way library.ts parses it. */
  manifest: () => Promise<SavedFile[]>;
}

/**
 * Evaluates the worker with a fake `self` and a fake `navigator`.
 *
 * <p><c>new Function</c> rather than an import, and naming the globals as parameters shadows Node's
 * own — the point being that this is a classic script which registers its listeners as a side effect
 * of being evaluated, which is exactly how a browser runs it.</p>
 */
function boot(): Harness {
  // A list per type, not one handler per type: a worker delivers every event to every listener, and
  // a Map that overwrote would quietly test a file with all but its last listener removed. The
  // mistake swMedia.test.ts's harness made first, where it read as the feature being broken.
  const listeners = new Map<string, ((event: unknown) => void)[]>();

  const self: Record<string, unknown> = {
    location: new URL(`${Origin}/sw-download.js`),
    addEventListener: (type: string, handler: (event: unknown) => void) => {
      const existing = listeners.get(type) ?? [];
      existing.push(handler);
      listeners.set(type, existing);
    },
  };

  const navigator = { storage: { getDirectory: async () => root } };

  const factory = new Function('self', 'navigator', source);

  factory(self, navigator);

  const offline = () => root.directories.get('offline');

  return {
    download: self.du1Download as Surface,

    fire: async (type: string, registration: unknown) => {
      const waited: unknown[] = [];

      for (const handler of listeners.get(type) ?? []) {
        handler({ registration, waitUntil: (promise: unknown) => waited.push(promise) });
      }

      // allSettled rather than all: a handler whose copy failed is one of the cases below, and what
      // is being asserted about it is the state of the disk afterwards rather than the rejection.
      await Promise.allSettled(waited);
    },

    offline,

    manifest: async () => {
      const dir = offline();
      if (!dir) return [];

      const handle = dir.files.get('index.json');
      if (!handle) return [];

      const parsed: unknown = JSON.parse(await new FakeFile(handle.bytes, 'index.json').text());

      return Array.isArray(parsed) ? (parsed as SavedFile[]) : [];
    },
  };
}

const meta: Meta = {
  key: '/d/film2026/file',
  name: 'a-short-film.webm',
  type: 'video/webm',
  bytes: 4096,
  savedAt: 1_700_000_000_000,
  watchUrl: '/d/film2026/watch',
};

/**
 * What the browser hands the worker: an id, and the records it downloaded under it.
 *
 * <p><c>Bytes</c> and not a bare <c>Uint8Array</c> for the body. TS 5.7 distinguishes
 * <c>Uint8Array&lt;ArrayBuffer&gt;</c> from <c>Uint8Array&lt;ArrayBufferLike&gt;</c> and only the
 * first is a <c>BodyInit</c> — the same distinction <c>Scripts/crypto/format.ts</c> introduced
 * <c>Bytes</c> for, and the same one <c>swMedia.test.ts</c> reaches for <c>slice</c> over.</p>
 */
function registrationOf(
  id: string,
  records: { url?: string; body?: Bytes | null; ok?: boolean; redirected?: boolean }[] = [
    { body: new Uint8Array(64).fill(7) as Bytes },
  ],
) {
  return {
    id,
    matchAll: async () => records.map((record) => ({
      request: { url: record.url ?? `${Origin}${meta.key}` },
      responseReady: Promise.resolve({
        ok: record.ok ?? true,
        redirected: record.redirected ?? false,
        body: record.body === null
          ? null
          : new Response(record.body ?? new Uint8Array(0)).body,
      }),
    })),
  };
}

beforeEach(() => {
  root = new FakeDirectory();
  writeThrowsAfter = null;
  dataWrites = 0;

  define('DOMException', class extends Error {});
});

// ── the names both sides have to agree on ─────────────────────────────────────────────────────────

describe('the names it shares with the offline library', () => {
  /**
   * <b>The agreement test.</b> The worker hand-copies <c>fileNameFor</c> and the directory name out
   * of library.ts and there is no way for it to import them. If the copy drifts — a different
   * escape, a different suffix, a different directory — the worker writes a perfectly good file
   * somewhere the page never looks, nothing throws anywhere, and the reader is told the download did
   * not finish.
   *
   * <p>Pinned against what library.ts actually does rather than against the rule written out again:
   * a real <c>save()</c> is run and the name it chose is read back off the fake disk.</p>
   */
  it('stages under the name library.ts uses for the same key, plus a suffix', async () => {
    define('navigator', {
      storage: {
        getDirectory: async () => root,
        estimate: async () => ({ quota: 1024 * 1024 * 1024, usage: 0 }),
        persist: async () => true,
      },
    });

    await save(
      { ...meta, written: 0 },
      async (write: (chunk: Bytes) => Promise<void>) => {
        await write(new Uint8Array(16) as Bytes);
      },
    );

    const dir = root.directories.get('offline');
    const written = [...dir!.files.keys()].filter((name) => name !== 'index.json');

    expect(written).toHaveLength(1);
    expect(boot().download.stagedNameFor(meta.key)).toBe(`${written[0]}.raw`);
  });

  /**
   * And the other end of the same agreement: the staging name is not the name a player opens.
   *
   * <p>This is the safety property of the whole file. What a background fetch downloads is whatever
   * the URL served, which for a locked film is ciphertext, and this worker has no key and cannot
   * tell. Writing it under the playable name would give the reader a two-hour film of noise and the
   * conclusion that the file is broken.</p>
   */
  it('never stages under the name a player opens', async () => {
    const harness = boot();

    expect(harness.download.stagedNameFor(meta.key))
      .not.toBe(harness.download.stagedNameFor(meta.key).replace(/\.raw$/, ''));
  });
});

describe('the registration id, which is where the film’s details travel', () => {
  it('carries the details there and back', () => {
    const { download } = boot();

    expect(download.metaFrom(download.idFor(meta))).toEqual(meta);
  });

  /**
   * A key is a URL full of slashes and a name may be Persian. An id that did not survive either
   * would fail on the files this product is actually for.
   */
  it('survives a key full of slashes and a name that is not ASCII', () => {
    const { download } = boot();
    const awkward = { ...meta, key: '/files/9f2c-4a/content?v=2', name: 'فیلم کوتاه.webm' };

    expect(download.metaFrom(download.idFor(awkward))).toEqual(awkward);
  });

  /**
   * <b>Everything that is not ours is left entirely alone.</b> This origin may one day start a
   * background fetch for something else, and swallowing its completion event presents as that
   * feature never finishing — with nothing anywhere to say which file ate it.
   */
  it.each([
    ['a fetch belonging to something else', 'some-other-feature'],
    ['our prefix over rubbish', 'du1-save:not-json'],
    ['our prefix over an empty payload', 'du1-save:'],
    ['our prefix over a payload missing a field', `du1-save:${encodeURIComponent('{"key":"/d/x/file"}')}`],
    ['our prefix over a name that is not a string', `du1-save:${encodeURIComponent(JSON.stringify({ ...meta, name: 42 }))}`],
    ['our prefix over a size that is not finite', `du1-save:${encodeURIComponent(JSON.stringify({ ...meta, bytes: null }))}`],
  ])('reads nothing out of %s', (_why, id) => {
    expect(boot().download.metaFrom(id)).toBeNull();
  });

  it('claims only ids carrying its own prefix', () => {
    const { download } = boot();

    expect(download.claims(download.idFor(meta))).toBe(true);
    expect(download.claims('some-other-feature')).toBe(false);
    expect(download.claims(undefined)).toBe(false);
  });
});

// ── the one moment the browser wakes it ───────────────────────────────────────────────────────────

describe('a download that finished while nothing was watching', () => {
  it('writes the bytes it was given, under the staging name', async () => {
    const harness = boot();
    const bytes = new Uint8Array(64).fill(7);

    await harness.fire('backgroundfetchsuccess', registrationOf(harness.download.idFor(meta), [{ body: bytes }]));

    const staged = await harness.download.stagedFile(meta.key);

    expect(staged).not.toBeNull();
    expect(staged?.size).toBe(64);
    expect(new Uint8Array(staged!.bytes)).toEqual(bytes);
  });

  /**
   * <b>The record, in library.ts's own shape and no other.</b>
   *
   * <p><c>partial</c> because none of this is playable. <c>staged</c> because <c>partial</c> alone
   * would be the useful kind of lie — an ordinary unfinished save has plaintext at the playable name
   * and resumes by asking the server for the rest; this has nothing there and resumes by decrypting
   * something else entirely, and a reader that cannot tell them apart seeks the first and finds
   * nothing. <c>written: 0</c> because that field counts plaintext bytes at the playable name and
   * there are exactly none — recording the size of the staged file would show a full progress bar
   * for a film nobody can watch.</p>
   */
  it('records it as staged, unfinished, and nothing written', async () => {
    const harness = boot();

    await harness.fire('backgroundfetchsuccess', registrationOf(harness.download.idFor(meta)));

    expect(await harness.manifest()).toEqual([{
      key: meta.key,
      name: meta.name,
      type: meta.type,
      bytes: meta.bytes,
      savedAt: meta.savedAt,
      watchUrl: meta.watchUrl,
      partial: true,
      staged: true,
      written: 0,
    }]);
  });

  /**
   * The manifest is one file written by two implementations, so what one writes the other has to be
   * able to read: a JSON array at <c>offline/index.json</c>, of objects carrying every field
   * <c>SavedFile</c> declares as required.
   */
  it('writes a manifest of the shape library.ts reads', async () => {
    const harness = boot();

    await harness.fire('backgroundfetchsuccess', registrationOf(harness.download.idFor(meta)));

    const text = await new FakeFile(
      harness.offline()!.files.get('index.json')!.bytes,
      'index.json',
    ).text();

    const parsed: unknown = JSON.parse(text);

    expect(Array.isArray(parsed)).toBe(true);

    const [entry] = parsed as SavedFile[];

    expect(typeof entry.key).toBe('string');
    expect(typeof entry.name).toBe('string');
    expect(typeof entry.type).toBe('string');
    expect(typeof entry.bytes).toBe('number');
    expect(typeof entry.savedAt).toBe('number');
    expect(typeof entry.watchUrl).toBe('string');
    expect(typeof entry.written).toBe('number');
  });

  it('offers what is staged to whichever page comes back', async () => {
    const harness = boot();

    await harness.fire('backgroundfetchsuccess', registrationOf(harness.download.idFor(meta)));

    const staged = await harness.download.staged();

    expect(staged.map((entry) => entry.key)).toEqual([meta.key]);
    expect(staged[0].name).toBe(meta.name);
  });

  /** Somebody who saves the same film twice has one copy, not two, and one row rather than two. */
  it('replaces its own earlier staging rather than accumulating one per attempt', async () => {
    const harness = boot();
    const id = harness.download.idFor(meta);

    await harness.fire('backgroundfetchsuccess', registrationOf(id, [{ body: new Uint8Array(32).fill(1) }]));
    await harness.fire('backgroundfetchsuccess', registrationOf(id, [{ body: new Uint8Array(64).fill(2) }]));

    expect(await harness.manifest()).toHaveLength(1);
    expect((await harness.download.stagedFile(meta.key))?.size).toBe(64);

    // And no tail of the first attempt hanging off the end of the second, which is what keeping the
    // existing data would have left: a file that is the right length nowhere.
    expect(new Uint8Array((await harness.download.stagedFile(meta.key))!.bytes).every((b) => b === 2))
      .toBe(true);
  });

  /** Several requests under one registration, and the film is the one whose address is the key. */
  it('takes the record whose address is the key rather than the first one', async () => {
    const harness = boot();

    await harness.fire('backgroundfetchsuccess', registrationOf(harness.download.idFor(meta), [
      { url: `${Origin}/d/film2026/poster`, body: new Uint8Array(8).fill(9) },
      { url: `${Origin}${meta.key}`, body: new Uint8Array(64).fill(7) },
    ]));

    expect((await harness.download.stagedFile(meta.key))?.size).toBe(64);
  });
});

// ── every ending that is not a success ────────────────────────────────────────────────────────────

describe('a download that did not finish', () => {
  it.each(['backgroundfetchfail', 'backgroundfetchabort'])('leaves nothing behind after %s', async (type) => {
    const harness = boot();

    await harness.fire(type, registrationOf(harness.download.idFor(meta)));

    expect(await harness.download.stagedFile(meta.key)).toBeNull();
    expect(await harness.manifest()).toEqual([]);
    expect(await harness.download.staged()).toEqual([]);
  });

  /**
   * A film staged a week ago, never finished by the reader, and started again. The second attempt
   * failing has to take the first attempt's bytes with it — they are stale by then, and nothing
   * would ever look at them again, so nothing would ever free them.
   */
  it.each(['backgroundfetchfail', 'backgroundfetchabort'])('clears an earlier staging on %s', async (type) => {
    const harness = boot();
    const id = harness.download.idFor(meta);

    await harness.fire('backgroundfetchsuccess', registrationOf(id));
    expect(await harness.download.stagedFile(meta.key)).not.toBeNull();

    await harness.fire(type, registrationOf(id));

    expect(await harness.download.stagedFile(meta.key)).toBeNull();
    expect(await harness.manifest()).toEqual([]);
  });

  /**
   * <b>The sw-media.js lesson, on the disk this time.</b>
   *
   * <p><c>/files/{id}/content</c> is cookie-authenticated and answers an unauthenticated request
   * with a 302 to the sign-in page; fetch follows redirects; that page is a 200 with a body. The
   * media worker fed one into AES-GCM as segment zero and the reader got a player that never
   * produced a frame. Here it would be written down: a background download running while a session
   * expires would stage a page of HTML under a film's name and report success.</p>
   */
  it('writes nothing when the download succeeded at fetching the sign-in page', async () => {
    const harness = boot();

    await harness.fire('backgroundfetchsuccess', registrationOf(harness.download.idFor(meta), [
      { body: new TextEncoder().encode('<!doctype html><title>Sign in</title>'), redirected: true },
    ]));

    expect(await harness.download.stagedFile(meta.key)).toBeNull();
    expect(await harness.manifest()).toEqual([]);
  });

  it('writes nothing for a response it will not vouch for', async () => {
    const harness = boot();

    await harness.fire('backgroundfetchsuccess', registrationOf(harness.download.idFor(meta), [
      { body: new Uint8Array(64), ok: false },
    ]));

    expect(await harness.download.stagedFile(meta.key)).toBeNull();
    expect(await harness.manifest()).toEqual([]);
  });

  /**
   * A copy that runs out of room part-way. Unlike library.ts — where what was written is kept,
   * because it is plaintext somebody waited hours for and can be resumed into — half a staged file
   * is worth nothing at all: it cannot be resumed, because the download it came from is finished and
   * gone, and it cannot be decrypted, because it stops inside a segment.
   */
  it('cleans up after itself when the copy fails part-way', async () => {
    writeThrowsAfter = 0;

    const harness = boot();

    await harness.fire('backgroundfetchsuccess', registrationOf(harness.download.idFor(meta)));

    expect(harness.offline()?.files.has(harness.download.stagedNameFor(meta.key))).toBe(false);
    expect(await harness.manifest()).toEqual([]);
  });

  /**
   * <b>A film somebody already has is never overwritten by one nobody can play.</b>
   *
   * <p>An entry for this key that is not staged is library.ts's — a finished film, or an unfinished
   * one carrying a checkpoint somebody waited hours for. Replacing it with a staged record orphans
   * its bytes, because the next <c>list()</c> sweeps names nothing references, so the price of
   * overwriting is the film itself. The background download is dropped instead, which is a wasted
   * download rather than a lost one.</p>
   */
  it('refuses to replace a real save with a staged one', async () => {
    const harness = boot();
    const dir = await root.getDirectoryHandle('offline', { create: true });

    const finished: SavedFile = { ...meta, written: meta.bytes };
    const handle = await dir.getFileHandle('index.json', { create: true });
    handle.bytes = new TextEncoder().encode(JSON.stringify([finished]));

    await harness.fire('backgroundfetchsuccess', registrationOf(harness.download.idFor(meta)));

    expect(await harness.manifest()).toEqual([finished]);
    expect(await harness.download.stagedFile(meta.key)).toBeNull();
  });

  /**
   * And a registration this worker cannot read is one it has no business deleting bytes over. Null
   * metadata means «leave everything alone», never «clean up».
   */
  it('touches nothing at all for a fetch that is not ours', async () => {
    const harness = boot();

    await harness.fire('backgroundfetchsuccess', registrationOf('some-other-feature'));
    await harness.fire('backgroundfetchfail', registrationOf('some-other-feature'));
    await harness.fire('backgroundfetchabort', registrationOf('some-other-feature'));

    expect(harness.offline()).toBeUndefined();
  });
});

describe('discarding one by hand, which is what the page does once it has finished the job', () => {
  it('removes the bytes and the record together', async () => {
    const harness = boot();

    await harness.fire('backgroundfetchsuccess', registrationOf(harness.download.idFor(meta)));
    await harness.download.discard(meta.key);

    expect(await harness.download.stagedFile(meta.key)).toBeNull();
    expect(await harness.manifest()).toEqual([]);
  });

  /** The same rule as everywhere else in this file: a real save is not this worker's to delete. */
  it('leaves a real save for the same key where it is', async () => {
    const harness = boot();
    const dir = await root.getDirectoryHandle('offline', { create: true });

    const finished: SavedFile = { ...meta, written: meta.bytes };
    const handle = await dir.getFileHandle('index.json', { create: true });
    handle.bytes = new TextEncoder().encode(JSON.stringify([finished]));

    await harness.download.discard(meta.key);

    expect(await harness.manifest()).toEqual([finished]);
  });

  it('does not fall over discarding something that was never staged', async () => {
    await expect(boot().download.discard('/d/never/file')).resolves.toBeUndefined();
  });
});
