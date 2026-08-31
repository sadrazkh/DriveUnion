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

/**
 * `keys()` is in the File System Access specification and in every browser that has OPFS at all; it
 * is simply absent from the DOM types this project builds against. Declared rather than cast at the
 * one call site, so the orphan sweep below reads as the ordinary iteration it is.
 */
declare global {
  interface FileSystemDirectoryHandle {
    keys(): AsyncIterableIterator<string>;
  }
}

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

  /**
   * True between «the bytes have started» and «the bytes are all there».
   *
   * <p><b>Written before the first byte, on purpose.</b> A save that is interrupted by the tab
   * closing runs no catch and no finally — nothing of ours executes at all — so the only thing that
   * can identify the megabytes left on the disk afterwards is a record made before they were
   * written. Without one they are storage nobody can see, account for or remove.</p>
   *
   * <p>An entry carrying this is <b>kept and listed</b>, not swept. It used to be thrown away on the
   * argument that a film 40% there is worse than nothing — which is true of playing it and false of
   * keeping it. 40% of a six-gigabyte film is two and a half gigabytes somebody has already waited
   * for, and deleting it because they took a call is the expensive answer. It can be carried on, or
   * removed, by the person who paid for it.</p>
   */
  readonly partial?: boolean;

  /**
   * Plaintext bytes actually on the disk.
   *
   * <p>Equal to <see cref="bytes"/> once finished. While unfinished it is where a resume starts, and
   * it is <b>the last checkpoint rather than the last write</b>: OPFS publishes nothing until the
   * writable is closed, so anything written since the last close is not there to be resumed from.
   * Measured — a file mid-write reports zero bytes and its real length the moment it closes.</p>
   */
  readonly written: number;

  /**
   * True when the save stopped because somebody pressed Stop.
   *
   * <p><b>Not the same thing as unfinished, and the difference is the whole of whether it may be
   * picked back up on its own.</b> A save the network or the phone ended is one to carry on with
   * when the app comes back; a save a person ended is one they meant to end. Resuming that on their
   * mobile data would be, in the upload queue's words about the same distinction, the worst bug in
   * the product.</p>
   *
   * <p>Cleared by the next <c>save</c>, because pressing Continue is asking for it again.</p>
   */
  readonly stoppedByHand?: boolean;

  /**
   * True while the bytes on disk are what the server sent, not what a player can open.
   *
   * <p>Background Fetch keeps a download running after the tab is gone, and hands the worker the raw
   * response. For a locked film that is ciphertext, and the worker has no key — the key is derived in
   * a page from a typed passphrase, and the point of the feature is that no page was open. So the
   * worker writes to <c>&lt;name&gt;.raw</c> and marks this, and a page finishes the job when
   * somebody comes back and unlocks it.</p>
   *
   * <p><b>Everything that walks this directory has to know about it.</b> The sweep below removes
   * files no record accounts for, and a staged entry's file is under a different name — so without
   * this flag it dropped the record <i>and</i> deleted the download, on the next page load, silently.
   * That is the whole reason the flag is here rather than only in the worker.</p>
   */
  readonly staged?: boolean;
}

/** What a caller wants to know and be able to do while a save is running. */
export interface SaveOptions {
  /**
   * Total plaintext bytes on the disk, cumulative and counting what a resume started from — a bar
   * that restarted at zero on the second attempt would be reporting the work rather than the film.
   */
  readonly onProgress?: (written: number) => void;

  /** Stops it. What was written stays, as an unfinished save; see `save`. */
  readonly signal?: AbortSignal;

  /**
   * How much may be written before the file is closed, recorded and reopened.
   *
   * <p>This is the whole of what makes a resume possible across a crash. A writable holds its bytes
   * in a swap file and publishes them only on close, so without a checkpoint an interrupted save is
   * worth nothing however far it got. Thirty-two mebibytes is about a second of writing and a
   * worst-case loss of thirty-two megabytes of somebody's traffic.</p>
   */
  readonly checkpointEvery?: number;
}

/** See SaveOptions.checkpointEvery. */
const CheckpointEvery = 32 * 1024 * 1024;

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

/**
 * Where an entry's bytes actually are, which is not the same question as what its key is.
 *
 * <p>A staged download is under a `.raw` suffix — see SavedFile.staged. Every pass that touches the
 * directory goes through here, because the one that did not deleted the downloads.</p>
 */
