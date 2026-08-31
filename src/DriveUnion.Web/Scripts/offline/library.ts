/**
 * Films kept on the device, so watching one is not a download that has to keep up.
 *
 * <p><b>The problem this solves is buffering, and buffering is a network problem.</b> A locked film
 * streams as ciphertext pulled a segment at a time from Drive through our server, and every seek is a
 * fresh upstream request across two hops. That is fine for a clip and it stalls on a two-hour film on
 * a domestic connection. A copy on disk has no hops at all: the element gets a blob URL and seeks
 * natively, at the speed of the disk.</p>
 *
 * <p><b>OPFS and not the Cache API or IndexedDB.</b> What comes back out of here has to be a
 * <c>File</c>, because a blob URL over a File is the one arrangement where the browser answers the
 * media element's range requests itself — no service worker, no hand-written 206s, no code of ours
 * between the seek and the bytes. OPFS is also the only one of the three with a streaming writer, so
 * a 4 GB film is written a segment at a time instead of being assembled in memory first.</p>
 *
 * <p><b>What is stored is plaintext</b>, decided by the owner of this product rather than here. It is
 * worth being plain about the consequence, because a sentence on the watch page used to promise the
 * opposite: a film saved for offline is readable on that device by anything that can reach the
 * browser's storage, and it no longer asks for the passphrase. See UiText.Offline.</p>
 */

import type { Bytes } from '../crypto/format';

/** Everything lives under one directory, so «clear» is one call and an orphan is visible. */
const Directory = 'offline';

/** The manifest, beside the files it describes. */
const IndexFile = 'index.json';

/**
 * Headroom left free after a save.
 *
 * <p>Filling a quota exactly does not fail cleanly: the write that crosses the line throws part-way
 * through and leaves a half-written film behind, and every other thing the origin wants to store —
 * the shell cache, the manifest below — starts failing at the same moment. 64 MiB is enough for the
 * rest of this app to keep working while a film sits on the disk.</p>
 */
const Headroom = 64 * 1024 * 1024;

export interface SavedFile {
  /** The content address it was fetched from, which is what makes it unique on this origin. */
  readonly key: string;
  readonly name: string;
  readonly type: string;
  readonly bytes: number;

  /** Epoch milliseconds. Written by the caller so this module holds no clock. */
  readonly savedAt: number;

  /** Where to watch it, for a list that has to link somewhere. */
  readonly watchUrl: string;
}

export interface Room {
  /** What the browser says this origin may use. Zero when it will not say. */
  readonly quota: number;
  readonly usage: number;
  readonly free: number;
}

export type SaveRefusal =
  /** The device does not have room, and `Room` says by how much. */
  | 'no-room'
  /** No OPFS: an old browser, or a private window in some of them. */
  | 'unsupported';

export type SaveResult =
  | { readonly ok: true; readonly saved: SavedFile }
  | { readonly ok: false; readonly reason: SaveRefusal; readonly room: Room };

/** Whether this browser can keep anything at all. Checked before a control is offered. */
export function supported(): boolean {
  return typeof navigator !== 'undefined'
    && navigator.storage !== undefined
    && typeof navigator.storage.getDirectory === 'function';
}

/**
 * What the browser will admit to.
 *
 * <p>Both figures are estimates and the quota is a soft one — Safari in particular reports a number
 * it will grant and then evicts anyway. It is still the only figure there is, and refusing against it
 * is better than starting a 6 GB download that dies at 900 MB having spent the traffic.</p>
 */
export async function room(): Promise<Room> {
  if (!supported() || typeof navigator.storage.estimate !== 'function') {
    return { quota: 0, usage: 0, free: 0 };
  }

  const estimate = await navigator.storage.estimate();
  const quota = estimate.quota ?? 0;
  const usage = estimate.usage ?? 0;

  return { quota, usage, free: Math.max(0, quota - usage) };
}

/** Whether a file of this size would fit, with the headroom above left over. */
export async function fits(bytes: number): Promise<boolean> {
  const { free } = await room();

  return free > 0 && bytes + Headroom <= free;
}

async function directory(): Promise<FileSystemDirectoryHandle> {
  const root = await navigator.storage.getDirectory();

  return root.getDirectoryHandle(Directory, { create: true });
}

/**
 * A key turned into something that can be a filename.
 *
 * <p>The key is a URL — `/files/{id}/content`, `/d/{slug}/file` — because that is the one string that
 * is already unique per file per origin and is already on the page. OPFS will not take a slash, and
 * a name that collided would be one film playing under another's title.</p>
 */
function fileNameFor(key: string): string {
  return `${encodeURIComponent(key).replace(/[^A-Za-z0-9._-]/g, '_')}.bin`;
}

