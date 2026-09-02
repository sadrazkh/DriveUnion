/**
 * Where a film was stopped, so coming back to it is not starting it again.
 *
 * <p><b>Per file, per browser, on the device and nowhere else.</b> There is no server side to this
 * and there is deliberately not going to be one: a position is three numbers, it is worth almost
 * nothing to synchronise, and the moment it goes to the server it becomes a record of what somebody
 * watched and when — kept by us, for a stranger holding a public link as much as for the owner. The
 * device already knows what it played. Nothing else needs to.</p>
 *
 * <p><b>localStorage and not the offline library.</b> The library is OPFS and asynchronous, and this
 * has to answer before the first frame — the seek must happen as metadata lands, not a microtask
 * queue later, or the reader watches the opening seconds and is then yanked forward. localStorage is
 * synchronous, which is normally its worst property and is here the only one that matters. The whole
 * record is small enough that the synchronous write is not a stall: see WriteEvery.</p>
 *
 * <p><b>Keyed by the content URL, which is the key the offline library already uses.</b> Same string,
 * same reason — it is the one thing already on the page that is unique per file per origin. It has a
 * consequence worth stating: the panel's route and a public link's route are different URLs for the
 * same film, so watching through both leaves two records. That is the offline library's behaviour
 * too (two copies, two rows), and making this one clever enough to disagree with it would be a
 * second answer to a question that already has one.</p>
 *
 * <p>Kept out of <c>filePlayer.ts</c> because every decision in here is a threshold with an argument
 * behind it, and a threshold with an argument behind it is a thing to test. The player has no test —
 * there is no jsdom in this project — so anything that can live here does.</p>
 */

/**
 * The prefix every record is stored under.
 *
 * <p>One key per file rather than one map of all of them. A map would have to be read, parsed,
 * mutated and rewritten on every write, and two tabs playing two different films would then race:
 * each holds the map as it was when it started and the later write drops the other's entry. A key
 * per file cannot lose a neighbour's record, and it makes «forget this one» a single removeItem with
 * no parsing at all — which is what the offline library's <c>remove</c> calls.</p>
 *
 * <p>The key is the content URL appended verbatim. localStorage keys are arbitrary strings, so there
 * is nothing to escape, and appending verbatim is what lets any caller holding the same key the
 * library holds address the same record without knowing this module's rules.</p>
 */
export const PositionKeyPrefix = 'driveunion-watched:';

export interface WatchPosition {
  /** Seconds into the file. */
  readonly at: number;

  /** The file's length in seconds, as the element reported it. Zero when it never said. */
  readonly length: number;

  /** Epoch milliseconds, for the pruning below. */
  readonly savedAt: number;
}

/**
 * How often a position may actually be written.
 *
 * <p><c>timeupdate</c> fires about four times a second. A two-hour film is thirty thousand events,
 * and writing on each of them is thirty thousand synchronous localStorage writes — each one a main
 * thread stall and a disk touch, for a number that changed by a quarter of a second. Ten seconds
 * turns that into seven hundred, which is a rounding error against everything else a video element
 * is doing.</p>
 *
 * <p>Ten and not sixty because of what the interval costs when it is lost. The tab can be killed at
 * any moment — iOS discards a backgrounded web app without warning — and what survives is the last
 * write, so the interval is the worst-case error in where the reader is put back. Ten seconds early
 * is a few seconds of film watched twice. A minute early is the reader wondering whether it
 * remembered anything at all. Being early is also the right side to be wrong on: nobody minds seeing
 * a moment twice and everybody minds missing one.</p>
 *
 * <p>The interval is not the only thing that writes. Pause, end, the page being hidden and the
 * player being torn down all flush immediately — see <c>flush</c>. The throttle exists for the
 * steady state of a film simply playing, which is the only state that produces thousands of
 * events.</p>
 */
const WriteEvery = 10_000;

/**
 * Fifteen seconds, used at both ends of the film, for the same reason at each.
 *
 * <p><b>At the start:</b> a position under fifteen seconds is not worth restoring. Somebody who
 * stopped twelve seconds in has not lost their place, and seeking them to 0:12 is a jolt with
 * nothing bought by it — worse, it hides the opening of the film behind a jump they did not ask
 * for.</p>
 *
 * <p><b>At the end:</b> it is the floor under the end-of-film threshold below, so that a short thing
 * does not have to be watched to the last frame to count as watched.</p>
 */
const Negligible = 15;

/**
 * How much of the end counts as «finished», as a fraction of the length.
 *
 * <p>Five per cent of a feature film is about six minutes, which is more than the credits and would
 * throw away somebody who stopped in the last scene — so it is capped. Five per cent of a
 * three-minute track is nine seconds, which is less than the floor — so it is floored. The fraction
 * is what governs the middle, where a forty-five-minute episode gets a hundred and thirty-five
 * seconds and the credits are inside it.</p>
 */
const EndFraction = 0.05;

/** The cap on the above. Two minutes is long credits; more than that is still the film. */
const EndCap = 120;

