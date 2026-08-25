/**
 * The files table's enhancement: a select-all box, a live count, and dragging files into folders.
 *
 * Strictly an enhancement. Everything it does can be done without it — the checkboxes are real
 * inputs in a real form, the «انتقال» button next to a folder picker moves whatever is ticked, and
 * the server does not know or care whether a move arrived from a drag or from a press. What this
 * adds is the gesture people expect from a file manager, and it adds nothing that is only reachable
 * through the gesture.
 *
 * No framework. It is three listeners on one element and a fetch, which is a Vue app's worth of
 * bundle for a screen that already renders whole on the server.
 */

/** The one form the table lives in. Every id below is one of its checkboxes. */
type Grid = HTMLFormElement;

interface Wiring {
  readonly teardown: () => void;
}

/** What a drag carries. Set on dragstart, read on drop, cleared on dragend. */
let dragging: readonly string[] = [];

export function mountFileGrid(root: HTMLElement): Wiring {
  const grid = root.matches('[data-file-grid]')
    ? (root as Grid)
    : root.querySelector<Grid>('[data-file-grid]');

  if (grid === null) return { teardown: () => {} };

  // One controller for every listener this mount adds, so a navigation takes all of them out at
  // once. The alternative is remembering each one, which is how a listener gets left behind on a
  // detached node once per page the reader visits.
  const leaving = new AbortController();
  const on = { signal: leaving.signal } as const;

  const boxes = () => [...grid.querySelectorAll<HTMLInputElement>('input.rowcheck')];
  const ticked = () => boxes().filter((b) => b.checked);

  const count = grid.querySelector<HTMLElement>('[data-selection-count]');
  const lang = document.documentElement.lang === 'en' ? 'en' : 'fa';

  function say(): void {
    if (count === null) return;

    const n = ticked().length;

    // Written here rather than fetched from the catalogue, because it is one number in two
    // languages and a round trip to render it would be a round trip per click.
    count.textContent = n === 0
      ? ''
      : lang === 'en'
        ? n === 1 ? '1 selected' : `${n} selected`
        : `${persian(n)} انتخاب شده`;

    for (const row of grid.querySelectorAll<HTMLElement>('.dtable-row[data-file]')) {
      const box = row.querySelector<HTMLInputElement>('input.rowcheck');
      row.classList.toggle('is-selected', box?.checked === true);
    }

    if (all !== null) {
      const total = boxes().length;
      all.checked = n > 0 && n === total;
      all.indeterminate = n > 0 && n < total;
    }
  }

  // The header's select-all, drawn hidden by the view so that a reader with no script never meets a
  // control that does nothing.
  const slot = grid.querySelector<HTMLElement>('[data-select-all]');
  let all: HTMLInputElement | null = null;

  if (slot !== null) {
    all = document.createElement('input');
    all.type = 'checkbox';
    all.className = 'rowcheck';
    all.setAttribute('aria-label', lang === 'en' ? 'Select all' : 'انتخاب همه');
    slot.replaceChildren(all);
    slot.hidden = false;

    all.addEventListener('change', () => {
      for (const box of boxes()) box.checked = all!.checked;
      say();
    }, on);
  }

  grid.addEventListener('change', (event) => {
    if ((event.target as HTMLElement | null)?.matches('input.rowcheck') === true) say();
  }, on);

  // ---------------------------------------------------------------- dragging

  grid.addEventListener('dragstart', (event) => {
    const row = (event.target as HTMLElement | null)?.closest<HTMLElement>('[data-file]');
    const id = row?.dataset.file;

    if (id === undefined) return;

    // Dragging an unticked row takes that row alone; dragging a ticked one takes the whole
    // selection. That is the rule every file manager uses, and the alternative — always the
    // selection — means picking up one file after ticking twenty moves all twenty.
    const box = row!.querySelector<HTMLInputElement>('input.rowcheck');
    const chosen = ticked().map((b) => b.value);

    dragging = box?.checked === true && chosen.length > 0 ? chosen : [id];

    event.dataTransfer?.setData('text/plain', dragging.join(','));
    if (event.dataTransfer !== null) event.dataTransfer.effectAllowed = 'move';
  }, on);

  document.addEventListener('dragend', () => {
    dragging = [];
    for (const target of document.querySelectorAll('.is-drop-target')) {
      target.classList.remove('is-drop-target');
    }
  }, on);

  // Delegated on the document rather than on the grid: the breadcrumb is a drop target too — that
  // is how a file goes *up* — and it is drawn outside the table.
  document.addEventListener('dragover', (event) => {
    const target = dropTarget(event);
    if (target === null) return;

    // Both calls, and both matter: without the first the browser refuses the drop, and without the
    // second the cursor says «copy» for something that moves.
    event.preventDefault();
    if (event.dataTransfer !== null) event.dataTransfer.dropEffect = 'move';

    target.classList.add('is-drop-target');
  }, on);

  document.addEventListener('dragleave', (event) => {
    dropTarget(event)?.classList.remove('is-drop-target');
  }, on);

  document.addEventListener('drop', (event) => {
    const target = dropTarget(event);
    if (target === null) return;

    event.preventDefault();
    target.classList.remove('is-drop-target');

    const ids = dragging.length > 0
      ? [...dragging]
      : (event.dataTransfer?.getData('text/plain') ?? '').split(',').filter((id) => id.length > 0);

    if (ids.length === 0) return;

    move(grid, ids, target.dataset.dropFolder ?? '');
  }, on);

  say();

  return { teardown: () => leaving.abort() };
}