function nameOnDisk(entry: SavedFile): string {
  return entry.staged === true ? `${fileNameFor(entry.key)}.raw` : fileNameFor(entry.key);
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
        // Unfinished ones are kept and listed, so somebody can carry one on or remove it. What is
        // not kept is one whose bytes have gone: an entry with no file is a row that offers to play
        // or resume something that is not there.
        await dir.getFileHandle(nameOnDisk(entry));
        alive.push(entry);
      } catch {
        // Evicted by the browser, or interrupted before a single checkpoint.
      }
    }

    if (alive.length !== recorded.length) await writeIndex(dir, alive);

    // The other orphan: bytes with no record at all, left by a save interrupted before its manifest
    // write. Nothing would ever look at them again, so nothing would ever free them.
    const known = new Set([IndexFile, ...alive.map(nameOnDisk)]);

    for await (const name of dir.keys()) {
      if (!known.has(name)) {
        try {
          await dir.removeEntry(name);
        } catch {
          // Held open, or already gone. It will be swept on the next pass.
        }
      }
    }

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
 * <p><b>It resumes.</b> An unfinished record for the same key means the bytes up to its checkpoint
 * are already on the disk, so <c>produce</c> is told where to start and asks the server only for the
 * rest. On a six-gigabyte film interrupted at four, that is two gigabytes of traffic rather than
 * eight.</p>
 *
 * <p>A failure part-way through leaves what was checkpointed, as an unfinished save. It used to
 * delete it; keeping it is what makes the paragraph above worth anything.</p>
 */
export async function save(
  entry: SavedFile,
  produce: (write: (chunk: Bytes) => Promise<void>, from: number) => Promise<void>,
  options: SaveOptions = {},
): Promise<SaveResult> {
  const space = await room();

  if (!supported()) return { ok: false, reason: 'unsupported', room: space };

  // What is already on the disk does not need room found for it again.
  const existing = (await list()).find((e) => e.key === entry.key && e.partial === true);
  const resumeFrom = existing?.written ?? 0;

  if (!(await fits(entry.bytes - resumeFrom))) {
    return { ok: false, reason: 'no-room', room: space };
  }

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
  const every = options.checkpointEvery ?? CheckpointEvery;

  let written = resumeFrom;

  /** Records how far the disk has actually got. Only ever called just after a close. */
  const mark = async (done: boolean, byHand = false) => {
    const at = await directory();

    await writeIndex(at, [
      ...(await readIndex(at)).filter((e) => e.key !== entry.key),
      done
        ? { ...entry, written }
        : { ...entry, written, partial: true, ...(byHand ? { stoppedByHand: true } : {}) },
    ]);
  };

  // The record goes down before the first byte. This is the only thing that can identify what is on
  // the disk if the tab is closed mid-write — see SavedFile.partial.
  await mark(false);

  // keepExistingData only when there is existing data to keep: on a fresh save it would leave the
  // tail of whatever was there before if a previous file of the same name had been longer.
  let writable = await handle.createWritable({ keepExistingData: resumeFrom > 0 });

  if (resumeFrom > 0) await writable.seek(resumeFrom);

  let sinceCheckpoint = 0;

  try {
    await produce(async (chunk) => {
      // Checked per chunk rather than only at the top: a stop pressed during a four-gigabyte film
      // has to take effect within a segment, not at the end of one that is still hours away.
      if (options.signal?.aborted) throw new DOMException('stopped', 'AbortError');

      await writable.write(chunk);

      written += chunk.length;
      sinceCheckpoint += chunk.length;
      options.onProgress?.(written);

      // Close, record, reopen. A writable holds its bytes in a swap file and publishes them only on
      // close, so this is the only thing that makes an interrupted save worth anything at all.
      if (sinceCheckpoint >= every) {
        await writable.close();
        await mark(false);

        writable = await handle.createWritable({ keepExistingData: true });
        await writable.seek(written);
        sinceCheckpoint = 0;
      }
    }, resumeFrom);

    await writable.close();
  } catch (error) {
    // close() publishes what this run wrote, which is what makes it resumable. On an aborted
    // writable it throws in some browsers, and then the last checkpoint is what survives — which is
    // the same guarantee, one checkpoint further back.
    try {
      await writable.close();
    } catch {
      written -= sinceCheckpoint;
    }

    // Whether a person stopped this is recorded here and nowhere else, because here is the only
    // place that knows: the signal is the caller's own, and everything downstream sees an identical
    // half-written file either way.
    await mark(false, options.signal?.aborted === true);
    throw error;
  }

  await mark(true);

  return { ok: true, saved: { ...entry, written } };
}

/** Removes one copy and its manifest entry. Silent about a key that was never saved. */
export async function remove(key: string): Promise<void> {
  if (!supported()) return;

  try {
    const dir = await directory();

    // Both names, because a staged download is under the `.raw` one and this is called without
    // knowing which kind it is. Removing the wrong one would leave the bytes for the sweep to find
    // later, which works and is a lot of storage to hold in the meantime.
    for (const name of [fileNameFor(key), `${fileNameFor(key)}.raw`]) {
      try {
        await dir.removeEntry(name);
      } catch {
        // Not there. The manifest is still worth correcting.
      }
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
