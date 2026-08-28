import type { EncryptionHeader } from './format';
import { serviceWorkerReady } from '../serviceWorker';

/**
 * Handing a decrypted file to a media element, without decrypting it first.
 *
 * <p>The page owns the key and the worker does the work. This module is the join: it registers a
 * stream with the worker, hands back a URL that a <c>&lt;video&gt;</c> can be pointed at, and stays
 * around to answer the worker when it forgets — which it does constantly, because a Service Worker
 * is terminated between requests as a matter of course.</p>
 *
 * <p><b>The key travels as a <c>CryptoKey</c>, not as bytes.</b> <c>unseal</c> produces a
 * non-extractable key and structured clone carries it as one, so the worker receives something it
 * can decrypt with and cannot read out. There is deliberately no path in this file that turns a key
 * into a <c>Uint8Array</c>; the moment one existed, the key would be a thing that could be written
 * down somewhere.</p>
 */

/** What the worker needs to answer for one file. */
interface Stream {
  readonly header: EncryptionHeader;
  readonly key: CryptoKey;

  /** Where the ciphertext is — the ordinary public download address. */
  readonly source: string;

  /** What the element should think it is playing. */
  readonly type: string;
}

/**
 * Every stream this page has opened, so a recall can be answered.
 *
 * <p>It is the page and not the worker that is the durable half here, which is the opposite of the
 * usual arrangement and is the whole reason it works: the page lives exactly as long as the reader
 * is looking at the video, and the worker does not.</p>
 */
const opened = new Map<string, Stream>();

let listening = false;

/**
 * Makes a URL that plays <paramref name="stream"/>, or null where the browser cannot.
 *
 * <p>Null rather than a thrown error, and rather than a URL that will not work: the caller's job is
 * to offer a player only when there is one, and every reason this fails — no service worker, an
 * insecure context, a private window — is a browser doing something reasonable rather than a fault.</p>
 */
export async function openStream(id: string, stream: Stream): Promise<string | null> {
  const registration = await serviceWorkerReady();

  // No worker means no interception, and a URL under /du1/ would reach the server and 404. The
  // caller falls back to unlock-and-download, which is what every reader had before this existed.
  if (!registration) return null;

  const worker = registration.active ?? navigator.serviceWorker.controller;
  if (!worker) return null;

  listen();
  opened.set(id, stream);

  // Waited for rather than fired and forgotten. A media element pointed at the URL immediately can
  // out-race the message: the request arrives at a worker that has not processed it, takes the
  // recall path, and asks this page for something it has already sent. That works, and it is a
  // round trip and a three-second timeout in the one case that should be instant.
  const handed = await new Promise<boolean>((resolve) => {
    const channel = new MessageChannel();
    const timer = self.setTimeout(() => resolve(false), 3000);

    channel.port1.onmessage = () => {
      self.clearTimeout(timer);
      resolve(true);
    };

    worker.postMessage(
      { du1: 'open', id, header: stream.header, key: stream.key, source: stream.source, type: stream.type },
      [channel.port2],
    );
  });

  if (!handed) {
    opened.delete(id);
    return null;
  }

  return `/du1/${id}`;
}

/**
 * Forgets a stream, in the worker and here.
 *
 * <p>Called when the player is taken off the screen. Not merely tidiness: the stream holds the key
 * for a file the reader has stopped looking at, and the rule this feature is built on is that a
 * decrypted file exists for exactly as long as somebody is watching it.</p>
 */
export function closeStream(id: string): void {
  opened.delete(id);

  navigator.serviceWorker?.controller?.postMessage({ du1: 'close', id });
}

/**
 * Answers the worker when it asks for a stream it has lost.
 *
 * <p>Registered once, lazily. A page with no locked media on it should not be holding a listener for
 * a message it can never receive.</p>
 */
function listen(): void {
  if (listening || !('serviceWorker' in navigator)) return;

  listening = true;

  navigator.serviceWorker.addEventListener('message', (event: MessageEvent) => {
    const data = event.data as { du1?: string; id?: string } | null;
    if (!data || data.du1 !== 'recall' || typeof data.id !== 'string') return;

    const port = event.ports[0];
    if (!port) return;

    // Undefined for a stream this page never opened, which the worker reads as "no" and answers
    // with a 404. That is the right answer: the alternative is one page's key being handed out for
    // another page's file, and this is the only place that could happen.
    port.postMessage({ stream: opened.get(data.id) });
  });
}
