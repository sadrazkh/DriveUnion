import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { unseal } from './envelope';
import { decryptInto } from './stream';
import { KdfIterations, Scheme, type Bytes, type EncryptionHeader } from './format';

/**
 * The same format, written by the other implementation.
 *
 * <p>`du1` exists twice: here, where every browser runs it, and in `Du1.cs`, which is what encrypts a
 * file the server fetched from a URL. Two implementations of one thing drift, and drift here means a
 * file somebody cannot open — so each side writes a fixture and the other opens it. Nothing else can
 * catch it: a test on either side alone is that side agreeing with itself.</p>
 *
 * <p><c>tests/DriveUnion.Tests/Storage/Du1Tests.cs</c> is the other half of this file.</p>
 */

interface Fixture {
  readonly secret: string;
  readonly scheme: number;
  readonly segmentSize: number;
  readonly noncePrefix: string;
  readonly plaintextLength: number;
  readonly kdfSalt: string;
  readonly kdfIterations: number;
  readonly wrappedKey: string;
  readonly ciphertext: string;
  readonly plaintext: string;
}

const load = (name: string): Fixture =>
  JSON.parse(readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8')) as Fixture;

const bytes = (base64: string): Bytes => Uint8Array.from(Buffer.from(base64, 'base64'));

const headerOf = (fixture: Fixture): EncryptionHeader => ({
  scheme: fixture.scheme,
  segmentSize: fixture.segmentSize,
  noncePrefix: fixture.noncePrefix,
  plaintextLength: fixture.plaintextLength,
  kdfSalt: fixture.kdfSalt,
  kdfIterations: fixture.kdfIterations,
  wrappedKey: fixture.wrappedKey,
});

/** What the download page does: unwrap, then read every segment. */
async function open(fixture: Fixture, secret: string) {
  const header = headerOf(fixture);
  const key = await unseal({ kind: 'passphrase', value: secret }, header);

  if (!key) return null;

  const cipher = bytes(fixture.ciphertext);
  const written: number[] = [];

  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(cipher);
      controller.close();
    },
  });

  const result = await decryptInto(body, key, header, (plain) => {
    written.push(...plain);
  });

  return result.ok ? new Uint8Array(written) : null;
}

describe('a file the server sealed', () => {
  it('opens here, with the shipped reader and nothing special', async () => {
    // The direction this feature turns on: a file fetched from a link is encrypted by C#, and the
    // customer opens it on the public download page with the island and the code below — no second
    // path, no second format, no «which kind is this» branch in the reader.
    const fixture = load('server-sealed.json');

    const opened = await open(fixture, fixture.secret);

    expect(opened).not.toBeNull();
    expect(opened).toEqual(bytes(fixture.plaintext));
  });

  it('refuses a wrong secret exactly as a browser-sealed one does', async () => {
    const fixture = load('server-sealed.json');

    expect(await open(fixture, 'not the passphrase')).toBeNull();
  });

  it('was written with the constants this side believes in', () => {
    const fixture = load('server-sealed.json');

    // The two numbers that are genuinely shared constants rather than header fields. If C# ever
    // moves one of them, files written after that would still open — and files written before would
    // be opened with the wrong derivation, which is a failure that looks like a wrong passphrase.
    expect(fixture.scheme).toBe(Scheme);
    expect(fixture.kdfIterations).toBe(KdfIterations);
  });
});

describe('a file this side sealed', () => {
  it('is the fixture the C# tests read, in the shape they expect', () => {
    // The other direction is asserted in Du1Tests. This half only guards the fixture's shape, so a
    // rename here and a green suite there cannot be two tests passing about nothing.
    const fixture = load('browser-sealed.json');

    expect(fixture.scheme).toBe(Scheme);
    expect(fixture.kdfIterations).toBe(KdfIterations);
    expect(fixture.segmentSize).toBeGreaterThan(0);
    expect(bytes(fixture.plaintext).length).toBe(fixture.plaintextLength);

    // Three segments' worth, so the fixture exercises the index in the nonce and the final flag in
    // the AAD rather than a single segment where both are trivially right.
    expect(fixture.plaintextLength).toBeGreaterThan(fixture.segmentSize * 2);
  });

  it('still opens with its own reader, which is what makes it a fair fixture', async () => {
    const fixture = load('browser-sealed.json');

    expect(await open(fixture, fixture.secret)).toEqual(bytes(fixture.plaintext));
  });
});
