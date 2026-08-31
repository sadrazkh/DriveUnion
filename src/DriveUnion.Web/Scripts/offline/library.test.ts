import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Bytes } from '../crypto/format';
import { clear, fits, list, open, remove, room, save, supported, type SavedFile } from './library';

/**
 * Keeping a film on the device.
 *
 * <p>OPFS is faked here in the same spirit as the XHR in <c>store.test.ts</c>: what is worth pinning
 * down is the decisions — refusing before the download rather than after, removing a partial file,
 * reconciling a manifest against what is really there — and every one of those is made against a
 * handle API that a fake can provide exactly.</p>
 *
 * <p>The one thing this cannot show is that a real browser's OPFS behaves as its specification says.
 * What it can show is that this module does not, for instance, record a file it failed to write.</p>
 */

// ── OPFS, as much of it as this module touches ────────────────────────────────────────────────────

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

  /** Writes of film bytes seen by this handle, across every writable opened on it. */
  dataWrites = 0;

  constructor(readonly name: string) {}

  getFile() {
    return Promise.resolve(new FakeFile(this.bytes, this.name));
  }

  createWritable(options?: { keepExistingData?: boolean }) {
    // Seeded from what is on the file when the caller asked to keep it, so a resume that seeks and
    // appends produces one whole film rather than only its tail.
    const chunks: Uint8Array[] = options?.keepExistingData ? [this.bytes.slice()] : [];

    return Promise.resolve({
      seek: async (_at: number) => {
        // The module only ever seeks to the end of what it kept, which is where writes already land.
      },
      // A real FileSystemWritableFileStream takes a string as readily as it takes bytes, and the
      // manifest is written as one. A fake that only accepted bytes wrote a run of zeros instead,
      // which parsed as nothing and made every list come back empty.
      write: async (chunk: Uint8Array | string) => {
        // Counted on the handle rather than on this writable: a save now closes and reopens one per
        // checkpoint, and a per-writable counter would reset every time and never fail at all.
        // Only the film's own bytes count — the manifest is written through this same fake.
        if (this.name !== 'index.json') {
          if (writeThrowsAfter !== null && this.dataWrites >= writeThrowsAfter) {
            throw new Error('quota');
          }

          this.dataWrites++;
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
let quota: number;
let usage: number;
let persisted: number;

/** Set to make the writer fail after N chunks, which is what a quota being reached looks like. */
let writeThrowsAfter: number | null;

function define(name: string, value: unknown) {
  Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
}

const MiB = 1024 * 1024;

beforeEach(() => {
  root = new FakeDirectory();
  quota = 1024 * MiB;
  usage = 0;
  persisted = 0;
  writeThrowsAfter = null;

  define('DOMException', class extends Error {});
  define('navigator', {
    storage: {
      getDirectory: async () => root,
      estimate: async () => ({ quota, usage }),
      persist: async () => {
        persisted++;
        return true;
      },
    },
  });
});

const entryFor = (bytes: number): SavedFile => ({
  key: '/d/film2026/file',
  name: 'a-short-film.webm',
  type: 'video/webm',
  bytes,
  savedAt: 1_700_000_000_000,
  watchUrl: '/d/film2026/watch',
  written: 0,
});

/** Writes the manifest the way the module does, for the tests that stage a half-finished save. */
async function writeManifest(dir: FakeDirectory, entries: unknown[]) {
  const handle = await dir.getFileHandle('index.json', { create: true });
  const writable = await handle.createWritable();

  await writable.write(JSON.stringify(entries));
  await writable.close();
}

/** Fills the writer with `chunks` megabyte-ish blocks. */
const producing = (chunks: number) => async (write: (c: Bytes) => Promise<void>, _from = 0) => {
  for (let i = 0; i < chunks; i++) await write(new Uint8Array(16).fill(i) as Bytes);
};

describe('what the device will hold', () => {
  it('reports what is left after what is used', async () => {
    usage = 24 * MiB;

    expect(await room()).toEqual({ quota: 1024 * MiB, usage: 24 * MiB, free: 1000 * MiB });
  });

  /**
   * <b>The refusal that matters.</b> A 6.2 GB film on a phone does not fit, and the answer has to
   * arrive before the download rather than 900 MB into it — by which point the traffic is spent and
   * the failure looks like ours.
   */
  it('refuses a film larger than the room left, before anything is fetched', async () => {
    quota = 1024 * MiB;
    usage = 0;

    const produce = vi.fn();
    const result = await save(entryFor(6_200 * MiB), produce);

    expect(result.ok).toBe(false);
    expect(result.ok === false && result.reason).toBe('no-room');
    expect(produce).not.toHaveBeenCalled();

    // And the figures come back, so the screen can say how much is short rather than «no».
    expect(result.ok === false && result.room.free).toBe(1024 * MiB);
  });

  /**
   * Headroom. Filling a quota exactly leaves every other thing this origin stores failing at the
   * same moment, including the manifest that records what was just saved.
   */
  it('leaves room for the rest of the app rather than filling the quota exactly', async () => {
    quota = 100 * MiB;
    usage = 0;

    expect(await fits(99 * MiB)).toBe(false);
    expect(await fits(20 * MiB)).toBe(true);
  });

  it('says nothing fits when the browser will not say what the quota is', async () => {
    quota = 0;

    expect(await fits(1)).toBe(false);
  });
});

describe('keeping a film', () => {
  it('writes it, records it, and hands it back as a File', async () => {
    const result = await save(entryFor(64), producing(4));

    expect(result.ok).toBe(true);

    const saved = await open('/d/film2026/file');

    expect(saved).not.toBeNull();
    expect(saved?.size).toBe(64);

    expect((await list()).map((e) => e.name)).toEqual(['a-short-film.webm']);
  });

  /** Persistence is asked for, or the browser treats a film saved for a flight as expendable. */
  it('asks the browser not to evict what was just saved', async () => {
    await save(entryFor(64), producing(4));

    expect(persisted).toBe(1);
  });

  /**
   * <b>What a failure leaves is an unfinished save, not nothing.</b>
   *
   * <p>This used to delete everything it had written, and the argument was that a film 40% there is
   * worse than nothing. That is true of playing one and false of keeping one — 40% of a six-gigabyte
   * film is hours of somebody's connection — so what is left is recorded as unfinished and can be
   * carried on. Nothing plays it: the screens read <c>partial</c> and offer Continue rather than a
   * player.</p>
   */
  it('leaves what it wrote as an unfinished save when the write fails part-way', async () => {
    writeThrowsAfter = 2;

    await expect(save(entryFor(64), producing(4), { checkpointEvery: 16 })).rejects.toThrow();

    const left = await list();

    expect(left).toHaveLength(1);
    expect(left[0].partial).toBe(true);
    expect(left[0].written).toBe(32);
  });

  /**
   * <b>How far it has got.</b> A button that says nothing for the eleven minutes a 6 GB film takes
   * is a button somebody presses twice and then force-quits.
   */
  it('reports what it has written as it writes it', async () => {
    const seen: number[] = [];

    await save(entryFor(64), producing(4), { onProgress: (n) => seen.push(n) });

    // Cumulative, not per-chunk: what a bar needs is «this much of that much», and a caller that
    // had to add them up would be a second place the total could be got wrong.
    expect(seen).toEqual([16, 32, 48, 64]);
  });

  /**
   * Stopping it is the other half of showing progress: a number with no exit is just a number.
   *
   * <p>And stopping keeps what it had, for the reason above — somebody who stops a download at 80%
   * to get on a train has not asked for those four gigabytes to be thrown away.</p>
   */
  it('can be stopped part-way, and keeps what it had', async () => {
    const controller = new AbortController();

    const produce = async (write: (c: Bytes) => Promise<void>) => {
      await write(new Uint8Array(16) as Bytes);
      await write(new Uint8Array(16) as Bytes);
      controller.abort();
      await write(new Uint8Array(16) as Bytes);
    };

    await expect(
      save(entryFor(64), produce, { signal: controller.signal, checkpointEvery: 16 }),
    ).rejects.toThrow();

    const left = await list();

    expect(left).toHaveLength(1);
    expect(left[0].partial).toBe(true);
    expect(left[0].written).toBe(32);
  });

  /**
   * <b>The record goes down before the bytes.</b>
   *
   * <p>Asserted from inside <c>produce</c>, which is the only moment it can be: a tab closed during
   * a save runs no catch and no finally, so anything written afterwards is not written at all. This
   * is the half that the test below cannot see, because that one stages the manifest by hand — and
   * without this one, deleting the marker from <c>save</c> broke nothing.</p>
   */
  it('records the save as unfinished before it writes a single byte', async () => {
    let seenDuring: unknown[] = [];

    await save(entryFor(64), async (write) => {
      const dir = await root.getDirectoryHandle('offline');
      const text = await (await (await dir.getFileHandle('index.json')).getFile()).text();
      seenDuring = JSON.parse(text);

      await write(new Uint8Array(64) as Bytes);
    });

    expect(seenDuring).toHaveLength(1);
    expect((seenDuring[0] as SavedFile).partial).toBe(true);

    // And it is not still marked once the bytes are all there, or the next visit would sweep away a
    // film that finished perfectly well.
    expect((await list())[0].partial).toBeUndefined();
  });

  /**
   * <b>An unfinished save is kept, not swept.</b>
   *
   * <p>It used to be thrown away on the next visit, on the argument that a film 40% there is worse
   * than nothing. That is true of <i>playing</i> it and false of keeping it: 40% of a six-gigabyte
   * film is two and a half gigabytes somebody has already waited for, and deleting it silently
   * because they took a call is the expensive answer. It stays, it is listed as unfinished, and it
   * can be carried on or removed — but by the person, not by us.</p>
   */
  it('keeps an unfinished save and says how far it got', async () => {
    writeThrowsAfter = 2;
    await save(entryFor(64), producing(4), { checkpointEvery: 16 }).catch(() => {});

    const kept = await list();

    expect(kept).toHaveLength(1);
    expect(kept[0].partial).toBe(true);

    // Two chunks were written and checkpointed before the third threw.
    expect(kept[0].written).toBe(32);
    expect(kept[0].bytes).toBe(64);
  });

  /**
   * <b>Who stopped it, recorded.</b>
   *
   * <p>An unfinished save the network ended is picked back up when the app returns; one a person
   * ended is not, and nothing downstream can tell them apart by looking — both leave an identical
   * half-written file. This flag is the only difference, and it is written where the difference is
   * known.</p>
   */
  it('remembers whether a person stopped it or something else did', async () => {
    const controller = new AbortController();

    await expect(save(entryFor(64), async (write) => {
      await write(new Uint8Array(16) as Bytes);
      controller.abort();
      await write(new Uint8Array(16) as Bytes);
    }, { signal: controller.signal, checkpointEvery: 16 })).rejects.toThrow();

    expect((await list())[0].stoppedByHand).toBe(true);

    // The other way: a write that failed on its own is not somebody's decision.
    await clear();
    writeThrowsAfter = 1;

    await expect(save(entryFor(64), producing(4), { checkpointEvery: 16 })).rejects.toThrow();

    expect((await list())[0].stoppedByHand).toBeUndefined();
  });

  /** Pressing Continue is asking for it again, so the flag must not outlive that. */
  it('clears the stopped-by-hand mark when it is asked for again', async () => {
    const controller = new AbortController();

    await expect(save(entryFor(64), async (write) => {
      await write(new Uint8Array(16) as Bytes);
      controller.abort();
      await write(new Uint8Array(16) as Bytes);
    }, { signal: controller.signal, checkpointEvery: 16 })).rejects.toThrow();

    writeThrowsAfter = null;

    await save(entryFor(64), async (write, from) => {
      for (let at = from; at < 64; at += 16) await write(new Uint8Array(16) as Bytes);
    }, { checkpointEvery: 16 });

    expect((await list())[0].stoppedByHand).toBeUndefined();
  });

  /**
   * <b>Carrying one on.</b> The producer is told where to start, so the second attempt asks the
   * server for the part that is missing rather than the whole film again.
   */
  it('resumes from where it stopped rather than starting again', async () => {
    writeThrowsAfter = 2;
    await save(entryFor(64), producing(4), { checkpointEvery: 16 }).catch(() => {});

    writeThrowsAfter = null;
    const askedFor: number[] = [];

    const result = await save(
      entryFor(64),
      async (write, from) => {
        askedFor.push(from);
        // Only what is missing: the caller ranges its request from here.
        for (let at = from; at < 64; at += 16) await write(new Uint8Array(16) as Bytes);
      },
      { checkpointEvery: 16 },
    );

    expect(askedFor).toEqual([32]);
    expect(result.ok).toBe(true);

    const file = await open('/d/film2026/file');

    // The two halves make one whole film, not one and a bit.
    expect(file?.size).toBe(64);
    expect((await list())[0].partial).toBeUndefined();
  });

  /** Progress on a resumed save counts what is already there, or the bar would restart at zero. */
  it('counts what is already on the disk when it carries on', async () => {
    writeThrowsAfter = 2;
    await save(entryFor(64), producing(4), { checkpointEvery: 16 }).catch(() => {});

    writeThrowsAfter = null;
    const seen: number[] = [];

    await save(
      entryFor(64),
      async (write, from) => {
        for (let at = from; at < 64; at += 16) await write(new Uint8Array(16) as Bytes);
      },
      { checkpointEvery: 16, onProgress: (n) => seen.push(n) },
    );

    expect(seen).toEqual([48, 64]);
  });

  /**
   * <b>A download the service worker finished while nothing was watching.</b>
   *
   * <p>Background Fetch writes the raw bytes under <c>&lt;name&gt;.raw</c> and records the entry as
   * staged, because it cannot decrypt: the key is derived in a page from a typed passphrase and the
   * whole point is that no page was open. This pass has to leave both alone or the feature dies on
   * the next page load — which is exactly what it did, and only a test says so, because everything
   * looks fine until the customer comes back to a list with nothing in it.</p>
   */
  it('leaves a background download alone instead of sweeping it', async () => {
    const dir = await root.getDirectoryHandle('offline', { create: true });
    const raw = await dir.getFileHandle('_2Fd_2Ffilm2026_2Ffile.bin.raw', { create: true });
    raw.bytes = new Uint8Array(4096);

    await writeManifest(dir, [{ ...entryFor(4096), partial: true, staged: true, written: 0 }]);

    const listed = await list();

    expect(listed).toHaveLength(1);
    expect(listed[0].staged).toBe(true);

    // And the bytes are still there. They are the download; sweeping them is throwing away the
    // traffic the whole feature exists to have spent in the background.
    expect(dir.files.has('_2Fd_2Ffilm2026_2Ffile.bin.raw')).toBe(true);
  });

  /**
   * And the opposite orphan: bytes with no record at all, which is what a save interrupted before
   * its manifest write leaves. Nothing would ever look at them again.
   */
  it('sweeps away bytes that no record accounts for', async () => {
    const dir = await root.getDirectoryHandle('offline', { create: true });
    const stray = await dir.getFileHandle('_2Fd_2Fstray_2Ffile.bin', { create: true });
    stray.bytes = new Uint8Array(4096);

    await list();

    expect(dir.files.has('_2Fd_2Fstray_2Ffile.bin')).toBe(false);
  });

  it('replaces rather than duplicates when the same film is saved twice', async () => {
    await save(entryFor(64), producing(4));
    await save(entryFor(32), producing(2));

    expect(await list()).toHaveLength(1);
    expect((await list())[0].bytes).toBe(32);
  });
});

describe('emptying it again', () => {
  it('removes one and leaves the rest', async () => {
    await save(entryFor(64), producing(4));
    await save({ ...entryFor(64), key: '/d/other/file', name: 'other.webm' }, producing(4));

    await remove('/d/film2026/file');

    expect((await list()).map((e) => e.name)).toEqual(['other.webm']);
    expect(await open('/d/film2026/file')).toBeNull();
  });

  it('empties everything', async () => {
    await save(entryFor(64), producing(4));
    await save({ ...entryFor(64), key: '/d/other/file' }, producing(4));

    await clear();

    expect(await list()).toEqual([]);
  });

  it('does not fall over removing something that was never there', async () => {
    await expect(remove('/d/never/file')).resolves.toBeUndefined();
  });

  /**
   * The browser may evict one file without saying so, and a save interrupted between the bytes and
   * the manifest leaves the opposite kind of orphan. A list that offers to play something that is
   * gone is worse than a short list.
   */
  it('drops manifest entries whose file the browser has evicted', async () => {
    await save(entryFor(64), producing(4));

    const dir = await root.getDirectoryHandle('offline');
    for (const name of [...dir.files.keys()]) {
      if (name !== 'index.json') dir.files.delete(name);
    }

    expect(await list()).toEqual([]);
  });
});

describe('a browser without it', () => {
  it('says so rather than pretending, and every call is a no-op', async () => {
    define('navigator', {});

    expect(supported()).toBe(false);
    expect(await list()).toEqual([]);
    expect(await open('/d/film2026/file')).toBeNull();
    expect(await room()).toEqual({ quota: 0, usage: 0, free: 0 });

    const result = await save(entryFor(64), producing(1));

    expect(result.ok === false && result.reason).toBe('unsupported');
  });
});