/**
 * The folder under the pointer, or null.
 *
 * A drop onto the folder a file is already in is not a target: it would be a round trip and a page
 * load to change nothing, and the row flashing as droppable is a promise of something happening.
 */
function dropTarget(event: DragEvent): HTMLElement | null {
  if (dragging.length === 0 && event.type !== 'drop') return null;

  return (event.target as HTMLElement | null)?.closest<HTMLElement>('[data-drop-folder]') ?? null;
}

/**
 * Sends the move by submitting the form the table is already in.
 *
 * <p>A native submit and not a fetch. The form carries the antiforgery token, the search term and
 * the folder in its action, and the server answers with a redirect to where the files went — so
 * this is the same request the «انتقال» button makes, from the same form, and the two cannot
 * behave differently.</p>
 *
 * <p>The first version did fetch it and then set <code>location.href</code> to the response's URL,
 * which worked and swallowed the confirmation: the notice lives in TempData, TempData is read once,
 * and the fetch's own redirect-following GET was what read it. The reader saw the files move and
 * was told nothing. A native submit is one request instead of three and keeps the sentence.</p>
 *
 * <p>The dragged ids are written onto the checkboxes rather than into a body, because the form is
 * what is being posted: after this the boxes say exactly what is about to be moved, which is also
 * what the reader sees for the instant before the page turns.</p>
 */
function move(grid: Grid, ids: readonly string[], destination: string): void {
  const wanted = new Set(ids);

  for (const box of grid.querySelectorAll<HTMLInputElement>('input.rowcheck[name="ids"]')) {
    box.checked = wanted.has(box.value);
  }

  const select = grid.querySelector<HTMLSelectElement>('select[name="destination"]');
  if (select !== null) select.value = destination;

  // A label typed but not applied must not ride along with a move. The server ignores it for
  // act=move, and clearing it is what stops the next screen showing it half-typed.
  const label = grid.querySelector<HTMLInputElement>('input[name="label"]');
  if (label !== null) label.value = '';

  const act = document.createElement('input');
  act.type = 'hidden';
  act.name = 'act';
  act.value = 'move';
  grid.appendChild(act);

  grid.submit();
}

/** European digits are what a template literal produces; this panel writes Persian ones. */
function persian(value: number): string {
  return String(value).replace(/\d/g, (d) => '۰۱۲۳۴۵۶۷۸۹'[Number(d)]);
}
