/**
 * The on-disk format for a file this product cannot read.
 *
 * ─────────────────────────────────────────────────────────────────────────────────────────────────
 * The whole design in one paragraph. The browser generates a random content key, encrypts the file
 * with it before a byte leaves the machine, and sends the operator ciphertext. The content key is
 * wrapped with a key derived from what the customer typed and only the wrapped form is stored, so
 * the server holds something it cannot open — not by policy, but because it does not have the
 * material. The server's cost is zero: it stores and relays the same bytes it always did.
 * ─────────────────────────────────────────────────────────────────────────────────────────────────
 *
 * **Segmented, not one AEAD over the file.** A single AES-GCM over 200 MB means decrypting 200 MB to
 * read the last minute of a video, and means holding all of it to verify the tag. So the plaintext is
 * cut into fixed segments, each encrypted independently, and a byte range maps to a handful of them
 * by arithmetic. That is what makes seeking possible at all, and it is the reason the segment size is
 * part of the stored header rather than a constant somebody may change later.
 *
 * **Nonces.** Twelve bytes: eight random per file, then the segment index big-endian. The content key
 * is random and per file, so a nonce repeats only if a segment index does — which it cannot, within
 * one file. Deriving them rather than storing them is what keeps the header small enough to sit in a
 * database row.
 *
 * **What the AAD binds, and why each part is there.** Every segment authenticates its own index and
 * whether it is the last one. Without the index, two segments of one file could be swapped and both
 * tags would still verify — a video whose scenes are reordered, with no error anywhere. Without the
 * final flag, a truncated file decrypts cleanly and simply ends early. The stored plaintext length
 * catches that too, and both are here because a format with one check is a format that fails the day
 * that check is bypassed.
 *
 * **What this deliberately does not do.** It does not hide the file's length, which is visible from
 * the ciphertext's length; it does not hide the name, which the panel needs to draw a list; and it
 * does not defend against an operator who serves altered JavaScript, because nothing running in a
 * browser can. Those are stated so that «encrypted» is not read as more than it is.
 */

/**
 * A view over a buffer this tab owns.
 *
 * <p>A bare <c>Uint8Array</c> also admits one backed by a <c>SharedArrayBuffer</c>, and <c>Blob</c>,
 * <c>FileSystemWritableFileStream</c> and the rest of the platform refuse those — every byte on this
 * path ends up in one of them, so the narrower type is named once here instead of cast at each
 * boundary.</p>
 */
export type Bytes = Uint8Array<ArrayBuffer>;

/** The one scheme there is. A file records which it was written with, so a second can be added. */
export const Scheme = 1;

/** Plaintext bytes per segment. 1 MiB: a seek costs one segment, and the tag overhead is 0.0015%. */
export const SegmentSize = 1024 * 1024;

/** AES-GCM's tag, appended to every segment's ciphertext. */
export const TagBytes = 16;

/** The random half of the nonce. The other four bytes are the segment index. */
export const NoncePrefixBytes = 8;

/**
 * PBKDF2 rounds.
 *
 * <p>Six hundred thousand is OWASP's 2023 figure for PBKDF2-SHA256, and PBKDF2 is used rather than
 * Argon2id for one reason: it is in Web Crypto and Argon2 is not, so the alternative is shipping a
 * WebAssembly build to every visitor of every page. It is the weaker choice against an attacker with
 * GPUs and it is the one that runs everywhere without a download — stated here rather than left for
 * somebody to discover in a comparison table.</p>
 */
export const KdfIterations = 600_000;

/** What is stored beside the file. None of it is secret; none of it opens anything on its own. */
export interface EncryptionHeader {
  readonly scheme: number;
  readonly segmentSize: number;

  /** Base64. The per-file half of every nonce. */
  readonly noncePrefix: string;

  /** The real size, which the ciphertext's length only approximates. */
  readonly plaintextLength: number;

  /** Base64. Salts the passphrase so two people choosing the same one derive different keys. */
  readonly kdfSalt: string;

  readonly kdfIterations: number;

  /** Base64. The content key, encrypted with the key derived from the passphrase. */
  readonly wrappedKey: string;
}

/** Ciphertext bytes for a plaintext of this length. */
export function cipherLength(plaintextLength: number, segmentSize = SegmentSize): number {
  if (plaintextLength === 0) return 0;

  return plaintextLength + TagBytes * segmentCount(plaintextLength, segmentSize);
}

export function segmentCount(plaintextLength: number, segmentSize = SegmentSize): number {
  return Math.ceil(plaintextLength / segmentSize);
}

/** Where segment `index` starts in the ciphertext, and how long it is there. */
export function segmentSpan(
  index: number,
  plaintextLength: number,
  segmentSize = SegmentSize,
): { start: number; length: number } {
  const start = index * (segmentSize + TagBytes);
  const plain = Math.min(segmentSize, Math.max(0, plaintextLength - index * segmentSize));

  return { start, length: plain + TagBytes };
}

/**
 * Twelve bytes: the file's prefix, then the index.
 *
 * <p>Big-endian because it is the order every wire format uses and the order somebody debugging a hex
 * dump will assume.</p>
 */
export function nonceFor(prefix: Bytes, index: number): Bytes {
  const nonce = new Uint8Array(12);

  nonce.set(prefix.subarray(0, NoncePrefixBytes), 0);
  new DataView(nonce.buffer).setUint32(NoncePrefixBytes, index, false);

  return nonce;
}

/**
 * What a segment authenticates besides itself.
 *
 * <p>`du1`, the index, and whether this is the last segment. See the file's own summary for what each
 * of those prevents — reordering and truncation, both of which decrypt cleanly without them.</p>
 */
export function aadFor(index: number, isFinal: boolean): Bytes {
  const aad = new Uint8Array(8);

  aad.set([0x64, 0x75, 0x31], 0); // "du1"
  new DataView(aad.buffer).setUint32(3, index, false);
  aad[7] = isFinal ? 1 : 0;

  return aad;
}

export function toBase64(bytes: Bytes): string {
  let binary = '';

  // Chunked, because String.fromCharCode(...array) blows the argument limit somewhere around a
  // hundred thousand bytes — and a wrapped key is small but a caller may pass anything.
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }

  return btoa(binary);
}

export function fromBase64(value: string): Bytes {
  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);

  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);

  return bytes;
}
