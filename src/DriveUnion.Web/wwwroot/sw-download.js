/*
 * A save that keeps downloading after the tab is closed.
 *
 * ── What this is for ─────────────────────────────────────────────────────────────────────────────
 *
 * Scripts/offline/library.ts writes a film into OPFS while a page is open, and «while a page is
 * open» is the whole of the problem. Six gigabytes is eleven minutes on a good connection and a
 * great deal longer on a phone: the tab gets closed, the PWA is swiped away, the screen locks and
 * the operating system reclaims the process. A save interrupted that way runs no catch and no
 * finally — nothing of ours executes at all — and library.ts is built around that fact rather than
 * against it: it checkpoints every 32 MiB, records the save as unfinished before the first byte, and
 * resumes from the last checkpoint next time. What it cannot do is keep downloading, because there
 * is nothing left running to download with.
 *
 * Background Fetch is the only thing in a browser that can. The page hands the browser a request and
 * walks away; the browser downloads on its own schedule, draws its own progress notification, keeps
 * going with every page of this origin closed, and wakes this worker exactly once when it is
 * finished. That is the feature. It is also the whole of the feature — see the next section, which
 * is the part worth reading.
 *
 * ── What it cannot do, and why this file writes to a name nothing plays ──────────────────────────
 *
 * A locked file is `du1` ciphertext and the content key is derived in the page from a passphrase the
 * reader typed. It exists in the page and nowhere else — that is the product, and sw-media.js says
 * at length why the worker holding a non-extractable CryptoKey handed over by a live page is as far
 * as that ever goes. A background download has no page: there is no one to type a passphrase to and
 * nothing to derive from, and a key parked in IndexedDB so that a worker could find one later would
 * be this product's central claim with an exception written into it, and the exception would be the
 * interesting half.
 *
 * So the network half can happen in the background and the decryption half cannot, and this file is
 * only ever the network half. What lands on the disk is whatever the URL served — ciphertext for a
 * locked file, plaintext for an unlocked one — and this worker cannot tell which and must not guess.
 * It therefore writes to `<fileNameFor(key)>.raw`, which is deliberately not the name
 * library.ts's `open()` looks under, and records the entry as `staged: true` with `written: 0`.
 *
 * The alternative was writing those bytes straight to `<fileNameFor(key)>` and trusting every future
 * reader to check a flag first. That is the sw-media.js `credentials` bug in a new place: the
 * failure of the flag being missed is not a save that does not work, it is a player that opens a
 * file, believes it, and plays a two-hour film of noise — and the reader concludes the film is
 * broken. An unusable file under a name nothing opens is a worse afternoon for whoever finishes this
 * and a better one for everybody else.
 *
 * ── The seam this leaves for the page ────────────────────────────────────────────────────────────
 *
 * `self.du1Download` at the bottom is the whole of what the page side needs: the id encoding, so the
 * metadata travels with the fetch rather than in a variable in a tab that will be gone; the staging
 * name, spelled once; a way to read what is staged and to read its bytes; and a way to throw one
 * away. Finishing a staged save is a page's job and cannot be anything else — it is the only side
 * that can ask for a passphrase.
 *
 * ── Chromium only, and silently so ───────────────────────────────────────────────────────────────
 *
 * BackgroundFetchManager does not exist in Safari and there is no sign of it coming. Nothing here
 * needs a feature test: on a browser without it these three events are never fired, this file
 * registers three listeners nothing will ever call, and a save behaves exactly as it did before —
 * which is the required degradation and costs one dead object. The page side does need the test,
 * before it offers a control that would throw.
 *
 * ── What is deliberately not here ────────────────────────────────────────────────────────────────
 *
 * No `backgroundfetchclick`, which fires when somebody taps the browser's progress notification. It
 * would have to navigate somewhere, and every address this product opens from a notification goes
 * through the origin check in sw-push.js — a second, differently-spelled navigation path is exactly
 * the kind of thing that is right on the day it is written and wrong a year later. The browser's own
 * download UI already does something sensible when tapped.
 *
 * No `updateUI()`. It replaces the browser's notification title with a string, and a worker is
 * compiled once and has no culture: the string would be Persian shipped to an English reader or the
 * reverse. sw-push.js hit the same wall and answered it with the product's name; here there is no
 * need to answer it at all, because the title the browser writes for itself is already in the
 * reader's language.
 *
 * No `fetch` listener, for the reason written at length in sw.js: there is one, it lives there, and
 * two of them race with the winner decided by registration order.
 */

