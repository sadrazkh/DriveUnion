import { beforeEach, describe, expect, it } from 'vitest';
import {
  createRecorder,
  finished,
  forgetAllPositions,
  forgetPosition,
  PositionKeyPrefix,
  positionFor,
  timecode,
} from './watchPosition';
import { clear as clearLibrary, remove as removeSaved } from './offline/library';

/**
 * Remembering where a film was stopped, taken away from the player and asked what it decides.
 *
 * <p>Everything worth arguing about in this feature is a threshold — how often to write, how near
 * the end is «watched it», how near the start is «has not started» — and none of them can be seen
 * from outside except through what the module reports it did. That is what <c>Recorded</c> is for,
 * and it is most of what this file asserts.</p>
 *
 * <p>There is no jsdom in this project and there is not going to be one, so the player's own
 * behaviour — the seek, the «start over» control, the four paths a source can arrive by — is not
 * exercised here at all. What is exercised is every decision the player delegates, which is why
 * those decisions were moved out of it.</p>
 */

// ── localStorage, as much of it as this module touches ────────────────────────────────────────────

/**
 * A real Storage, near enough.
 *
 * <p><c>length</c> and <c>key(i)</c> are here rather than stubbed away because the sweep and the
 * pruning both walk the index, and the bug that walk invites — removing while iterating, so every
 * second key is skipped — is only reproducible against something that renumbers.</p>
 */
class FakeStorage {
  entries = new Map<string, string>();

  /** Set to make every write throw, which is what a full quota looks like from in here. */
  refuseWrites = false;

  get length() {
    return this.entries.size;
  }

  key(index: number): string | null {
    return [...this.entries.keys()][index] ?? null;
  }

  getItem(name: string): string | null {
    return this.entries.get(name) ?? null;
  }

  setItem(name: string, value: string): void {
    if (this.refuseWrites) throw new Error('QuotaExceededError');

    this.entries.set(name, value);
  }

  removeItem(name: string): void {
    this.entries.delete(name);
  }
}

let store: FakeStorage;

/** The clock the recorder is given, in milliseconds. Moved by hand. */
let clock: number;

const now = () => clock;

/** Records this module wrote, by the file they belong to. */
function recorded(key: string) {
  const raw = store.getItem(PositionKeyPrefix + key);

  return raw === null ? null : (JSON.parse(raw) as { at: number; length: number; savedAt: number });
}

beforeEach(() => {
  store = new FakeStorage();
  clock = 1_000_000;

  Object.defineProperty(globalThis, 'localStorage', {
    value: store,
    configurable: true,
    writable: true,
  });
});

// ── the throttle ──────────────────────────────────────────────────────────────────────────────────

describe('how often a position is written', () => {
  it('writes the first position it is given', () => {
    const recorder = createRecorder('/files/a/content', { now });

    expect(recorder.record(60, 3600)).toBe('wrote');
    expect(recorded('/files/a/content')?.at).toBe(60);
  });

  it('declines every position inside the window and writes the one after it', () => {
    const recorder = createRecorder('/files/a/content', { now, writeEvery: 10_000 });

    recorder.record(60, 3600);

    // Four a second for nine seconds, which is what a playing element actually produces.
    for (let tick = 0; tick < 36; tick++) {
      clock += 250;
      expect(recorder.record(60 + tick * 0.25, 3600)).toBe('throttled');
    }

    clock += 1_000;
    expect(recorder.record(70, 3600)).toBe('wrote');
    expect(recorded('/files/a/content')?.at).toBe(70);
  });

  it('leaves the last written position in place while it is declining to write', () => {
    const recorder = createRecorder('/files/a/content', { now, writeEvery: 10_000 });

    recorder.record(60, 3600);
    clock += 5_000;
    recorder.record(65, 3600);

    // Not 65. The whole point of the throttle is that this number is allowed to be stale, and how
    // stale it is allowed to be is the window.
    expect(recorded('/files/a/content')?.at).toBe(60);
  });

  it('writes regardless of the window when it is flushed', () => {
    const recorder = createRecorder('/files/a/content', { now, writeEvery: 10_000 });

    recorder.record(60, 3600);
    clock += 1_000;

    // Pause, end, the page being hidden, the player torn down: the four moments where the next
    // event may never come, so the window has to be ignored.
    expect(recorder.flush(61, 3600)).toBe('wrote');
    expect(recorded('/files/a/content')?.at).toBe(61);
  });

  it('starts a fresh window per file, so two players do not throttle each other', () => {
    const one = createRecorder('/files/a/content', { now, writeEvery: 10_000 });
    const two = createRecorder('/files/b/content', { now, writeEvery: 10_000 });

    expect(one.record(60, 3600)).toBe('wrote');
    expect(two.record(90, 3600)).toBe('wrote');

    expect(recorded('/files/a/content')?.at).toBe(60);
    expect(recorded('/files/b/content')?.at).toBe(90);
  });
});

// ── the end of the film ───────────────────────────────────────────────────────────────────────────