/**
 * How many records are kept before the oldest are dropped.
 *
 * <p>Nothing here expires by age, and that is deliberate: «continue watching» is a feature about
 * long gaps, and a record that expired after a month would fail exactly the person it is for. What
 * it must not do is grow without limit on a browser somebody has used for years, so it is bounded by
 * count instead — two hundred films is far more than anyone will have open at once and about twelve
 * kilobytes of a five-megabyte quota.</p>
 */
const Keep = 200;

/**
 * What a call to <c>record</c> or <c>flush</c> actually did.
 *
 * <p>Returned rather than kept private because it is the only way to test the thresholds: a caller
 * cannot see the difference between «declined to write because it is too soon» and «wrote» by
 * reading storage, and those are the two behaviours this module exists to get right.</p>
 */
export type Recorded =
  /** The position was written. */
  | 'wrote'
  /** Near the end, or near the start: any record for this file was removed. */
  | 'cleared'
  /** Inside the window since the last write, or already cleared. Storage was not touched. */
  | 'throttled'
  /** Nothing usable was offered — no length, or a time that is not a number. */
  | 'ignored';

function storage(): Storage | null {
  try {
    // Absent in a worker, and a private window in some browsers throws on the property itself
    // rather than on the call. Either way this feature is simply not available, and a film that
    // does not remember its place is a film that plays.
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}

/**
 * How much of the end of this file counts as having watched it.
 *
 * <p>Exported for the one caller that has to agree with it — a player deciding whether to draw
 * «start over» has to reach the same verdict this module reached when it cleared the record, or the
 * page says one thing and storage holds another.</p>
 */
export function endsWithin(length: number): number {
  return Math.max(Negligible, Math.min(length * EndFraction, EndCap));
}

/** Whether a position in a file of this length is «watched it» rather than «stopped here». */
export function finished(at: number, length: number): boolean {
  if (!(length > 0)) return false;

  return length - at <= endsWithin(length);
}

/**
 * The position to resume from, or zero when there is nothing worth resuming to.
 *
 * <p><c>length</c> is what the element now says the file is, and it is checked against the record
 * rather than trusted from it. A record can outlive the file it describes — a slug revoked and
 * reissued over a different file is the same URL over different bytes — and seeking ninety minutes
 * into a three-minute file leaves a player that will not start with nothing on screen saying
 * why.</p>
 */
export function positionFor(key: string, length = 0): number {
  const record = read(key);

  if (!record) return 0;

  const at = record.at;

  if (!Number.isFinite(at) || at < Negligible) return 0;

  // Both lengths get a say. The stored one is what the file was when it was written; the passed one
  // is what the element says it is now. A position past either is not a position.
  if (length > 0 && (at >= length || finished(at, length))) return 0;
  if (record.length > 0 && finished(at, record.length)) return 0;

  return at;
}

function read(key: string): WatchPosition | null {
  const store = storage();

  if (!store) return null;

  try {
    const raw = store.getItem(PositionKeyPrefix + key);

    if (raw === null) return null;

    const parsed: unknown = JSON.parse(raw);

    if (typeof parsed !== 'object' || parsed === null) return null;

    const record = parsed as Partial<WatchPosition>;

    if (typeof record.at !== 'number') return null;

    return {
      at: record.at,
      length: typeof record.length === 'number' ? record.length : 0,
      savedAt: typeof record.savedAt === 'number' ? record.savedAt : 0,
    };
  } catch {
    // Unparseable, or a browser that refuses to read. Treated as «nothing remembered», which is the
    // state the reader is put in and is never worse than where they would otherwise have been.
    return null;
  }
}

/** Removes one file's position. Silent about a file that was never watched. */
export function forgetPosition(key: string): void {
  try {
    storage()?.removeItem(PositionKeyPrefix + key);
  } catch {
    // Nothing to undo.
  }
}

/**
 * Removes every position this origin holds.
 *
 * <p>Called by the offline library's <c>clear</c>, so that «empty the local storage» on the offline
 * screen empties this too. A reader who has just pressed a button labelled «remove everything» and
 * then finds the next film still knows where they stopped has been told something untrue.</p>
 */
export function forgetAllPositions(): void {
  const store = storage();

  if (!store) return;

  try {
    // Collected first and removed after. Removing while walking the index shifts every key above the
    // one just removed, so the walk skips one each time and half the records survive it.
    const doomed: string[] = [];

    for (let index = 0; index < store.length; index++) {
      const name = store.key(index);

      if (name !== null && name.startsWith(PositionKeyPrefix)) doomed.push(name);
    }

    for (const name of doomed) store.removeItem(name);
  } catch {
    // No storage. Nothing was kept, so nothing is left.
  }
}

export interface RecorderOptions {
  /**
   * The clock, for the throttle.
   *
   * <p>Injected so a test of the throttle needs no fake timers: the thing being tested is «has ten
   * seconds passed», and a function that answers that is the whole of what this needs to know about
   * time.</p>
   */
  readonly now?: () => number;

  /** See <see cref="WriteEvery"/>. Overridden only by its own test. */
  readonly writeEvery?: number;
}

export interface PositionRecorder {
  /** Throttled. This is what a <c>timeupdate</c> calls, thousands of times. */
  record(at: number, length: number): Recorded;

  /** Immediate. Pause, end, the page being hidden, the player being torn down. */
  flush(at: number, length: number): Recorded;

  /** «Start over» — the record goes, and the next write starts a new one. */
  forget(): void;
}

/**
 * A recorder for one file, holding the throttle's state.
 *
 * <p>An object rather than a module-level function because the throttle is per file and per visit:
 * two players on one page — which the offline screen could grow — must not throttle each other, and
 * a state that outlived the page would make the first write of a new visit arbitrary.</p>
 */
export function createRecorder(key: string, options: RecorderOptions = {}): PositionRecorder {
  const now = options.now ?? Date.now;
  const every = options.writeEvery ?? WriteEvery;

  let lastWriteAt = Number.NEGATIVE_INFINITY;

  /**
   * Whether storage is known to hold no record for this file.
   *
   * <p>Without it the last two minutes of every film are four hundred and eighty removeItem calls
   * for a key that went on the first of them. Set on a clear and unset on a write, so the guard can
   * never leave a stale record behind: the only way to skip a clear is to have just done one.</p>
   */
  let cleared = false;

  /** Pruned once per recorder, on the write that may have added a key. See <see cref="Keep"/>. */
  let pruned = false;

  function write(at: number, length: number): Recorded {
    const store = storage();

    if (!store) return 'ignored';

    try {
      store.setItem(
        PositionKeyPrefix + key,
        JSON.stringify({ at, length, savedAt: now() } satisfies WatchPosition),
      );

      lastWriteAt = now();
      cleared = false;

      if (!pruned) {
        pruned = true;
        prune(store);
      }

      return 'wrote';
    } catch {
      // Quota, or a browser that refuses to write. Not worth an exception that would take the
      // timeupdate handler — and with it the player — down with it.
      return 'ignored';
    }
  }

  function drop(): Recorded {
    if (cleared) return 'throttled';

    forgetPosition(key);
    cleared = true;

    return 'cleared';
  }

  function decide(at: number, length: number, throttled: boolean): Recorded {
    // A length of zero is a file the element has not measured yet, and nothing here can be decided
    // without it: whether this is the end is a question about the remainder.
    if (!Number.isFinite(at) || at < 0 || !(length > 0)) return 'ignored';

    // Both ends clear rather than skip, and the near end is the interesting one. Somebody who drags
    // the scrubber back to the beginning and walks away has said «start this again» as plainly as
    // the button does; leaving the old record standing would carry them back to where they had
    // deliberately just left.
    if (at < Negligible || finished(at, length)) return drop();

    if (throttled && now() - lastWriteAt < every) return 'throttled';

    return write(at, length);
  }

  return {
    record: (at, length) => decide(at, length, true),
    flush: (at, length) => decide(at, length, false),
    forget: () => {
      forgetPosition(key);
      cleared = true;
      lastWriteAt = Number.NEGATIVE_INFINITY;
    },
  };
}

/** Drops the oldest records once there are more than <see cref="Keep"/> of them. */
function prune(store: Storage): void {
  try {
    const records: { name: string; savedAt: number }[] = [];

    for (let index = 0; index < store.length; index++) {
      const name = store.key(index);

      if (name === null || !name.startsWith(PositionKeyPrefix)) continue;

      const raw = store.getItem(name);
      let savedAt = 0;

      try {
        const parsed = JSON.parse(raw ?? '') as Partial<WatchPosition>;

        savedAt = typeof parsed.savedAt === 'number' ? parsed.savedAt : 0;
      } catch {
        // Unreadable, so it is worth nothing and is the first thing to go: savedAt stays zero,
        // which sorts it to the front of the queue below.
      }

      records.push({ name, savedAt });
    }

    if (records.length <= Keep) return;

    records.sort((a, b) => a.savedAt - b.savedAt);

    for (const record of records.slice(0, records.length - Keep)) store.removeItem(record.name);
  } catch {
    // A browser that will not enumerate. The records stay; they are small.
  }
}

/**
 * A place in a film, written the way the player's own control bar writes it.
 *
 * <p><c>duration()</c> in the upload queue says «42m 15s», which is how long something takes. This
 * says «42:15», which is where you are — and it has to match the scrubber the sentence sits under,
 * or the reader has to translate between two ways of writing the same instant to check it.</p>
 *
 * <p>Always LTR digits, and the views put <c>dir="ltr"</c> on the span that holds it: a timecode
 * reversed by the Persian paragraph around it reads as a different time.</p>
 */
export function timecode(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '0:00';

  const whole = Math.floor(seconds);
  const hours = Math.floor(whole / 3600);
  const minutes = Math.floor((whole % 3600) / 60);
  const rest = whole % 60;

  const pad = (value: number) => String(value).padStart(2, '0');

  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(rest)}` : `${minutes}:${pad(rest)}`;
}
