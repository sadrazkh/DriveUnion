import { unseal } from './crypto/envelope';
import { decryptInto } from './crypto/stream';
import { segmentSpan, type Bytes, type EncryptionHeader } from './crypto/format';
import { closeStream, openStream } from './crypto/play';
import { list as savedList, open as openSaved, remove as removeSaved, room, save, supported } from './offline/library';
import { bytes as formatBytes } from './uploads/store';

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

  const stop = el.querySelector<HTMLButtonElement>('[data-player-stop]');
  const progress = el.querySelector<HTMLElement>('[data-player-progress]');
  const bar = el.querySelector<HTMLElement>('[data-player-bar]');
  const barFill = el.querySelector<HTMLElement>('[data-player-bar-fill]');
  const percentOut = el.querySelector<HTMLElement>('[data-player-percent]');
  const soFarOut = el.querySelector<HTMLElement>('[data-player-sofar]');

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
    showProgress(0);

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
  }

  function endProgress(): void {
    saving = null;

    if (progress) progress.hidden = true;
    if (stop) stop.hidden = true;
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
      removeEventListener('beforeunload', warnOnLeaving);

      if (streamId) closeStream(streamId);
    },
  };
}
