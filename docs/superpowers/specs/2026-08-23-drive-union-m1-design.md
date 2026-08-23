# Drive Union — M1: one file, one link

**Date:** 2026-08-23 · **Status:** design approved in conversation; blocked on §11 before the first
line of implementation

## 1. The brief, and what it actually asked for

The owner's words:

> «یه وب‌اپ اختصاصی که چند اکانت گوگل درایو (فعلاً ۲ تا، ۵ ترابایت هرکدوم) رو به‌صورت یکپارچه مدیریت
> کنه، با تمرکز روی سرعت آپلود/دانلود و کنترل کامل روی نحوه‌ی اشتراک‌گذاری فایل‌ها.»

A design bundle arrived with it (`docs/design/drive-union/`): seven high-fidelity screens, final
colour/type/spacing tokens, and a suggested API surface. The design is not the question. The product
model behind it was, and four answers settled it:

1. **Separate repo.** Drive Union is its own product, not a Harbora module. Only the *embedded-Vue*
   build pattern is borrowed, because Harbora already proved it.
2. **Multi-tenant SaaS.** Customers sign up and use the panel.
3. **The Google accounts are the operator's.** Customers never authenticate with Google. This is the
   single most consequential answer in the whole document — see §2.
4. **Customers see the same panel, scoped down.** Same components, two permission levels. The account
   filter, the daily-quota bars, the Google Accounts screen and the proxy table are operator-only. A
   customer must never learn which Google account holds their file.

So: **a file-hosting service whose storage backend is a pool of Google Drive accounts the operator
owns.** Not a Google Drive client.

## 2. The constraint that stopped being a constraint

`https://www.googleapis.com/auth/drive` is a Google *restricted* scope. Had customers connected their
own accounts, production launch would have required OAuth verification plus a CASA security
assessment — weeks of process and recurring cost, entirely outside our control, and until it cleared:
a 100-user cap and a scary consent screen for every customer.

Because the accounts are the operator's, none of that applies to customers. Only the operator ever
sees a Google consent screen, twice, ever.

**One piece of it does survive, and it will bite in week two if ignored.** While the OAuth consent
screen sits in *Testing* publishing status, refresh tokens issued to external users expire after
seven days. The operator would have to reconnect both accounts every week. The consent screen must be
moved to *In production* before launch. Unverified + production + restricted scope still shows an
"unverified app" warning — the operator clicks through it once per account and it never appears again,
because no one else authenticates. Confirm current Google policy at setup time; this is the one
external dependency whose rules can change under us.

If the two accounts turn out to be Google **Workspace** rather than consumer Google One, a service
account with domain-wide delegation removes user OAuth entirely — no consent screen, no expiry, no
warning. Worth checking before building the connect flow; it is strictly better when available.

## 3. Decomposition

M1 is the only slice that is worth anything on its own. Everything else hangs off its spine.

| # | Slice | Contents |
|---|---|---|
| **M1** | **One file, one link** | Repo, auth, operator connects one Google account, upload, "my files", share link, public `/d/{slug}` page, streamed download with `Range`, RTL shell + both themes at the handoff's exact tokens |
| M2 | Pool and quota | Second/third account, union view, upload policy, per-account 750 GB/day tracking, operator dashboard |
| M3 | Queue and transfer | `jobs` table, background worker, `files.copy`, parallel chunk upload, live progress over SignalR, the 750 GB copy ceiling and `userRateLimitExceeded` backoff |
| M4 | Full link control | Password, alias filename, revoke, expired/404 states, download analytics, abuse controls beyond M1's per-IP rate limit |
| M5 | Roles and tenancy | owner/uploader/viewer inside a tenant, invitations, per-tenant storage cap |
| M6 | Network tuning | Proxy egress IPs, chunk tuning, S3 export, traffic chart |

Each slice gets its own spec and plan.

## 4. M1 architecture

.NET 10.0.400, Node 22 — both already on the machine.

| Project | Responsibility | Depends on |
|---|---|---|
| `DriveUnion.Core` | Entities, business rules, and the **interfaces** (`IDriveClient`, `ITokenProtector`, `ISlugGenerator`, `IClock`) | nothing |
| `DriveUnion.Infrastructure` | EF Core + Postgres, the real `IDriveClient` over Google Drive API v3, token encryption | Core |
| `DriveUnion.Web` | Razor views, controllers, Vue islands built by Vite | Core, Infrastructure |
| `tests/DriveUnion.Tests` | xUnit — unit over Core, integration over Web with a fake `IDriveClient` | all |

Three projects, not Harbora's four. Splitting Domain from Application buys nothing at this size; if
the team prefers shape-consistency with Harbora, the split costs one afternoon later.

`IDriveClient` lives in Core *because this machine has no Docker and tests must never reach Google*.
Every decision worth testing — which account, what counts as a download, how a rate-limit is retried,
what an expired link renders — has to be reachable without a network.

