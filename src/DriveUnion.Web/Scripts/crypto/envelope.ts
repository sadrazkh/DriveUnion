import {
  KdfIterations,
  NoncePrefixBytes,
  Scheme,
  SegmentSize,
  aadFor,
  fromBase64,
  nonceFor,
  toBase64,
  type Bytes,
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
 * One derivation, kept for as long as a batch of files needs it.
 *
 * <p>Six hundred thousand rounds is between half a second and a second of blocked CPU. Per file that
 * is nothing; for the twenty somebody drops onto the upload screen at once it is twenty seconds of a
 * frozen tab before the first byte moves, which is the difference between a feature and one nobody
 * uses. So the salt and the wrapping key it derives are made once and the batch shares them.</p>
 *
 * <p>What is <i>not</i> shared is the content key: every file still gets its own, and every file is
 * still opened on its own. The salt exists to stop one precomputed table from attacking every
 * passphrase in the world at once, and a salt that is fresh per batch does that as well as one that
 * is fresh per file. Sharing the content key instead would have been the version of this that is
 * actually weaker, and it is worth saying which corner was cut and which was not.</p>
 */
export interface Wrapping {
  readonly salt: Bytes;
  readonly iterations: number;
  readonly key: CryptoKey;
}

export async function deriveWrapping(secret: Secret): Promise<Wrapping> {
  const salt = crypto.getRandomValues(new Uint8Array(16));

  return { salt, iterations: KdfIterations, key: await wrappingKey(secret, salt, KdfIterations) };
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
  salt: Bytes,
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
  return sealWith(await deriveWrapping(secret), plaintextLength);
}

/** <see cref="seal"/> against a derivation already paid for. */
export async function sealWith(
  wrapping: Wrapping,
  plaintextLength: number,
): Promise<SealedFile> {
  const noncePrefix = crypto.getRandomValues(new Uint8Array(NoncePrefixBytes));
  const wrapNonce = crypto.getRandomValues(new Uint8Array(12));

  const key = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, true, [
    'encrypt',
    'decrypt',
  ]);

  const wrapped = new Uint8Array(
    await crypto.subtle.wrapKey('raw', key, wrapping.key, {
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
      kdfSalt: toBase64(wrapping.salt),
      kdfIterations: wrapping.iterations,
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
  return unwrap(secret, header, false, ['decrypt']);
}

/**
 * The content key back, and readable by this script.
 *
 * <p>Only the sharing path may call this, and it is a separate function rather than a flag so that
 * calling it is a decision somebody made rather than a default they inherited. <c>unseal</c> returns
 * a key Web Crypto will not export — the download path can decrypt with it and cannot read it, which
 * is the right shape for a key that only has to open a file.</p>
 *
 * <p>Re-wrapping needs the opposite: <c>wrapKey</c> has to read the key to encrypt it, and Web Crypto
 * has no other way. So this hands back an extractable key, and the window in which the raw bytes are
 * reachable is the few lines in <c>rewrap</c> between the two calls. That is the cost of being able
 * to share a file without sharing the passphrase, and it is worth naming rather than burying.</p>
 */
export async function unsealForRewrap(
  secret: Secret,
  header: EncryptionHeader,
): Promise<CryptoKey | null> {
  return unwrap(secret, header, true, ['decrypt', 'encrypt']);
}

async function unwrap(
  secret: Secret,
  header: EncryptionHeader,
  extractable: boolean,
  usages: KeyUsage[],
): Promise<CryptoKey | null> {
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
      extractable,
      usages,
    );
  } catch {
    return null;
  }
}

/** The three fields that constitute custody. The rest of a header describes the ciphertext. */
export interface LinkKeyMaterial {
  readonly kdfSalt: string;
  readonly kdfIterations: number;
  readonly wrappedKey: string;
}

/**
 * The same content key, wrapped again under a different secret.
 *
 * <p>This is the whole of sharing an encrypted file. Nothing is re-encrypted and nothing is
 * re-uploaded — the ciphertext on disk is untouched and is the same ciphertext both secrets open.
 * What is made is a second wrapped copy of one 32-byte key, which is why sharing a 40 GB film costs
 * one request and no bytes.</p>
 *
 * <p>A fresh salt per link, so two links to one file derive two unrelated wrappers and neither tells
 * you anything about the other.</p>
 */
export async function rewrap(key: CryptoKey, secret: Secret): Promise<LinkKeyMaterial> {
  const wrapping = await deriveWrapping(secret);
  const nonce = crypto.getRandomValues(new Uint8Array(12));

  const wrapped = new Uint8Array(
    await crypto.subtle.wrapKey('raw', key, wrapping.key, {
      name: 'AES-GCM',
      iv: nonce as BufferSource,
    }),
  );

  // Nonce in front of the wrapped key, exactly as `sealWith` writes it — this has to be readable by
  // the same `unseal` on the other end, and a second layout would be a second thing to get wrong.
  const envelope = new Uint8Array(nonce.length + wrapped.length);
  envelope.set(nonce, 0);
  envelope.set(wrapped, nonce.length);

  return {
    kdfSalt: toBase64(wrapping.salt),
    kdfIterations: wrapping.iterations,
    wrappedKey: toBase64(envelope),
  };
}

/** One segment, sealed. `isFinal` is bound into the tag — see <c>aadFor</c>. */
export async function encryptSegment(
  key: CryptoKey,
  header: EncryptionHeader,
  index: number,
  isFinal: boolean,
  plain: Bytes,
): Promise<Bytes> {
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
  sealed: Bytes,
): Promise<Bytes | null> {
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
