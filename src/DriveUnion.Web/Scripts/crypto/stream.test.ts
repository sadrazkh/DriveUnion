import { describe, expect, it } from 'vitest';
import { seal } from './envelope';
import { TagBytes, cipherLength, type Bytes, type EncryptionHeader } from './format';
import { cipherSource, decryptInto, plainSource } from './stream';

/**
 * The two halves of a transfer, put back together.
 *
 * <p>format.test.ts proves the primitives. This is about the arithmetic around them, which is where
 * the mistakes that only appear on a large file live: a chunk boundary that lands inside a segment,
 * a last segment that is shorter than the rest, a retry that has to produce the identical bytes.
 * Every case here uses a tiny segment size so that a 300-byte fixture exercises what a 40 GB upload
 * would.</p>
 */

/** Small enough that a few hundred bytes is several segments, and not a power of the chunk sizes. */
const Segment = 64;

const fill = (length: number): Bytes => {
  const out = new Uint8Array(length);
  for (let i = 0; i < length; i++) out[i] = (i * 31 + 7) & 0xff;

  return out;
};

/**
 * A real header with the segment size dialled down.
 *
 * <p>Legitimate rather than a fixture: `segmentSize` is in the header precisely so it can be
 * something other than the constant, and nothing in the format reads it from anywhere else.</p>
 */
async function locked(plaintextLength: number) {
  const sealed = await seal({ kind: 'passphrase', value: 'correct horse battery' }, plaintextLength);

  return { key: sealed.key, header: { ...sealed.header, segmentSize: Segment } };
}

/** The whole ciphertext, gathered the way the uploader gathers it: one window at a time. */
async function upload(file: Bytes, key: CryptoKey, header: EncryptionHeader, chunk: number) {
  const source = cipherSource(new Blob([file as BlobPart]), key, header);
  const parts: Bytes[] = [];

  for (let at = 0; at < source.size; at += chunk) {
    const to = Math.min(at + chunk, source.size);
    parts.push(new Uint8Array(await (await source.slice(at, to)).arrayBuffer()));
  }

  const out = new Uint8Array(source.size);
  let filled = 0;
  for (const part of parts) {
    out.set(part, filled);
    filled += part.length;
  }

  return { bytes: out, size: source.size };
}

/** The whole plaintext, gathered the way the download page gathers it. */
async function download(cipher: Bytes, key: CryptoKey, header: EncryptionHeader, chunk = 7) {
  const written: number[] = [];

  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      // Deliberately not segment-sized: a network chunk has no idea where a segment ends, and the
      // queue in decryptInto is the thing that has to know.
      for (let at = 0; at < cipher.length; at += chunk) {
        controller.enqueue(cipher.subarray(at, Math.min(at + chunk, cipher.length)));
      }
      controller.close();
    },
  });

  const result = await decryptInto(body, key, header, (plain) => {
    written.push(...plain);
  });

  return { result, plain: new Uint8Array(written) };
}

describe('the ciphertext a file becomes', () => {
  it('is the file plus one tag per segment, whatever the last segment is', async () => {
    for (const length of [1, Segment - 1, Segment, Segment + 1, Segment * 3, Segment * 3 + 5]) {
      const { key, header } = await locked(length);
      const { size } = await upload(fill(length), key, header, 1000);

      // The number the server is told before a byte moves, and the number the quota is spent on.
      expect(size).toBe(cipherLength(length, Segment));
      expect(size).toBe(length + TagBytes * Math.ceil(length / Segment));
    }
  });

  it('survives a chunk boundary that lands inside a segment', async () => {
    const length = Segment * 5 + 13;
    const file = fill(length);
    const { key, header } = await locked(length);

    // 37 is coprime with 80, so every window starts and ends mid-segment. That is the ordinary case
    // in production — the wire chunk is 32 MiB and a segment is 1 MiB plus 16 bytes — and it is the
    // one an implementation that assumed alignment would pass every other test without.
    const { bytes } = await upload(file, key, header, 37);
    const { result, plain } = await download(bytes, key, header);

    expect(result).toEqual({ ok: true });
    expect(plain).toEqual(file);
  });

  it('is identical when the same window is asked for twice', async () => {
    const { key, header } = await locked(Segment * 3);
    const source = cipherSource(new Blob([fill(Segment * 3) as BlobPart]), key, header);

    const first = new Uint8Array(await (await source.slice(20, 200)).arrayBuffer());
    const again = new Uint8Array(await (await source.slice(20, 200)).arrayBuffer());

    // What a retry depends on. A chunk resent after a dropped connection has to be the same chunk,
    // and it is only the same because the nonce is derived from the segment index rather than drawn
    // fresh — a random nonce here would be a different chunk each time and a file that never
    // reassembles.
    expect(again).toEqual(first);
  });

  it('goes out untouched when there is nothing to lock', async () => {
    const file = fill(300);
    const source = plainSource(new Blob([file as BlobPart]));

    expect(source.size).toBe(300);
    expect(new Uint8Array(await (await source.slice(0, 300)).arrayBuffer())).toEqual(file);
  });
});

