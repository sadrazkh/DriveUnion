import {
  KdfIterations,
  NoncePrefixBytes,
  Scheme,
  SegmentSize,
  aadFor,
  fromBase64,
  nonceFor,
  toBase64,
  type EncryptionHeader,
} from './format';

/**
 * Making, wrapping and unwrapping the key a file is encrypted with — and the segments themselves.
 *
 * <p>Everything here runs in the browser and nothing here is ever sent anywhere. The one thing that
 * crosses the wire is <see cref="EncryptionHeader"/>, which is the wrapped key and the parameters
 * needed to derive its wrapper — useless without what the customer typed.</p>
 */

/** How a customer proves they may open a file. */
export type Secret =
  /** Something they chose and will remember. Weak by nature, which is what the KDF is for. */
  | { readonly kind: 'passphrase'; readonly value: string }
  /** Thirty-two random bytes we generated and showed them once. Strong; theirs to keep. */
  | { readonly kind: 'recoveryKey'; readonly value: string };

export interface SealedFile {
  readonly header: EncryptionHeader;
  readonly key: CryptoKey;
}

/**
 * A recovery key, for the customer who would rather not choose a passphrase.
 *
 * <p>Thirty-two bytes of base64url in groups of six, because a string somebody may have to type or
 * read aloud needs somewhere for the eye to rest. It goes through the same derivation a passphrase
 * does — wasted work against a key that is already random, and one code path instead of two on the
 * one screen where a second path would be a second chance to get key handling wrong.</p>
 */
export function newRecoveryKey(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(24));
  const raw = toBase64(bytes).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

  return (raw.match(/.{1,6}/g) ?? [raw]).join('-');
}

/** Derives the wrapping key. The salt and the count come from the header, so an old file still opens. */
async function wrappingKey(
  secret: Secret,
  salt: Uint8Array,
  iterations: number,
): Promise<CryptoKey> {
  const material = await crypto.subtle.importKey(
    'raw',
    new TextEncoder().encode(secret.value.normalize('NFKC')),
    'PBKDF2',
    false,
    ['deriveKey'],
  );

  return crypto.subtle.deriveKey(
    { name: 'PBKDF2', salt: salt as BufferSource, iterations, hash: 'SHA-256' },
    material,
    { name: 'AES-GCM', length: 256 },
    false,
    ['wrapKey', 'unwrapKey'],
  );
}

/**
 * A fresh content key, wrapped for storage.
 *
 * <p>The content key is generated here and never leaves this function unwrapped. It is marked
 * extractable only so that <c>wrapKey</c> can read it — Web Crypto has no other way to encrypt a key
 * it holds — and the plain form is never serialised anywhere.</p>
 */
export async function seal(secret: Secret, plaintextLength: number): Promise<SealedFile> {
  const salt = crypto.getRandomValues(new Uint8Array(16));
  const noncePrefix = crypto.getRandomValues(new Uint8Array(NoncePrefixBytes));
  const wrapNonce = crypto.getRandomValues(new Uint8Array(12));

  const key = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, true, [
    'encrypt',
    'decrypt',
  ]);

  const wrapper = await wrappingKey(secret, salt, KdfIterations);

  const wrapped = new Uint8Array(
    await crypto.subtle.wrapKey('raw', key, wrapper, {
      name: 'AES-GCM',
      iv: wrapNonce as BufferSource,
    }),
  );

  // The nonce travels in front of the wrapped key rather than in its own field: it is twelve bytes
  // that mean nothing on their own, and one column is one fewer thing for a migration to forget.
  const envelope = new Uint8Array(wrapNonce.length + wrapped.length);
  envelope.set(wrapNonce, 0);
  envelope.set(wrapped, wrapNonce.length);

  return {
    key,
    header: {
      scheme: Scheme,
      segmentSize: SegmentSize,
      noncePrefix: toBase64(noncePrefix),
      plaintextLength,
      kdfSalt: toBase64(salt),
      kdfIterations: KdfIterations,
      wrappedKey: toBase64(envelope),
    },
  };
}

/**
 * The content key back, or null when the secret is wrong.
 *
 * <p>Null rather than a throw, because «wrong passphrase» is the commonest thing that happens on this
 * path and it is not exceptional. AES-GCM's own tag is what decides: a wrong wrapping key produces a
 * failed unwrap and nothing else, so there is no oracle here beyond «yes or no».</p>
 */
export async function unseal(secret: Secret, header: EncryptionHeader): Promise<CryptoKey | null> {
  if (header.scheme !== Scheme) return null;

  const envelope = fromBase64(header.wrappedKey);
  const wrapper = await wrappingKey(secret, fromBase64(header.kdfSalt), header.kdfIterations);

  try {
    return await crypto.subtle.unwrapKey(
      'raw',
      envelope.subarray(12) as BufferSource,
      wrapper,
      { name: 'AES-GCM', iv: envelope.subarray(0, 12) as BufferSource },
      { name: 'AES-GCM', length: 256 },
      false,
      ['decrypt'],
    );
  } catch {
    return null;
  }
}

/** One segment, sealed. `isFinal` is bound into the tag — see <c>aadFor</c>. */
export async function encryptSegment(
  key: CryptoKey,
  header: EncryptionHeader,
  index: number,
  isFinal: boolean,
  plain: Uint8Array,
): Promise<Uint8Array> {
  const sealed = await crypto.subtle.encrypt(
    {
      name: 'AES-GCM',
      iv: nonceFor(fromBase64(header.noncePrefix), index) as BufferSource,
      additionalData: aadFor(index, isFinal) as BufferSource,
    },
    key,
    plain as BufferSource,
  );

  return new Uint8Array(sealed);
}

/**
 * One segment back, or null when it does not verify.
 *
 * <p>Null covers every reason at once — the wrong key, a flipped bit, a segment moved to another
 * index, a truncated file whose last segment is not marked final. They are one answer because they
 * have one remedy: this is not the file you asked for.</p>
 */
export async function decryptSegment(
  key: CryptoKey,
  header: EncryptionHeader,
  index: number,
  isFinal: boolean,
  sealed: Uint8Array,
): Promise<Uint8Array | null> {
  try {
    const plain = await crypto.subtle.decrypt(
      {
        name: 'AES-GCM',
        iv: nonceFor(fromBase64(header.noncePrefix), index) as BufferSource,
        additionalData: aadFor(index, isFinal) as BufferSource,
      },
      key,
      sealed as BufferSource,
    );

    return new Uint8Array(plain);
  } catch {
    return null;
  }
}
