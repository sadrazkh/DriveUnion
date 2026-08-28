import { createApp } from 'vue';
import ShareLockedFile from './islands/ShareLockedFile.vue';
import ThemeLanguageToggle from './islands/ThemeLanguageToggle.vue';
import UnlockDownload from './islands/UnlockDownload.vue';
import UploadDock from './islands/UploadDock.vue';
import UploadPanel from './islands/UploadPanel.vue';
import { createUploadStore, type UploadConfig, type UploadStore } from './uploads/store';
import { mountCopyLink } from './copyLink';
import { mountLockFile } from './lockFile';
import { mountFileGrid } from './fileGrid';
import { mountGoogleConnect } from './googleConnect';
import { mountNavToggle } from './nav';
import { mountNotifications } from './notifications';
import { startNavigation } from './navigate';
import { registerServiceWorker } from './serviceWorker';

/**
 * The one entry Vite compiles. Everything the panel loads on every page hangs off this file.
 *
 * "Islands", not an SPA: Razor renders the page and we hydrate only the interactive nodes. A mount
 * point is any element carrying `data-island="<name>"`; its `data-*` attributes are the props. By
 * attribute rather than by id because a page can have more than one of the same island — the
 * theme control appears in the panel header and again on the public download card.
 *
 * Islands used to be mounted once, at load, and that was correct for exactly as long as every
 * navigation was a page load. It is not one any more (navigate.ts, and the 96 GB file that is the
 * reason), so an island now has two moments instead of one: mounted when its region arrives and
 * unmounted when its region leaves. Skipping the second half is not a tidiness question — it is a
 * Vue app and its listeners left behind on detached nodes, once per navigation, for as long as the
 * tab is open.
 */

/** What a mount leaves behind, so a swap can take it back out again. */
type IslandTeardown = () => void;

interface Island {
  /**
   * Where this island's mount points are drawn, and therefore which lifecycle it gets.
   *
   * 'content' is inside `main.app-content`: mounted on arrival, unmounted on departure, once per
   * navigation. 'shell' is outside it — the header's theme control, the upload dock — mounted once
   * at load and never touched again, which is the only reason an upload can outlive a navigation.
   *
   * Nothing here reads it at runtime. The swap unmounts whatever the departing region contains and
   * mounts whatever the arriving one does, so containment decides the behaviour and no declaration
   * can contradict it. IslandRegistrationTests reads it, and compares it against where the views
   * actually draw the mount point: an island written to be mounted once that is put inside the
   * swapped region is a leak per navigation, and one written to be re-mounted that is put in the
   * shell simply stops updating, and neither of those fails a build.
   */
  readonly region: 'content' | 'shell';

  /** Mounts on one element and returns the way back. */
  readonly mount: (el: HTMLElement) => IslandTeardown;
}

/**
 * The upload queue: one object, here, above everything a navigation touches.
 *
 * Two islands are views onto it — the dock in the shell and the upload screen inside the swapped
 * region — and neither owns it. The screen used to, which is exactly why an upload ended the moment
 * somebody clicked «فایل‌ها»: the view that held the queue was the view that left.
 *
 * Built on first use rather than at load, because the public download page runs this same bundle and
 * has no queue, no dock and no workspace behind it.
 */
let uploads: UploadStore | null = null;

const uploadStore = (): UploadStore => (uploads ??= createUploadStore(readUploadConfig));

/**
 * Read on every call, never captured. `data-upload-config` is re-rendered by every response and its
 * attributes are refreshed by every swap (navigate.ts), because the antiforgery token in it is
 * per-response and an upload outlives many responses. A value read once at mount would be the token
 * of whichever page the dock happened to be born on.
 */
function readUploadConfig(): UploadConfig {
  const el = document.querySelector<HTMLElement>('[data-upload-config]');

  return {
    beginUrl: el?.dataset.beginUrl ?? '/api/uploads',
    antiforgeryHeader: el?.dataset.antiforgeryHeader ?? '',
    antiforgeryToken: el?.dataset.antiforgeryToken ?? '',
    lang: el?.dataset.lang === 'en' ? 'en' : 'fa',
  };
}