'use strict';

/*
 * Three facts restated from Scripts/offline/library.ts, because a classic worker script cannot
 * import a TypeScript module and making this a bundle is what sw.js refuses at the top of itself.
 *
 * This is the same bargain sw-media.js makes with the `du1` constants and it carries the same risk:
 * a copy is a second place for one fact to be spelled, and the two drifting apart here would not
 * fail anywhere. The worker would write beside the manifest instead of into it, and the page would
 * find nothing staged and report an honest, wrong «the download did not finish».
 *
 * Scripts/swDownload.test.ts pins them against library.ts's own — the directory and the file name by
 * running library.ts's real `save()` and reading back what it actually called things, rather than by
 * repeating the rule a third time in a test where it would agree with this file by construction.
 */
const Directory = 'offline';
const IndexFile = 'index.json';

/**
 * What marks a Background Fetch registration as one of ours.
 *
 * <p>A registration id is any string the page likes, and this origin may one day start background
 * fetches for something that is not a film. An id that does not begin with this is not answered,
 * not cleaned up and not touched, because swallowing another feature's event is a failure that
 * presents as that feature never finishing.</p>
 */
const IdPrefix = 'du1-save:';

/**
 * The film's own name for the file, which is the manifest key turned into something OPFS will take.
 *
 * <p>A hand copy of library.ts's function of the same name — see the note above the constants. The
 * key is a URL because that is the one string already unique per file per origin and already on the
 * page; OPFS will not take a slash, and a name that collided would be one film playing under
 * another's title.</p>
 */
function fileNameFor(key) {
  return `${encodeURIComponent(key).replace(/[^A-Za-z0-9._-]/g, '_')}.bin`;
}

/**
 * Where this worker puts bytes it cannot vouch for.
 *
 * <p>Not <c>fileNameFor(key)</c>, and the suffix is the whole of the safety argument in the header:
 * these bytes may be ciphertext, nothing here can tell, and the name a player opens must never hold
 * something a player cannot play.</p>
 */
function stagedNameFor(key) {
  return `${fileNameFor(key)}.raw`;
}

/**
 * The film's details, carried on the registration id.
 *
 * <p>The metadata has to travel with the fetch, and this is the only place it can travel. The point
 * of the whole feature is that the download outlives the page: a variable in the tab is gone, a
 * message channel to it is gone, and the one thing left when this worker is woken hours later is the
 * registration — which carries an id the page chose and nothing else that is ours.</p>
 *
 * <p>Writing the manifest entry from the page first and merging into it here was the other
 * candidate, and it does not survive contact with library.ts's <c>list()</c>: an entry whose bytes
 * are not on the disk yet is dropped on the next visit, which is any moment at all while a
 * background download is running. Something that is deleted by an ordinary page load is not a
 * handover.</p>
 */
function idFor(entry) {
  return IdPrefix + encodeURIComponent(JSON.stringify({
    key: entry.key,
    name: entry.name,
    type: entry.type,
    bytes: entry.bytes,
    savedAt: entry.savedAt,
    watchUrl: entry.watchUrl,
  }));
}

/**
 * The details back out again, or null.
 *
 * <p>Every field is checked rather than trusted. An id is a string that has been sitting in the
 * browser's own storage across an update of this file and possibly across a version of it that
 * wrote a different shape, so «it is ours because it has the prefix» is not enough — and a manifest
 * entry with <c>undefined</c> where its name should be is a row in the offline list with a blank
 * where the film is.</p>
 *
 * <p>Null means «leave everything alone», never «clean up». A registration this worker cannot read
 * is one it has no business deleting bytes over.</p>
 */
