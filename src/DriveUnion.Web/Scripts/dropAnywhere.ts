import { visit } from './navigate';

/**
 * Dropping files on any panel screen, the way every upload site behaves.
 *
 * <p>Before this, the only place a file could be dropped was the upload screen's own dropzone. That
 * is the one screen somebody is least likely to already be looking at: files arrive while you are in
 * a folder, or on the dashboard, and the answer was to go and find the upload page first and then go
 * back and find the file again.</p>
 *
 * <p><b>The drop stages; it does not send.</b> Files land in the shared queue as `staged` and the
 * reader is taken to the upload screen to look at what they have got — see uploads/store.ts. A drop
 * that started an 8 GB transfer because a folder was dragged an inch too far is exactly the thing
 * the staging step exists to prevent, and making one entry point behave differently from the other
 * would put it straight back.</p>
 */

/** Where a drop takes the reader, and where the queue is drawn. */
const UploadScreen = '/files/upload';

/** What a mount leaves behind. */
export interface DropAnywhere {
  teardown: () => void;
}

/**
 * Whether this drag is files from outside the browser.
 *
 * <p>The panel has its own drags — a row onto a folder, which is how files are moved — and those
 * carry the grid's own data and no `Files` entry. Without this check, dragging a file into a folder
 * would also offer to upload it, which is the same file arriving twice by two routes.</p>
 */
function carriesFiles(transfer: DataTransfer | null): boolean {
  return transfer !== null && Array.from(transfer.types).includes('Files');
}

/**
 * Whether something else has already claimed this drop.
 *
 * <p>The upload screen's own dropzone is a real element with a real handler, and it is inside the
 * region this listener covers. Both firing means one drop and two `add` calls.</p>
 */
function alreadyHandled(target: EventTarget | null): boolean {
  return target instanceof Element && target.closest('[data-island="upload-panel"]') !== null;
}

/**
 * Starts listening. `add` is the store's, so the queue that receives the files is the one that
 * survives the navigation this then performs.
 */
export function mountDropAnywhere(
  add: (files: FileList | File[]) => void,
  label: string,
): DropAnywhere {
  const sheet = document.createElement('div');
  sheet.className = 'drop-sheet';
  sheet.hidden = true;

  // aria-hidden, and the label is for the eye only: this appears in response to a drag, which is a
  // pointer gesture, and a screen reader user is not the one who needs to be told where to let go.
  sheet.setAttribute('aria-hidden', 'true');
  sheet.textContent = label;

  document.body.appendChild(sheet);

  /**
   * Nested elements fire dragleave as the pointer crosses between them, so a plain
   * enter-shows/leave-hides pair flickers the whole time the file is over the page. Counting them
   * is the usual answer and it is the correct one: the sheet goes when as many leaves have been
   * seen as enters.
   */
  let depth = 0;

  const show = () => {
    sheet.hidden = false;
  };

  const hide = () => {
    depth = 0;
    sheet.hidden = true;
  };

  const onEnter = (event: DragEvent) => {
    if (!carriesFiles(event.dataTransfer) || alreadyHandled(event.target)) return;

    depth++;
    show();
  };

  const onOver = (event: DragEvent) => {
    if (!carriesFiles(event.dataTransfer) || alreadyHandled(event.target)) return;

    // Both, and both matter: without preventDefault the browser refuses the drop, and without
    // dropEffect the cursor says «move» over a page that is going to copy.
    event.preventDefault();

    if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy';
  };

  const onLeave = (event: DragEvent) => {
    if (!carriesFiles(event.dataTransfer)) return;

    depth = Math.max(0, depth - 1);
    if (depth === 0) sheet.hidden = true;
  };

  const onDrop = (event: DragEvent) => {
    if (!carriesFiles(event.dataTransfer) || alreadyHandled(event.target)) {
      hide();
      return;
    }

    // Without this the browser navigates to the file, replacing the panel with the video the reader
    // meant to upload — which also takes the queue with it.
    event.preventDefault();
    hide();

    const files = event.dataTransfer?.files;
    if (!files || files.length === 0) return;

    add(files);

    // Soft, so the store above the swapped region keeps the handles. See navigate.ts `visit`.
    if (location.pathname.toLowerCase() !== UploadScreen) visit(UploadScreen);
  };

  document.addEventListener('dragenter', onEnter);
  document.addEventListener('dragover', onOver);
  document.addEventListener('dragleave', onLeave);
  document.addEventListener('drop', onDrop);

  // A drag that ends outside the window never fires drop, and the sheet would stay up over a page
  // with nothing happening to it.
  window.addEventListener('blur', hide);

  return {
    teardown: () => {
      document.removeEventListener('dragenter', onEnter);
      document.removeEventListener('dragover', onOver);
      document.removeEventListener('dragleave', onLeave);
      document.removeEventListener('drop', onDrop);
      window.removeEventListener('blur', hide);
      sheet.remove();
    },
  };
}
