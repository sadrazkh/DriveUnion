import { seal } from './crypto/envelope';
import { toBase64, type Bytes } from './crypto/format';

/**
 * Locking a file that is already stored, from the file detail panel.
 *
 * <p><b>The passphrase never leaves this function.</b> It derives a wrapping key from it, seals a
 * fresh content key under that, and posts the header plus the content key — so the server receives a
 * key to this one file, which it is about to read anyway, and nothing that would open any other.
 * That is the difference between this and the link-upload path, which sends what the customer typed:
 * a customer uses one secret for everything, and a server that has seen it once could open every
 * file they ever locked in their own browser.</p>
 *
 * <p>There is no no-script version, and that is the honest answer rather than a gap. A form that
 * posted a passphrase would be the weaker protocol wearing this one's clothes; without a bundle the
 * button is simply not drawn, and the file can still be locked by downloading it and uploading it
 * again with the box ticked, which is what everybody did until now.</p>
 */
export function mountLockFile(el: HTMLElement): void {
  const form = el.querySelector<HTMLFormElement>('[data-lock-form]');
  const secret = el.querySelector<HTMLInputElement>('[data-lock-secret]');
  const button = el.querySelector<HTMLButtonElement>('[data-lock-submit]');
  const said = el.querySelector<HTMLElement>('[data-lock-said]');

  if (!form || !secret || !button || !said) return;

  const url = el.dataset.lockUrl;
  const token = el.dataset.lockToken;
  const length = Number(el.dataset.lockLength ?? '0');

  if (!url || !token) return;

  // Server-rendered hidden, revealed only once this is running. A card that asks for a passphrase
  // and cannot do anything with it is worse than no card: the reader types their secret into a
  // page that then does nothing with it, which is exactly the shape of a phishing form.
  form.hidden = false;

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    void run();
  });

  async function run(): Promise<void> {
    const typed = secret!.value;

    if (typed.length === 0) return;

    button!.disabled = true;
    said!.textContent = el.dataset.lockWorking ?? '';

    try {
      // 600,000 PBKDF2 rounds, which is between half a second and a second of blocked CPU. The
      // button is already disabled and saying so, because a tab that stops responding with no
      // explanation is one somebody reloads halfway through.
      const { header, key } = await seal({ kind: 'passphrase', value: typed }, length);

      // Exportable only so this line can read it — see `sealWith`, which marks it so for exactly
      // this reason. What is posted is the key to one file; the passphrase stays here.
      const raw = new Uint8Array(await crypto.subtle.exportKey('raw', key)) as Bytes;

      const body = new URLSearchParams({
        __RequestVerificationToken: token!,
        'header.Scheme': String(header.scheme),
        'header.SegmentSize': String(header.segmentSize),
        'header.NoncePrefix': header.noncePrefix,
        'header.PlaintextLength': String(header.plaintextLength),
        'header.KdfSalt': header.kdfSalt,
        'header.KdfIterations': String(header.kdfIterations),
        'header.WrappedKey': header.wrappedKey,
        key: toBase64(raw),
      });

      const response = await fetch(url!, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
        body,
      });

      // Cleared whatever happened. It has been used, it is not needed again, and leaving it in a
      // field is leaving it in the page's memory and in the browser's form restoration.
      secret!.value = '';

      const answer = (await response.json()) as { started: boolean; error?: string | null };

      if (!answer.started) {
        said!.textContent = answer.error ?? el.dataset.lockFailed ?? '';
        button!.disabled = false;
        return;
      }

      said!.textContent = el.dataset.lockQueued ?? '';

      // The row on the screen is server-rendered and this page is now out of date about it. A
      // reload is the honest way to show the queue rather than a second copy of its rendering here.
      window.setTimeout(() => window.location.reload(), 1200);
    } catch {
      secret!.value = '';
      said!.textContent = el.dataset.lockFailed ?? '';
      button!.disabled = false;
    }
  }
}
