import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { webcrypto } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { aadFor, nonceFor, segmentSpan, SegmentSize, TagBytes, toBase64, type Bytes } from './crypto/format';

/**
 * Playing a locked file without downloading it first.
 *
 * <p>The file under test is <c>wwwroot/sw-media.js</c> itself, read off disk and evaluated against
 * stand-ins for the globals a worker has — the shipped bytes, not a module pulled through Vite,
 * because the file is outside the bundle on purpose and a bundled copy is one that is never
 * served. The same bargain <c>sw.test.ts</c> and <c>swPush.test.ts</c> make.</p>
 *
 * <p><b>The important half is the agreement tests.</b> That file is the third implementation of the
 * <c>du1</c> format — <c>Scripts/crypto/format.ts</c> is the first and <c>Du1.cs</c> the second —
 * and it is a hand copy because a classic worker script cannot import a TypeScript module. Two
 * implementations of a wire format that drift apart produce a file that decrypts to noise, and
 * nothing anywhere reports it: the reader sees a video that will not play and assumes the file is
 * broken. So every constant and every piece of arithmetic in the worker is compared against
 * <c>format.ts</c>'s own, over indices chosen to cross a segment boundary.</p>
 */

const Origin = 'https://panel.driveunion.test';

const source = readFileSync(
  resolve(import.meta.dirname, '../wwwroot/sw-media.js'),
  'utf8',
);

interface Harness {
  /** What the file installed on `self`. */
  media: {
    Prefix: string;
    claims: (url: URL) => boolean;
    answer: (event: Record<string, unknown>, url: URL) => Promise<Response>;
  };

  /** Delivers a message the way the page would. */
  message: (data: unknown, ports?: unknown[]) => Promise<void>;

  /** Every upstream range the worker asked for. */
  ranges: string[];

  /** And every address it asked for them from. */
  sources: string[];

  /** The private helpers, reached for the agreement tests. */
  internals: Record<string, (...args: never[]) => unknown>;
}

/**
 * Evaluates the worker with a fake `self`, and hands back both its public surface and the private
 * helpers the agreement tests need.
 *
 * <p>The helpers are returned by appending one expression to the source rather than by exporting
 * them from the file: a worker script has no exports, and adding some for a test's benefit would
 * change the thing being tested into something that is not shipped.</p>
 */
function boot(ciphertext: Bytes = new Uint8Array(0) as Bytes): Harness {
  // A list per type, not one handler per type. A worker delivers every event to every listener, and
  // a Map that overwrote would quietly test a file with all but its last listener removed — which is
  // exactly the mistake this harness made first, and it read as the feature being broken.
  const listeners = new Map<string, ((event: unknown) => void)[]>();
  const ranges: string[] = [];
  const sources: string[] = [];

  const self: Record<string, unknown> = {
    location: new URL(`${Origin}/sw-media.js`),
    addEventListener: (type: string, handler: (event: unknown) => void) => {
      const existing = listeners.get(type) ?? [];
      existing.push(handler);
      listeners.set(type, existing);
    },
    clients: { get: async () => null },
  };

  const fetch = async (url: string, init?: { headers?: Record<string, string> }) => {
    const header = init?.headers?.Range ?? '';
    ranges.push(header);
    sources.push(url);

    const match = /^bytes=(\d+)-(\d+)$/.exec(header);

    // slice and not subarray: TS 5.7 distinguishes Uint8Array<ArrayBuffer> from
    // Uint8Array<ArrayBufferLike>, subarray widens to the second, and only the first is a BodyInit.
    // The same distinction Scripts/crypto/format.ts introduced `Bytes` for.
    const slice = match
      ? ciphertext.slice(Number(match[1]), Number(match[2]) + 1)
      : ciphertext;

    return new Response(slice, { status: 206 });
  };

  const factory = new Function(
    'self', 'crypto', 'fetch', 'atob', 'Response', 'ReadableStream', 'MessageChannel', 'setTimeout', 'clearTimeout',
    `${source}\n;return { segmentSpan, nonceFor, aadFor, fromBase64, parseRange, SegmentSize, TagBytes };`,
  );

  const internals = factory(
    self,
    webcrypto,
    fetch,
    (value: string) => Buffer.from(value, 'base64').toString('binary'),
    Response,
    ReadableStream,
    MessageChannel,
    setTimeout,
    clearTimeout,
  );

  return {
    media: self.du1Media as Harness['media'],
    message: async (data: unknown, ports: unknown[] = []) => {
      for (const handler of listeners.get('message') ?? []) handler({ data, ports });
    },
    ranges,
    sources,
    internals,
  };
}

