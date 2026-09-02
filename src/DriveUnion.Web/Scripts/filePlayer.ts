import { unseal } from './crypto/envelope';
import { decryptInto } from './crypto/stream';
import { segmentSpan, type Bytes, type EncryptionHeader } from './crypto/format';
import { closeStream, openStream } from './crypto/play';
import { list as savedList, open as openSaved, remove as removeSaved, room, save, supported } from './offline/library';
import { bytes as formatBytes, duration } from './uploads/store';
import { createScreenLock } from './screenLock';
import { canBackground, stagedBytes, startBackground } from './offline/background';
import { createRecorder, positionFor, timecode } from './watchPosition';

/**
 * Watching a file from the panel, without downloading it.
 *
 * <p>Until this existed the owner of a two-hour film had no way to watch it here at all: the panel
 * served no bytes, so the only route to your own file was to make a public link and open that. This
 * points a media element at <c>/files/{id}/content</c>, which is metered and capped exactly as that
 * link would have been — watching a film costs what watching a film costs, whichever door it came
 * through.</p>
 *
 * <p>A locked file goes the long way round: the passphrase is typed here, the content key is
 * unwrapped here, and the service worker decrypts a segment at a time behind a URL the element
 * treats like any other. That is P7b's machinery reused rather than repeated — see
 * <c>crypto/play.ts</c>.</p>
 */