const islands: Record<string, Island> = {
  'theme-language': {
    region: 'shell',
    mount: (el) => {
      const app = createApp(ThemeLanguageToggle, {
        lang: el.dataset.lang === 'en' ? 'en' : 'fa',
        showLanguage: el.dataset.showLanguage === 'true',
      });

      app.mount(el);
      return () => app.unmount();
    },
  },

  'upload-dock': {
    // The reason all of this exists. It is in the shell, outside the region a navigation replaces,
    // so it is mounted once and never unmounted — and the transfer it is drawing outlives every
    // link the reader presses.
    region: 'shell',
    mount: (el) => {
      const app = createApp(UploadDock, { store: uploadStore(), config: readUploadConfig });

      app.mount(el);
      return () => app.unmount();
    },
  },

  'upload-panel': {
    // The screen, which is a view and no longer an owner: it is unmounted on the way out like any
    // other content, and the queue it was drawing carries on in the dock.
    region: 'content',
    mount: (el) => {
      const app = createApp(UploadPanel, { store: uploadStore(), config: readUploadConfig });

      app.mount(el);
      return () => app.unmount();
    },
  },

  'unlock-download': {
    // Drawn on the public download card, which is its own layout with no swapped region in it — the
    // same reason the theme control there is a shell island. This page is loaded, used and left.
    region: 'shell',
    mount: (el) => {
      const app = createApp(UnlockDownload, {
        // The view wrote the header into the attribute; the C# that put it there never read it.
        header: JSON.parse(el.dataset.header ?? '{}'),
        downloadUrl: el.dataset.downloadUrl ?? '',
        fileName: el.dataset.fileName ?? 'download',

        // Empty for anything that cannot be played, which is the ordinary case. The view decides
        // this, not the bundle: whether a type is safe to hand to a media element is the same
        // judgement Previews makes for unlocked files, and it is made in one place.
        media: el.dataset.media === 'video' || el.dataset.media === 'audio' ? el.dataset.media : '',
        mimeType: el.dataset.mimeType ?? '',
        lang: el.dataset.lang === 'en' ? 'en' : 'fa',
      });

      app.mount(el);
      return () => app.unmount();
    },
  },

  'share-locked-file': {
    // Inside the detail panel, which is inside the swapped region — so it is mounted when a file is
    // selected and unmounted when the reader navigates away, like the table beside it.
    region: 'content',
    mount: (el) => {
      const app = createApp(ShareLockedFile, {
        header: JSON.parse(el.dataset.header ?? '{}'),
        fileName: el.dataset.fileName ?? '',
        lang: el.dataset.lang === 'en' ? 'en' : 'fa',
      });

      app.mount(el);
      return () => app.unmount();
    },
  },

  'file-grid': {
    // Inside the swapped region, and it must be: its listeners are on the document as well as on
    // the table, so a mount that outlived its page would answer drops for a table that has gone.
    region: 'content',
    mount: (el) => {
      const wiring = mountFileGrid(el);

      return () => wiring.teardown();
    },
  },

  'notifications': {
    // Inside the swapped region: the card is on one screen, and it holds a per-response antiforgery
    // token that a navigation replaces. A shell island here would be a mount that kept the first
    // token it ever saw and started answering 400 after one navigation.
    region: 'content',
    mount: (el) => {
      mountNotifications(el);

      // Nothing to take back, for the reason copy-link gives: every listener it adds is on a
      // descendant of `el`, which leaves the document with the rest of the region and is collected
      // with it. The empty function is the answer rather than the omission of one.
      return () => {};
    },
  },

  'lock-file': {
    // Drawn inside the file detail panel, which a navigation replaces.
    region: 'content',
    mount: (el) => {
      mountLockFile(el);

      // Nothing to take back, for the reason written on copy-link below.
      return () => {};
    },
  },

  'copy-link': {
    region: 'content',
    mount: (el) => {
      mountCopyLink(el);

      // Nothing to take back. Every listener it adds is on `el` itself, which leaves the document
      // with the rest of the region and is collected with it; removing them would be removing them
      // from a node already on its way out. The empty function is the answer, not the omission of
      // one — a mounter with no teardown does not compile.
      return () => {};
    },
  },
};