describe('a film watched to the end', () => {
  it('is cleared rather than remembered, so nobody is resumed into the credits', () => {
    const recorder = createRecorder('/files/a/content', { now });

    recorder.record(3_000, 3_600);
    clock += 20_000;

    // A two-hour film ends within its last two minutes; a one-hour one within its last three.
    expect(recorder.record(3_500, 3_600)).toBe('cleared');
    expect(recorded('/files/a/content')).toBeNull();
    expect(positionFor('/files/a/content', 3_600)).toBe(0);
  });

  it('does not touch storage again once it has cleared', () => {
    const recorder = createRecorder('/files/a/content', { now });

    recorder.record(3_000, 3_600);
    clock += 20_000;

    expect(recorder.record(3_500, 3_600)).toBe('cleared');

    // The last minutes of a film are hundreds of timeupdates past the threshold, and each of them
    // would otherwise be a removeItem for a key that went on the first.
    clock += 20_000;
    expect(recorder.record(3_550, 3_600)).toBe('throttled');
  });

  it('caps how much of a long film counts as the end', () => {
    // Five per cent of two hours is six minutes, which is the last scene rather than the credits.
    expect(finished(7_200 - 300, 7_200)).toBe(false);
    expect(finished(7_200 - 60, 7_200)).toBe(true);
  });

  it('floors how much of a short one does', () => {
    // Five per cent of three minutes is nine seconds, which would demand the last frame.
    expect(finished(180 - 12, 180)).toBe(true);
    expect(finished(180 - 30, 180)).toBe(false);
  });

  it('takes a position at or past the length as finished', () => {
    const recorder = createRecorder('/files/a/content', { now });

    expect(recorder.record(3_600, 3_600)).toBe('cleared');
  });

  it('refuses to resume a stored position the file is no longer long enough for', () => {
    store.setItem(
      `${PositionKeyPrefix}/d/kx91mzq4/file`,
      JSON.stringify({ at: 5_400, length: 7_200, savedAt: clock }),
    );

    // The record is honest about the file it was written for; the element now says three minutes.
    // A slug reissued over different bytes is the same URL over a different film.
    expect(positionFor('/d/kx91mzq4/file', 180)).toBe(0);
    expect(positionFor('/d/kx91mzq4/file', 7_200)).toBe(5_400);
  });
});

// ── the start of the film ─────────────────────────────────────────────────────────────────────────

describe('a film barely started', () => {
  it('is not written at all', () => {
    const recorder = createRecorder('/files/a/content', { now });

    expect(recorder.record(9, 3_600)).toBe('cleared');
    expect(recorded('/files/a/content')).toBeNull();
  });

  it('clears a position somebody has deliberately scrubbed back past', () => {
    const recorder = createRecorder('/files/a/content', { now });

    recorder.record(2_400, 3_600);
    clock += 20_000;

    // Dragging the scrubber to the beginning and walking away says «start this again» as plainly
    // as the button does. Leaving the old record would carry them back to where they just left.
    expect(recorder.record(3, 3_600)).toBe('cleared');
    expect(positionFor('/files/a/content', 3_600)).toBe(0);
  });

  it('is not offered as a resume even if something else wrote one', () => {
    store.setItem(
      `${PositionKeyPrefix}/files/a/content`,
      JSON.stringify({ at: 4, length: 3_600, savedAt: clock }),
    );

    expect(positionFor('/files/a/content', 3_600)).toBe(0);
  });

  it('says nothing at all while the element has not measured the file', () => {
    const recorder = createRecorder('/files/a/content', { now });

    // duration is NaN until loadedmetadata, and every question here is about the remainder.
    expect(recorder.record(60, 0)).toBe('ignored');
    expect(recorder.record(60, Number.NaN)).toBe('ignored');
    expect(store.length).toBe(0);
  });
});

// ── one file's key is one file's key ──────────────────────────────────────────────────────────────

describe('the key', () => {
  it('never returns one file\'s position for another', () => {
    const one = createRecorder('/files/a/content', { now });
    const two = createRecorder('/files/b/content', { now });

    one.record(600, 3_600);
    two.record(1_800, 3_600);

    expect(positionFor('/files/a/content', 3_600)).toBe(600);
    expect(positionFor('/files/b/content', 3_600)).toBe(1_800);
    expect(positionFor('/files/c/content', 3_600)).toBe(0);
  });

  it('keeps the panel route and the public route apart, as the offline library does', () => {
    createRecorder('/files/a/content', { now }).record(600, 3_600);

    // The same film through two doors is two records. Stated rather than fixed: the offline library
    // keeps two copies for the same reason, and one of them disagreeing would be the bug.
    expect(positionFor('/d/kx91mzq4/file', 3_600)).toBe(0);
  });

  it('does not confuse a key with one that starts the same way', () => {
    createRecorder('/files/a/content', { now }).record(600, 3_600);

    expect(positionFor('/files/a/content/extra', 3_600)).toBe(0);
    expect(positionFor('/files/a', 3_600)).toBe(0);
  });

  it('ignores anything else this origin has stored', () => {
    store.setItem('driveunion-theme', 'dark');
    createRecorder('/files/a/content', { now }).record(600, 3_600);

    forgetAllPositions();

    expect(store.getItem('driveunion-theme')).toBe('dark');
    expect(store.length).toBe(1);
  });
});

