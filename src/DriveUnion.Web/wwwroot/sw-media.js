/*
 * Playing an encrypted file without downloading it first.
 *
 * ── What this is for ─────────────────────────────────────────────────────────────────────────────
 *
 * A file locked in the browser is `du1`: AES-256-GCM over one-mebibyte segments. Until now the only
 * way to open one was UnlockDownload.vue, which fetches the whole thing, decrypts it into a Blob and
 * hands the Blob to the reader. That is right for a document and wrong for a film: a two-hour video
 * is a two-hour wait staring at a progress bar, on a phone it is a two-hour wait that may not fit in
 * memory, and there is no way to skip to the middle because there is no middle until the end has
 * arrived.
 *
 * `du1` was built for this even though nothing used it yet. Segment `i` sits at exactly
 * `i * (segmentSize + 16)` in the ciphertext and authenticates its own index, so a plaintext byte
 * range maps to a contiguous ciphertext range with arithmetic and no scanning. This file is that
 * arithmetic, put behind a URL a <video> element can be pointed at.
 *
 * ── Why it lives in the worker ───────────────────────────────────────────────────────────────────
 *
 * A media element will not read from a page. It issues its own requests, with its own Range headers,
 * and expects 206s back — none of which a page can answer. A Service Worker can, and it is the only
 * thing in a browser that can: it sits where the network is, so from the element's point of view the
 * decrypted file is simply a URL that behaves like any other.
 *
 * ── The key, and where it is not ─────────────────────────────────────────────────────────────────
 *
 * The page derives the content key and posts it here as a `CryptoKey`. That matters more than it
 * looks: `unseal` produces a non-extractable key, structured clone carries it as one, and this
 * worker therefore holds something it can decrypt with and cannot read out. No raw key material ever
 * exists on this side, so there is nothing here to leak into storage even by accident.
 *
 * Streams live in a Map in memory and are written nowhere. A worker is terminated whenever the
 * browser feels like it — constantly, on iOS — and that Map goes with it, so `recall()` below asks
 * the page for the stream again rather than persisting anything. The whole point of this product is
 * that the server holds no readable copy; a phone quietly keeping content keys in IndexedDB would be
 * that claim with an exception in it, and the exception would be the interesting half.
 */

'use strict';

/*
 * The `du1` format, restated.
 *
 * This is the third implementation of these constants — Scripts/crypto/format.ts is the first and
 * DriveUnion.Core's Du1.cs is the second — and a third copy of a wire format is a third chance for
 * one of them to drift. It is a copy because it has to be: a classic worker script cannot import a
 * TypeScript module, and the alternative was making the worker a bundle, which M5 refused for
 * reasons written at the top of sw.js.
 *
 * Scripts/swMedia.test.ts evaluates this file and checks every one of these against format.ts's own,
 * over the same indices, so a change to one that is not made to the other fails rather than
 * producing a file that decrypts to noise.
 */
const SegmentSize = 1024 * 1024;
const TagBytes = 16;
const NoncePrefixBytes = 8;

/** Where segment `index` starts in the ciphertext, and how long it is there. */
function segmentSpan(index, plaintextLength, segmentSize) {
  const start = index * (segmentSize + TagBytes);
  const plain = Math.min(segmentSize, Math.max(0, plaintextLength - index * segmentSize));

  return { start, length: plain + TagBytes };
}

/** Twelve bytes: the file's prefix, then the index, big-endian. */
function nonceFor(prefix, index) {
  const nonce = new Uint8Array(12);

  nonce.set(prefix.subarray(0, NoncePrefixBytes), 0);
  new DataView(nonce.buffer).setUint32(NoncePrefixBytes, index, false);

  return nonce;
}

/** "du1", the index, and whether this is the last segment — what a segment authenticates besides itself. */
function aadFor(index, isFinal) {
  const aad = new Uint8Array(8);

  aad.set([0x64, 0x75, 0x31], 0);
  new DataView(aad.buffer).setUint32(3, index, false);
  aad[7] = isFinal ? 1 : 0;

  return aad;
}

function fromBase64(value) {
  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);

  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);

  return bytes;
}

/**
 * The streams this worker is currently able to answer for.
 *
 * <p>Never persisted, and see the file header for why that is the point rather than an omission.</p>
 */
