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

  constructor(readonly name: string) {}

  getFile() {
    return Promise.resolve(new FakeFile(this.bytes, this.name));
  }

  createWritable() {
    const chunks: Uint8Array[] = [];

    return Promise.resolve({
      // A real FileSystemWritableFileStream takes a string as readily as it takes bytes, and the
      // manifest is written as one. A fake that only accepted bytes wrote a run of zeros instead,
      // which parsed as nothing and made every list come back empty.
      write: async (chunk: Uint8Array | string) => {
        if (writeThrowsAfter !== null && chunks.length >= writeThrowsAfter) {
          throw new Error('quota');
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
});

/** Writes the manifest the way the module does, for the tests that stage a half-finished save. */
async function writeManifest(dir: FakeDirectory, entries: unknown[]) {
  const handle = await dir.getFileHandle('index.json', { create: true });
  const writable = await handle.createWritable();

  await writable.write(JSON.stringify(entries));
  await writable.close();
}

/** Fills the writer with `chunks` megabyte-ish blocks. */
const producing = (chunks: number) => async (write: (c: Bytes) => Promise<void>) => {
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
   * <b>A half-written film is not a shorter film.</b> It is a file the player would open, start and
   * stop in the middle of, having said nothing was wrong.
   */
  it('throws away what it wrote when the write fails part-way', async () => {
    writeThrowsAfter = 2;

    await expect(save(entryFor(64), producing(4))).rejects.toThrow();

    expect(await open('/d/film2026/file')).toBeNull();
    expect(await list()).toEqual([]);
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

  /** Stopping it is the other half of showing progress: a number with no exit is just a number. */
  it('can be stopped part-way, and leaves nothing behind', async () => {
    const controller = new AbortController();

    const produce = async (write: (c: Bytes) => Promise<void>) => {
      await write(new Uint8Array(16) as Bytes);
      controller.abort();
      await write(new Uint8Array(16) as Bytes);
    };

    await expect(save(entryFor(64), produce, { signal: controller.signal })).rejects.toThrow();

    expect(await open('/d/film2026/file')).toBeNull();
    expect(await list()).toEqual([]);
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
   * <b>The tab closed mid-save.</b> Nothing runs — no catch, no finally — so the bytes are left on
   * the disk and the only thing that can find them later is a record written <i>before</i> they were
   * started. Without one they are gigabytes nobody can see, account for, or remove.
   */
  it('clears away a save the browser was closed in the middle of', async () => {
    // A save that stops the way a closed tab stops it: the writer never throws and nothing after it
    // runs, so what is left is a partial file and the record written before it began.
    writeThrowsAfter = 2;
    await save(entryFor(64), producing(4)).catch(() => {});

    // Put the partial file back, which is the state a killed tab leaves and a thrown write does not.
    const dir = await root.getDirectoryHandle('offline', { create: true });
    const handle = await dir.getFileHandle('_2Fd_2Ffilm2026_2Ffile.bin', { create: true });
    handle.bytes = new Uint8Array(32);
    await writeManifest(dir, [{ ...entryFor(64), partial: true }]);

    // The next time anything asks, it is gone — from the list and from the disk.
    expect(await list()).toEqual([]);
    expect(dir.files.has('_2Fd_2Ffilm2026_2Ffile.bin')).toBe(false);
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
