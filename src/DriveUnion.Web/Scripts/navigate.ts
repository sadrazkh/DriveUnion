/**
 * Same-origin navigation without a page load.
 *
 * A `File` handle does not survive one, and that single fact decides this file. The product's claim
 * is a 96 GB upload; a Service Worker could only read such a file after a navigation by first
 * copying it somewhere the worker can reach, and copying 96 GB into IndexedDB to avoid reloading a
 * page is not a trade. So the page stops reloading instead. A left-click on a same-origin link is
 * fetched, the response's `main.app-content` replaces this one, and everything above it — the
 * sidebar, the header, and the upload queue that lives in the shell — is never torn down.
 *
 * All of it is interception. The markup is ordinary links and ordinary forms; with no bundle, or a
 * bundle that failed to load, every one of them still works and lands on the same page. Nothing in a
 * view may be written so that it only makes sense while this file is running.
 *
 * The rule for anything unexpected is the same throughout: hand the address back to the browser. A
 * swap that quietly does nothing leaves the reader looking at a page that did not change, which is a
 * worse failure than the reload this file exists to avoid.
 */

/**
 * The element a navigation replaces, and this file's contract with _Layout.cshtml.
 *
 * IslandRegistrationTests reads this string out of this file and looks for the element in the
 * layout. The two halves are written in different languages, they are owned by different people, and
 * that test is the only place they can be compared: renaming the class in Razor would not fail a
 * build, it would just make every link in the panel fall back to a full page load — silently, and
 * with the upload queue dying on each one.
 */
const ContentSelector = 'main.app-content';

/** Where a history entry keeps the reader's place, so Back returns to it rather than to the top. */
const ScrollKey = 'duScrollY';

/** A push is a click; a pop is Back or Forward, where the address bar has already moved. */
type Mode = 'push' | 'pop';

export interface SwapHooks {
  /** The region that is about to leave the document, while it is still attached and still whole. */
  leaving: (region: HTMLElement) => void;

  /** The region that has just arrived and is now in the document. */
  entered: (region: HTMLElement) => void;
}

/**
 * The swap, reachable from code that has no anchor to click.
 *
 * <p>Null until <see cref="startNavigation"/> has run, and on any page that has no swappable region
 * at all. <see cref="visit"/> is the only reader.</p>
 */
let swapTo: ((url: URL, mode: Mode) => Promise<void>) | null = null;

/**
 * Goes somewhere the way a link would, from a place with no link.
 *
 * <p><b>It has to be the swap and not <c>location.href</c>.</b> A drop on the files screen stages
 * <c>File</c> handles in the upload store, which lives above the region a navigation replaces — and
 * a real page load takes the whole document with it, handles included. The reader would be moved to
 * the upload screen and find it empty, having watched their files being accepted a moment earlier.
 * See Scripts/dropAnywhere.ts, which is the reason this exists.</p>
 *
 * <p>The hard navigation is kept only for the case where there is no swap to be had. Nothing that
 * holds unsaved state can reach that branch: it is the public download page, which has no queue.</p>
 */
export function visit(href: string): void {
  const url = sameOrigin(href);
  if (url === null) return;

  if (swapTo === null) {
    location.href = url.href;
    return;
  }

  void swapTo(url, 'push');
}

