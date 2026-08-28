import { unseal } from './crypto/envelope';
import type { EncryptionHeader } from './crypto/format';
import { closeStream, openStream } from './crypto/play';

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

  const contentUrl = el.dataset.playerUrl ?? '';
  const mime = el.dataset.playerMime ?? '';

  let streamId = '';

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

      if (streamId) closeStream(streamId);
    },
  };
}
