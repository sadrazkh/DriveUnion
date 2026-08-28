# Drive Union as a phone app — plan

Written 2026-08-27. The ask: install it on an iPhone home screen, and manage files from there.

## What is already true

Three things make this much smaller than it looks:

- **`Scripts/navigate.ts` never reloads the page.** It exists because a `File` handle does not
  survive a navigation and the product claims a 96 GB upload, so a left-click swaps
  `main.app-content` and the shell stays up. That is the app-shell a PWA otherwise has to be
  rewritten to get.
- **Uploads already resume**, and they resume against *the server's own byte count* rather than
  what the client believes it sent (`Scripts/uploads/store.ts`).
- **`viewport-fit=cover`** is already on the viewport meta in both layouts.

And three things are simply absent: no icon of any kind (not even a favicon), no manifest, no
service worker, and **no `env(safe-area-inset-*)` anywhere in the CSS**. The panel's smallest
breakpoint is 900px; the 760px one only touches the public download card. On a notched iPhone in
standalone mode the header runs under the status bar and the sidebar foot under the home indicator.

## What iOS will not do, decided rather than discovered later

| Wanted | On iOS |
|---|---|
| Share a photo from Photos **into** Drive Union | **No.** `share_target` is Chromium-only. Upload is from inside the app, through the file picker. |
| A large upload continuing while the app is backgrounded | **No.** No Background Fetch in WebKit; iOS suspends the web app. It resumes when reopened — M4 makes that automatic. |
| An in-page "Install" button | **No.** No `beforeinstallprompt`. Share → Add to Home Screen, with a one-time hint from us. |
| Push notifications | **Yes**, iOS 16.4+, only once installed to the home screen, and only from a user gesture. |
| Sending a share link through the iOS share sheet | **Yes.** `navigator.share` (outbound) works. |

One caveat that does not apply here but would elsewhere: Apple removed standalone home-screen web
apps in the EU under the DMA. This product's audience is not in the EU.

## Decisions taken

- **Offline scope: shell and assets only.** No file names, no workspace names, nothing from the
  catalogue on the phone's disk. This is a product whose whole claim is that the server holds no
  readable copy; caching the catalogue onto a phone is not a contradiction but it is a decision, and
  the answer is no. `/d/{slug}` is never cached in any case — a revoked link must die at once.
- **Push is in scope, as M7.**
- **A new icon is being designed** rather than reusing the `brand-mark` letterform.

## Phases

### M1 — installable — **done, 8a4152d**
- Design the icon first, as a small set of directions to choose from, *before* generating eight
  sizes of the wrong thing.
- `manifest.webmanifest`: `display: standalone`, `scope: /`, `start_url`, `background_color` and
  `theme_color` from `tokens.css`. Served from a controller rather than as a static file so the name
  follows `PanelCulture` — which means the `<link>` needs `crossorigin="use-credentials"`, or the
  manifest is fetched without the culture cookie and always comes back in one language.
- Icons: 192, 512, 512-maskable, and `apple-touch-icon` at 180 — iOS reads manifest icons on recent
  versions and the `<link>` on older ones, so both.
- `theme-color` twice, with `media="(prefers-color-scheme: …)"`, or the standalone status bar is the
  wrong colour in one of the two themes.
- A favicon, which the product currently 404s on.
- Verify `.webmanifest` is actually served — an extension the static-file middleware does not know
  is a 404, not a wrong content type.

Size: small. Depends on nothing.

### M2 — safe areas and a phone layout — **done, 5979bb5**
- `env(safe-area-inset-*)` on the header, the sidebar and `.sidebar-foot`.
- A real breakpoint below 760px for the *panel*, not only the public card.
- `.dtable` is a `--cols` grid; on a phone it has to become cards, not a squeezed table.
- 44px touch targets.
- Check the upload dock and the new abuse-queue cards at 390px.

Size: medium. Depends on M1 (so it can be judged in standalone, where the insets exist).

### M3 — staying signed in — **done, 6e1e2e1**

`ConfigureApplicationCookie` sets no `ExpireTimeSpan`, and `RememberMe` is off by default, so an
ordinary sign-in is a session cookie. An installed iOS web app gets its own cookie jar and is
evicted from memory often, so today the answer is "sign in again, most times you open it". Set an
explicit sliding expiry and default the checkbox on.