export function startNavigation(hooks: SwapHooks): void {
  // No region to swap: the public download page wears its own chrome-less layout, has no upload
  // queue to protect, and its links are ordinary links. Nothing below is installed there.
  if (document.querySelector(ContentSelector) === null) return;

  // We restore the scroll ourselves, after the content has arrived. Left on "auto" the browser
  // restores it against a page that is still the old one, which is a jump to a position that means
  // nothing followed by our own. The cost is that a genuine reload now starts at the top.
  history.scrollRestoration = 'manual';

  // A real navigation announces the new page; a swap is silent, and a reader who cannot see the
  // screen is left believing the link did nothing. Built here rather than in the layout because it
  // is only true while this file is running — with no bundle there is no swap to announce.
  const announcer = document.createElement('p');
  announcer.className = 'visually-hidden';
  announcer.setAttribute('role', 'status');
  announcer.setAttribute('aria-live', 'polite');
  document.body.append(announcer);

  /** The address whose content is on screen. Not `location.href`, which moves before we do. */
  let showing = location.href;

  /** The fetch in flight, if any. A newer navigation abandons it rather than racing it. */
  let pending: AbortController | null = null;

  let scrollWrite = 0;

  document.addEventListener('click', (event) => {
    // defaultPrevented first: a page that has already answered this click owns it. The accounts
    // screen's «برو به تنظیمات» is one of those.
    if (event.defaultPrevented || event.button !== 0) return;

    // A modified click is the reader asking for a new tab, a window, a download or a saved link.
    // Every one of those is the browser's answer to give, and none of them reloads this page.
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;

    const from = event.target;
    if (!(from instanceof Element)) return;

    const anchor = from.closest('a');
    if (!(anchor instanceof HTMLAnchorElement)) return;

    const url = swappableLink(anchor);
    if (url === null) return;

    event.preventDefault();
    void navigate(url, 'push');
  });

  document.addEventListener('submit', (event) => {
    if (event.defaultPrevented) return;

    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;

    // GET only. Every POST in the panel either writes something the server must own the redirect
    // for — the culture cookie, the sign-out — or targets a popup window, and a fetch of a POST
    // would leave the browser's own resubmission story broken for the sake of one round trip.
    if (form.method.toLowerCase() !== 'get') return;
    if (form.target.length > 0 && form.target !== '_self') return;

    const url = sameOrigin(form.action);
    if (url === null) return;

    const query = new URLSearchParams();

    for (const [name, value] of new FormData(form).entries()) {
      // A GET form has no business carrying a file, and a File has no spelling in a query string.
      // Anything that does gets the browser's own submission instead of a guess at one.
      if (typeof value !== 'string') return;
      query.append(name, value);
    }

    // The button that was pressed carries its own name/value pair when it has one, which is how a
    // form with two submit buttons says which was used. Native submission includes it; so does this.
    const submitter = event.submitter;
    if (
      (submitter instanceof HTMLButtonElement || submitter instanceof HTMLInputElement)
      && submitter.name.length > 0
    ) {
      query.append(submitter.name, submitter.value);
    }

    // Assigned rather than merged, because that is what a browser does: a GET submission replaces
    // the action's query string entirely.
    url.search = query.toString();
    url.hash = '';

    event.preventDefault();
    void navigate(url, 'push');
  });

  addEventListener('popstate', () => {
    const next = new URL(location.href);
    const current = new URL(showing);

    // Back over a fragment jump — same page, different anchor. There is nothing to fetch, and with
    // scrollRestoration on "manual" the browser will not move for us either.
    if (next.pathname === current.pathname && next.search === current.search) {
      showing = next.href;
      restoreScroll(next, 'pop');
      return;
    }

    void navigate(next, 'pop');
  });

  // The reader's place on the page they are about to leave, recorded while they still are on it:
  // popstate fires after the entry has already changed, so there is no later moment to ask.
  //
  // Debounced, and not only for the sake of the main thread: Safari throttles replaceState to about
  // a hundred calls per thirty seconds and starts throwing after that, so one write per scroll is a
  // broken back button on one browser.
  addEventListener('scroll', () => {
    window.clearTimeout(scrollWrite);
    scrollWrite = window.setTimeout(rememberScroll, 250);
  }, { passive: true });

  // Published for `visit`, once there is something to publish. Assigned here rather than at the top
  // of `startNavigation` so a page that gave up above never hands out a swap it cannot perform.
  swapTo = navigate;

  async function navigate(url: URL, mode: Mode): Promise<void> {
    const region = document.querySelector<HTMLElement>(ContentSelector);
    if (region === null) {
      giveUp(url, mode);
      return;
    }

    pending?.abort();
    const controller = new AbortController();
    pending = controller;

    // aria-busy rather than a spinner: this file ships no CSS and owns none, and this is the half of
    // a loading state that assistive technology actually reads.
    region.setAttribute('aria-busy', 'true');

    const abandon = (): void => {
      region.removeAttribute('aria-busy');
      giveUp(url, mode);
    };

    let response: Response;
    try {
      response = await fetch(url.href, {
        headers: { Accept: 'text/html' },
        credentials: 'same-origin',
        signal: controller.signal,
      });
    } catch {
      // An abort is a newer navigation taking over, and it owns the page now. Anything else is the
      // network, and the browser says so better than a blank region would.
      if (controller.signal.aborted) return;
      abandon();
      return;
    }

    if (controller !== pending) return;

    // Where the response actually came from, which after a redirect is not what was asked for — an
    // expired session answers /Files with the sign-in page. That is the address to push.
    const landed = response.url.length > 0 ? new URL(response.url) : url;

    if (
      !response.ok
      || landed.origin !== location.origin
      || !(response.headers.get('Content-Type') ?? '').includes('text/html')
    ) {
      abandon();
      return;
    }

    let html: string;
    try {
      html = await response.text();
    } catch {
      if (controller.signal.aborted) return;
      abandon();
      return;
    }

    if (controller !== pending) return;
    pending = null;

    const incoming = new DOMParser().parseFromString(html, 'text/html');
    const arriving = incoming.querySelector<HTMLElement>(ContentSelector);

    if (arriving === null) {
      abandon();
      return;
    }

    // A page that brings its own script cannot be swapped in. Markup inserted as parsed nodes never
    // executes its scripts, so the page would arrive looking correct and behaving as though the
    // bundle had failed — /Identity/Account/Setup would render its password suggestion box with
    // nothing behind the button. The shell's own scripts are in <head>; a script in the body is a
    // page's own `section Scripts` and nothing else.
    if (incoming.body.querySelector('script') !== null) {
      abandon();
      return;
    }

    // Recorded before the swap and not after: replacing the content changes the height of the
    // document, and a browser clamps the scroll position when the page it is on gets shorter. Asked
    // afterwards, this would file the new page's position under the old page's entry, and Back would
    // return to a place the reader has never been.
    if (mode === 'push') rememberScroll();

    hooks.leaving(region);
    region.replaceWith(document.adoptNode(arriving));

    document.title = incoming.title;
    refreshShell(incoming);

    // Before the islands, because from the line above the content on the screen is already the new
    // page's. An island that throws while mounting is a bug with a stack trace; an address bar that
    // still names the previous page is a bug with nothing at all, and every link on the screen would
    // then resolve against the wrong place.
    if (mode === 'push') history.pushState({ [ScrollKey]: 0 }, '', landed.href);
    showing = landed.href;

    hooks.entered(arriving);

    restoreScroll(landed, mode);
    focusContent(arriving);

    // Cleared first, then set: the live region only speaks when its text changes, and two screens in
    // the panel can carry the same title. The gap is what gives the change time to be noticed.
    announcer.textContent = '';
    window.setTimeout(() => {
      announcer.textContent = document.title;
    }, 50);
  }
}