export function mountFilePlayer(el: HTMLElement): { stop: () => void } {
  const media = el.querySelector<HTMLMediaElement>('[data-player-media]');
  const start = el.querySelector<HTMLButtonElement>('[data-player-start]');
  const form = el.querySelector<HTMLFormElement>('[data-player-form]');
  const secret = el.querySelector<HTMLInputElement>('[data-player-secret]');
  const said = el.querySelector<HTMLElement>('[data-player-said]');

  const keep = el.querySelector<HTMLButtonElement>('[data-player-keep]');
  const forget = el.querySelector<HTMLButtonElement>('[data-player-forget]');
  const kept = el.querySelector<HTMLElement>('[data-player-kept]');

  const resume = el.querySelector<HTMLElement>('[data-player-resume]');
  const resumeAt = el.querySelector<HTMLElement>('[data-player-resume-at]');
  const resumeNote = el.querySelector<HTMLElement>('[data-player-resume-note]');
  const startOver = el.querySelector<HTMLButtonElement>('[data-player-start-over]');

  const inBackground = el.querySelector<HTMLButtonElement>('[data-player-background]');
  const stop = el.querySelector<HTMLButtonElement>('[data-player-stop]');
  const progress = el.querySelector<HTMLElement>('[data-player-progress]');
  const bar = el.querySelector<HTMLElement>('[data-player-bar]');
  const barFill = el.querySelector<HTMLElement>('[data-player-bar-fill]');
  const percentOut = el.querySelector<HTMLElement>('[data-player-percent]');
  const soFarOut = el.querySelector<HTMLElement>('[data-player-sofar]');
  const speedOut = el.querySelector<HTMLElement>('[data-player-speed]');
  const leftOut = el.querySelector<HTMLElement>('[data-player-left]');

  const contentUrl = el.dataset.playerUrl ?? '';
  const mime = el.dataset.playerMime ?? '';
  const title = el.dataset.playerTitle ?? 'file';
  const watchUrl = el.dataset.playerWatch ?? location.pathname;
  const sizeBytes = Number(el.dataset.playerBytes ?? '0');

  let streamId = '';

  /** Revoked on teardown, or it is a File pinned in memory for the life of the tab. */
  let objectUrl = '';

  /** Held after an unlock so a save can reuse it rather than asking for the passphrase twice. */
  let contentKey: CryptoKey | null = null;

  /** Non-null exactly while a save is running, which is what Stop and beforeunload both ask. */
  let saving: AbortController | null = null;

  /**
   * Recent progress, for a rate.
   *
   * <p>Over a window rather than since the start: a download that spent its first minute stalled and
   * has been at full speed since would report the average of the two for ever, which is the figure
   * least useful to somebody deciding whether to wait. The same window the upload queue uses, and
   * for the same reason.</p>
   */
  let samples: { at: number; bytes: number }[] = [];

  /**
   * Holds the screen on while a save runs.
   *
   * <p>A phone that dims and then sleeps suspends the page, and a suspended page is a download that
   * has stopped. This is the only thing a page can do about that, and it is half a measure by
   * construction — the browser revokes the lock the moment the app is backgrounded, which is exactly
   * the case it cannot help with. It covers the other one: a film downloading while the customer
   * watches it happen, on a phone that would otherwise sleep in thirty seconds.</p>
   */
  const screen = createScreenLock(() => saving !== null);

  if (!media || !contentUrl) return { stop: () => {} };

  // The header is present only for a locked file, and its presence is what decides which of the two
  // paths this element is on.
  let header: EncryptionHeader | null = null;

  if (el.dataset.playerHeader) {
    try {
      header = JSON.parse(el.dataset.playerHeader) as EncryptionHeader;
    } catch {
      header = null;
    }
  }

  // Revealed rather than server-rendered visible. Playing needs script for the locked case and, for
  // the unlocked one, a control that does nothing without a bundle is worse than no control: the
  // reader presses it and concludes their file is broken.
  if (header ? form : start) (header ? form! : start!).hidden = false;

  /**
   * Where this film was stopped last time, and the machinery for putting it back there.
   *
   * <p>The thresholds and the storage are in <c>watchPosition.ts</c> and are tested there. What is
   * here is the hard half: a media element has four different ways of acquiring a source on this
   * page — a copy off the disk, a plain byte route, a service-worker stream, and a swap from the
   * second to the first when a save finishes — and a seek has to happen after whichever of them
   * happened, exactly once, and only when the source can actually be seeked.</p>
   *
   * <p>So none of the four paths is touched. Everything below hangs off the element's own events,
   * which is the one thing all four have in common: <c>loadstart</c> means a new source, and it is
   * what re-arms this after a save swaps a stream for a file. Adding the seek to each path instead
   * would have been four places to get it right and four places for the next path to forget.</p>
   */
  const position = createRecorder(contentUrl);

  /**
   * False until the restore for the current source has been decided.
   *
   * <p><b>This is what stops the feature erasing itself.</b> Setting <c>currentTime</c> does not
   * take effect at once — the element seeks and reports back — and a <c>timeupdate</c> in between
   * carries a time of nearly zero. Recorded, that is «near the start», which the module clears. The
   * first visit would write a position and the second would delete it on arrival, for ever, and the
   * feature would look like it had simply never worked.</p>
   */
  let recording = false;

  /** True once the restore has been decided, so the two triggers below do not decide it twice. */
  let settled = false;

  /**
   * Which source is loading, counted up on every <c>loadstart</c>.
   *
   * <p>The deferred <c>seeked</c> handler below belongs to one source. Without this, a source
   * swapped out while that handler was still pending would have it fire against the next one and
   * start recording before that one had been restored — which is the paragraph above happening on a
   * different path.</p>
   */
  let generation = 0;

  /** NaN until the element has measured the file, and every decision here needs a real number. */
  function lengthOf(): number {
    return Number.isFinite(media!.duration) ? media!.duration : 0;
  }

  function showResumed(at: number): void {
    if (resumeAt) resumeAt.textContent = timecode(at);
    if (resume) resume.hidden = false;
    if (resumeNote) resumeNote.hidden = false;
  }

  /** Hidden together with the record it describes, so the page never outlives what it is about. */
  function hideResumed(): void {
    if (resume) resume.hidden = true;
    if (resumeNote) resumeNote.hidden = true;
  }

  /**
   * Puts the player back where it was, at most once per source.
   *
   * <p><b>Not during a save.</b> A seek is not free: on the locked path it makes the service worker
   * fetch a fresh range of the same file that the save is already pulling down, over the same
   * connection, and the two then take turns. It costs nothing to wait — a finished save swaps the
   * element onto the copy on disk, which is a fresh <c>loadstart</c>, and this runs then against a
   * source that seeks at the speed of the disk.</p>
   *
   * <p><b>Not before it is seekable.</b> <c>seekable</c> is empty until the element knows the source
   * answers ranges, and assigning <c>currentTime</c> before then is a seek the browser is entitled
   * to drop — leaving the reader at the beginning and a sentence above the film claiming otherwise.
   * <c>loadedmetadata</c> is where it is populated for all four sources; <c>canplay</c> is the
   * second chance and the last one, because a source that can play and still will not seek is a
   * source that never will.</p>
   */
  function restorePosition(lastChance: boolean): void {
    if (settled || saving !== null) return;

    const length = lengthOf();

    if (length <= 0) return;

    const at = positionFor(contentUrl, length);

    if (at <= 0) {
      // Nothing remembered, or what was remembered is the end of the film. Either way this source
      // starts where it is, and recording has to begin now or the visit is not remembered at all.
      settled = true;
      recording = true;
      return;
    }

    const ranges = media!.seekable;

    if (ranges.length === 0 || at >= ranges.end(ranges.length - 1)) {
      if (!lastChance) return;

      settled = true;
      recording = true;
      return;
    }

    settled = true;
    generation++;

    const mine = generation;

    media!.addEventListener('seeked', () => {
      if (mine === generation) recording = true;
    }, { once: true });

    media!.currentTime = at;
    showResumed(at);
  }

  const onLoadStart = () => {
    settled = false;
    recording = false;
    generation++;
    hideResumed();
  };

  const onMetadata = () => restorePosition(false);
  const onCanPlay = () => restorePosition(true);

  const onTimeUpdate = () => {
    if (!recording) return;

    // Throttled inside the module — this fires about four times a second for the length of the
    // film, and what reaches storage is one write every ten seconds. See WriteEvery there.
    if (position.record(media!.currentTime, lengthOf()) === 'cleared') hideResumed();
  };

  /**
   * The unthrottled write, for the moments where the next event may never arrive.
   *
   * <p>Pausing, reaching the end, the tab being hidden, the page being torn down. The last two are
   * the ones that matter on a phone: iOS discards a backgrounded web app without running anything
   * of ours afterwards, so <c>visibilitychange</c> is the last instruction this page is certain to
   * receive and the position has to be on the disk by the end of it.</p>
   */
  const rememberNow = () => {
    if (!recording) return;

    if (position.flush(media!.currentTime, lengthOf()) === 'cleared') hideResumed();
  };

  media.addEventListener('loadstart', onLoadStart);
  media.addEventListener('loadedmetadata', onMetadata);
  media.addEventListener('canplay', onCanPlay);
  media.addEventListener('timeupdate', onTimeUpdate);
  media.addEventListener('pause', rememberNow);
  media.addEventListener('ended', rememberNow);

  // `pagehide` and not `beforeunload`: iOS fires the first reliably and the second hardly at all,
  // and a phone is where losing the last ten seconds of a two-hour film is most annoying.
  addEventListener('pagehide', rememberNow);

  startOver?.addEventListener('click', () => {
    // Forgotten before the seek, not after. Going back to the beginning writes a time of nearly
    // zero, which the module reads as «has not started» and clears anyway — but only if it gets
    // there first, and the order should not be the thing this depends on.
    position.forget();
    hideResumed();

    if (media.seekable.length > 0) media.currentTime = 0;

    void media.play().catch(() => {
      // Autoplay refused. The element has controls and the reader is already looking at them.
    });
  });

  /**
   * The saved copy first, before anything is offered and before anything is fetched.
   *
   * <p>This is what the whole feature is for. A film on the disk plays from a blob URL, which the
   * browser range-serves itself: no server, no service worker, no segment decryption per seek, and
   * therefore none of the stalling that made watching a long film unusable.</p>
   *
   * <p>For a locked file it also means no passphrase, because what was kept is the decrypted copy.
   * That is the trade this product's owner chose and the button that makes it says so — see
   * UiText.Offline.KeptOpensWithoutTheKey.</p>
   */
  void showSavedCopy();

  async function showSavedCopy(): Promise<boolean> {
    const entry = (await savedList()).find((e) => e.key === contentUrl);

    // Unfinished. It is emphatically not played: half a film in a player is a film that stops in
    // the middle having said nothing was wrong, which is the failure this whole feature is against.
    // What it gets instead is the same button, saying Continue and showing how far it got.
    if (entry?.partial) {
      if (keep && supported() && !header) keep.hidden = false;
      if (inBackground && canBackground() && !header) inBackground.hidden = false;
      if (forget) forget.hidden = false;

      keep?.setAttribute('data-resuming', 'true');
      if (keep) keep.textContent = el.dataset.playerContinue ?? '';

      showProgress(entry.written);
      if (progress) progress.hidden = false;

      return false;
    }

    const file = entry ? await openSaved(contentUrl) : null;

    if (!file) {
      // Nothing kept, so offer to keep it — but only where there is somewhere to put it, and for a
      // locked file only once there is a key. Offering it earlier would be a button whose whole
      // behaviour on first press is to say «unlock it first».
      if (keep && supported() && !header) keep.hidden = false;
      if (inBackground && canBackground() && !header) inBackground.hidden = false;
      return false;
    }

    if (form) form.hidden = true;
    if (start) start.hidden = true;
    if (keep) keep.hidden = true;
    if (progress) progress.hidden = true;
    if (forget) forget.hidden = false;
    if (kept) kept.hidden = false;

    objectUrl = URL.createObjectURL(file);
    media!.src = objectUrl;
    media!.hidden = false;

    return true;
  }

  keep?.addEventListener('click', () => void keepOnDevice());
  inBackground?.addEventListener('click', () => void handToBrowser());
  stop?.addEventListener('click', () => saving?.abort());
  forget?.addEventListener('click', () => void forgetFromDevice());

  async function forgetFromDevice(): Promise<void> {
    await removeSaved(contentUrl);

    // A reload rather than an unwind. Taking the copy away puts this page back to the state it has
    // on a first visit — gate or play button, nothing playing — and reproducing that by hand here
    // is three branches that would each be wrong once.
    location.reload();
  }

  /**
   * Keeps a copy, refusing before it fetches anything if it will not fit.
   *
   * <p>A locked file needs its content key, which only exists after the passphrase — so this is
   * offered on the gate as well, and takes the key from whichever unlock has already happened.</p>
   */
  async function keepOnDevice(): Promise<void> {
    if (!keep) return;

    const space = await room();

    if (!supported()) {
      say(el.dataset.playerCannotKeep ?? '');
      return;
    }

    if (sizeBytes > 0 && sizeBytes + 64 * 1024 * 1024 > space.free) {
      // Both figures. «Not enough space» leaves the reader unable to tell «go and clear something»
      // from «this will never fit on this phone».
      say(`${el.dataset.playerNoRoom ?? ''} `
        + `${el.dataset.playerNeeds ?? ''} ${formatBytes(sizeBytes)}, `
        + `${formatBytes(space.free)} ${el.dataset.playerFree ?? ''}`);
      return;
    }

    // A locked file cannot be written to disk before it is opened, so the gate comes first and this
    // picks up afterwards with the key it produced.
    if (header && !contentKey) {
      say(el.dataset.playerNeedsUnlock ?? '');
      return;
    }

    keep.disabled = true;
    keep.textContent = el.dataset.playerKeeping ?? '';

    // The bar, the figures and the way out, all three at once: a number that cannot be stopped is
    // only half an answer to «how long is this going to take».
    if (progress) progress.hidden = false;
    if (stop) stop.hidden = false;

    saving = new AbortController();
    samples = [];
    showProgress(0);
    void screen.take();

    try {
      await save(
        {
          key: contentUrl,
          name: title,
          type: mime,
          bytes: sizeBytes,
          // Stamped by the caller: the library holds no clock, so a test of it needs no fake one.
          savedAt: Date.now(),
          watchUrl,
          written: 0,
        },
        async (write, from) => {
          // A background download already fetched this, so the bytes are on the disk and the only
          // thing left is the half the worker could not do. No network, no range, no meter: this is
          // a decrypt pass over a local file, which on a six-gigabyte film is minutes rather than
          // the tens of minutes the download took.
          const staged = await stagedBytes(contentUrl);

          if (staged) {
            if (!header || !contentKey) {
              const reader = staged.stream().getReader();

              for (;;) {
                const next = await reader.read();
                if (next.done) break;
                await write(next.value as Bytes);
              }

              return;
            }

            const done = await decryptInto(staged.stream(), contentKey, header, write);

            if (!done.ok) throw new Error(done.reason);
            return;
          }

          // Where in the *stored* bytes to start. For an unlocked file that is the plaintext offset
          // itself; for a locked one it is the ciphertext offset of the segment the plaintext offset
          // begins at, which is arithmetic and not a search — see crypto/format.ts.
          const segment = header ? Math.floor(from / header.segmentSize) : 0;
          const at = header
            ? segmentSpan(segment, header.plaintextLength, header.segmentSize).start
            : from;

          const response = await fetch(contentUrl, {
            credentials: 'same-origin',
            // Absent rather than `bytes=0-` when starting from the beginning: a plain GET is a
            // request every server answers the same way, and a range is not.
            headers: at > 0 ? { Range: `bytes=${at}-` } : {},
          });

          if (response.redirected) throw new Error('signed out');
          if (!response.ok || !response.body) throw new Error('unreadable');

          // A server that ignored the range and sent the whole file would have this write the film
          // from its start on top of the part already there. 206 is the only answer that means the
          // body begins where it was asked to.
          if (at > 0 && response.status !== 206) throw new Error('no-resume');

          if (!header || !contentKey) {
            const reader = response.body.getReader();

            for (;;) {
              const next = await reader.read();
              if (next.done) break;

              // A fetch body is never backed by a SharedArrayBuffer, which is the only thing the
              // narrower Bytes rules out. See crypto/format.ts, where that alias is named.
              await write(next.value as Bytes);
            }

            return;
          }

          // Verified a segment at a time and written as it goes, so a four-gigabyte film costs a
          // megabyte of memory rather than four gigabytes of it. Nothing unverified is ever written.
          const result = await decryptInto(
            response.body, contentKey, header, write, undefined, segment);

          if (!result.ok) throw new Error(result.reason);
        },
        { onProgress: showProgress, signal: saving.signal },
      );
    } catch (error) {
      const stopped = saving?.signal.aborted === true;

      endProgress();
      keep.disabled = false;
      keep.textContent = el.dataset.playerKeepText ?? '';

      // Two different sentences, because they are two different situations: one is the reader's own
      // decision and needs no apology, and the other is a failure they may want to try again after.
      say(stopped
        ? el.dataset.playerKeepStopped ?? ''
        : el.dataset.playerKeepFailed ?? '');

      // What is on the disk is worth carrying on from, so the button says so. Reading the record
      // rather than assuming: a failure before the first checkpoint leaves nothing to resume.
      const unfinished = (await savedList()).find((e) => e.key === contentUrl && e.partial);

      if (unfinished) {
        keep.textContent = el.dataset.playerContinue ?? '';
        keep.setAttribute('data-resuming', 'true');
        showProgress(unfinished.written);

        if (progress) progress.hidden = false;
        if (forget) forget.hidden = false;
      }

      void error;
      return;
    }

    endProgress();
    clearResuming();
    keep.disabled = false;

    // Straight onto the saved copy, so the thing that plays from here on is the one on the disk.
    await showSavedCopy();
    say('');
  }

  function say(message: string): void {
    if (said) said.textContent = message;
  }

  /**
   * The bar and the two figures.
   *
   * <p>Both figures, and this is the point of the whole change: a percentage alone does not tell
   * somebody whether the next fifteen minutes are worth waiting through, and «412 MB» alone does not
   * say how much of the film that is. The size is known before a byte is fetched, so both are
   * available from the first tick.</p>
   */
  function showProgress(written: number): void {
    const percent = sizeBytes > 0 ? Math.min(100, (written / sizeBytes) * 100) : 0;

    if (barFill) barFill.style.inlineSize = `${percent}%`;
    if (bar) bar.setAttribute('aria-valuenow', String(Math.round(percent)));
    if (percentOut) percentOut.textContent = `${Math.round(percent)}%`;

    if (soFarOut) {
      soFarOut.textContent = sizeBytes > 0
        ? `${formatBytes(written)} / ${formatBytes(sizeBytes)}`
        : formatBytes(written);
    }

    const rate = speedFrom(written);

    // Both, or neither is much use. A rate says whether the connection is working; a time says
    // whether to wait for it — and «14 MB/s» on a six-gigabyte film still leaves arithmetic to do.
    if (speedOut) speedOut.textContent = rate > 0 ? `${formatBytes(rate)}/s` : '';

    if (leftOut) {
      const remaining = sizeBytes > written && rate > 0 ? (sizeBytes - written) / rate : 0;

      leftOut.textContent = remaining > 0 ? duration(remaining) : '';
    }
  }

  /** Bytes per second over the recent window, or zero until there is enough to divide by. */
  function speedFrom(written: number): number {
    const now = performance.now();

    samples.push({ at: now, bytes: written });

    while (samples.length > 2 && now - samples[0].at > 5000) samples.shift();

    const first = samples[0];
    const span = (now - first.at) / 1000;

    // Under a fifth of a second is one checkpoint's worth of noise, not a rate.
    return span > 0.2 ? Math.max(0, (written - first.bytes) / span) : 0;
  }

  function endProgress(): void {
    saving = null;
    screen.release();

    if (progress) progress.hidden = true;
    if (stop) stop.hidden = true;
  }

  /**
   * Carrying on by itself when the app comes back.
   *
   * <p><b>This is the answer to a phone, and it is the only one there is.</b> iOS suspends a web app
   * the moment it is backgrounded or the screen locks; no web API keeps a download running through
   * that, and none will. What can be fixed is the other half — that coming back left the customer
   * looking at a stopped bar with a button to press. It resumes from the last checkpoint, which is
   * the same machinery the Continue button uses.</p>
   *
   * <p>Only where it can actually finish. A locked film needs its content key, and the key lives in
   * this page's memory: it survives being backgrounded and does not survive a reload. So a phone that
   * was merely locked resumes on its own, and one that dropped the tab asks for the passphrase again
   * — which is the truth of where the key is rather than a limitation of this function.</p>
   *
   * <p>`visibilitychange` and not a timer, because it is the one signal WebKit gives for «the app is
   * in front again». `online` too, for the tunnel.</p>
   */
  async function resumeIfInterrupted(): Promise<void> {
    if (saving !== null || !supported()) return;
    if (document.visibilityState !== 'visible') return;

    // A locked film with no key in hand cannot be finished without asking, and asking is what the
    // gate is for. Resuming into a failure would replace a Continue button with an error.
    if (header && !contentKey) return;

    const unfinished = (await savedList()).find((e) => e.key === contentUrl && e.partial);
    if (!unfinished) return;

    // Somebody pressed Stop. Starting it again because they came back to the app would be doing the
    // opposite of what they asked, on their mobile data. Continue is still there for when they mean
    // it — see SavedFile.stoppedByHand.
    if (unfinished.stoppedByHand) return;

    await keepOnDevice();
  }

  const onWake = () => {
    // Going away is the half of this event nothing used to listen for. On iOS it is the last thing
    // this page is told before the app may be discarded outright, so the position is written here
    // rather than hoped for later; `resumeIfInterrupted` returns immediately when hidden anyway.
    if (document.visibilityState === 'hidden') rememberNow();

    void resumeIfInterrupted();
  };

  document.addEventListener('visibilitychange', onWake);
  addEventListener('online', onWake);

  /**
   * Hands the download to the browser, so it survives this page being closed.
   *
   * <p>Offered only where it exists, which is Chromium. On the platform this was most asked for — an
   * iPhone — there is no such API and the control is not drawn, because a button that silently did
   * nothing would be worse than its absence.</p>
   *
   * <p>It stops the in-page save first. Two downloads of one film into one file is the one way to
   * corrupt it, and the browser's copy is the one that survives.</p>
   */
  async function handToBrowser(): Promise<void> {
    if (!inBackground) return;

    saving?.abort();

    const handed = await startBackground({
      key: contentUrl,
      name: title,
      type: mime,
      bytes: sizeBytes,
      savedAt: Date.now(),
      watchUrl,
      written: 0,
    });

    say(handed
      ? el.dataset.playerHandedOver ?? ''
      : el.dataset.playerKeepFailed ?? '');

    if (handed) inBackground.hidden = true;
  }

  /** Back to Save, for when a finished copy means there is nothing left to carry on. */
  function clearResuming(): void {
    keep?.removeAttribute('data-resuming');
    if (keep) keep.textContent = el.dataset.playerKeepText ?? '';
  }

  /**
   * The browser's own warning, for the one case this page cannot handle itself.
   *
   * <p>A save is this page's work and dies with it. There is no resuming it — the bytes written so
   * far are swept on the next visit, deliberately, because a film that is 40% there is not a shorter
   * film. So the honest thing is to make leaving take a decision rather than happen by accident.</p>
   *
   * <p>Registered once and asked per event rather than added and removed around each save: a
   * listener that is added on one path and removed on another is the kind that survives one of
   * them.</p>
   */
  const warnOnLeaving = (event: BeforeUnloadEvent) => {
    if (!saving || saving.signal.aborted) return;

    event.preventDefault();

    // The deprecated half, and still the one Safari and Firefox act on. See main.ts.
    event.returnValue = el.dataset.playerLeavingStops ?? '';
  };

  addEventListener('beforeunload', warnOnLeaving);

  start?.addEventListener('click', () => {
    // Nothing is fetched until this is pressed. A detail panel that started loading a film every
    // time somebody clicked a row would spend the workspace's traffic on curiosity.
    media.src = contentUrl;
    media.hidden = false;
    start.hidden = true;
    void media.play().catch(() => {
      // Autoplay refused, which is a browser being reasonable. The element has controls.
    });
  });

  form?.addEventListener('submit', (event) => {
    event.preventDefault();
    void unlock();
  });

  async function unlock(): Promise<void> {
    if (!secret || !said || !header) return;

    const typed = secret.value;
    if (typed.length === 0) return;

    said.textContent = el.dataset.playerWorking ?? '';

    const key = await unseal({ kind: 'passphrase', value: typed }, header);

    // Cleared whichever way this goes: it has been used, and a passphrase left in an input is one
    // left in the page and in the browser's form restoration.
    secret.value = '';

    if (!key) {
      said.textContent = el.dataset.playerWrongKey ?? '';
      return;
    }

    // Kept so «save on this device» does not ask for the passphrase a second time. It is in memory
    // for as long as this element is, which is the same lifetime the stream's copy has.
    contentKey = key;

    // Now that there is a key, keeping a copy is possible. The control is hidden until here for a
    // locked file rather than shown and refusing, which would be a button whose only behaviour on
    // first press is to tell you to do something else first.
    //
    // showSavedCopy ran before the passphrase existed and could not offer anything for a locked
    // file, so this is also where a half-finished one gets its Continue.
    if (inBackground && canBackground()) inBackground.hidden = false;

    if (keep && supported()) {
      keep.hidden = false;

      const unfinished = (await savedList()).find((e) => e.key === contentUrl && e.partial);

      if (unfinished) {
        keep.textContent = el.dataset.playerContinue ?? '';
        keep.setAttribute('data-resuming', 'true');
        showProgress(unfinished.written);

        if (progress) progress.hidden = false;
        if (forget) forget.hidden = false;
      }
    }

    streamId = crypto.randomUUID();

    const url = await openStream(streamId, {
      header: header!,
      key,

      // The panel's own byte route. The worker reads ciphertext from it a segment at a time, so a
      // seek costs the part that was watched rather than the film.
      source: contentUrl,
      type: mime,
    });

    if (!url) {
      // No service worker — a private window, an insecure context, a browser without one. There is
      // no way to answer a media element's range requests without it, so this says so rather than
      // pointing the element at ciphertext it would report as a corrupt file.
      streamId = '';
      said.textContent = el.dataset.playerNoWorker ?? '';
      return;
    }

    said.textContent = '';
    form!.hidden = true;
    media!.src = url;
    media!.hidden = false;

    void media!.play().catch(() => {});
  }

  return {
    stop: () => {
      // First, and before anything below touches the element. On the panel this teardown is what
      // runs when the reader clicks away mid-film — the commonest way of all to leave a film — and
      // three lines further down `currentTime` is zero and `duration` is NaN, so there would be
      // nothing left to write.
      rememberNow();

      media.removeEventListener('loadstart', onLoadStart);
      media.removeEventListener('loadedmetadata', onMetadata);
      media.removeEventListener('canplay', onCanPlay);
      media.removeEventListener('timeupdate', onTimeUpdate);
      media.removeEventListener('pause', rememberNow);
      media.removeEventListener('ended', rememberNow);
      removeEventListener('pagehide', rememberNow);

      // The panel is inside the region a navigation replaces, so this runs on every link the reader
      // presses. Letting the element go on holding a stream would leave the key registered for a
      // file nobody is watching — and the rule this feature is built on is that a decrypted file
      // exists for exactly as long as somebody is looking at it.
      media.pause();
      media.removeAttribute('src');
      media.load();

      // A blob URL holds its File alive for as long as the document does, whatever the element is
      // pointed at afterwards. On the panel this teardown runs on every link the reader presses, so
      // not revoking would be a film's worth of memory leaked per navigation.
      if (objectUrl) {
        URL.revokeObjectURL(objectUrl);
        objectUrl = '';
      }

      contentKey = null;

      // A save is this page's work and does not outlive it. Aborting here rather than leaving it
      // running against a torn-down element is what makes the partial file get swept: the library
      // removes what it wrote when the write throws.
      saving?.abort();
      screen.release();

      removeEventListener('beforeunload', warnOnLeaving);
      document.removeEventListener('visibilitychange', onWake);
      removeEventListener('online', onWake);

      if (streamId) closeStream(streamId);
    },
  };
}