describe('the du1 format, as the worker spells it', () => {
  it('uses the same constants as the format module', () => {
    const { internals } = boot();

    expect(internals.SegmentSize).toBe(SegmentSize);
    expect(internals.TagBytes).toBe(TagBytes);
  });

  it('puts every segment where the format module puts it', () => {
    const { internals } = boot();
    const span = internals.segmentSpan as (i: number, len: number, seg: number) => unknown;

    // A length that is not a whole number of segments, so the last one is short — which is the
    // case the arithmetic gets wrong if it is wrong at all.
    const lengths = [0, 1, SegmentSize - 1, SegmentSize, SegmentSize + 1, SegmentSize * 3 + 77];

    for (const length of lengths) {
      for (let index = 0; index < 5; index++) {
        expect(span(index, length, SegmentSize))
          .toEqual(segmentSpan(index, length, SegmentSize));
      }
    }
  });

  it('derives the same nonce as the format module', () => {
    const { internals } = boot();
    const nonce = internals.nonceFor as (p: Uint8Array, i: number) => Uint8Array;
    const prefix = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]);

    // 255 and 256 cross a byte boundary in the big-endian index; 65_536 crosses two. A nonce that
    // is wrong for one index and right for the others decrypts most of a file and fails in the
    // middle, which reads as a corrupt file rather than as a bug.
    for (const index of [0, 1, 255, 256, 65_535, 65_536]) {
      expect(Array.from(nonce(prefix, index))).toEqual(Array.from(nonceFor(prefix as Bytes, index)));
    }
  });

  it('authenticates the same extra data as the format module', () => {
    const { internals } = boot();
    const aad = internals.aadFor as (i: number, f: boolean) => Uint8Array;

    for (const index of [0, 1, 255, 256, 65_536]) {
      for (const isFinal of [true, false]) {
        expect(Array.from(aad(index, isFinal))).toEqual(Array.from(aadFor(index, isFinal)));
      }
    }
  });
});

describe('the range it asks a media element to accept', () => {
  const cases: [string, number, unknown][] = [
    ['bytes=0-99', 1000, { from: 0, to: 99 }],
    ['bytes=500-', 1000, { from: 500, to: 999 }],
    ['bytes=-200', 1000, { from: 800, to: 999 }],
    ['bytes=0-99999', 1000, { from: 0, to: 999 }],
    ['bytes=1000-1100', 1000, null],
    ['bytes=900-100', 1000, null],
    ['bytes=-0', 1000, null],
    ['bytes=-', 1000, null],
    ['items=0-10', 1000, null],
    ['', 1000, null],
  ];

  it.each(cases)('reads %s over %i bytes', (header, length, expected) => {
    const { internals } = boot();
    const parse = internals.parseRange as (h: string, l: number) => unknown;

    expect(parse(header, length)).toEqual(expected);
  });
});

describe('what it will and will not answer for', () => {
  it('claims its own prefix and nothing else', () => {
    const { media } = boot();

    expect(media.claims(new URL(`${Origin}/du1/abc`))).toBe(true);
    expect(media.claims(new URL(`${Origin}/d/kx91mzq4`))).toBe(false);
    expect(media.claims(new URL(`${Origin}/api/v1/files`))).toBe(false);
    expect(media.claims(new URL(`${Origin}/files`))).toBe(false);
  });

  it('answers 404 for a stream nothing has opened', async () => {
    const { media } = boot();

    // Not an empty 200, which a media element reports to the reader as a corrupt file rather than
    // as a link that has gone.
    const response = await media.answer(
      { request: new Request(`${Origin}/du1/nothing`), clientId: '' },
      new URL(`${Origin}/du1/nothing`),
    );

    expect(response.status).toBe(404);
  });
});