Size: small. Depends on nothing. Highest value per line in this plan.

> **Correction, written after the work.** The diagnosis above is wrong in its first sentence and it
> is worth leaving visible rather than editing away. The missing `ExpireTimeSpan` was *not* what made
> an ordinary sign-in a session cookie — Identity already defaults the ticket to fourteen days.
> `RememberMe` defaulting to off was the entire cause: a cookie only gets a browser expiry when the
> sign-in was persistent. The explicit thirty days is a deliberate ceiling rather than the fix.
>
> The trap the work nearly shipped with, for anyone touching a checkbox default again: an unticked
> checkbox posts nothing, and the model binder leaves a property it finds no value for at its
> initialiser. Flipping the initialiser to `true` on its own renders a box that can be unticked and
> binds to `true` anyway — silently, on exactly the shared-computer case the box exists for. It needs
> a hidden companion field after it.

### M4 — uploads that survive the phone — **done, cb4a602**
- On `visibilitychange` → visible, resume anything stalled, automatically. The machinery exists; it
  currently waits for a tap.
- `navigator.wakeLock` while an upload is running, released on finish — Safari 16.4+.
- Say plainly on the upload screen that leaving the app pauses a transfer. It is not a bug we can
  fix and it is a bug the customer will otherwise report.

Size: small. Depends on nothing.

### M5 — service worker — **done, 12c250f**
- **It cannot be a Vite entry as things stand.** Vite hashes everything into `/build/`; a service
  worker needs a stable URL at the root of its scope. Either a second Vite input with a fixed
  `entryFileNames`, or a hand-written `/sw.js` outside the bundle.
- **It must be written to share.** P7b — the Service Worker for streaming encrypted video — needs
  the *same registration*; two workers cannot hold one scope. M5 owns the file and the install and
  fetch plumbing, and P7b adds a handler to it. Getting this wrong means one of the two silently
  unregisters the other.

  > **P7b has since landed on this seam**, and the shape turned out one step better than described:
  > it adds no handler at all. `sw-media.js` exposes `self.du1Media` and the single `fetch` listener
  > in `sw.js` asks it, so there is still exactly one handler and one place the routing order is
  > decided — which is what the rule was protecting rather than the letter of it.
- Cache: fonts, CSS, the hashed bundle, and an offline page. Nothing authenticated, nothing under
  `/d/`, nothing from the API.
- A version/skip-waiting story, or a stale worker serves last week's CSS against this week's HTML.

Size: medium. Depends on M1.

### M6 — the iOS share sheet — **done, 5c3ab4e**
`navigator.share` on the created-link screen, behind a feature test, beside the existing copy
button rather than replacing it — the desktop panel has no share sheet.

Size: small. Depends on M1 (permission-free, but only reachable on a phone).

### M7 — push notifications — **done, bd66814**
- VAPID keys in configuration, never in the repository.
- A `PushSubscription` entity: endpoint, `p256dh`, `auth`, `TenantId`, `UserId`, created, last seen,
  consecutive failures. **No foreign key to `Tenant`** — `TenantStorageMeter` detaches the tenant
  after `ExecuteUpdate` and cascade-detaches tracked dependents, which is how `UploadSession`,
  `RemoteFetch` and `AbuseReport` all came to have none.
- One migration, and only one at a time.
- Sending needs Web Push encryption (VAPID JWT + AES128GCM). That is a dependency decision — either
  a library or a hand-rolled implementation with test vectors.
- What is worth waking a phone for, and nothing else: a link-upload (`RemoteFetch`) that finished or
  failed, a queued deletion that finished, and — for the operator only — a new abuse report, since
  that one is racing Google.
- Permission must be asked from a gesture, and on iOS only once installed. So a control in settings
  that detects standalone mode and explains itself when it cannot be used yet.
- Every subscription is per device. Pruning on repeated failure is part of this, not a follow-up:
  an endpoint that 410s forever is a queue that never drains.

Size: large. Depends on M5 (the worker receives the push).

## Order

M3 and M4 are independent of everything and are the two that make the app usable on a phone at all,
so they can go first or alongside. M1 → M2 is the visible half. M5 gates M7.

## Verification

Tests as usual, and then on a real iPhone: install from Safari, check the status bar and home
indicator against M2, sign out and back in and confirm the session survives a cold open (M3), start
a large upload and background the app (M4), turn off the network and open it (M5).