// ── clearing ──────────────────────────────────────────────────────────────────────────────────────

describe('forgetting', () => {
  it('removes one file\'s position and leaves the rest', () => {
    createRecorder('/files/a/content', { now }).record(600, 3_600);
    createRecorder('/files/b/content', { now }).record(900, 3_600);

    // This is the call the offline library's `remove` makes: taking the copy off the device takes
    // the note of where you were in it too.
    forgetPosition('/files/a/content');

    expect(positionFor('/files/a/content', 3_600)).toBe(0);
    expect(positionFor('/files/b/content', 3_600)).toBe(900);
  });

  it('is silent about a file that was never watched', () => {
    expect(() => forgetPosition('/files/never/content')).not.toThrow();
  });

  it('removes every position when the device library is emptied', () => {
    for (const name of ['a', 'b', 'c', 'd']) {
      createRecorder(`/files/${name}/content`, { now }).record(600, 3_600);
    }

    // «Remove everything» on the offline screen, which would otherwise leave four notes of what was
    // watched behind a button that said it had removed everything.
    forgetAllPositions();

    expect(store.length).toBe(0);
  });

  /**
   * The two calls the offline library makes, exercised through the library itself.
   *
   * <p>There is no OPFS here, so <c>supported()</c> is false and both functions return early — which
   * is exactly the path worth pinning down. The position is forgotten <b>before</b> that guard, on
   * purpose: a film that was only ever streamed has a position and no copy, and on a browser that
   * can keep nothing at all it would otherwise be unremovable.</p>
   */
  it('goes when the saved copy is removed, even where nothing could be saved', async () => {
    createRecorder('/files/a/content', { now }).record(600, 3_600);
    createRecorder('/files/b/content', { now }).record(900, 3_600);

    await removeSaved('/files/a/content');

    expect(positionFor('/files/a/content', 3_600)).toBe(0);
    expect(positionFor('/files/b/content', 3_600)).toBe(900);
  });

  it('goes when the device library is emptied, even where nothing could be saved', async () => {
    createRecorder('/files/a/content', { now }).record(600, 3_600);
    store.setItem('driveunion-theme', 'dark');

    await clearLibrary();

    expect(positionFor('/files/a/content', 3_600)).toBe(0);
    expect(store.getItem('driveunion-theme')).toBe('dark');
  });

  it('lets a recorder write again after the reader has started over', () => {
    const recorder = createRecorder('/files/a/content', { now });

    recorder.record(600, 3_600);
    recorder.forget();

    expect(positionFor('/files/a/content', 3_600)).toBe(0);

    // No waiting for the window: pressing «start over» and then watching has to be recordable at
    // once, or the first ten seconds of the second attempt are not remembered either.
    expect(recorder.record(600, 3_600)).toBe('wrote');
  });
});

// ── the storage underneath ────────────────────────────────────────────────────────────────────────

describe('when the browser will not co-operate', () => {
  it('reports a refused write rather than throwing into the timeupdate handler', () => {
    store.refuseWrites = true;

    const recorder = createRecorder('/files/a/content', { now });

    // A private window, or a full quota. A film that does not remember its place is still a film
    // that plays, and an exception here would take the player's event handler with it.
    expect(recorder.record(600, 3_600)).toBe('ignored');
  });

  it('treats an unreadable record as nothing remembered', () => {
    store.setItem(`${PositionKeyPrefix}/files/a/content`, 'not json');

    expect(positionFor('/files/a/content', 3_600)).toBe(0);
  });

  it('treats a record with no number in it as nothing remembered', () => {
    store.setItem(`${PositionKeyPrefix}/files/a/content`, JSON.stringify({ at: 'half way' }));

    expect(positionFor('/files/a/content', 3_600)).toBe(0);
  });

  it('drops the oldest records rather than growing without limit', () => {
    // Two hundred and one films, oldest first, each a second apart.
    for (let index = 0; index < 201; index++) {
      clock += 1_000;
      createRecorder(`/files/${index}/content`, { now }).record(600, 3_600);
    }

    expect(store.length).toBe(200);
    expect(positionFor('/files/0/content', 3_600)).toBe(0);
    expect(positionFor('/files/200/content', 3_600)).toBe(600);
  });
});

// ── how it reads ──────────────────────────────────────────────────────────────────────────────────

describe('the timecode beside the sentence', () => {
  it('reads as the player\'s own control bar reads', () => {
    expect(timecode(0)).toBe('0:00');
    expect(timecode(9)).toBe('0:09');
    expect(timecode(2_535)).toBe('42:15');
    expect(timecode(3_600)).toBe('1:00:00');
    expect(timecode(4_517)).toBe('1:15:17');
  });

  it('says a time rather than nothing when the element has said nothing useful', () => {
    expect(timecode(Number.NaN)).toBe('0:00');
    expect(timecode(-5)).toBe('0:00');
  });
});
