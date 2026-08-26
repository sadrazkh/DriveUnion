import { describe, expect, it } from 'vitest';
import {
  SegmentSize,
  TagBytes,
  aadFor,
  cipherLength,
  fromBase64,
  nonceFor,
  segmentCount,
  segmentSpan,
  toBase64,
} from './format';
import { decryptSegment, encryptSegment, newRecoveryKey, seal, unseal } from './envelope';

/**
 * The format, tested for the properties that make it worth having.
 *
 * Most of these are about what must *fail*. Encrypting and decrypting the same bytes is one line and
 * proves almost nothing: a format that returns the plaintext but does not notice a segment moved,
 * a file truncated, or a bit flipped is a format that silently hands somebody the wrong video.
 */

const passphrase = { kind: 'passphrase', value: 'correct horse battery staple' } as const;

/** A short header so the tests run in a second — the real one derives 600,000 times. */
async function fastSeal(length: number) {
  const sealed = await seal(passphrase, length);

  return sealed;
}

function bytes(fill: number, length: number): Uint8Array {
  return new Uint8Array(length).fill(fill);
}

describe('lengths and offsets', () => {
  it('accounts for one tag per segment', () => {
    expect(cipherLength(0)).toBe(0);
    expect(cipherLength(1)).toBe(1 + TagBytes);
    expect(cipherLength(SegmentSize)).toBe(SegmentSize + TagBytes);

    // The boundary that is easy to get wrong by one: exactly one segment is one tag, one byte more
    // is two. A listing that reported the wrong size here would refuse uploads at the quota edge.
    expect(cipherLength(SegmentSize + 1)).toBe(SegmentSize + 1 + 2 * TagBytes);
    expect(segmentCount(SegmentSize)).toBe(1);
    expect(segmentCount(SegmentSize + 1)).toBe(2);
  });

  it('places every segment where reading it back expects to find it', () => {
    const length = SegmentSize * 3 + 500;
    let offset = 0;

    for (let i = 0; i < segmentCount(length); i++) {
      const span = segmentSpan(i, length);

      // Contiguous, with no gap and no overlap — which is what makes a byte range a subtraction
      // rather than a lookup table.
      expect(span.start).toBe(offset);
      offset += span.length;
    }

    expect(offset).toBe(cipherLength(length));
  });
});

describe('nonces and additional data', () => {
  it('never repeats a nonce within a file', () => {
    const prefix = bytes(7, 8);
    const seen = new Set<string>();

    for (let i = 0; i < 1000; i++) seen.add(toBase64(nonceFor(prefix, i)));

    // A repeated nonce under one key is the failure that loses AES-GCM everything at once, so this
    // is the one property worth checking exhaustively rather than by inspection.
    expect(seen.size).toBe(1000);
  });

  it('binds the index and the final flag', () => {
    expect(toBase64(aadFor(1, false))).not.toBe(toBase64(aadFor(2, false)));
    expect(toBase64(aadFor(1, false))).not.toBe(toBase64(aadFor(1, true)));
  });
});

describe('base64', () => {
  it('survives bytes that are not text', async () => {
    const raw = crypto.getRandomValues(new Uint8Array(1024));

    expect(fromBase64(toBase64(raw))).toEqual(raw);
  });
});

describe('sealing a file', () => {
  it('round-trips a segment', async () => {
    const { header, key } = await fastSeal(100);
    const plain = bytes(42, 100);

    const sealed = await encryptSegment(key, header, 0, true, plain);
    expect(sealed.length).toBe(plain.length + TagBytes);
    expect(sealed).not.toEqual(plain);

    expect(await decryptSegment(key, header, 0, true, sealed)).toEqual(plain);
  });

  it('refuses a segment presented at another index', async () => {
    const { header, key } = await fastSeal(SegmentSize * 2);

    const first = await encryptSegment(key, header, 0, false, bytes(1, 16));
    const second = await encryptSegment(key, header, 1, true, bytes(2, 16));

    // Two segments of one file swapped. Without the index in the AAD both tags still verify and the
    // reader gets a file whose halves are in the wrong order, with no error anywhere.
    expect(await decryptSegment(key, header, 1, true, first)).toBeNull();
    expect(await decryptSegment(key, header, 0, false, second)).toBeNull();
  });

  it('refuses a last segment that claims not to be the last', async () => {
    const { header, key } = await fastSeal(16);
    const only = await encryptSegment(key, header, 0, true, bytes(3, 16));

    // Which is how a truncated file is caught: what is left ends on a segment marked final, and a
    // reader that expected more finds the flag disagrees.
    expect(await decryptSegment(key, header, 0, false, only)).toBeNull();
  });

  it('refuses a single flipped bit', async () => {
    const { header, key } = await fastSeal(64);
    const sealed = await encryptSegment(key, header, 0, true, bytes(9, 64));

    sealed[10] ^= 0x01;

    expect(await decryptSegment(key, header, 0, true, sealed)).toBeNull();
  });

  it('gives two files different keys and different nonces', async () => {
    const a = await fastSeal(10);
    const b = await fastSeal(10);

    expect(a.header.noncePrefix).not.toBe(b.header.noncePrefix);
    expect(a.header.kdfSalt).not.toBe(b.header.kdfSalt);

    // The same passphrase twice must not produce the same wrapping key, or one cracked file is
    // every file. That is what the salt is for, and this is what says so.
    expect(a.header.wrappedKey).not.toBe(b.header.wrappedKey);
  });
});

describe('opening a file', () => {
  it('returns the key to the right secret and null to a wrong one', async () => {
    const { header, key } = await fastSeal(32);
    const plain = bytes(5, 32);
    const sealed = await encryptSegment(key, header, 0, true, plain);

    const opened = await unseal(passphrase, header);
    expect(opened).not.toBeNull();
    expect(await decryptSegment(opened!, header, 0, true, sealed)).toEqual(plain);

    // One character out. Null rather than a throw, because a wrong passphrase is the commonest
    // thing that happens here and it is not exceptional.
    expect(await unseal({ kind: 'passphrase', value: 'correct horse battery stapl' }, header)).toBeNull();
    expect(await unseal({ kind: 'passphrase', value: '' }, header)).toBeNull();
  });

  it('refuses a scheme it does not know', async () => {
    const { header } = await fastSeal(8);

    // A file written by a future version must not be opened by guesswork. Refusing is what lets a
    // second scheme be added without this one pretending it can read it.
    expect(await unseal(passphrase, { ...header, scheme: 99 })).toBeNull();
  });

  it('opens with a recovery key the way it opens with a passphrase', async () => {
    const value = newRecoveryKey();

    // Base64url in readable groups, and long enough that it is the passphrase that is the weak
    // option rather than this.
    expect(value).toMatch(/^[A-Za-z0-9_-]{6}(-[A-Za-z0-9_-]{1,6})+$/);

    const secret = { kind: 'recoveryKey', value } as const;
    const { header, key } = await seal(secret, 16);
    const sealed = await encryptSegment(key, header, 0, true, bytes(8, 16));

    const opened = await unseal(secret, header);
    expect(await decryptSegment(opened!, header, 0, true, sealed)).toEqual(bytes(8, 16));

    expect(await unseal({ kind: 'recoveryKey', value: newRecoveryKey() }, header)).toBeNull();
  });

  it('does not care which kind of secret it is told a value came from', async () => {
    const { header } = await seal({ kind: 'passphrase', value: 'shared' }, 8);

    // One derivation path for both kinds, so «which box did they type it in» cannot become a reason
    // a file will not open.
    expect(await unseal({ kind: 'recoveryKey', value: 'shared' }, header)).not.toBeNull();
  });
});