/**
 * The link this click is for, or null to let the browser have it.
 *
 * Everything refused here is refused because the browser's answer is better than a swap's, not
 * because a swap is hard: a download, a new tab, another origin, a jump within this page.
 */
function swappableLink(anchor: HTMLAnchorElement): URL | null {
  const href = anchor.getAttribute('href');
  if (href === null || href.length === 0) return null;

  // A bare fragment. The browser scrolls, pushes its own entry and does it without a round trip.
  if (href.startsWith('#')) return null;

  if (anchor.hasAttribute('download')) return null;
  if (anchor.target.length > 0 && anchor.target !== '_self') return null;
  if (anchor.relList.contains('external')) return null;

  // href is resolved against the document here, so a relative link is compared correctly. mailto:
  // and tel: have no origin and fall out of the same check.
  const url = sameOrigin(anchor.href);
  if (url === null) return null;

  // An anchor into the page already on screen. Swapping would fetch what is already here and throw
  // away the position the browser was about to scroll to.
  if (url.hash.length > 0 && url.pathname === location.pathname && url.search === location.search) {
    return null;
  }

  return url;
}

function sameOrigin(href: string): URL | null {
  let url: URL;
  try {
    url = new URL(href, location.href);
  } catch {
    return null;
  }

  if (url.origin !== location.origin) return null;
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return null;

  return url;
}