### Frontend build

Copy Harbora's proven arrangement: Vite compiles `Scripts/main.ts` into `wwwroot/build` with the
manifest written to `build/manifest.json` — **not** the default `.vite/manifest.json`, because the
.NET SDK excludes dot-folders from `dotnet publish` and the app comes up with no CSS at all. Razor
resolves hashed assets through a ported `ViteManifest`. No Node process runs in production.

**No Tailwind.** The design specifies `oklch()` tokens and off-scale values (`13.5px`, `--row-pad:
11px 14px`, radii of 9/10/11/12/14/18/20). Expressing that in Tailwind means either arbitrary values
everywhere or a full config rewrite — both worse than plain CSS. A single `tokens.css` carries the
handoff's `:root` and `[data-theme="dark"]` blocks verbatim; component CSS lives in the Vue SFCs.

Vazirmatn is self-hosted. The handoff is explicit: the server is in Germany, no foreign CDN.

## 5. Data model

```
Tenant        { Id, Name, Slug, CreatedAt }
AppUser       { Identity fields…, TenantId?, IsOperator }
GoogleAccount { Id, Email, Label, RefreshTokenProtected, AccessTokenProtected,
                AccessTokenExpiresAt, QuotaTotalBytes, QuotaUsedBytes, Status, CreatedAt }
StoredFile    { Id, TenantId, GoogleAccountId, DriveFileId, Name, MimeType,
                SizeBytes, CreatedAt, ModifiedAt, DeletedAt? }
UploadSession { Id, TenantId, GoogleAccountId, FileName, SizeBytes, MimeType,
                DriveResumableUri, BytesConfirmed, Status, CreatedAt, ExpiresAt }
ShareLink     { Id, Slug, StoredFileId, TenantId, ExpiresAt?, MaxDownloads?,
                DownloadCount, IsActive, CreatedAt }
DownloadEvent { Id, ShareLinkId, OccurredAt, IpHash, UserAgent }
```

`GoogleAccount` deliberately has **no** `TenantId` — the pool belongs to the operator. `StoredFile`
carries both `TenantId` (who owns it) and `GoogleAccountId` (where it physically sits); M2 changes
only how the latter is chosen.

Each tenant gets a folder per Google account: `DriveUnion/{tenant-slug}/`. Hygiene now, and M3's
`files.copy` needs a destination parent anyway.

`DownloadCount` is a denormalised counter on `ShareLink` (the panel reads it constantly);
`DownloadEvent` is the audit trail behind it.

Refresh and access tokens are encrypted with ASP.NET Core Data Protection. **Persist the Data
Protection keys to the database**, not the container filesystem — otherwise the first redeploy
silently orphans every stored token and both Google accounts appear to have "disconnected" for no
visible reason.

## 6. Upload

Browser → OVH → Google. Never browser → Google directly: the customer must never hold a credential or
a session URI belonging to the operator's account, and the whole point of the OVH box is to be the
fast path to Google that the customer's own connection is not.

1. `POST /api/uploads` — body is `{ fileName, sizeBytes, mimeType }`. The server opens a Drive
   resumable session (`uploadType=resumable`), stores the returned session URI in `UploadSession`,
   returns `{ id, chunkSize }`.
2. `PUT /api/uploads/{id}/chunk` with `Content-Range` — the server **streams the request body
   straight to the Drive session URI** at the matching byte range. No disk spooling, no full-buffer
   read. Chunk size is 32 MiB: a multiple of 256 KiB, which Drive requires for every chunk but the
   last.
3. The final chunk's response carries the Drive file metadata → insert `StoredFile`, mark the session
   complete.
4. `GET /api/uploads/{id}` — resume. The server asks Drive for the confirmed range (an empty `PUT`
   with `Content-Range: bytes */total`, answered with `308` and a `Range` header) and tells the client
   where to continue.

Chunked rather than one long `POST` because a 96 GB request that dies at 90% must not lose 86 GB, and
because this is the honest precursor to M3's parallel chunks — M3 changes the client's scheduling and
the server's concurrency, not the protocol.

Drive resumable sessions expire after about a week; an `UploadSession` past `ExpiresAt` is marked
failed and the client restarts.

M1 uploads to *the* account. The selection seam (`IUploadTargetSelector`) exists and returns the only
account; M2 fills it in.

## 7. Download — the part the product is actually sold on

`GET /d/{slug}` is a server-rendered Razor page. Server-rendered, not a Vue island, because the
handoff wants `Accept-Language`/`?lang=` to decide FA vs EN before the HTML leaves the server — better
for SEO and for caching, and it means the page is readable with JavaScript off.

`GET /d/{slug}/file` streams the bytes:

- `files.get?alt=media` with `HttpCompletionOption.ResponseHeadersRead`, then copy the response
  stream into `Response.Body`. Never `ReadAsByteArrayAsync`, never a `MemoryStream` — a 214 GB file
  must cost the server a buffer, not a copy.
