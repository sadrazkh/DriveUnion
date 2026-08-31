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
