import { describe, expect, it } from 'vitest';
import { deriveWrapping, rewrap, seal, sealWith, unseal, unsealForRewrap } from './envelope';
import type { Bytes, EncryptionHeader } from './format';
import { cipherSource, decryptInto } from './stream';

/**
 * Sharing one locked file without sharing the passphrase that opens the rest.
 *
 * <p>Before this, giving somebody a locked file meant giving them the secret it was uploaded with —
 * and a batch of files uploaded together shares one derivation, so that secret opened all of them,
 * for ever, with no way to take it back. The tests below are about the property that replaces it:
 * two secrets, one file each, and neither reaching past the file it was made for.</p>
 */

const Segment = 64;

const owner = { kind: 'passphrase', value: 'the owner remembers this' } as const;
const forLink = { kind: 'recoveryKey', value: 'K7bQ2x-9mWzAa-Lp03Rd' } as const;

const fill = (length: number): Bytes => {
  const out = new Uint8Array(length);
  for (let i = 0; i < length; i++) out[i] = (i * 53 + 17) & 0xff;

  return out;
};

/** A file as the uploader leaves it: ciphertext on disk, and the header stored beside it. */
async function uploaded(length: number, wrapping?: Awaited<ReturnType<typeof deriveWrapping>>) {
  const sealed = wrapping
    ? await sealWith(wrapping, length)
    : await seal(owner, length);

  const header: EncryptionHeader = { ...sealed.header, segmentSize: Segment };
  const plain = fill(length);
  const source = cipherSource(new Blob([plain as BlobPart]), sealed.key, header);
  const cipher = new Uint8Array(await (await source.slice(0, source.size)).arrayBuffer());

  return { header, plain, cipher };
}

/** What the public page does with a header and a secret. */
async function open(header: EncryptionHeader, secret: Parameters<typeof unseal>[0], cipher: Bytes) {
  const key = await unseal(secret, header);
  if (!key) return null;

  const written: number[] = [];
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(cipher);
      controller.close();
    },
  });

  const result = await decryptInto(body, key, header, (p) => {
    written.push(...p);
  });

  return result.ok ? new Uint8Array(written) : null;
}

describe('a link with its own key', () => {
  it('opens the file with a secret the owner never had to give away', async () => {
    const file = await uploaded(Segment * 3);

    // What the panel does: the owner opens their own file once, and the browser wraps that same
    // content key under a secret generated for the link.
    const key = await unsealForRewrap(owner, file.header);
    expect(key).not.toBeNull();

    const material = await rewrap(key!, forLink);

    // What the server stores and hands to the visitor: the file's own format fields, and the link's
    // three custody fields in place of the file's.
    const forVisitor: EncryptionHeader = { ...file.header, ...material };

    expect(await open(forVisitor, forLink, file.cipher)).toEqual(file.plain);

    // And the same bytes. Nothing was re-encrypted and nothing was re-uploaded — which is why
    // sharing a 40 GB film costs one request.
    expect(await open(file.header, owner, file.cipher)).toEqual(file.plain);
  });

  it('does not open the other files that were uploaded with it', async () => {
    // One derivation, two files — exactly what dropping two files onto the upload screen produces,
    // and exactly the reason the owner's passphrase is the wrong thing to hand somebody.
    const batch = await deriveWrapping(owner);
    const shared = await uploaded(Segment * 2, batch);
    const private_ = await uploaded(Segment * 2, batch);

    const key = await unsealForRewrap(owner, shared.header);
    const material = await rewrap(key!, forLink);

    // The link's secret against the file it was made for: opens.
    expect(await open({ ...shared.header, ...material }, forLink, shared.cipher))
      .toEqual(shared.plain);

    // The link's secret against the other file in the same batch: nothing. This is the whole point.
    // The two files have different content keys; only one of them was ever re-wrapped.
    expect(await unseal(forLink, private_.header)).toBeNull();

    // And the owner's own passphrase still opens both, which is what it is for.
    expect(await unseal(owner, private_.header)).not.toBeNull();
  });

  it('leaves the owner unable to be locked out of their own file', async () => {
    const file = await uploaded(Segment);

    const key = await unsealForRewrap(owner, file.header);
    await rewrap(key!, forLink);

    // Re-wrapping writes a second copy; it does not move the first. A share that cost the owner
    // access to their own file would be a share nobody would risk making.
    expect(await unseal(owner, file.header)).not.toBeNull();
  });

  it('gives two links to one file two unrelated wrappers', async () => {
    const file = await uploaded(Segment);
    const key = await unsealForRewrap(owner, file.header);

    const first = await rewrap(key!, forLink);
    const second = await rewrap(key!, forLink);

    // Same key, same secret, different salt and different nonce — so the stored material differs and
    // neither row says anything about the other. Revoking one link cannot weaken the other.
    expect(second.kdfSalt).not.toBe(first.kdfSalt);
    expect(second.wrappedKey).not.toBe(first.wrappedKey);

    // Both still open the same file.
    expect(await unseal(forLink, { ...file.header, ...first })).not.toBeNull();
    expect(await unseal(forLink, { ...file.header, ...second })).not.toBeNull();
  });

  it('refuses the wrong secret exactly as the file itself does', async () => {
    const file = await uploaded(Segment);
    const key = await unsealForRewrap(owner, file.header);
    const material = await rewrap(key!, forLink);

    const forVisitor = { ...file.header, ...material };

    // The owner's own passphrase does not open the link's copy either. That is not a bug — the link
    // carries one wrapped key and it is not the owner's — and it is worth stating, because it means
    // an owner testing their own link needs the secret they generated for it.
    expect(await unseal(owner, forVisitor)).toBeNull();
    expect(await unseal({ kind: 'passphrase', value: 'guessing' }, forVisitor)).toBeNull();
  });
});

describe('the key the sharing path asks for', () => {
  it('is readable only where re-wrapping needs it to be', async () => {
    const file = await uploaded(Segment);

    // The download path's key cannot be exported, so nothing on that path can leak the raw bytes
    // even by accident.
    const forDownload = await unseal(owner, file.header);
    expect(forDownload!.extractable).toBe(false);

    // The sharing path's can, because wrapKey has to read it and Web Crypto offers no other way.
    // Two functions rather than a flag, so this is a decision somebody made rather than a default.
    const forSharing = await unsealForRewrap(owner, file.header);
    expect(forSharing!.extractable).toBe(true);
  });

  it('is null for a wrong secret rather than throwing', async () => {
    const file = await uploaded(Segment);

    expect(await unsealForRewrap({ kind: 'passphrase', value: 'not it' }, file.header)).toBeNull();
  });
});