- The client's `Range` header is forwarded to Drive and Drive's `Content-Range`/`206` is mirrored
  back, so video seeking and resumed downloads work.
- **No redirect to `drive.google.com`, ever.** The Drive file ID and the account email never appear
  in a response body, header, or URL.

**Counting a download.** Increment when the request has no `Range` header, or when its range starts at
byte 0. Otherwise one viewer scrubbing a video burns twenty of a customer's five hundred downloads.
This rule is a unit test, not a comment.

**Refusing a download.** Inactive, expired, or at the download cap all render the same "this link is
no longer available" card. An unknown slug renders *the same card* — the response must not reveal
whether a slug exists.

## 8. Tenant isolation, and the trap that has already been paid for

Harbora has been bitten by exactly the mistake this design must avoid, and it cost four silent
failures in one day. There, `IsUnscoped` was `HttpContext is null` — so background work was unscoped
but an **anonymous HTTP request was scoped to `Guid.Empty`**, and every filtered read came back empty.
Webhooks 401'd, deploys reported "App not found", deletes returned quietly while the container kept
running.

`/d/{slug}` is an anonymous HTTP request. With a global EF query filter on `TenantId`, every public
link in the product would 404 while its row sat plainly in the table — and it would look like a
routing bug, not an isolation bug.

**So M1 has no global query filter.** Tenant scoping is an explicit `tenantId` argument on every
repository method. The type system makes the requirement visible; a forgotten filter becomes a
compile error rather than an empty result set. The public path takes a different repository
(`IPublicLinkReader`) that queries by slug and has no tenant concept at all.

Two tests hold the line: an anonymous integration test that fetches `/d/{slug}` and expects `200`, and
a cross-tenant test that asserts tenant B cannot read tenant A's file through the panel API.

## 9. Errors

- Google 403 `userRateLimitExceeded` / 429 → exponential backoff with jitter in a `DelegatingHandler`
  around the Drive `HttpClient`. The stated budget is 12,000 queries per 60 seconds; M1 will not
  approach it, but the handler is where M3 will need it and it costs nothing now.
- Access tokens live one hour. Refresh on expiry with a single-flight lock so twenty concurrent chunk
  uploads do not trigger twenty refreshes.
- A Drive stream that dies mid-response cannot change a status code already sent — log it and abort
  the response; the client's `Range` resume covers it.
- `/d/*` gets ASP.NET Core rate limiting per IP from day one. It is the only anonymous, expensive,
  publicly-guessable route in the product.

## 10. Frontend surface for M1

Five Vue islands mounted on Razor views, all against the handoff's tokens:

| Island | Screen |
|---|---|
| `filesTable` | "My files" — the compact table, selection, footer actions |
| `fileDetails` | The sticky detail panel |
| `uploadPanel` | Chunked upload with progress |
| `linkSettings` | Slug, expiry, download cap (password and alias are M4) |
| `publicDownload` | Theme + language toggles on the public card |

The shell is `dir="rtl" lang="fa"` with `data-theme` on the root, persisted to `localStorage` and
seeded from `prefers-color-scheme`. Layout uses the handoff's logical properties (`inline-start/end`,
`padding-inline`) so the public page's LTR mode needs no fork.

Loading and empty states are built in M1, not retrofitted: skeleton rows at `--row-pad` height, and a
centred `13px` muted message with the relevant action button.

Files are a flat list in M1. The design shows one folder row and a path field; folders arrive with M2's
union view, where they can be designed once against multiple accounts instead of twice.

## 11. Before implementation starts

Four things are needed from the owner. The first two block the first commit of real code.

1. **Google Cloud project + OAuth client** (client ID, client secret, authorised redirect URI) — and
   confirmation of whether the two Drive accounts are consumer Google One or Workspace, which decides
   OAuth-vs-service-account per §2.
2. **Postgres for development.** A local instance is running on this machine but its credentials are
   not known here, and there is no Docker to stand up a throwaway one. Integration tests need a
   reachable database.
3. **The domain**, for `https://<domain>/d/{slug}`. Configurable as `PublicBaseUrl`; a placeholder is
   fine to start.
4. **The brand name and logo.** The handoff ships a «د» glyph in an accent square as a placeholder.

## 12. Deliberately not in M1

Multi-account union, upload policy and quota tracking (M2). Job queue, `files.copy`, parallel chunks,
SignalR (M3). Link passwords and alias filenames (M4). In-tenant roles, invitations, per-tenant
storage caps (M5). Proxy egress IPs and S3 export (M6). Folders (M2).

**Billing is absent from the brief entirely.** A multi-tenant SaaS on a metered 10 TB pool eventually
needs both a per-tenant storage cap and a way to charge for it. The cap is scoped into M5; charging is
not scoped anywhere yet, and should be before customers arrive.
