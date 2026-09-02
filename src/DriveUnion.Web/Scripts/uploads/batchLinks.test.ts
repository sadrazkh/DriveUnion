import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Fetching many addresses at once.
 *
 * <p>Held against the source, because the sender lives in <c>UploadPanel.vue</c> and this project has
 * no DOM to mount a component in. Three of its decisions are the kind that look like style and are
 * not: one of them is a key, one is a rate limit, and one is whether somebody can find the two
 * addresses that failed among twenty that did not.</p>
 */

const panel = readFileSync(
  resolve(import.meta.dirname, '../islands/UploadPanel.vue'),
  'utf8',
);

const sender = panel.slice(
  panel.indexOf('async function sendLink()'),
  panel.indexOf('async function stopFetch'),
);

describe('a box with several addresses in it', () => {
  it('reads one address per line and ignores the blank ones', () => {
    const reader = panel.slice(panel.indexOf('function addresses()'), panel.indexOf('async function sendLink()'));

    expect(reader).toContain('split(/\\r?\\n/)');
    expect(reader).toContain('trim()');
    expect(reader).toContain('line.length > 0');
  });

  /**
   * <b>The one with a key in it.</b>
   *
   * <p><c>linkBody</c> seals a fresh content key per call. Calling it once and reusing the body would
   * put every file in the batch under one key — which is not what «locked per file» means anywhere
   * else in this product, and would be invisible until somebody was handed a passphrase that opened
   * more than they were given.</p>
   */
  it('builds custody per address rather than once for the batch', () => {
    expect(panel).toContain('async function linkBody(address: string)');

    // Inside the loop, so a fresh seal per address.
    const loop = sender.slice(sender.indexOf('for (const one of lines)'));

    expect(loop).toContain('linkBody(one)');
  });

  /**
   * Sequential, because the server counts what is in flight per workspace. Twenty simultaneous
   * requests would race that cap into refusing an arbitrary subset, which is a worse answer than a
   * straight one in order.
   */
  it('sends them one at a time', () => {
    expect(sender).toContain('for (const one of lines)');
    expect(sender).toContain('await fetch(');

    // No Promise.all — that is the shape this test exists to forbid.
    expect(sender).not.toContain('Promise.all');
  });

  /**
   * <b>What was accepted disappears; what was refused stays.</b>
   *
   * <p>An emptied box loses the addresses that still need fixing. A box left full makes somebody find
   * the three that failed among twenty. What is left is exactly the work remaining, which is also
   * what makes pressing the button again the right thing to do.</p>
   */
  it('leaves only the refused addresses in the box', () => {
    expect(sender).toContain('url.value = refused.join("\\n")');
    expect(sender).toContain('refused.push(one)');
  });

  /**
   * One bad address among twenty must not refuse the other nineteen — every URL goes through the
   * server's own refusals individually.
   */
  it('keeps going after one address is refused', () => {
    const loop = sender.slice(sender.indexOf('for (const one of lines)'));

    // The catch is inside the loop, so a thrown request is one address rather than the batch.
    expect(loop.indexOf('catch')).toBeLessThan(loop.indexOf('url.value = refused'));
  });

  /**
   * A single failure still reads as it always did. A tally of one — «Started 0. 1 could not be
   * started» — is worse English than the sentence it replaced, for the case that was the whole
   * feature until now.
   */
  it('still says the plain thing when one address fails alone', () => {
    expect(sender).toContain('taken === 0 && refused.length === 1');
  });
});