/**
 * The parts of the shell that are facts about the response rather than about the reader.
 *
 * A swap leaves them describing the page that was left. Each one below goes stale in a way somebody
 * would report as a bug, and none of them holds a live handle.
 */
function refreshShell(incoming: Document): void {
  // The upload queue's antiforgery token. It is issued per response, and an upload that outlives ten
  // navigations has to send the current one — _Layout.cshtml says the same thing from its end. The
  // element is updated in place and never replaced: the queue reads it, and a new node would be a
  // second copy for the queue to not know about.
  const config = document.querySelector<HTMLElement>('[data-upload-config]');
  const freshConfig = incoming.querySelector<HTMLElement>('[data-upload-config]');

  if (config !== null && freshConfig !== null) {
    for (const attribute of Array.from(freshConfig.attributes)) {
      config.setAttribute(attribute.name, attribute.value);
    }
  }

  // Where the language switch comes back to. The header's form carries the page it was rendered on,
  // so without this a reader who walks to /Files and then switches language is sent back to wherever
  // they last loaded the shell — the switch would move them sideways and backwards at once.
  const here = document.querySelector<HTMLInputElement>('.app-header input[name="returnUrl"]');
  const freshHere = incoming.querySelector<HTMLInputElement>('.app-header input[name="returnUrl"]');

  if (here !== null && freshHere !== null) here.value = freshHere.value;

  // The sidebar: which item is drawn active, and the capacity card, whose figures the upload or the
  // delete the reader just made have already changed. It is server-rendered Razor holding no
  // JavaScript state, so re-rendering it costs nothing.
  //
  // Guarded, and skipped rather than forced. The dock is going into the shell and could land here;
  // replacing this markup with a mounted Vue app inside it would unmount that app without telling
  // it, and take the upload queue with it. A stale highlight is a blemish. A destroyed queue is the
  // product.
  const sidebar = document.querySelector<HTMLElement>('.app-sidebar');
  const freshSidebar = incoming.querySelector<HTMLElement>('.app-sidebar');

  if (
    sidebar !== null
    && freshSidebar !== null
    && sidebar.querySelector('[data-island], [data-upload-config]') === null
  ) {
    sidebar.innerHTML = freshSidebar.innerHTML;
  }
}

/**
 * Focus, which a swap otherwise drops on the floor.
 *
 * The link that was activated no longer exists, so the browser leaves focus on `<body>` and the next
 * Tab starts the whole page again from the top — past the sidebar, past the header, to reach content
 * the reader is already looking at. The attribute is set here and not in the markup because it is
 * only true while this file is running.
 */
function focusContent(region: HTMLElement): void {
  region.tabIndex = -1;
  region.focus({ preventScroll: true });

  // Taken off again on the way out so the region is not left carrying a focus ring's worth of
  // styling for the rest of its life.
  region.addEventListener('blur', () => region.removeAttribute('tabindex'), { once: true });
}

/** A new page starts at the top, an anchor at its anchor, and a Back where the reader was. */
function restoreScroll(url: URL, mode: Mode): void {
  if (mode === 'pop') {
    const stored = storedScroll();
    if (stored !== null) {
      window.scrollTo(0, stored);
      return;
    }
  }

  const anchor = url.hash.length > 1
    ? document.getElementById(decodeURIComponent(url.hash.slice(1)))
    : null;

  if (anchor !== null) {
    anchor.scrollIntoView();
    return;
  }

  window.scrollTo(0, 0);
}

function rememberScroll(): void {
  const state = (history.state ?? {}) as Record<string, unknown>;
  history.replaceState({ ...state, [ScrollKey]: window.scrollY }, '');
}

function storedScroll(): number | null {
  const state = history.state as Record<string, unknown> | null;
  const stored = state?.[ScrollKey];

  return typeof stored === 'number' ? stored : null;
}

/**
 * Hand the address back to the browser.
 *
 * On a click the address bar has not moved, so it has to be told where to go. On a Back or Forward
 * it has already moved, and a reload lands on exactly the page the entry names — assigning the same
 * address there would push a duplicate entry and break the button that was just pressed.
 */
function giveUp(url: URL, mode: Mode): void {
  if (mode === 'pop') {
    location.reload();
    return;
  }

  location.assign(url.href);
}
