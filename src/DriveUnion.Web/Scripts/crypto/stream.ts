import { decryptSegment, encryptSegment } from './envelope';
import {
  TagBytes,
  type Bytes,
  cipherLength,
  segmentCount,
  segmentSpan,
  type EncryptionHeader,
} from './format';

/**
 * The two ends of the format, as the rest of the app needs them: a file's ciphertext addressable by
 * byte range on the way out, and a response body turned back into plaintext on the way in.
 *
 * <p>Neither holds the file. That is the whole point of segmenting it — the upload asks for the
 * window it is about to send and the download writes each megabyte as it verifies, so a 40 GB file
 * costs a segment of memory in each direction rather than 40 GB.</p>
 */

/** A file's ciphertext, readable by range without ever existing all at once. */
export interface ByteSource {
  /** What actually goes on the wire. Longer than the file by one tag per segment. */
  readonly size: number;
  slice(from: number, to: number): Promise<Blob>;
}

/** The unencrypted case, so the upload loop has one shape rather than two. */
export function plainSource(file: Blob): ByteSource {
  return { size: file.size, slice: (from, to) => Promise.resolve(file.slice(from, to)) };
}

/**
 * The same file, encrypted, addressed in ciphertext coordinates.
 *
 * <p>The upload chunk the server asks for is 32 MiB and a segment is 1 MiB, so a chunk boundary lands
 * inside a segment and the two segments at the ends of a window get encrypted twice — once for each
 * chunk that overlaps them. That is two megabytes of AES per 32 MiB chunk, and the alternative is
 * making the wire chunk a whole number of segments, which is a number Drive's resumable protocol
 * will not take: it wants multiples of 256 KiB and a segment plus its tag is 16 bytes past one.</p>
 *
 * <p>Encrypting the same segment twice is safe here and would not be in general. AES-GCM is
 * deterministic given a key, a nonce and a plaintext, and all three are fixed by the segment index —
 * so the second pass produces the byte-for-byte identical segment rather than a second encryption
 * under a reused nonce. It is also what makes a retry work at all: a chunk resent after a dropped
 * connection has to be the same chunk.</p>
 */
export function cipherSource(file: Blob, key: CryptoKey, header: EncryptionHeader): ByteSource {
  const plainSize = header.plaintextLength;
  const segments = segmentCount(plainSize, header.segmentSize);
  const stride = header.segmentSize + TagBytes;

  return {
    size: cipherLength(plainSize, header.segmentSize),

    async slice(from: number, to: number): Promise<Blob> {
      const parts: BlobPart[] = [];

      for (let index = Math.floor(from / stride); index * stride < to; index++) {
        const at = index * header.segmentSize;

        const plain = new Uint8Array(
          await file.slice(at, Math.min(at + header.segmentSize, plainSize)).arrayBuffer(),
        );

        const sealed = await encryptSegment(key, header, index, index === segments - 1, plain);

        // Trimmed to the window, because the first and last segments of a chunk are usually only
        // partly in it.
        parts.push(
          sealed.subarray(
            Math.max(from - index * stride, 0),
            Math.min(to - index * stride, sealed.length),
          ),
        );
      }

      return new Blob(parts);
    },
  };
}

/** Why a decryption stopped, for a screen that has to say something specific. */
export type DecryptFailure =
  /** A segment did not verify: the wrong key, a flipped bit, or a file that is not this file. */
  | 'corrupt'
  /** The bytes ran out before the header said they should. */
  | 'truncated';

export type DecryptResult = { ok: true } | { ok: false; reason: DecryptFailure };

/**
 * A response body, verified and written out one segment at a time.
 *
 * <p><c>write</c> is awaited, which is what lets the caller hand this a file on disk and have this
 * function apply backpressure to a 40 GB download rather than race ahead of it.</p>
 *
 * <p>Nothing is written before it verifies. A segment that fails its tag stops the whole thing —
 * which means a caller that has been streaming to disk is left holding a partial file, and it is the
 * caller's job to throw that away. Writing unverified plaintext so the failure could be reported at
 * the end would be handing somebody a file made of whatever an attacker put in the middle of it.</p>
 */
export async function decryptInto(
  body: ReadableStream<Uint8Array>,
  key: CryptoKey,
  header: EncryptionHeader,
  write: (plain: Bytes) => Promise<void> | void,
  onProgress?: (plainBytes: number) => void,
): Promise<DecryptResult> {
  const segments = segmentCount(header.plaintextLength, header.segmentSize);
  const reader = body.getReader();
  const held = new Queue();

  let done = false;
  let written = 0;

  try {
    for (let index = 0; index < segments; index++) {
      const need = segmentSpan(index, header.plaintextLength, header.segmentSize).length;

      while (held.length < need && !done) {
        const next = await reader.read();
        if (next.done) done = true;
        else if (next.value.length > 0) held.push(next.value);
      }

      if (held.length < need) return { ok: false, reason: 'truncated' };

      // The index and the final flag are what this segment is checked against, so a segment moved to
      // another position fails here rather than decrypting into the wrong minute of a video.
      const plain = await decryptSegment(
        key,
        header,
        index,
        index === segments - 1,
        held.take(need),
      );

      if (!plain) return { ok: false, reason: 'corrupt' };

      await write(plain);

      written += plain.length;
      onProgress?.(written);
    }
  } finally {
    // Whatever is still coming is not wanted: every segment carried its own index and the last one
    // said so, so trailing bytes can neither add to the file nor move any part of it.
    await reader.cancel().catch(() => undefined);
  }

  return written === header.plaintextLength ? { ok: true } : { ok: false, reason: 'truncated' };
}

/**
 * Bytes waiting for the segment they belong to.
 *
 * <p>A list of what arrived rather than one growing array: a network chunk is 16 KB and a segment is
 * 1 MiB, so concatenating on every read would copy the buffer sixty-four times to fill it once.</p>
 */
class Queue {
  private readonly parts: Uint8Array[] = [];
  private offset = 0;

  length = 0;

  /** Takes the wide type the platform hands out; `take` is what narrows, by copying. */
  push(part: Uint8Array): void {
    this.parts.push(part);
    this.length += part.length;
  }

  /** Exactly `count` bytes off the front. The caller has already checked there are that many. */
  take(count: number): Bytes {
    const out = new Uint8Array(count);
    let filled = 0;

    while (filled < count) {
      const head = this.parts[0].subarray(this.offset);
      const used = Math.min(head.length, count - filled);

      out.set(head.subarray(0, used), filled);
      filled += used;

      if (used === head.length) {
        this.parts.shift();
        this.offset = 0;
      } else {
        this.offset += used;
      }
    }

    this.length -= count;
    return out;
  }
}