function metaFrom(id) {
  if (typeof id !== 'string' || !id.startsWith(IdPrefix)) return null;

  let parsed;
  try {
    parsed = JSON.parse(decodeURIComponent(id.slice(IdPrefix.length)));
  } catch {
    return null;
  }

  if (!parsed || typeof parsed !== 'object') return null;

  const strings = ['key', 'name', 'type', 'watchUrl'];
  const numbers = ['bytes', 'savedAt'];

  for (const field of strings) {
    if (typeof parsed[field] !== 'string' || parsed[field] === '') return null;
  }

  for (const field of numbers) {
    if (typeof parsed[field] !== 'number' || !Number.isFinite(parsed[field])) return null;
  }

  return {
    key: parsed.key,
    name: parsed.name,
    type: parsed.type,
    bytes: parsed.bytes,
    savedAt: parsed.savedAt,
    watchUrl: parsed.watchUrl,
  };
}

async function directory() {
  const root = await navigator.storage.getDirectory();

  return root.getDirectoryHandle(Directory, { create: true });
}

/**
 * The manifest, or an empty one.
 *
 * <p>Absent on a first run and unreadable if a write was interrupted, and both mean «nothing
 * recorded» — the same answer library.ts gives, and it has to be the same answer, because the two
 * of them read and write one file.</p>
 */
