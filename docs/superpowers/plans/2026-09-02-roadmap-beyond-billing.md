# Roadmap beyond billing — what the market has, what this app has, and what to build next

**Written:** 2026-09-02 · **Purpose:** a menu. The owner picks items and hands each to a model to
execute; every item below is written to be executable on its own by a model that has never seen this
conversation. Billing, sign-up and email (B1–B3) are deliberately out of scope here — they are their
own plan.

Sizes are in model-sessions: **S** ≤ half a session, **M** one session, **L** two or three, **XL** a
plan of its own. «Depends on» is a hard dependency; everything else may run in parallel, subject to the
file-ownership rule in §5.

---

## 1. What the app is today (verified in code, not from memory)

Screens: Dashboard (customer + operator) · Files (folders, tags, search, multi-select, drag-to-folder,
trash) · one file's own page `/files/{id}` · Upload (chunked, resumable, client-side encrypted, staged
queue, remote fetch from URL) · Links · Watch pages (panel + public, encrypted streaming through the
service worker) · On-this-device library (OPFS, resume, Background Fetch on Chromium) · Public download
page with previews (image / video / audio / PDF ≤ 25 MB) · API keys + JSON API (list, get, content) ·
S3 gateway (GET/PUT/HEAD/DELETE, multipart) · Telegram (identity link, `/files`, `/quota`, send and
receive documents through a self-hosted Bot API) · Plans & quotas (storage, per-file, monthly egress,
seats) · Abuse queue · Backups · Tenants admin · Google account pool · Push notifications · PWA
(installable, offline shell, share target).

Share link fields: slug, expiry, max downloads, note, active. **No password. Files only, no folders.**

Confirmed absent: link password · folder links · zip download · file requests / upload-only links ·
thumbnails · rename file · edit a link after creation · vanity slug · Open Graph tags on the public
page · 2FA · sessions/devices · activity log · versioning · dedupe · auto-expire · folder upload ·
clipboard paste · health endpoint · metrics · virus scan · WebDAV · torrent/magnet · HLS.

Rate limiting exists (`UseRateLimiter`). `DownloadEvent` records link, time, hashed IP, user agent —
enough for analytics that nobody has drawn yet.

## 2. What the market has (September 2026)