const Streams = new Map();

/**
 * Asks the page for a stream this worker has forgotten.
 *
 * <p>A worker is terminated between requests as a matter of course, and a media element does not
 * know that: it seeks, issues a range request, and would meet a 404 for a film it is in the middle
 * of. So the page keeps the stream and this borrows it back over a MessageChannel.</p>
 *
 * <p>Bounded by a timeout, because the page may be gone too — a client id outlives the page in some
 * browsers — and a media element waiting forever on a promise is a player that hangs with no error.</p>
 */
async function recall(id, clientId) {
  if (!clientId) return null;

  const client = await self.clients.get(clientId);
  if (!client) return null;

  return await new Promise((resolve) => {
    const channel = new MessageChannel();
    const timer = setTimeout(() => resolve(null), 3000);

    channel.port1.onmessage = (event) => {
      clearTimeout(timer);
      resolve(event.data && event.data.stream ? event.data.stream : null);
    };

    client.postMessage({ du1: 'recall', id }, [channel.port2]);
  });
}

/**
 * The decrypted bytes of one plaintext range, as they arrive.
 *
 * <p>A ReadableStream and not a Blob. The point of the whole feature is that playback starts before
 * the file has been read, and buffering the range first would reintroduce exactly the wait this
 * replaces — on a seek into a four-gigabyte film, it would also be four gigabytes.</p>
 *
 * <p>Memory is bounded to one segment: ciphertext is accumulated until a whole segment is present,
 * that segment is decrypted and pushed, and the buffer is dropped.</p>
 */
function decryptedBody(stream, from, to) {
  const { header, key } = stream;
  const segmentSize = header.segmentSize;
  const prefix = fromBase64(header.noncePrefix);
  const total = Math.ceil(header.plaintextLength / segmentSize);

  const first = Math.floor(from / segmentSize);
  const last = Math.floor(to / segmentSize);

  let index = first;
  let reader = null;
  let held = new Uint8Array(0);

  return new ReadableStream({
    async start() {
      const cipherFrom = segmentSpan(first, header.plaintextLength, segmentSize).start;
      const lastSpan = segmentSpan(last, header.plaintextLength, segmentSize);
      const cipherTo = lastSpan.start + lastSpan.length - 1;

      // The upstream range is the ciphertext one, which is not the plaintext one: every segment
      // carries sixteen bytes of tag that the reader never sees.
      const response = await fetch(stream.source, {
        headers: { Range: `bytes=${cipherFrom}-${cipherTo}` },

        // The public download path is anonymous and the link is the credential. Sending cookies
        // would make this request the signed-in owner's, which it is not — the reader may be a
        // stranger holding a link.
        credentials: 'omit',
      });

      if (!response.ok || !response.body) throw new Error('the ciphertext could not be read');

      reader = response.body.getReader();
    },

    async pull(controller) {
      if (index > last) {
        controller.close();
        return;
      }

      const span = segmentSpan(index, header.plaintextLength, segmentSize);

      // Gather exactly one segment's ciphertext before decrypting: AES-GCM verifies a tag over the
      // whole segment, so there is no such thing as half of one.
      while (held.length < span.length) {
        const { done, value } = await reader.read();

        if (done) {
          // The upstream ended inside a segment. Not an empty tail — a truncated file, and the
          // honest answer is to break the response so the element reports an error rather than
          // playing silence to the end.
          controller.error(new Error('the ciphertext ended early'));
          return;
        }

        const grown = new Uint8Array(held.length + value.length);
        grown.set(held, 0);
        grown.set(value, held.length);
        held = grown;
      }

      const sealed = held.subarray(0, span.length);
      held = held.subarray(span.length);

      let plain;
      try {
        plain = new Uint8Array(await crypto.subtle.decrypt(
          {
            name: 'AES-GCM',
            iv: nonceFor(prefix, index),
            additionalData: aadFor(index, index === total - 1),
          },
          key,
          sealed,
        ));
      } catch {
        // The wrong key, a flipped bit, or a segment moved to another index — one answer, because
        // they have one remedy. Erroring the stream is what makes the element say so.
        controller.error(new Error('a segment did not verify'));
        return;
      }

      // The requested range starts and ends inside a segment, so the first and last are trimmed to
      // it. Everything between them is whole.
      const segmentStart = index * segmentSize;
      const sliceFrom = index === first ? from - segmentStart : 0;
      const sliceTo = index === last ? to - segmentStart + 1 : plain.length;

      controller.enqueue(plain.subarray(sliceFrom, sliceTo));
      index++;
    },

    cancel() {
      // The element seeked somewhere else, or the page went away. Let go of the upstream rather
      // than leaving a Drive connection open for a range nobody is waiting for any more.
      if (reader) void reader.cancel();
    },
  });
}

