import { beforeEach, describe, expect, it, vi } from 'vitest';

const visit = vi.fn();

vi.mock('./navigate', () => ({ visit: (href: string) => visit(href) }));

const { mountDropAnywhere } = await import('./dropAnywhere');

/**
 * Dropping files anywhere in the panel.
 *
 * <p>There is no DOM in this project's test setup — no jsdom, no happy-dom — so the browser is
 * hand-rolled here exactly as <c>store.test.ts</c> hand-rolls XHR. That is not a shortcut: what this
 * file has to pin down is which drags are claimed and which are let through, and both of those are
 * decisions the module makes about the event before it touches anything a real DOM would provide.</p>
 *
 * <p>The one thing a real browser would add is whether the listeners fire at all, and the answer to
 * that is that they are registered on <c>document</c> by name, which is asserted.</p>
 */

// ── the browser, as much of it as a drop needs ─────────────────────────────────────────────────────

/** Stands in for a DOM element, with the one method the module asks for. */
class FakeElement {
  constructor(private readonly inUploadPanel = false) {}

  closest(selector: string): FakeElement | null {
    return selector === '[data-island="upload-panel"]' && this.inUploadPanel ? this : null;
  }
}

let listeners: Map<string, ((event: unknown) => void)[]>;
let sheet: Record<string, unknown>;

function define(name: string, value: unknown) {
  Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
}

function fire(type: string, event: Record<string, unknown>) {
  for (const listener of listeners.get(type) ?? []) listener(event);
}

/**
 * A drag event. `types` is what decides whether this module wants it at all: a row being dragged
 * onto a folder carries the grid's own payload and no `Files`, and claiming those would make moving
 * a file into a folder also offer to upload it.
 */
function drag(types: string[], target: FakeElement, files: unknown[] = []) {
  const transfer = { types, files, dropEffect: '' };

  return {
    dataTransfer: transfer,
    target,
    preventDefault: vi.fn(),
    transfer,
  };
}

beforeEach(() => {
  listeners = new Map();
  visit.mockClear();

  sheet = { className: '', hidden: false, textContent: '', setAttribute: vi.fn(), remove: vi.fn() };

  const subscribe = (type: string, listener: (event: unknown) => void) => {
    listeners.set(type, [...(listeners.get(type) ?? []), listener]);
  };

  define('Element', FakeElement);
  define('document', {
    createElement: () => sheet,
    body: { appendChild: vi.fn() },
    addEventListener: subscribe,
    removeEventListener: vi.fn(),
  });
  define('window', { addEventListener: subscribe, removeEventListener: vi.fn() });
  define('location', { pathname: '/files' });
});

const anywhere = new FakeElement();
const insideUploadPanel = new FakeElement(true);

describe('dropping files on any panel screen', () => {
  it('listens for the four drag events and for the window losing focus', () => {
    const add = vi.fn();
    mountDropAnywhere(add, 'Drop it');

    expect([...listeners.keys()].sort())
      .toEqual(['blur', 'dragenter', 'dragleave', 'dragover', 'drop']);
  });

  /** <b>The feature.</b> Files land in the queue and the reader is taken to where the queue is. */
  it('stages what was dropped and goes to the upload screen', () => {
    const add = vi.fn();
    mountDropAnywhere(add, 'Drop it');

    const files = [{ name: 'holiday.mp4' }];
    const event = drag(['Files'], anywhere, files);

    fire('drop', event);

    expect(event.preventDefault).toHaveBeenCalled();
    expect(add).toHaveBeenCalledWith(files);
    expect(visit).toHaveBeenCalledWith('/files/upload');
  });

  /**
   * The other drag this panel has. A row dragged onto a folder is how files are moved, and it
   * carries no `Files` — so this module must not so much as call preventDefault on it, or the grid's
   * own handler would be working against a default that had already been cancelled.
   */
  it('ignores a drag that is not carrying files from outside', () => {
    const add = vi.fn();
    mountDropAnywhere(add, 'Drop it');

    const event = drag(['application/x-driveunion-rows'], anywhere);

    fire('dragover', event);
    fire('drop', event);

    expect(event.preventDefault).not.toHaveBeenCalled();
    expect(add).not.toHaveBeenCalled();
    expect(visit).not.toHaveBeenCalled();
  });

  /**
   * The upload screen's own dropzone is inside the region this listener covers, and it has its own
   * handler. Both running means one drop and two files.
   */
  it('leaves a drop on the upload panel to the upload panel', () => {
    const add = vi.fn();
    mountDropAnywhere(add, 'Drop it');

    fire('drop', drag(['Files'], insideUploadPanel, [{ name: 'a.mp4' }]));

    expect(add).not.toHaveBeenCalled();
    expect(visit).not.toHaveBeenCalled();
  });

  /** Already there: staging is the whole job, and a navigation to this page would be a flicker. */
  it('does not navigate when the upload screen is already open', () => {
    define('location', { pathname: '/files/upload' });

    const add = vi.fn();
    mountDropAnywhere(add, 'Drop it');

    fire('drop', drag(['Files'], anywhere, [{ name: 'a.mp4' }]));

    expect(add).toHaveBeenCalled();
    expect(visit).not.toHaveBeenCalled();
  });

  /**
   * Nested elements fire dragleave as the pointer crosses between them. A plain show/hide pair
   * flickers for the whole time a file is held over the page, which is the entire time the sheet is
   * meant to be telling somebody where to let go.
   */
  it('keeps the sheet up while the pointer crosses between elements', () => {
    mountDropAnywhere(vi.fn(), 'Drop it');

    fire('dragenter', drag(['Files'], anywhere));
    expect(sheet.hidden).toBe(false);

    // Into a child: one more enter, then the leave from the parent.
    fire('dragenter', drag(['Files'], anywhere));
    fire('dragleave', drag(['Files'], anywhere));

    expect(sheet.hidden).toBe(false);

    fire('dragleave', drag(['Files'], anywhere));
    expect(sheet.hidden).toBe(true);
  });

  /** A drag that ends outside the window never fires drop, and the sheet would stay up for ever. */
  it('takes the sheet down when the window loses focus mid-drag', () => {
    mountDropAnywhere(vi.fn(), 'Drop it');

    fire('dragenter', drag(['Files'], anywhere));
    expect(sheet.hidden).toBe(false);

    fire('blur', {});
    expect(sheet.hidden).toBe(true);
  });

  it('asks for a copy cursor rather than the move one the panel uses for folders', () => {
    mountDropAnywhere(vi.fn(), 'Drop it');

    const event = drag(['Files'], anywhere);
    fire('dragover', event);

    expect(event.preventDefault).toHaveBeenCalled();
    expect(event.transfer.dropEffect).toBe('copy');
  });
});