International — [MEGA](https://www.cloudwards.net/review/mega/),
[pCloud](https://www.pcloud.com/features/file-sharing.html),
[Proton Drive](https://proton.me/support/drive-shareable-link),
[Filen](https://docs.filen.io/docs/web/sharing/),
[WeTransfer / Dropbox](https://fast.io/resources/wetransfer-vs-dropbox/):

| Feature | MEGA | pCloud | Proton | Filen | WeTransfer/Dropbox | **Drive Union** |
|---|---|---|---|---|---|---|
| Link password | Pro | Premium | free | free | yes | **no** (has E2E lock — stronger, and a different thing) |
| Link expiry | yes | yes | yes | yes | yes | yes |
| Download limit | – | – | – | – | – | **yes** |
| Folder link | yes | yes | yes | yes | yes | **no** |
| File request (upload-only link) | yes | yes + expiry + limit | no (asked since 2024) | no (feature request) | yes, branded | **no** |
| Download analytics | – | – | no | – | yes | counts only |
| Branded download page | – | – | – | – | paid | **no** |
| Zero-knowledge | free | paid add-on | free | free | no | **yes** (per file, opt-in) |
| Watch encrypted video without download | no | no | no | no | – | **yes** |

Iranian hosts — [uupload](https://my.uupload.ir/), Picofile, ParsLoka, Trainbit, upload.ir
([survey 1](https://dmbaam.com/top-uploads-websites/), [survey 2](https://webkima.com/best-upload-site/)):
the selling points are **direct links**, **domestic (نیم‌بها) traffic**, **remote upload as a way to
convert foreign links to domestic traffic**, sub-users with limited rights (uupload), and
delete-after-N-days-without-download on free tiers. Trainbit throttled remote upload within a week of
a viral tutorial — demand for it is real, and it is a bandwidth liability.

Leech / seedbox hosts — [Bitport](https://en.wikipedia.org/wiki/Bitport),
[awesome-file-hosts](https://github.com/FahadBinHussain/awesome-file-hosts): torrent-to-direct-link,
streaming, mass zip download, IDM support (Range), antivirus check.

**Where Drive Union is already ahead:** per-file zero-knowledge with in-browser streaming playback,
offline copies with resume, an S3 gateway, a Telegram channel, per-link download caps, and a self-hosted
Google pool no competitor's economics can match. **Where it is behind:** everything about sharing more
than one file at a time, receiving files from strangers, link passwords, and the visible polish a
sharing product is judged on (thumbnails, Open Graph previews, a branded page).

---

## 3. Phases

Each item: **why** (evidence), **what** (spec), **where** (seams), **tests**, **size**, **depends on**,
**risk**. An executor reads §6 first.

### Phase A — Sharing that matches the market  (all S/M, independent of each other)

**A1 · Link password.**
*Why:* every competitor has it; the one control a stranger-facing product is judged on first. The
existing E2E lock is stronger but asks the *owner* to hold a key per file; a link password is a
lighter thing the *sender* sets per link.
*What:* optional password on a `ShareLink`, hashed with the Identity hasher, never stored plain. The
public page shows a password card instead of the file card; download, preview and watch require a
short-lived cookie set by a correct answer, scoped to that slug (cookie name includes the slug, so two
tabs with two links do not share it). Wrong answers are rate-limited per IP+slug. A locked (E2E) file
may also carry a link password — they answer different questions.
*Where:* `Core/Sharing/ShareLink.cs` (+ migration), `ShareLinkService`, `PublicDownloadController`
(`Landing`, `File`, `Preview`, `Watch` all gate), `Views/Public/Download.cshtml`, the Links create
form, `UiText.Sharing`.
*Tests:* wrong password → 403 with no oracle about slug existence; correct password sets the cookie and
the next `File` GET streams; a revoked link still refuses regardless of cookie; the password card
carries no file name (it is the refusal card's sibling and must leak as little).
*Size:* M.

**A2 · Edit a link after creation** (expiry, max downloads, note, password).
*Why:* today a link is create-or-revoke; changing the expiry means a new slug and re-sending it.
*What:* an edit form per row on the Links screen; `ShareLinkService.UpdateAsync(tenantId, id, …)`.
Revoke stays irreversible (slug is burned — M4 §2).
*Where:* `LinksController`, `Views/Links/Index.cshtml`, service + tests.
*Tests:* another tenant's link id is not-found rather than forbidden; extending past the cap
re-enables the link; shortening below `DownloadCount` is refused with a sentence.
*Size:* S.

**A3 · Vanity slug.**
*Why:* `/d/kx91mzq4` versus `/d/holiday-2026`. Iranian hosts and WeTransfer both let paid users name
the link.
*What:* optional slug on create, 4–40 chars `[a-z0-9-]`, unique per deployment, reserved-word list
(`file`, `watch`, `preview`, `report`, `abuse`, `embed`, every existing route segment). Generated slugs
stay the default.
*Where:* `ShareLinkService.CreateAsync`, `PublicLinkFormatter`, the Links create form.
*Tests:* collision → refusal; reserved word → refusal; case-folded on write and on lookup.
*Size:* S.

**A4 · Open Graph and Twitter-card tags on the public page.**
*Why:* a link pasted into Telegram, WhatsApp or Twitter today unfurls as a bare URL. Every competitor
unfurls with name, size and a picture; that is how a sharing product advertises itself.
*What:* `og:title` (file name), `og:description` (size · type · shared by), `og:image` (the preview URL
for images, a generic per-type card otherwise — real thumbnails arrive with D1), `og:url`. **The
refusal page carries no tags** — a revoked link must not unfurl.
*Where:* `Views/Public/Download.cshtml`, the head section of `_PublicLayout.cshtml`, a test under
`tests/…/Links/` that the refusal page has no `og:` and the live page has all four.
*Size:* S.

**A5 · Embeddable player.**
*Why:* «put this film on my site» is the second thing a video host is asked for.
*What:* `/d/{slug}/embed` — the watch stage alone, no shell; frame headers relaxed for that one route;
an «Embed» button on the public page that copies the `<iframe>`. Spends a download on play exactly as
`Watch` does. Not offered for E2E-locked files: a passphrase inside an iframe on somebody else's page
is a phishing surface.
*Where:* `PublicDownloadController.Embed`, a view over `_Player` with a minimal layout, CSP / frame
headers scoped to the route.
*Tests:* embed route for a locked file → 404; frame headers present only on `/embed`.
*Size:* S–M.

**A6 · QR code for a link.**
*What:* server-side SVG QR (no third-party service — the slug must not leave the deployment) on the
Links screen and the public page. *Size:* S.

### Phase B — Folders as first-class share units

**B1 · Folder links.**
*Why:* the largest single gap against every competitor in §2.
*What:* `ShareLink` gains an optional `FolderId` (exactly one of file / folder set — a check
constraint). The public page for a folder link lists its files and subfolders, server-rendered and
sortable; each row downloads through the same metered path; the folder link's cap and expiry apply to
every file in it. Locking a file revokes any folder link that contains it (the rule locking already
applies to file links). A file moved into the folder becomes reachable; one moved out stops being
reachable — the link is the folder.
*Where:* `ShareLink`, migration, `ShareLinkService`, `PublicDownloadController` (a folder branch), a
new `Views/Public/Folder.cshtml`, `DownloadCounting` (a folder link counts per file downloaded).
*Tests:* moved-out file unreachable; moved-in file reachable; another tenant's folder id → not found;
lock revokes the containing folder link.
*Size:* L. *Depends on:* A1 if passwords should apply to folders (recommended: A1 first).

**B2 · Zip download** (a selection in the panel; «download all» on a folder link).
*Why:* Bitport, MEGA, pCloud, every Iranian host. Ten photos as ten downloads is what people leave a
product over.
*What:* a streaming zip — `System.IO.Compression` in create mode over the response body, store-only,
no temp file — reading each file from Drive in turn. Metered as the sum. **E2E-locked files are
excluded with a note**: the server cannot produce their plaintext and a zip of ciphertext is a file
nobody can open. Hard ceiling on total bytes per zip (configurable, default 20 GB) refused before the
first byte with the figure.
*Where:* `FilesController.Selection` (a new verb), `PublicDownloadController.FolderZip`, a
`ZipStreamer` in Infrastructure with the egress meter wired in; `EveryEgressPathIsMeteredTests`
enumerates byte routes and must be extended.
*Tests:* meter charged the sum; a locked file in the selection is skipped and named; the cap refuses
before the first byte.
*Size:* M–L. *Depends on:* B1 for the public half; the panel half stands alone.

**B3 · Folder upload** (drag a folder, or `webkitdirectory`), recreating the tree.
*What:* the upload store carries a relative path per item; the uploads API accepts it and the server
creates missing folders once per batch.
*Where:* `UploadPanel.vue`, `uploads/store.ts`, `UploadsController`, `IFolderTree.EnsurePathAsync`.
*Tests:* two files in one new subfolder create it once; a path containing `..` is refused.
*Size:* M.

### Phase C — Receiving files from strangers

**C1 · File requests (upload-only links).**
*Why:* pCloud and MEGA have it; Proton and Filen users have asked for two years. For an Iranian
customer collecting videos from clients it is *the* feature.
*What:* an `UploadRequest` row: slug, target folder, optional expiry, optional per-file and total byte
caps, optional message, optional password (A1's shape). Public page at `/r/{slug}`: a dropzone and
nothing else — **the uploader never sees a listing** (Proton's editor-link mistake is the thing to
avoid). Uploads use the existing chunked session machinery with the request slug as the credential;
files land in the requester's folder and count against the requester's storage quota, refused at the
cap with a sentence. Optional: the uploader may lock the file client-side with a passphrase given to
the requester out of band — offered, default off.
*Where:* new `Core/Uploads/UploadRequest.cs` (**no FK to Tenant**, see §6), migration,
`UploadsController` gains a request-scoped session begin, a `PublicUploadController`, a Vue island
reusing `UploadPanel`'s dropzone, a `Requests` screen in the panel listing what arrived.
*Tests:* an expired request refuses `begin`; a chunk against a request session cannot be redirected
to another folder; storage quota enforced; the public page HTML contains no file names.
*Size:* L. *Depends on:* A1 for the password only.

### Phase D — Media and previews

**D1 · Thumbnails and a gallery view.**
*Why:* a file list without pictures is the visible difference between this and every consumer product.
*What:* thumbnails generated server-side on upload completion (ImageSharp; images first — video
posters need ffmpeg, see D5), **stored in the pool account beside the file** as a second Drive file
(`DriveThumbnailId` on `StoredFile`), served through a panel route that is meter-exempt (argued in
`EveryEgressPathIsMeteredTests` the way locking is argued). Never Google's `thumbnailLink` — it exposes
googleusercontent, the one thing the product may never do. A gallery toggle on the Files screen.
E2E-locked files get none in this cut (the server holds ciphertext); a browser-generated encrypted
thumbnail is a second cut.
*Where:* a `ThumbnailWorker` (testable class + thin `BackgroundService` + separate registration, §6),
`StoredFile` migration, `FilesController.Thumbnail`, `Views/Files/Index.cshtml` gallery mode,
`fileGrid.ts`.
*Tests:* worker skips encrypted files; a corrupt image yields no thumbnail and no failure; the route
answers 404 for another tenant's file.
*Size:* L.

**D2 · Remember playback position** («continue watching»).
*What:* per file per browser in `localStorage`, keyed by content URL; the watch page seeks on load and
offers «start over». Trivial and disproportionately loved. *Size:* S.

**D3 · Subtitles.**
*What:* upload `.vtt` / `.srt` beside a video (`.srt` converted to VTT in the browser), attached by
`SubtitleOfFileId`; the player adds `<track>`. For an E2E-locked film the subtitle is locked with the
same content key. *Size:* M.

**D4 · Text, code and Markdown preview** on the public and file pages, size-capped, rendered into a
`<pre>` server-side — never served as a document (the reason `text/plain` is excluded from `Previews`
today is in its comment; this answers it rather than removing it). *Size:* S–M.

**D5 · Video posters and HLS transcoding.**
*What:* ffmpeg on the server, a transcode queue, renditions stored in the pool. **XL, CPU-bound,
doubles storage per film, and cannot apply to E2E-locked films at all.** Listed so it is decided, not
re-asked; recommended: not yet.

### Phase E — Security and account hygiene

**E1 · Two-factor authentication (TOTP).**
*Why:* a product holding films behind a single password. Identity already has the plumbing.
*What:* enable / disable with QR and recovery codes on a Security screen; operators may be required
to have it. *Where:* `Areas/Identity`, a `Security` controller + view, `UiText.Security`.
*Tests:* sign-in with 2FA on requires the code; a recovery code is single-use. *Size:* M.

**E2 · Sessions and devices** — list active sessions (user agent, last seen, hashed IP), sign out one
or all others. `SecurityStamp` rotation signs out everywhere; per-session needs a `Session` row keyed
by a cookie claim. *Size:* M.

**E3 · Activity log** per workspace — uploads, deletes, links created / revoked, locks, fetches, API
key use — from the events that already exist (`DownloadEvent`, `DeletionJob`, `FileLock`,
`RemoteFetch`) plus a small `Activity` row for the rest. A screen with filters and CSV export.
*Size:* M.

**E4 · Per-link download analytics** — a chart per link from `DownloadEvent`: downloads per day,
unique IP hashes, top user agents. The data is already there; nobody has drawn it. *Size:* S–M.

**E5 · Abuse automation** — a hash blocklist: when the operator removes a file from the abuse queue,
its SHA-256 (from F3) goes on a list and future uploads of the same bytes are refused. *Size:* M.
*Depends on:* F3.

### Phase F — Upload productivity

**F1 · Clipboard paste** — an image pasted into the upload screen becomes a file. *Size:* S.

**F2 · Batch remote fetch** — paste many URLs, one per line; each becomes a `RemoteFetch` within
`MostInFlightPerTenant`. *Size:* S.

**F3 · Content hash at upload.**
*What:* SHA-256 over the plaintext computed in the browser during chunking (the bytes stream once
already), sent with the final chunk, stored on `StoredFile`. Enables E5, F4 and an integrity check on
download. For E2E-locked files a plaintext hash allows «is this known file present» queries against
the ciphertext — recommended: skip the hash for locked files and say so in the UI. *Size:* M.

**F4 · Dedupe on upload** — same hash already in this workspace → offer to link rather than re-upload.
Within one tenant only; cross-tenant dedupe is an oracle. *Size:* S. *Depends on:* F3.

**F5 · Auto-expire files** — per file or folder: delete after N days, or after N days without a
download (what Iranian free tiers do to strangers; here it is a tool the owner sets). Goes through
`IDeletionQueue` so trash retention still applies. *Size:* M.

**F6 · Rename file** — absent today. A form post and a Drive rename; the catalogue name is what the
customer sees. *Size:* S.

### Phase G — Telegram, deeper

**G1 · Share link from the bot** — `/link <file>` creates a link with default expiry and replies with
it; `/links` lists live ones. Revocation stays panel-only (assumption 4 of the remaining-work plan).
*Size:* M.

**G2 · «Send to my Telegram»** — a signed-in customer on a file page has the bot deliver the file
instead of downloading; spends a download. Panel first, public page second. *Size:* M.

**G3 · Bot search and folder browse** — `/find <text>`, an inline keyboard through folders. *Size:* M.

### Phase H — Operator and operations

**H1 · Health endpoint (O4)** — `/healthz` (liveness) and `/readyz` (DB reachable, one pool token
refreshable, worker loops alive within N minutes). Harbora needs it to deploy safely. *Size:* S.
**Do this first regardless of everything else.**

**H2 · Metrics** — OpenTelemetry meters for egress bytes, uploads, fetches, worker loop durations,
Drive API error rates; a Prometheus scrape endpoint behind the operator policy. *Size:* M.

**H3 · Per-tenant usage over time** — storage and egress per day on the operator's tenant page and
the customer dashboard, from a `UsageSample` row a worker writes daily. *Size:* M.

**H4 · Pool rebalancing UI** — `AccountMigrator` exists; a screen to move a tenant's files from one
account to another, with progress. *Size:* M.

**H5 · Virus scan** — ClamAV through the clamd socket on upload completion, quarantining to trash with
a notice. Cannot scan E2E-locked files (say so). *Size:* M, plus clamd to run.

### Phase I — Big bets (each is its own plan; listed so they are decided, not rediscovered)

**I1 · Torrent / magnet leech.** The single most-asked feature in the Iranian market and the way
Trainbit got throttled in a week. A server-side torrent client writing into the pool, then a
`RemoteFetch`-style completion. **Legal and bandwidth liability is the whole question**; the abuse
queue and H5 are prerequisites. XL.

**I2 · Branded public pages and custom domains.** Logo, colour and a sentence per tenant is M and worth
doing; a custom domain per tenant is L (TLS issuance, host-header routing, the public layout losing
the product's brand). Recommended: the M half.

**I3 · Edge cache for hot public files.** Domestic traffic is a deployment question (a node inside
Iran) more than code. The code half: a cache key per `(slug, range)` and `Cache-Control` on the public
byte route with the meter still charged from origin. L, and pointless without the node.

**I4 · WebDAV.** Mount as a drive in Explorer and Finder. The S3 gateway already gives rclone and
Cyberduck; WebDAV adds the OS-native mount and a second protocol to keep honest about metering. L.

**I5 · «Save to my workspace» from a public link.** Every file is in an operator-owned pool account,
so a server-side Drive copy is possible without the bytes leaving Google. L; interacts with quotas,
locks (copy ciphertext + header; the recipient needs the passphrase) and abuse.

**I6 · Roles inside a workspace (P6).** Deferred by the owner; still the right shape for the
«sub-users» uupload sells. XL.

---

## 4. Suggested order, if asked

1. **H1** (deploy safety, a morning), then **A4 + A2 + A3 + A6** — a day of polish that changes how
   every shared link looks and behaves.
2. **A1**, then **B1 → B2**, then **C1** — the block that closes the visible distance to pCloud and
   MEGA.
3. **D1 + D2 + D4**, **E1 + E4**, **F3 → F4 + F6** — polish, security, productivity; all parallel.
4. **G1–G3**, **H2–H4**, **E2, E3, E5, F1, F2, F5, D3** — as capacity allows; all parallel.
5. Decide I1–I6 one at a time, each with its own plan.

## 5. Parallelism — what may run at once

By file ownership. Two items touching the same file are sequential:

- `ShareLink.cs` / `ShareLinkService` / `PublicDownloadController`: A1, A2, A3, A5, B1, B2 (public),
  E4 — **one at a time, in that order**.
- `UploadPanel.vue` / `uploads/store.ts` / `UploadsController`: B3, C1, F1, F2, F3 — one at a time.
- `StoredFile.cs` + migration: D1, F3, F5, F6 — one at a time (**one migration at a time**, §6).
- `_Layout.cshtml` nav: any item adding a screen (C1, E1, E2, E3, H3) — sequential for that file.
- Everything else is independent.

## 6. Conventions the executor must know (all learned the hard way)

**Build and test.** A private NuGet feed in the machine's config is unreachable and
`TreatWarningsAsErrors` turns that into a build error. Use:

```
dotnet build DriveUnion.slnx -p:NuGetAudit=false -v q --nologo
dotnet test  DriveUnion.slnx --no-build --nologo
cd src/DriveUnion.Web && npx vitest run && npx vue-tsc --noEmit && npm run build
```

The dev server locks the DLLs; if the build says «file is locked by DriveUnion.Web», run
`taskkill //F //IM DriveUnion.Web.exe` first. **A failed build leaves the previous test DLL in place,
and `dotnet test --no-build` then reports false passes** — always confirm the passed count moved and
that a filter on the new test names matches something. After changing a `.cshtml`, rebuild before
restarting the server; after `npm run build`, restart the server (it caches the Vite manifest).

**Architecture rules.**
- No global EF query filters. Tenant scoping is an explicit `tenantId` argument on every call.
- **No new entity may have an FK to `Tenant`.** `TenantStorageMeter` detaches the tenant after an
  `ExecuteUpdate`, cascade-detaching tracked dependents. `UploadSession`, `RemoteFetch`, `FileLock`,
  `PushSubscription`, `AbuseReport`, `DeletionJob` all carry a bare `TenantId` Guid for this reason.
- SQLite (tests) will not compare or ORDER BY a `DateTimeOffset` — sort and filter in memory.
- One EF migration at a time; regenerate with `dotnet ef migrations remove` rather than editing a
  moved snapshot.
- Background work is a testable class + a thin `BackgroundService` + a **separate** registration
  extension (shared SQLite in tests → «database is locked» otherwise).
- Every byte route is metered and capped; `EveryEgressPathIsMeteredTests` enumerates them and must be
  extended, with an argument, for any exemption.
- Never expose a `googleusercontent.com` URL or a Drive id to a visitor.
- Refusals on public routes collapse to one identical page (revoked = expired = capped = never
  existed); do not add a fifth that is distinguishable.
- Passphrases never reach the server. E2E keys are derived in the browser; the server sees wrapped
  keys and, for link fetches, a content key for that one file.

**Front end.** Razor renders; Vue islands hydrate `data-island="…"` mount points and are registered in
`Scripts/main.ts` with a `region` of `content` (inside the swapped `main`) or `shell`. Every island's
strings come from Razor as JSON in a data attribute — `UiText` is the one place a sentence exists in
both languages, and `OfflineLibraryScreenTests` shows how to hold that seam. Service workers are
hand-written classic scripts under `wwwroot/` (`sw.js` owns the single `fetch` listener; `sw-media.js`,
`sw-push.js`, `sw-download.js` are `importScripts` seams and `sw.test.ts` asserts the import list
exactly). Vitest tests for workers read the shipped `.js` off disk and evaluate it against fakes.

**Tests that will bite.** `LocalizationCatalogueTests` renders every `UiText` member in both languages
and cannot supply arguments — new members are `Pick(fa, en)` only. `PanelLayoutTests` forbids `color:`
and `background:` inside a `style="…"` attribute and requires `.warn` / `.danger` to be the last rules
in `app.css` declaring `color`. Harnesses: `ServiceTestHarness` (services + fake Drive),
`PanelPageHarness` (panel pages, header-authenticated, no password), `TenantPanelHarness` (real
Identity cookies). Mutation-check anything that guards a state transition — a test that still passes
with the guard removed is not a test.

**Voice.** Comments and commit messages explain *why* and what failure a decision prevents, in the
discursive register the codebase already uses. Persian UI copy is the default; every string has an
English twin.