/** `bytes=0-1023`, `bytes=500-`, `bytes=-200`, or null for anything this does not understand. */
function parseRange(value, length) {
  if (typeof value !== 'string') return null;

  const match = /^bytes=(\d*)-(\d*)$/.exec(value.trim());
  if (!match) return null;

  const [, rawFrom, rawTo] = match;

  if (rawFrom === '' && rawTo === '') return null;

  // A suffix range — the last N bytes. Media elements use it to read a trailing index.
  if (rawFrom === '') {
    const wanted = Number(rawTo);
    if (wanted === 0) return null;

    return { from: Math.max(0, length - wanted), to: length - 1 };
  }

  const from = Number(rawFrom);
  const to = rawTo === '' ? length - 1 : Math.min(Number(rawTo), length - 1);

  if (from > to || from >= length) return null;

  return { from, to };
}

self.du1Media = {
  /** The prefix sw.js routes on. */
  Prefix: '/du1/',

  claims(url) {
    return url.pathname.startsWith('/du1/');
  },

  /**
   * Answers a media element's request for decrypted bytes.
   *
   * <p>Always advertises byte ranges, because a media element that is told a resource is not
   * seekable will not offer a scrub bar even when every seek would have worked.</p>
   */
  async answer(event, url) {
    const id = url.pathname.slice('/du1/'.length);

    let stream = Streams.get(id);

    if (!stream) {
      stream = await recall(id, event.clientId || event.resultingClientId);
      if (stream) Streams.set(id, stream);
    }

    // Nothing knows about this stream: the page that made it has gone, or it was never made. 404
    // rather than an empty 200, which a media element reports as a corrupt file.
    if (!stream) return new Response(null, { status: 404 });

    const length = stream.header.plaintextLength;

    const common = {
      'Content-Type': stream.type || 'application/octet-stream',
      'Accept-Ranges': 'bytes',

      // Never stored, by anything. This is decrypted content and the one rule of this feature is
      // that it exists in memory and nowhere else.
      'Cache-Control': 'no-store',
    };

    const header = event.request.headers.get('Range');

    if (header === null) {
      return new Response(decryptedBody(stream, 0, Math.max(0, length - 1)), {
        status: 200,
        headers: { ...common, 'Content-Length': String(length) },
      });
    }

    const range = parseRange(header, length);

    // A range that cannot be satisfied is answered as one, with the length, so the element can ask
    // again for something that exists rather than treating the file as broken.
    if (!range) {
      return new Response(null, {
        status: 416,
        headers: { ...common, 'Content-Range': `bytes */${length}` },
      });
    }

    return new Response(decryptedBody(stream, range.from, range.to), {
      status: 206,
      headers: {
        ...common,
        'Content-Length': String(range.to - range.from + 1),
        'Content-Range': `bytes ${range.from}-${range.to}/${length}`,
      },
    });
  },
};

/*
 * How a page hands a stream over, and takes it back.
 *
 * A `message` listener rather than anything in the fetch path, and it may live here rather than in
 * sw.js because messages do not race the way respondWith does — every listener sees every message,
 * so a second one cannot silently win.
 */
self.addEventListener('message', (event) => {
  const data = event.data;
  if (!data) return;

  if (data.du1 === 'open') {
    Streams.set(data.id, { header: data.header, key: data.key, source: data.source, type: data.type });

    // Answered, so the page knows the worker has it before it points an element at the URL. Without
    // this the first request can arrive before the message is processed, take the recall path, and
    // ask the page for something it has only just sent.
    if (event.ports && event.ports[0]) event.ports[0].postMessage({ du1: 'open', id: data.id });

    return;
  }

  if (data.du1 === 'close') Streams.delete(data.id);
});