async function readIndex(dir: FileSystemDirectoryHandle): Promise<SavedFile[]> {
  try {
    const handle = await dir.getFileHandle(IndexFile);
    const text = await (await handle.getFile()).text();
    const parsed: unknown = JSON.parse(text);

    return Array.isArray(parsed) ? (parsed as SavedFile[]) : [];
  } catch {
    // Absent on a first run, and unreadable if a write was interrupted. Both mean «nothing recorded»,
    // and `list` reconciles against the directory anyway.
    return [];
  }
}

async function writeIndex(dir: FileSystemDirectoryHandle, entries: SavedFile[]): Promise<void> {
  const handle = await dir.getFileHandle(IndexFile, { create: true });
  const writable = await handle.createWritable();

  await writable.write(JSON.stringify(entries));
  await writable.close();
}

/**
 * What is on the device, reconciled against what is actually there.
 *
 * <p>The manifest and the files can disagree — a browser may evict one file without telling anybody,
 * and a save interrupted between the bytes and the manifest leaves the other kind of orphan. An entry
 * with no file is dropped, because a list that offers to play something that is gone is worse than a
 * short list.</p>
 */
export async function list(): Promise<SavedFile[]> {
  if (!supported()) return [];

  try {
    const dir = await directory();
    const recorded = await readIndex(dir);
    const alive: SavedFile[] = [];

    for (const entry of recorded) {
      try {
        await dir.getFileHandle(fileNameFor(entry.key));
        alive.push(entry);
      } catch {
        // Evicted, or never finished. Either way there is nothing to play.
      }
    }

    if (alive.length !== recorded.length) await writeIndex(dir, alive);

    return alive;
  } catch {
    return [];
  }
}

/** The saved copy, or null. This is what the player asks before it reaches for the network. */
export async function open(key: string): Promise<File | null> {
  if (!supported()) return null;

  try {
    const dir = await directory();
    const handle = await dir.getFileHandle(fileNameFor(key));

    return await handle.getFile();
  } catch {
    return null;
  }
}

/**
 * Writes one file, streaming, and records it.
 *
 * <p><c>produce</c> is given a writer and is expected to fill it — which is how the two cases share
 * this: an unlocked file pipes a response body straight in, and a locked one runs
 * <c>decryptInto</c> against the same writer so the plaintext is written a verified segment at a time
 * and never assembled in memory.</p>
 *
 * <p>The room check is before anything is fetched. Starting a download that cannot land is the worst
 * of the three possible behaviours: it spends the workspace's traffic, takes the reader's time, and
 * ends in a failure that looks like ours.</p>
 *
 * <p>A failure part-way through removes the partial file. A half-written film is not a shorter film;
 * it is a file the player would open, start, and stop in the middle of.</p>
 */
export async function save(
  entry: SavedFile,
  produce: (write: (chunk: Bytes) => Promise<void>) => Promise<void>,
): Promise<SaveResult> {
  const space = await room();

  if (!supported()) return { ok: false, reason: 'unsupported', room: space };
  if (!(await fits(entry.bytes))) return { ok: false, reason: 'no-room', room: space };

  // Asked for once a save is actually happening. Without it the browser treats this origin's storage
  // as expendable and clears it under pressure — which for a film somebody saved to watch on a plane
  // is the one moment it must not do that.
  try {
    if (typeof navigator.storage.persist === 'function') await navigator.storage.persist();
  } catch {
    // Refused, or not implemented. The copy is still worth having; it is only less durable.
  }

  const dir = await directory();
  const handle = await dir.getFileHandle(fileNameFor(entry.key), { create: true });
  const writable = await handle.createWritable();

  try {
    await produce(async (chunk) => {
      await writable.write(chunk);
    });

    await writable.close();
  } catch (error) {
    // close() on an aborted writable throws in some browsers; the removal below is what matters.
    try {
      await writable.close();
    } catch {
      // Already closed, or never opened far enough to close.
    }

    await remove(entry.key);
    throw error;
  }

  const dirAgain = await directory();
  const entries = (await readIndex(dirAgain)).filter((e) => e.key !== entry.key);

  entries.push(entry);
  await writeIndex(dirAgain, entries);

  return { ok: true, saved: entry };
}

/** Removes one copy and its manifest entry. Silent about a key that was never saved. */
export async function remove(key: string): Promise<void> {
  if (!supported()) return;

  try {
    const dir = await directory();

    try {
      await dir.removeEntry(fileNameFor(key));
    } catch {
      // Not there. The manifest is still worth correcting.
    }

    await writeIndex(dir, (await readIndex(dir)).filter((e) => e.key !== key));
  } catch {
    // No OPFS at all, which `supported` should have caught. Nothing to undo.
  }
}

/** Everything, which is «empty the local storage» and is one press on the offline screen. */
export async function clear(): Promise<void> {
  if (!supported()) return;

  try {
    const root = await navigator.storage.getDirectory();

    await root.removeEntry(Directory, { recursive: true });
  } catch {
    // Nothing kept yet, or no OPFS.
  }
}