describe('serving a locked file', () => {
  /** One real du1 file, sealed with a small segment size so a few segments fit in a test. */
  async function sealed(plaintext: Uint8Array, segmentSize: number) {
    const key = await webcrypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
    const prefix = new Uint8Array([9, 8, 7, 6, 5, 4, 3, 2]);
    const total = Math.ceil(plaintext.length / segmentSize);

    const parts: Uint8Array[] = [];

    for (let index = 0; index < total; index++) {
      const slice = plaintext.subarray(index * segmentSize, (index + 1) * segmentSize);

      const cipher = new Uint8Array(await webcrypto.subtle.encrypt(
        {
          name: 'AES-GCM',
          iv: nonceFor(prefix as Bytes, index),
          additionalData: aadFor(index, index === total - 1),
        },
        key,
        slice,
      ));

      parts.push(cipher);
    }

    const ciphertext = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
    let at = 0;
    for (const part of parts) {
      ciphertext.set(part, at);
      at += part.length;
    }

    return {
      ciphertext,
      header: {
        scheme: 1,
        segmentSize,
        noncePrefix: toBase64(prefix as Bytes),
        plaintextLength: plaintext.length,
        kdfSalt: '',
        kdfIterations: 0,
        wrappedKey: '',
      },
      key,
    };
  }

  const segmentSize = 64;
  const plaintext = new Uint8Array(200).map((_, i) => i % 251);

  async function open() {
    const file = await sealed(plaintext, segmentSize);
    const harness = boot(file.ciphertext);

    await harness.message({
      du1: 'open',
      id: 'x',
      header: file.header,
      key: file.key,
      source: `${Origin}/d/kx91mzq4/file`,
      type: 'video/mp4',
    });

    return harness;
  }

  it('returns the whole file when nothing asks for a range', async () => {
    const harness = await open();

    const response = await harness.media.answer(
      { request: new Request(`${Origin}/du1/x`), clientId: '' },
      new URL(`${Origin}/du1/x`),
    );

    expect(response.status).toBe(200);
    expect(response.headers.get('Content-Length')).toBe('200');
    expect(response.headers.get('Content-Type')).toBe('video/mp4');

    // Seeking is offered even without a range on this request, or a media element decides the
    // resource is not seekable and draws no scrub bar however well every seek would have worked.
    expect(response.headers.get('Accept-Ranges')).toBe('bytes');

    // Decrypted content is never stored by anything, and the response says so itself.
    expect(response.headers.get('Cache-Control')).toBe('no-store');

    expect(new Uint8Array(await response.arrayBuffer())).toEqual(plaintext);
  });

  it('answers a range with exactly those plaintext bytes', async () => {
    const harness = await open();

    // 70 to 150 starts inside segment 1 and ends inside segment 2, so both ends are trimmed and a
    // whole segment sits between them. An off-by-one in the trimming shows up here and nowhere else.
    const response = await harness.media.answer(
      { request: new Request(`${Origin}/du1/x`, { headers: { Range: 'bytes=70-150' } }), clientId: '' },
      new URL(`${Origin}/du1/x`),
    );

    expect(response.status).toBe(206);
    expect(response.headers.get('Content-Range')).toBe('bytes 70-150/200');
    expect(response.headers.get('Content-Length')).toBe('81');
    expect(new Uint8Array(await response.arrayBuffer())).toEqual(plaintext.subarray(70, 151));
  });

  it('reads only the ciphertext the range needs', async () => {
    const harness = await open();

    await (await harness.media.answer(
      { request: new Request(`${Origin}/du1/x`, { headers: { Range: 'bytes=70-150' } }), clientId: '' },
      new URL(`${Origin}/du1/x`),
    )).arrayBuffer();

    // Segments 1 and 2 only. This is the whole feature: a seek into a two-hour film reads the part
    // being watched, and the owner's traffic is spent on that rather than on the film.
    const first = segmentSpan(1, 200, segmentSize);
    const last = segmentSpan(2, 200, segmentSize);

    expect(harness.ranges).toEqual([`bytes=${first.start}-${last.start + last.length - 1}`]);

    // And from the address the page handed over, which is the ordinary public link. The worker has
    // no other way to know where the ciphertext is, and getting this from anywhere else — a guess
    // built from the stream id, say — would be a second spelling of one fact.
    expect(harness.sources).toEqual([`${Origin}/d/kx91mzq4/file`]);
  });

  it('refuses a range past the end rather than serving something else', async () => {
    const harness = await open();

    const response = await harness.media.answer(
      { request: new Request(`${Origin}/du1/x`, { headers: { Range: 'bytes=500-600' } }), clientId: '' },
      new URL(`${Origin}/du1/x`),
    );

    expect(response.status).toBe(416);
    expect(response.headers.get('Content-Range')).toBe('bytes */200');
  });

  it('breaks the response when a segment does not verify', async () => {
    const file = await sealed(plaintext, segmentSize);

    // One bit, in the middle of the first segment. AES-GCM's tag is what catches this, and the only
    // honest answer is to break the stream: half a film that plays and then stops is a file the
    // reader believes they watched.
    file.ciphertext[10] ^= 0x01;

    const harness = boot(file.ciphertext);
    await harness.message({
      du1: 'open', id: 'x', header: file.header, key: file.key,
      source: `${Origin}/d/kx91mzq4/file`, type: 'video/mp4',
    });

    const response = await harness.media.answer(
      { request: new Request(`${Origin}/du1/x`), clientId: '' },
      new URL(`${Origin}/du1/x`),
    );

    await expect(response.arrayBuffer()).rejects.toThrow();
  });

  it('forgets a stream when the page says to', async () => {
    const harness = await open();

    await harness.message({ du1: 'close', id: 'x' });

    const response = await harness.media.answer(
      { request: new Request(`${Origin}/du1/x`), clientId: '' },
      new URL(`${Origin}/du1/x`),
    );

    expect(response.status).toBe(404);
  });
});