describe('what comes back', () => {
  it('is the file, for every size around a segment edge', async () => {
    for (const length of [1, Segment - 1, Segment, Segment + 1, Segment * 4 + 3]) {
      const file = fill(length);
      const { key, header } = await locked(length);
      const { bytes } = await upload(file, key, header, 1000);

      const { result, plain } = await download(bytes, key, header);

      expect(result).toEqual({ ok: true });
      expect(plain).toEqual(file);
    }
  });

  it('refuses a body that stops early rather than returning what it got', async () => {
    const length = Segment * 3;
    const { key, header } = await locked(length);
    const { bytes } = await upload(fill(length), key, header, 1000);

    // Two whole segments and part of a third. Every byte of it verifies, which is exactly why this
    // has to be caught by the count rather than by a tag: a truncated download that returned two
    // thirds of a file would be a corrupt file with no error attached to it.
    const { result } = await download(bytes.subarray(0, (Segment + TagBytes) * 2 + 5), key, header);

    expect(result).toEqual({ ok: false, reason: 'truncated' });
  });

  it('refuses a single flipped bit', async () => {
    const length = Segment * 2;
    const { key, header } = await locked(length);
    const { bytes } = await upload(fill(length), key, header, 1000);

    bytes[Segment + 20] ^= 0x01;

    const { result } = await download(bytes, key, header);

    expect(result).toEqual({ ok: false, reason: 'corrupt' });
  });

  it('refuses two segments that have been swapped', async () => {
    const length = Segment * 3;
    const { key, header } = await locked(length);
    const { bytes } = await upload(fill(length), key, header, 1000);

    const stride = Segment + TagBytes;
    const first = bytes.slice(0, stride);
    bytes.set(bytes.subarray(stride, stride * 2), 0);
    bytes.set(first, stride);

    // Both segments are genuine and both were encrypted with this key. Without the index in the AAD
    // they would both verify and the file would come back with its middle rearranged — a video whose
    // scenes are out of order and no error anywhere.
    const { result } = await download(bytes, key, header);

    expect(result).toEqual({ ok: false, reason: 'corrupt' });
  });

  it('refuses the wrong key before it has read anything', async () => {
    const length = Segment * 2;
    const { key, header } = await locked(length);
    const { bytes } = await upload(fill(length), key, header, 1000);

    const other = await locked(length);
    const { result } = await download(bytes, other.key, header);

    expect(result).toEqual({ ok: false, reason: 'corrupt' });
  });

  it('waits for the writer, so a slow disk is not raced', async () => {
    const length = Segment * 4;
    const file = fill(length);
    const { key, header } = await locked(length);
    const { bytes } = await upload(file, key, header, 1000);

    let inFlight = 0;
    let overlapped = false;
    const collected: number[] = [];

    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(bytes);
        controller.close();
      },
    });

    // The property a streamed 40 GB download rests on: if this function ran ahead of the writer it
    // would hold the whole file in memory instead of one segment, and the sink it is writing to is
    // a file on a disk somewhere.
    const result = await decryptInto(body, key, header, async (plain) => {
      if (inFlight > 0) overlapped = true;
      inFlight++;
      await new Promise((resolve) => setTimeout(resolve, 1));
      collected.push(...plain);
      inFlight--;
    });

    expect(result).toEqual({ ok: true });
    expect(overlapped).toBe(false);
    expect(new Uint8Array(collected)).toEqual(file);
  });
});
