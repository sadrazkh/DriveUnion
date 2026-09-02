import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Pasting a screenshot into the upload screen.
 *
 * <p>The handler lives in <c>UploadPanel.vue</c> and this project has no DOM to mount a component
 * in, so what is held here is the source: that the two decisions worth getting wrong are still made
 * the way they were argued. Both are the kind that break silently — a paste that stops working in a
 * text field is noticed by the person typing a URL, not by a suite.</p>
 */

const panel = readFileSync(
  resolve(import.meta.dirname, '../islands/UploadPanel.vue'),
  'utf8',
);

describe('a paste that lands on the upload screen', () => {
  /**
   * <b>The one that would break the feature beside it.</b>
   *
   * <p>The link box on this very screen is a text input somebody pastes a URL into. A document-level
   * paste handler that swallowed every paste would take that away, and the failure looks like the
   * link box being broken rather than like the paste handler being greedy.</p>
   */
  it('leaves a paste inside a field entirely alone', () => {
    expect(panel).toContain('input, textarea, [contenteditable]');

    // The guard returns before anything else happens, so the browser does the ordinary thing.
    const at = panel.indexOf('function onPaste');
    const body = panel.slice(at, panel.indexOf('function named'));

    expect(body.indexOf('closest')).toBeLessThan(body.indexOf('preventDefault'));
  });

  /**
   * `preventDefault` comes after the check for files, not before it. A paste carrying only text is
   * one this screen has no business swallowing — somebody may be pasting into something else on the
   * page entirely.
   */
  it('only takes the paste when it actually carried a file', () => {
    const at = panel.indexOf('function onPaste');
    const body = panel.slice(at, panel.indexOf('function named'));

    expect(body.indexOf('files.length === 0')).toBeLessThan(body.indexOf('preventDefault'));
  });

  /**
   * A pasted image is called `image.png` every time, so five screenshots become five files with one
   * name — a workspace nobody can read a week later.
   */
  it('gives a pasted image a name that tells it from the next one', () => {
    const at = panel.indexOf('function named');
    const body = panel.slice(at, panel.indexOf('</script>'));

    expect(body).toContain('image.png');
    expect(body).toContain('pasted-');

    // Local time rather than UTC: it is a label a person reads to tell one from another, and «which
    // is the one from this morning» is a question about their morning.
    expect(body).toContain('getFullYear');
    expect(body).not.toContain('toISOString');
  });

  /** A file that arrived with a real name keeps it — only the clipboard's placeholder is replaced. */
  it('leaves a name that was already a name', () => {
    const at = panel.indexOf('function named');
    const body = panel.slice(at, panel.indexOf('</script>'));

    expect(body).toContain('return file;');
  });

  /**
   * Added on mount and removed on unmount. The panel is inside the region a navigation replaces, so
   * a listener left behind is one more handler on the document per visit to this screen — and every
   * one of them would answer the next paste.
   */
  it('is taken off the document when the screen goes', () => {
    expect(panel).toContain('document.addEventListener("paste", onPaste)');
    expect(panel).toContain('document.removeEventListener("paste", onPaste)');
  });
});