async function readIndex(dir) {
  try {
    const handle = await dir.getFileHandle(IndexFile);
    const parsed = JSON.parse(await (await handle.getFile()).text());

    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

async function writeIndex(dir, entries) {
  const handle = await dir.getFileHandle(IndexFile, { create: true });
  const writable = await handle.createWritable();

  await writable.write(JSON.stringify(entries));
  await writable.close();
}

/**
 * Throws away one staged download: the bytes, and the record of them.
 *
 * <p><b>Only ever a staged entry.</b> The same key can perfectly well already have a real save
 * against it — finished, or unfinished and resumable — written by library.ts from a page, and that
 * one belongs to somebody who waited for it. Filtering by key alone would delete a film somebody
 * has on their phone because a background attempt at the same film was cancelled.</p>
 */
async function discardStaged(key) {
  try {
    const dir = await directory();

    try {
      await dir.removeEntry(stagedNameFor(key));
    } catch {
      // Never written, or already gone. The manifest is still worth correcting.
    }

    const recorded = await readIndex(dir);
    const kept = recorded.filter((entry) => !(entry.key === key && entry.staged === true));

    if (kept.length !== recorded.length) await writeIndex(dir, kept);
  } catch {
    // No OPFS, or a directory that will not open. There is nothing to undo and nobody to tell: a
    // worker has no reader, and the visible consequence is a staged file that the next `list()`
    // sweeps as an orphan, which is where it was going anyway.
  }
}

/** Whether an absolute URL from a fetch record names the same address as a manifest key. */
function sameAddress(url, key) {
  try {
    const parsed = new URL(url, self.location.origin);

    return `${parsed.pathname}${parsed.search}` === key;
  } catch {
    return false;
  }
}

/**
 * The one response this registration downloaded, if it is worth writing down.
 *
 * <p>Matched against the key rather than taken by position, because a registration may hold several
 * requests and position is the kind of assumption that holds until somebody adds a poster image to
 * the same fetch.</p>
 *
 * <p><b>`redirected` is the check that matters</b> and it is here because this exact mistake has
 * already been made once in this codebase. `/files/{id}/content` is cookie-authenticated and answers
 * an unauthenticated request with a 302 to the sign-in page; fetch follows redirects; the sign-in
 * page is a 200 with a body. sw-media.js fed one into AES-GCM as segment zero and the reader got a
 * player that never produced a frame. Here it would be worse, because it would be written to the
 * disk: a background download that runs while the session is expiring would stage a page of HTML
 * under a film's name and report success. Background Fetch sends credentials for a same-origin
 * request, so the ordinary case is fine — this is the case where it is not.</p>
 */
async function responseFor(registration, key) {
  const records = await registration.matchAll();
  if (!Array.isArray(records) || records.length === 0) return null;

  const wanted = records.find((record) => sameAddress(record.request && record.request.url, key))
    ?? records[0];

  const response = await wanted.responseReady;

  if (!response || !response.ok) return null;
  if (response.redirected) return null;

  return response;
}

/**
 * Copies a finished download into OPFS and records it as staged.
 *
 * <p>Bytes first and the record afterwards, which is the opposite of what library.ts does and for
 * the opposite reason. There the record has to exist before the bytes because a tab closing
 * mid-write runs nothing, and without a record the megabytes on the disk are storage nobody can see
 * or remove. Here the bytes already exist — the browser downloaded them before this handler was
 * woken — so the only failure available is the copy, and a record written first would name a file
 * that never appeared: a row offering to finish a film that is not there. The other way round, a
 * copy that fails leaves bytes with no record, which `list()` sweeps.</p>
 *
 * <p>No room check before the copy, and that is a departure from library.ts worth naming. There the
 * check is everything, because it happens before a 6 GB download spends somebody's traffic. Here the
 * traffic is already spent; all a check could buy is failing sooner, and getting its arithmetic
 * wrong would refuse a copy that would have fitted. The copy is streamed rather than buffered, so
 * running out of room fails one chunk in and cleans up below.</p>
 */
async function stage(meta, response) {
  const dir = await directory();

  /*
   * A save this worker must not touch.
   *
   * <p>An entry for this key that is not staged is library.ts's: a finished film, or an unfinished
   * one carrying a checkpoint somebody has already waited hours for. Replacing it with a staged
   * record would orphan its `.bin` — nothing would reference that name any more, and the next
   * `list()` sweeps names nothing references — so the price of overwriting is the film itself.</p>
   *
   * <p>The bytes are dropped instead. That is a background download wasted, which is a great deal
   * cheaper than the alternative, and it cannot happen in the intended flow: a page that starts a
   * background fetch for a key does not also run `save()` against it.</p>
   */
  if ((await readIndex(dir)).some((entry) => entry.key === meta.key && entry.staged !== true)) {
    return false;
  }

  const handle = await dir.getFileHandle(stagedNameFor(meta.key), { create: true });

  // keepExistingData is deliberately absent, which means false. A staged file left by an earlier
  // attempt at the same film is stale by definition, and keeping its tail would leave the end of the
  // old download hanging off the end of the new one — a file that is the right length nowhere and
  // fails to decrypt at a segment boundary somebody would have to go looking for.
  const writable = await handle.createWritable();

  try {
    // Streamed a chunk at a time and never assembled in memory, for the same reason library.ts
    // streams: a four-gigabyte film held whole is a tab the phone kills, and here it would be a
    // worker the phone kills, silently, having downloaded the whole thing.
    if (response.body) {
      const reader = response.body.getReader();

      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;

        await writable.write(value);
      }
    }

    await writable.close();
  } catch (error) {
    try {
      await writable.close();
    } catch {
      // An aborted writable throws on close in some browsers. Either way the partial file below is
      // what has to go.
    }

    await discardStaged(meta.key);
    throw error;
  }

  /*
   * The manifest entry, in Scripts/offline/library.ts's own shape and no other.
   *
   * `partial: true` because none of this film is playable yet, which is what that flag has always
   * meant and is why the offline screen offers Continue rather than a player against it.
   *
   * `staged: true` because `partial` alone would be a lie of the useful kind: an ordinary unfinished
   * save has some plaintext at `fileNameFor(key)` and resumes by asking the server for the rest,
   * and this has none there and resumes by decrypting something else entirely. A reader that cannot
   * tell them apart will seek the first and find nothing.
   *
   * `written: 0` for the same reason and it is not a placeholder: `written` counts plaintext bytes
   * on the disk at the name a player opens, and there are exactly none. Recording the size of the
   * staged file here would be the true answer to a different question and would show a reader a
   * progress bar at 100% for a film they cannot watch.
   */
  await writeIndex(dir, [
    ...(await readIndex(dir)).filter((entry) => entry.key !== meta.key),
    { ...meta, partial: true, staged: true, written: 0 },
  ]);

  return true;
}

/*
 * The browser has finished downloading. This is the one moment it wakes us.
 *
 * waitUntil is not optional here in the way it sometimes is elsewhere: this worker was started for
 * this event and has nothing else to do, so without it the browser is free to terminate it the
 * moment the handler returns — part-way through a copy of several gigabytes, on a schedule that
 * depends on how busy the phone is. That is the flakiest possible bug: it works on a desktop and
 * loses large films on the devices this feature exists for.
 */
self.addEventListener('backgroundfetchsuccess', (event) => {
  const meta = metaFrom(event.registration && event.registration.id);
  if (!meta) return;

  event.waitUntil((async () => {
    try {
      const response = await responseFor(event.registration, meta.key);

      // Null means the download succeeded at the wrong thing — a redirect to sign-in, or a status
      // this worker will not vouch for. Nothing is written, and nothing is cleaned up either,
      // because a previous good staging for this key is not made wrong by a later bad attempt.
      if (response) await stage(meta, response);
    } catch {
      // Deliberately silent, as everywhere else in this worker. There is no reader here, `stage`
      // has already removed what it wrote, and the visible consequence is a film that is not in the
      // offline list — which is the honest report of what happened.
    }
  })());
});

/*
 * The download will not be finishing.
 *
 * `fail` is the browser giving up — a response that was not ok, the connection gone for good, the
 * quota it downloads into exhausted. `abort` is somebody pressing cancel on the notification, or the
 * page calling abort() on the registration. One remedy, so one handler.
 *
 * Nothing has been staged for this key by this registration, because staging only happens on
 * success — so this is usually a no-op, and it is written anyway. A film saved in the background,
 * left unfinished by the reader, and started again a week later would otherwise leave the first
 * attempt's bytes on the disk under a name the new attempt is about to disagree with. Cleaning up
 * what is probably not there costs one directory read.
 */
function abandon(event) {
  const meta = metaFrom(event.registration && event.registration.id);
  if (!meta) return;

  event.waitUntil(discardStaged(meta.key));
}

self.addEventListener('backgroundfetchfail', abandon);
self.addEventListener('backgroundfetchabort', abandon);

/**
 * What the page side reaches this worker through.
 *
 * <p>Deliberately not a message channel, unlike sw-media.js. Streams there are held in memory and
 * have to be handed over by a live page; everything here is already on the disk under names both
 * sides can spell, so the only thing the page actually needs from this file is the spelling — and
 * a surface it can call directly is one that cannot be got wrong by a message arriving before a
 * listener is registered.</p>
 *
 * <p>Reached from a page as
 * <c>(await navigator.serviceWorker.ready)</c> for the registration and then the ordinary OPFS API
 * for the files; this object is what a <i>worker</i>-side caller uses and what the page-side module
 * should be written against so that the id encoding and the staging name are spelled once in this
 * repository rather than twice.</p>
 */
self.du1Download = {
  /** The prefix that marks a registration as this feature's. */
  IdPrefix,

  /** Whether a Background Fetch registration id is one of ours at all. */
  claims(id) {
    return typeof id === 'string' && id.startsWith(IdPrefix);
  },

  /** The registration id to start a background fetch under, from a SavedFile-shaped object. */
  idFor,

  /** The details back out of one, or null if it is not ours or not readable. */
  metaFrom,

  /** Where the bytes for a key are staged, which is not where a player looks. */
  stagedNameFor,

  /**
   * Everything waiting to be finished.
   *
   * <p>The minimum the page needs: what was downloaded while nobody was watching, so it can offer to
   * ask for a passphrase and turn one into a film.</p>
   */
  async staged() {
    try {
      return (await readIndex(await directory())).filter((entry) => entry.staged === true);
    } catch {
      return [];
    }
  },

  /**
   * The staged bytes themselves, as a File, or null.
   *
   * <p>A File and not a stream, because that is what both possible readers want: `du1` decryption
   * takes slices by offset, and an unlocked file is a copy that only has to be moved to the other
   * name.</p>
   */
  async stagedFile(key) {
    try {
      const dir = await directory();

      return await (await dir.getFileHandle(stagedNameFor(key))).getFile();
    } catch {
      return null;
    }
  },

  /** Throws one away — the bytes and the record. What the page calls once it has finished the job. */
  discard: discardStaged,
};