/** Every island currently on the page, and how to take each one off again. */
const live = new Map<HTMLElement, IslandTeardown>();

function mountIslands(root: ParentNode): void {
  for (const [name, island] of Object.entries(islands)) {
    root.querySelectorAll<HTMLElement>(`[data-island="${name}"]`).forEach((el) => {
      // A shell island is inside no region and is reached again by every document-wide pass; it is
      // mounted once and stays that way.
      if (live.has(el)) return;

      live.set(el, island.mount(el));
    });
  }
}

function unmountIslands(root: ParentNode): void {
  root.querySelectorAll<HTMLElement>('[data-island]').forEach((el) => {
    const teardown = live.get(el);
    if (teardown === undefined) return;

    // Dropped from the map before it is called, so a mount point this bundle does not know about —
    // and a teardown that throws — cannot leave a detached element referenced for ever.
    live.delete(el);
    teardown();
  });
}

mountIslands(document);

const nav = mountNavToggle();

// Enhances the accounts screen's OAuth form into a popup, and is a no-op on every other page.
mountGoogleConnect();

startNavigation({
  leaving: (region) => unmountIslands(region),

  entered: (region) => {
    mountIslands(region);

    // The <900px drawer is standing open over the page the reader has just left. A page load used
    // to close it; nothing does now unless this does.
    nav.close();

    // Not an island, and not registrable as one: its hooks are three different attributes on three
    // different screens, so there is no single mount point to name it by. It has to run again here
    // or the OAuth popup quietly stops working on any accounts screen a swap arrived at, and the
    // operator is back to a full round trip through Google with no sign that anything changed.
    //
    // Running it again is not free. On a screen that has connect forms it adds a second `message`
    // listener to window and removes neither, because it takes no AbortSignal and this file may not
    // give it one. What that costs is the previous screen's forms staying reachable: every listener
    // answers the same completed consent flow with the same reload, so the behaviour is identical
    // and only the memory is not. Teaching it to accept a signal is that file's change to make.
    mountGoogleConnect();
  },
});

// The service worker, which is what makes this installable rather than merely bookmarkable: the
// shell and its assets on disk, and a page that says «no connection» instead of the browser's error
// screen. It is registered from here because here is the one file every page in the panel loads,
// and because there must be exactly one registration — see Scripts/serviceWorker.ts, which is also
// where anything needing the worker itself asks for it rather than registering a second one.
//
// After the islands on purpose. An install fetches the offline page, and the screen the reader is
// looking at has the better claim on that connection.
registerServiceWorker();

// Not every navigation can be a swap, and the ones that cannot still end the queue: a cross-origin
// link, a POST, a reload, and every fallback navigate.ts makes when a response is not something it
// can swap in. The File handles go with the page and there is no way to get them back — the plan
// says so, and says the honest behaviour is to admit it rather than appear to resume.
//
// This is the last moment anything can be said. The browser writes the sentence, not us, and shows
// it only if the reader has interacted with the page — which somebody who started an upload has.
//
// `unfinished` rather than `busy`: a transfer the phone stopped is as lost as one in flight — the
// File handle goes with the page either way — and on a phone stopping is the ordinary case, not the
// rare one. Asking `busy` here meant the browser warned about a running upload and said nothing at
// all about the four the customer had just been told would carry on when they came back.
addEventListener('beforeunload', (event) => {
  if (uploads?.unfinished.value !== true) return;

  event.preventDefault();

  // The deprecated half, and still the one Safari and Firefox act on.
  event.returnValue = '';
});
