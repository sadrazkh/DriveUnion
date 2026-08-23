# Drive Union — M3: queue and transfer

**Date:** 2026-08-23 · **Status:** design proposed; blocked on §13 before the first line of
implementation · **Builds on:** M1 (`2026-08-23-drive-union-m1-design.md`), and on M2 for the
per-account daily quota counter

## 1. What M3 is

M1 moves one file through the box and hands back a link. M3 is where the product stops being a
single request and becomes a system that owes the user an answer over hours: a `jobs` table, a
background worker that drains it, and a panel that watches it move.

The handoff's queue screen states the two mechanisms in one line:

> «کپی بین درایوها سمت گوگل انجام می‌شود؛ آپلود از سرور OVH با chunkهای موازی.»

Server-side copy is the easy half — one API call, no bytes through OVH, and the dashboard row says
so: «کپی سمت گوگل · بدون مصرف پهنای باند سرور». The parallel-chunk half is the hard one, and it is
hard for a reason the mock does not show. §2 is that reason, and it is the section that decides the
shape of everything else in this slice.

M3 delivers:

| | |
|---|---|
| `jobs` + `job_events` + `upload_chunks` + `account_share_grants` | new tables (§4) |
| `JobRunner` | one `BackgroundService`, sessionless, claims work with `FOR UPDATE SKIP LOCKED` (§5) |
| `Copy` | `files.copy` between two operator accounts, idempotent, no bytes through OVH (§3) |
| `Upload` | N concurrent browser connections → one sequential Drive resumable session (§2) |
| `Relay` | Drive → OVH → Drive, the corrective action for a file too big to copy (§8.1) |
| `Export` | in the model and the enum from M3; handler ships in M6 (§4) |
| `JobsHub` | SignalR, ~1 Hz coalesced progress, tenant-scoped, polling fallback (§7) |
| `QuotaWait` | a job parked until the daily budget resets, resuming on its own (§9) |

## 2. The parallel-chunk question, answered

The design shows `resumable ×8` as a job type and «تعداد chunk هم‌زمان: 8» as a setting. Read
literally, that says eight concurrent range writes against one Drive resumable session. **Drive does
not permit that, and building it as drawn would produce a system that appears to work on small files
and corrupts or stalls on the large ones the product exists for.**

### 2.1 What the protocol actually is

Drive API v3 resumable upload, precisely:

1. `POST https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable` — note the `/upload/`
   path prefix, which is separate from the metadata endpoint. Headers `X-Upload-Content-Type` and
   `X-Upload-Content-Length` declare the media. The response's `Location` header is the session URI.
2. `PUT <session URI>` with `Content-Range: bytes <start>-<end>/<total>`.
   `end - start + 1` **must be a multiple of 262,144 bytes (256 KiB)** for every write except the one
   that reaches `total - 1`.
3. Drive answers `308` while the upload is incomplete, with `Range: bytes=0-<lastByteReceived>`. The
   header is **absent** when Drive has received nothing.
4. The write that reaches `total - 1` answers `200`/`201` with the file resource.
5. Status query: `PUT` with `Content-Range: bytes */<total>` and a zero-length body, answered `308` +
   `Range` (this is M1 §6 step 4).

Two properties of that protocol settle the question.

**The acknowledgement is a single contiguous prefix.** `Range: bytes=0-N` — always anchored at zero,
always one range. A server that accepted out-of-order writes would have to report a set of received
ranges, because that is what it would hold. The protocol has no way to say "I have 0–64 MiB and
192–256 MiB". It does not need one, because that state cannot arise.

**Drive may commit fewer bytes than were sent.** The next write must start at `N + 1` taken from that
header, not at whatever the client believes it just finished sending. A concurrent scheduler has no
coherent way to consume that feedback — every in-flight write is racing a moving offset it did not
observe.

Google's documentation for this protocol describes uploading chunks in order, and the equivalent GCS
resumable protocol states sequential ordering as a requirement outright. Nothing in Drive v3
corresponds to S3 multipart or GCS `compose`.

**A naming trap worth naming, because it is almost certainly what produced `×8` in the mock:** Drive
has an `uploadType=multipart`. It means "metadata and media in one request". It does not mean
"multiple parts uploaded in parallel". It is a single-request upload with a 5 MB ceiling.

### 2.2 What M3 builds instead: fan-in, one sequential writer

The parallelism is real; it just belongs on a different leg.

```
  browser                    OVH box                         Google
  ┌───────┐   8 concurrent   ┌─────────────────────────┐    ┌──────────┐
  │ file  │ ═══ PUT #3 ════▶ │  ordered reassembler     │    │ resumable│
  │ slice │ ═══ PUT #1 ════▶ │  ┌─ in-order → straight ─┼───▶│ session  │
  │ pool  │ ═══ PUT #5 ════▶ │  └─ out-of-order → spool │ 1  │ (one)    │
  └───────┘   (window = 8)   └─────────────────────────┘    └──────────┘
```

- **N concurrent connections on the source side.** For `Upload` these are browser `fetch` PUTs, each
  reading `File.slice(start, end)` — independent readable slices, no full-file buffering. For `Relay`
  they are N concurrent `files.get?alt=media` requests with `Range` headers against the source
  account; those are plain reads with no session state, so concurrency there is unproblematic.
- **Exactly one writer per resumable session**, strictly sequential, 256 KiB-aligned, taking its next
  offset from Drive's `Range` header every time.
- **A bounded reorder window.** A chunk whose index exceeds `confirmedIndex + WindowChunks` is
  refused with `409 { code: "ChunkOutOfWindow", confirmedBytes }`. This is what bounds the spool: the
  worst case is `WindowChunks - 1` chunks held at once, not the whole file.
- **The in-order chunk never touches disk.** If the arriving chunk is the next one expected, its
  request body is streamed straight to the Drive session exactly as in M1 §6. Only out-of-order
  chunks land in a spool file, and each is deleted the instant it drains.

This is a change to M1 §6's "no disk spooling, no full-buffer read", and it is a deliberate one — but
a narrow one. The M1 rule survives for the common case: on a healthy connection with chunks arriving
roughly in order, most chunks stream through untouched.

**Why this captures nearly all of the win.** The slow leg is browser → OVH: a customer's home upstream,
one TCP connection, real loss. N connections beat one there by a large factor, and that is where the
mock's `218 MB/s` has to come from. The OVH → Google leg is a 3 ms RTT to Google's edge (the handoff's
own proxy table says so) where a single stream has a small bandwidth-delay product and no reason to
be the constraint.

**Client protocol changes: none.** `POST /api/uploads` gains `concurrency` and `windowChunks` in its
response body; `PUT /api/uploads/{id}/chunk` gains `{ confirmedBytes }` in *its* response body so the
client can slide its window without a separate call; `409 ChunkOutOfWindow` is a new status on an
existing route. No new endpoints, no new verbs. M1 §6's sentence holds exactly: M3 changes the
client's scheduling and the server's concurrency, not the protocol.

### 2.3 What we are not certain of, and how to settle it

Stated plainly, because a fabricated API detail is worse than an admitted gap:

1. **The exact failure of an out-of-order or overlapping write.** We expect `400` with an invalid-range
   error, possibly `503`. We do not know whether a write that overlaps already-committed bytes but is
   otherwise aligned is tolerated or rejected, and we do not know whether a rejected write can
   invalidate the session.
2. **Whether a single session throttles below the link.** Single-stream upload rates to Drive are
   widely reported to plateau well under available bandwidth. We have no Google statement to that
   effect and are not going to assert one.
3. **Whether `files.copy` consumes the destination's 750 GB daily upload budget.** The design asserts
   a 750 GB copy ceiling, which is exactly the daily upload limit; that coincidence is strong evidence
   but it is evidence, not documentation. M3 is built as if it does, because that is the direction
   whose error is harmless.

**The experiment that settles 1 and 2, and it runs in week one, before the reassembler is written:**
a throwaway console app against a real Drive account. Open one resumable session on a 4 GB blob.
(a) Write chunk 0, then attempt chunk 2 — record the status, the body, and whether chunk 1 is
subsequently accepted. (b) Re-write chunk 0 after it is committed — record. (c) Upload the whole blob
single-stream and record wall-clock MB/s over three runs. Paste the raw responses into this file
under a `§2.3 findings` heading. Fifteen minutes of work that de-risks the largest unknown in the
slice.

If (c) shows a single session capping meaningfully below the box's egress, the fan-in design has hit
a ceiling it cannot raise, and the only remaining lever is the one M3 does not build:

### 2.4 The design we are not building

**Split the file across N independent Drive files and stitch on read.** N sessions genuinely run in
parallel; `StoredFile` becomes a manifest of ordered parts; `/d/{slug}/file` concatenates them into
the response, and M1's `Range` forwarding becomes offset arithmetic across part boundaries. It is
feasible precisely because we control the read path and no one ever sees a Drive file — but it makes
every file a multi-object entity, breaks per-file thumbnails and `md5Checksum`, multiplies the copy
path by N, and turns one clean failure mode into N.

It is not in M3. It stays available because M3's seam allows it: the reassembler writes through an
`IResumableWriter` that owns one session. A future `PartitionedResumableWriter` implementing the same
interface is an additive change, not a redesign. Revisit in M6 ("Network tuning") if and only if the
§2.3 experiment shows a per-session cap.

## 3. Server-side copy

### 3.1 The call

```
POST https://www.googleapis.com/drive/v3/files/{sourceFileId}/copy
     ?supportsAllDrives=true
     &fields=id,name,size,mimeType,md5Checksum,createdTime,modifiedTime
Authorization: Bearer <access token of the DESTINATION account>
Content-Type: application/json

{ "name": "<source name>",
  "parents": ["<DriveUnion/{tenant-slug} folder id, in the destination account>"],
  "appProperties": { "duJobId": "<job id>" } }
```

Four things in that request are load-bearing:

- **The token is the destination account's.** `files.copy` creates a new file owned by the caller.
  Authenticate as A1 and the copy lands in A1 and eats A1's storage — the job would report success
  while the pool accounting is silently wrong.
- **`parents` must be a folder the destination account owns.** M1 §5 already creates
  `DriveUnion/{tenant-slug}/` per account, which is exactly the destination this needs.
- **`fields` must be listed explicitly.** Drive v3 returns a partial resource (`kind, id, name,
  mimeType`) when `fields` is omitted, so `size` comes back null and the quota bookkeeping silently
  records zero.
- **`size` is a JSON string**, not a number — int64-as-string, per Drive v3 convention. Parse it as
  such or a 214 GB file deserialises to a surprise.

`supportsAllDrives=true` is harmless on My Drive and required if shared drives ever appear; send it
always. (`supportsTeamDrives` is the deprecated spelling — do not use it.)

### 3.2 How the destination account can see the source file

`files.copy` requires the caller to have at least read access to the source. Three ways to arrange it,
and the choice matters more than it looks:

**(a) Share the source file per job, copy, revoke.** As the source account, `permissions.create` with
`{ type: "user", role: "reader", emailAddress: "<destination>" }` and `sendNotificationEmail=false`;
copy; `permissions.delete`. Rejected. It costs two extra API calls on every job against a 12,000/60s
budget; Drive enforces a separate sharing-operation limit that surfaces as `sharingRateLimitExceeded`
and would be reached by a bulk transfer long before the query budget; permission propagation is not
instantaneous, so the copy immediately after the grant can see `404`; and a job that dies between
grant and revoke leaves a share behind with nothing tracking it.

**(b) A shared drive both accounts belong to.** Rejected, and this one is a trap worth spelling out:
files in a shared drive are owned by the organisation and consume *pooled* storage, not the individual
account's allotment. The product is 2 × 5 TB of individual account storage. Routing files through a
shared drive either destroys that model or costs two copies. It also requires Workspace, which M1 §11
lists as an open question.

**(c) A standing reader grant on the tenant-folder root, made once at account-connect time. This is
what M3 does.** When account B is connected, for every already-connected account A: as A,
`permissions.create` on A's `DriveUnion/` root folder granting B `role: "reader"`, and symmetrically.
Drive folder permissions inherit to descendants, so every file the product ever writes under that root
is already readable by every sibling account, and a copy job makes exactly one API call.

`role` is **reader**, never writer, and that is a guardrail rather than politeness: a writer grant
would let the worker set `parents` to a folder in the *source* account without failing, producing a
file whose owner and location disagree. Reader makes that mistake a hard error at the API.

Grants are recorded in `account_share_grants` (§4) with their Drive `permissionId`, so disconnecting
an account can revoke every grant it holds and every grant held on it. A startup reconciler compares
the table against `permissions.list` on each root folder and repairs drift.

**Failure modes, and what each does:**

| Failure | Cause | Job outcome |
|---|---|---|
| `403` on `permissions.create` at connect time | Workspace domain policy forbids sharing outside the domain, or the accounts are on different domains | Account connects, but is marked `TransferIsolated`; copy jobs targeting it fail pre-flight with `NoTransferGrant` and the panel offers `Relay` instead. The pool still works for upload and download. |
| `404` from `files.copy` | Grant revoked in the Drive UI, or the source file was moved out of `DriveUnion/` | One repair attempt: reconcile the grant, retry once. Still 404 → `Failed` with `SourceNotVisible`. |
| `403 storageQuotaExceeded` | Destination account is full | `Failed` with `DestinationFull`. Not retried — nothing about waiting fixes it. |
| `403` daily-upload-limit / quota reason | Destination hit its 750 GB day | `QuotaWait` (§9), not `Failed`. |
| Timeout / `504` on a multi-hour copy | Copy still running or already finished server-side | §3.3. |
| Source `> CopySizeCeilingBytes` | Too big to copy at all | `Failed` with `CopyExceedsDailyUploadCeiling` (§8.1). |

### 3.3 Idempotency, which is not optional here

A 214 GB copy is one long HTTP request. It will sometimes time out on a call that Google completed. A
blind retry produces a duplicate file and burns the destination's daily budget twice, and nothing in
the panel would show why the quota bar jumped.

The `appProperties: { duJobId }` in §3.1 is the fix. Before starting or retrying a copy, the worker
probes the destination account:

```
GET /drive/v3/files?q=appProperties has { key='duJobId' and value='<job id>' } and trashed=false
    &spaces=drive&fields=files(id,name,size,md5Checksum)&supportsAllDrives=true
```

A hit means the copy already landed: record `StoredFile`, mark the job `Done`, make no second call.
Exact quoting and escaping of that `q` clause should be confirmed against a live account during the
§2.3 session — it is Drive v3 query syntax and it is fiddly.

Integrity: if both source and copy expose `md5Checksum`, compare them and fail the job on mismatch.
Treat an absent checksum as "skip the check", not as a failure — Drive does not populate it for
Google-native types and we do not assume it is present on the copy the instant it returns. MD5 here
is a corruption check, not a security property.

### 3.4 Copy has no progress, and the mock says it does

The dashboard shows `Season-04-Master.mkv` at 73% under `files.copy`. **The Drive API cannot produce
that number.** `files.copy` is one request that returns a file resource or an error; the destination
file does not exist while the copy runs, so there is nothing to poll, and there is no operation
resource to query.

M3 publishes `percent: null` and `bytesPerSecond: 0` for copy jobs. The panel renders an
**indeterminate** bar — a shimmer in `--accent` travelling along the `--line` rail at the same `6px`
height and `6px` radius as the determinate one — and the monospace `11px` sub-line carries elapsed
time instead of rate and ETA, keeping the row's geometry identical. The «سرعت کل» stat card sums only
jobs that actually move bytes through OVH, so a running copy contributes zero to it; that is correct
and matches «بدون مصرف پهنای باند سرور», but it will look wrong to anyone who has not read this
paragraph.

This is a visible divergence from the approved comp. It is §13.4.

## 4. Data model

M3 **adds** four tables and **changes** two M1 tables. It redesigns nothing.

### Added

```
Job              { Id, TenantId, Type, Status, Priority, CreatedByUserId,
                   StoredFileId?, SourceGoogleAccountId?, TargetGoogleAccountId?,
                   UploadSessionId?, TargetDescriptor?,
                   DisplayName, SizeBytes?, BytesDone,
                   Attempt, MaxAttempts, ErrorCode?, ErrorDetail?,
                   NextAttemptAt?, ResumeAt?, CancelRequestedAt?,
                   LeaseOwner?, LeaseExpiresAt?, SupersededByJobId?,
                   IdempotencyKey, CreatedAt, StartedAt?, FinishedAt? }

JobEvent         { Id, JobId, OccurredAt, Kind, FromStatus?, ToStatus?, Detail }

UploadChunk      { Id, UploadSessionId, Index, StartByte, EndByte,
                   SpoolPath, ReceivedAt }

AccountShareGrant{ Id, SourceGoogleAccountId, GranteeGoogleAccountId,
                   RootFolderDriveId, DrivePermissionId, GrantedAt, RevokedAt? }
```

`Job.Type` is `Copy | Upload | Relay | Export`.

- `Copy` — §3, no bytes through OVH.
- `Upload` — §2, browser → OVH → Drive.
- `Relay` — Drive → OVH → Drive. Same writer as `Upload`; the source side is N concurrent `Range` GETs
  instead of N browser connections. This is the corrective action behind «آپلود مستقیم به‌جای کپی».
- `Export` — **defined in M3, handled in M6.** The value exists from the first migration so the
  persisted enum never shifts, and so the queue table, the stat cards and the SignalR contract can
  render the design's completed `invoices-2025.7z · S3 export · A2 → S3 · تمام شد` row without a
  schema change later. **No M3 code path can create one**, and the worker's handler lookup fails an
  `Export` job immediately with `NoHandlerForType` rather than silently leaving it queued.

`Job.Status` is `Queued | Running | QuotaWait | Failed | Done | Cancelled` — matching the handoff's
state list (`'running'|'queued'|'quota_wait'|'failed'|'done'`) plus `Cancelled` (§13.6).

`Job.TenantId` is **not nullable**. Every job belongs to the tenant that owns the file; there is no
system-owned job in M3, and no `Guid.Empty` anywhere near this table (§5).

`Job.TargetDescriptor` is `jsonb`, null for `Copy`/`Upload`/`Relay`; it is where M6's export target
lands without another migration.

`UploadChunk` holds rows **only for out-of-order chunks currently in the spool** — at most
`WindowChunks - 1` per session, deleted on drain. The contiguous prefix is already described by
`UploadSession.BytesConfirmed`; writing a row per chunk would put roughly 1,500 rows behind a single
96 GB upload for no gain. The rows exist so a process restart knows which spool files it still legitimately
holds; the spool directory alone is not trustworthy for that.

Indexes that matter: `(Status, NextAttemptAt, Priority, CreatedAt)` for the claim query,
`(TenantId, CreatedAt DESC)` for the panel list, `(Status, FinishedAt)` for the «ناموفق ۲۴ ساعت»
card, unique `(TenantId, IdempotencyKey)` so a double-clicked «انتقال به A2» cannot start two 214 GB
copies.

### Changed

- **`UploadSession`** gains `JobId?`, `ChunkSizeBytes`, `Concurrency`, `WindowChunks`,
  `SpoolDirectory?`. `BytesConfirmed` keeps its M1 meaning and it is now load-bearing: **the
  contiguous prefix Drive has acknowledged**, never bytes received from the client.
- **`GoogleAccount`** gains `TransferStatus` (`Ready | TransferIsolated`) for the §3.2 policy failure,
  and `GrantsReconciledAt`.
- `StoredFile`, `ShareLink`, `DownloadEvent`, `Tenant`, `AppUser`: unchanged.

**M3 does not add a daily-quota table.** M2 owns per-account 750 GB/day tracking. M3 reads it, and
needs slightly more than a counter — see §13.2.

## 5. The worker, and the sessionless rule

`JobRunner` is a single `BackgroundService` in `DriveUnion.Web`. It has no `HttpContext`, no
`ClaimsPrincipal`, and no ambient tenant. M1 §8 explains why that sentence is the most dangerous one
in this slice.

In the sibling project, `IsUnscoped` was `HttpContext is null`. Background work came out unscoped and
an anonymous HTTP request came out scoped to `Guid.Empty`, so every filtered read returned empty and
four things failed in one day without a single error. Here the mirror-image failure is worse, because
it is *quieter*: a worker whose job query is scoped to an empty tenant claims nothing, logs "no work",
and the panel sits at «در صف ۷» for ever. Nothing throws. Nothing 500s. The only symptom is that
uploads never finish, and the first place anyone would look is Drive.

**So, precisely how the worker reads and writes rows:**

1. **The claim is one atomic SQL statement, over all tenants, through an interface that has no tenant
   parameter.** `IJobStore` is the worker's repository and is the sibling of M1's `IPublicLinkReader`:
   it has no tenant concept at all, so there is no argument anyone could forget and no default anyone
   could get wrong.

   ```sql
   UPDATE jobs SET status = 'Running', lease_owner = @worker,
                   lease_expires_at = now() + @lease, started_at = coalesce(started_at, now())
   WHERE id = (
     SELECT id FROM jobs
     WHERE (status = 'Queued'    AND (next_attempt_at IS NULL OR next_attempt_at <= now()))
        OR (status = 'QuotaWait' AND resume_at <= now())
     ORDER BY priority DESC, created_at
     FOR UPDATE SKIP LOCKED LIMIT 1)
   RETURNING *;
   ```

   `FOR UPDATE SKIP LOCKED` rather than a distributed queue: Postgres is already a hard dependency and
   this is what it is for. One process runs the worker today; the moment a second exists, the failure
   mode of a non-atomic claim is a 214 GB file copied twice and a day's quota gone.

2. **`job.TenantId` is the only source of tenant identity inside the worker.** Every tenant-scoped
   repository call the worker makes — inserting the resulting `StoredFile`, reading the source
   `StoredFile`, resolving the destination folder — passes `job.TenantId` read off the row it is
   currently processing. There is no fallback, no "current tenant", no default.

3. **No ambient tenant accessor exists to be tempted by.** M1 deliberately has none; M3 must not add
   one. An architecture test asserts that no type in `DriveUnion.Core` or `DriveUnion.Infrastructure`
   exposes a current-tenant static or scoped service, and that no EF entity type has a global query
   filter. Both are one reflection assertion each and both fail loudly the day someone "simplifies"
   this.

4. **Scope per job, not per process.** Each iteration creates an `IServiceScope`, resolves a fresh
   `DbContext`, runs one job, disposes. A single long-lived `DbContext` across a six-hour copy
   accumulates a change tracker and holds a connection open across the whole thing.

5. **The lease is a heartbeat, not a promise.** `LeaseExpiresAt` is pushed forward every 15 s while a
   job runs. A reaper pass returns jobs whose lease expired to `Queued` — safe for every implemented
   type: `Copy` because of the `duJobId` probe (§3.3), `Upload` and `Relay` because a resumable
   session resumes from Drive's own offset.

6. **Restart recovery is the same reaper.** On boot, jobs left `Running` are returned to `Queued`
   regardless of lease, spool files with no `UploadChunk` row are deleted, and `UploadChunk` rows with
   no spool file are deleted.

**Concurrency limits inside the worker:**

- `MaxConcurrentJobs` slots, default 3.
- **At most one byte-moving job per destination Google account.** The 750 GB budget is per account;
  running two 200 GB jobs at the same account interleaves them into both parking half-finished at the
  cap instead of one finishing. `Copy` counts against its *destination*. This is why the mock shows
  two running jobs with distinct targets, `→ A2` and `A1 → A2` — read the second as "destination A2"
  and the rule holds for the comp as drawn.
- A **global outbound token bucket** in front of the Drive `HttpClient`, `DriveQueriesPerMinute`,
  default 6,000 — half the stated 12,000/60s budget. §8.2 explains why this, and not retry, is the
  actual answer to `userRateLimitExceeded`.

## 6. Job lifecycle

```
              ┌──────────────────────────── retry (§8.2), NextAttemptAt set
              ▼
  (enqueue)─▶ Queued ──claim──▶ Running ──┬──▶ Done
                 ▲                        ├──▶ Failed ──manual retry──▶ Queued
                 │                        ├──▶ QuotaWait ──ResumeAt──▶ Queued
                 └──lease expired─────────┤
                                          └──▶ Cancelled
```

Every transition writes one `JobEvent`. Progress ticks do not — that is the difference between an
audit trail and a log file in a table.

Enqueue points, all of which return the `Job` row so the panel can start watching it immediately:

| Route | Creates | Notes |
|---|---|---|
| `POST /api/transfer` | `Copy` | The comp's «انتقال به A2» footer button. Accepts a batch; `Idempotency-Key` header honoured. |
| `POST /api/uploads` | `Upload` | M1's route, now also creating the job that owns the session. |
| `POST /api/jobs/{id}/convert-to-relay` | `Relay` | The comp's «آپلود مستقیم به‌جای کپی». Links `SupersededByJobId`; does not mutate the failed row. |
| `POST /api/jobs/{id}/retry` | — | The comp's «تلاش مجدد با backoff». `Attempt = 0`, `NextAttemptAt = now`, status → `Queued`. |
| `POST /api/jobs/{id}/cancel` | — | §13.6. Sets `CancelRequestedAt`; best-effort. |

Cancellation is checked between chunks and at the top of each worker loop. **A `Copy` in flight is not
interruptible** — it is one blocking call inside Google. If it completes after cancellation was
requested, the job ends `Done` and keeps the file; we do not delete a copy that already cost a day's
quota to make.

## 7. Live progress over SignalR

The handoff asks for SignalR or 2-second polling; M3 ships SignalR with polling as the fallback,
because a panel that goes silent behind a corporate proxy is worse than one that is two seconds stale.

**Hub.** `JobsHub` at `/hubs/jobs`, `[Authorize]`, never anonymous — the queue reveals which accounts
hold what. `OnConnectedAsync` joins `tenant:{TenantId}` when the user has one, and `operator` when
`IsOperator`.

**The worker publishes through `IHubContext<JobsHub, IJobsClient>` resolved from the root provider.**
This matters given §5: the hub context is a singleton with no connection scope and no request context,
so publishing from a sessionless background service is safe by construction. The worker never touches
`Clients`, `Context`, or anything else that only exists inside a hub invocation.

**Messages.** Three, strongly typed:

```
JobProgress     { jobId, tenantId, bytesDone, bytesTotal?, percent?, bytesPerSecond,
                  etaSeconds?, sentAt }
JobStateChanged { jobId, tenantId, status, errorCode?, retryAt?, resumeAt?, attempt,
                  supersededByJobId? }
QueueSummary    { running, queued, quotaWait, aggregateBytesPerSecond, failedLast24h,
                  navBadge }
```

- `percent` is null for `Copy` (§3.4) and the bar renders indeterminate.
- `bytesDone` is `UploadSession.BytesConfirmed` — **Drive-acknowledged bytes, not bytes received from
  the browser.** With a reorder spool these differ by up to a window, and a bar that shows 100% while
  Drive still owes 30 GB is the classic lying progress bar. It is also the number resume arithmetic
  uses, so there is exactly one truth.
- `etaSeconds` is `(total - confirmed) / EWMA(rate)` over a 30 s window, null when the rate is zero or
  the type is `Copy`. The client formats it as the comp's `۰۰:۱۹:۲۴`.
- `aggregateBytesPerSecond` feeds «سرعت کل», summed across running jobs in the publisher rather than
  the database. Copy jobs contribute zero.
- `navBadge` is `running + quotaWait`, which is how the comp's sidebar shows `3` while the stat cards
  show `2` running and `7` queued — the three rows of «کارهای فعال» are the two moving jobs plus the
  one parked at the quota. Anyone who makes the badge `running + queued` will ship `9` and it will
  look wrong.

**Rate.** Progress is coalesced to at most **one message per job per second**. Eight chunks across
three jobs emitting per-write events would be a flood, and the comp's bars need roughly 1 Hz. The
publisher keeps the latest sample per job and flushes on a timer; `JobStateChanged` is never coalesced
because losing a transition loses the audit.

**What must never appear in a hub message:** a Drive file ID, a Google account email, a resumable
session URI, or a spool path. M1 §1 answer 4 is absolute — a customer must never learn which Google
account holds their file. **Consequently the queue table's «مقصد» column (`A1 → A2`) is operator-only**
and is either hidden or rendered as `→ pool` for a customer. The comp is the operator's view of that
screen; the customer's is the same table with one column gone.

**Subscription in the panel.** `@microsoft/signalr` with `withAutomaticReconnect()`. On `onreconnected`
the client calls `GET /api/jobs` and replaces its list wholesale — SignalR buffers nothing for an
absent client, so a job that finished during the gap would otherwise stay at 41% for ever. After
`ReconnectFallbackAttempts` failures the client falls back to polling `/api/jobs/active` every 2 s and
keeps trying the socket in the background.

**Scale-out.** One web process in M3, so no backplane. With two, a client connected to process B sees
nothing from process A's worker, and the fix is a Redis backplane — flagged here so nobody discovers
it during a scale-out at 02:00.

## 8. The two designed errors

### 8.1 «سقف ۷۵۰GB رد شد» — the copy ceiling

`CopySizeCeilingBytes = 750_000_000_000` — **decimal, not binary.** Google states the daily upload
limit in GB, and 750 GiB is 805 GB; using the wrong unit would let 55 GB of files through the check
that the API then refuses. Write the constant with the underscores and a comment, because this is
exactly the kind of thing that silently differs.

**The check runs in the worker's pre-flight, not at the API.** That is deliberate and it is the choice
a reviewer should push on. The reason: «انتقال به A2» over a multi-select enqueues a batch, and one
812 GB file among forty should not fail the request or silently vanish from it. The job is created,
claimed, fails its first check before any Drive call, and shows up exactly where the comp puts it —
as a `ناموفق` row in the queue and a card in «کارهای ناموفق» with its corrective action attached:

> «بزرگ‌تر از سقف ۷۵۰GB برای files.copy — نیاز به آپلود مستقیم دارد.» → «آپلود مستقیم به‌جای کپی»

The button posts to `/api/jobs/{id}/convert-to-relay`, which creates a `Relay` job with the same
source and destination and sets `SupersededByJobId` on the failed row. The failed row is never mutated.

**The relay is honest about what it costs**, and the panel must be too: 812 GB crosses OVH in both
directions, and 812 GB is more than one day's upload budget at the destination. So the relay will
itself hit the cap partway through, park in `QuotaWait`, and finish 62 GB into the next day. That is
not a defect — it is §9 doing exactly the work it exists for, and it is why the resumable session's
one-week lifetime (M1 §6) matters more in M3 than it did in M1.

`Relay` verifies what it wrote: the writer MD5s the ordered stream as it goes and compares against the
`md5Checksum` on the returned file resource. Size mismatch is always a hard failure. A read that
returns `403` with an abuse reason is a hard failure with `SourceBlockedByGoogle` — we do not set
`acknowledgeAbuse` on the operator's behalf.

### 8.2 «تلاش مجدد با backoff» — 403 `userRateLimitExceeded`

M1 §9 already puts the exponential-backoff-with-jitter `DelegatingHandler` around the Drive
`HttpClient`. M3 does not re-specify it. M3 adds three things around it.

**(1) The real fix is not retrying — it is not emitting them.** The 12,000-queries-per-60s budget is
consumed by request *count*, and chunk size is the lever nobody looks at: a 96 GB file at 64 MiB
chunks is about 1,500 PUTs spread over hours, which at eight concurrent is nowhere near the budget.
The budget is at risk from
`files.list` polling and from many small files, not from big uploads — ten thousand 4 MB files cost
more requests than a terabyte does. Hence the global token bucket in
§5 at `DriveQueriesPerMinute = 6,000`, half the stated ceiling, shared across every Drive call the
process makes. When a job's scheduler cannot draw a token it waits rather than firing and retrying — a
403 costs a round trip, a token costs nothing.

**(2) Two tiers of backoff, and they are for different problems.** The handler's is fast (seconds,
in-request, in-memory) and clears the burst case. When it exhausts its attempts it throws a typed
`DriveRateLimitedException` carrying `Retry-After` if present, and the worker converts that into the
slow tier: status back to `Queued`, `Attempt++`, `NextAttemptAt = now + jobBackoff(Attempt)` on the
order of minutes, persisted. Sustained pressure must not hold a worker slot for ten minutes while six
other jobs wait, and it must survive a restart.

**(3) The handler cannot retry the chunk PUT, and this is the sharpest edge in the slice.** The
in-order chunk's body is a forwarded, non-rewindable stream from the browser (§2.2). A `DelegatingHandler`
that retries it sends an empty or partial body and Drive commits garbage. So:

- Requests carrying a non-rewindable body set `HttpRequestOptions["DriveUnion.NoHandlerRetry"] = true`
  and the handler skips them entirely, rethrowing immediately.
- Recovery for those is at the job level: re-query the offset with `Content-Range: bytes */total`,
  then re-send. **The server can only re-send bytes it still holds** — an out-of-order chunk is in the
  spool and replays for free; an in-order chunk was streamed through and is gone, so the server answers
  the client with `409 { code: "ResendChunk", confirmedBytes }` and the browser, which still has the
  file, sends it again. For `Relay` there is no client and the server simply re-issues the `Range` GET.

**Classification, because the status code is not enough.** A `403` is retryable only when
`error.errors[0].reason` is `userRateLimitExceeded` or `rateLimitExceeded`. `403 storageQuotaExceeded`
means the account is full and no amount of waiting helps. The handler must therefore buffer the
*response* body to read the reason — cheap, they are small, and doing it on the response is unrelated
to the request-body rule above. Every classification decision records the raw reason string in
`Job.ErrorDetail` so the classifier can be tightened from production data rather than from guesses.

The comp's corrective button «تلاش مجدد با backoff» maps to `POST /api/jobs/{id}/retry`, available to
owners and the operator, which resets `Attempt` and re-queues immediately.

## 9. `QuotaWait` — «صبر سهمیه»

A job parked until the daily budget resets, resuming on its own. The comp:

> «در انتظار سهمیه A1» · «صف» · «شروع خودکار ساعت ۰۰:۰۰ بعد از ریست سهمیه»

**Two ways in, and both are needed.**

*Predictive.* Before a byte-moving job starts, the worker asks M2's ledger whether the destination
account has room. With «توقف خودکار نزدیک سهمیه» on, the ceiling is the design's soft one — «در ۷۲۰GB
از ۷۵۰GB روزانه» — not the hard 750. Insufficient room → `QuotaWait` before a single byte moves, which
is what puts `raw-photos-2026.zip` on the dashboard at 0% with a start time rather than at 43% with an
error.

*Reactive, and it is the ground truth.* A job that hits the real quota error mid-flight parks
regardless of what the counter believed. Counters drift; Google does not.

**Classifying that error honestly.** `403 storageQuotaExceeded` is "account full" and is a hard
failure. `403 userRateLimitExceeded` / `rateLimitExceeded` is §8.2. **We do not know the exact `reason`
string Google returns for the 750 GB/day upload cap** and will not invent one. So the classifier's
default arm is: an unrecognised `403` whose message mentions quota or limit **parks** rather than
fails, records the raw reason in `Job.ErrorDetail`, and the first real occurrence in production tells
us what to name it. Parking a job that should have failed costs a day of delay; failing a job that
should have parked loses hours of transferred bytes.

**When it resumes.** `ResumeAt` is the next occurrence of `QuotaResetLocalTime` (default `00:00`) in
`QuotaResetTimeZone`. No per-job timer exists — the §5 claim query already carries
`status = 'QuotaWait' AND resume_at <= now()`, so waking is free and survives a restart.

**We do not know Google's reset instant.** The documentation says "each day" without an anchor, and it
may be a rolling 24-hour window rather than a boundary. The comp promises «ساعت ۰۰:۰۰», which is a
promise to a user, so the value is configuration with a default and §13.3 asks the owner which zone it
means. If observation shows a rolling window, `ResumeAt` becomes `oldestChargeInWindow + 24h` and only
that one expression changes.

**A quota park does not consume the retry budget.** `Attempt` is not incremented and `MaxAttempts` is
not consulted. A 900 GB relay parked three nights running would otherwise exhaust its retries and fail
for no reason a user could understand.

**A parked job holds no resources.** Its worker slot is released, its per-account lock is released, and
for `Upload`/`Relay` the resumable session URI stays valid (M1 §6: about a week), so resumption is a
status query and a continue. An `UploadSession` past `ExpiresAt` while parked is failed with
`SessionExpiredWhileParked` — the client restarts, which for `Relay` is automatic.

## 10. Frontend surface

Two new Vue islands and one change to an M1 island, all at the handoff's exact tokens.

| Island | Screen | Notes |
|---|---|---|
| `queueTable` | «صف انتقال و آپلود» | Four stat cards `repeat(4,1fr)`, `12px` gap, `border-radius:12px`, `padding:14px`, label `11.5px` `--muted`, value `22px/800` monospace; «ناموفق ۲۴ ساعت» in `--danger`. Table `minmax(0,2fr) 130px 110px 1fr 90px`. |
| `activeJobsCard` | Dashboard «کارهای فعال» | Row: name `13px/600`, monospace `11px` `--muted` descriptor, percent or status word at the end, `6px` bar, monospace `11px` sub-line. `quota_wait` shows «صف» in `--warn` with a `0%` bar. |
| `failedJobsCard` | Dashboard «کارهای ناموفق» | `8px` `--danger` dot, count at the end, one outline action button per item at `12px`/`radius 8`. Behind the comp's `showErrorPanel` prop. |
| `uploadPanel` (changed) | M1's upload island | Gains the §2.2 scheduler: a fixed pool of `concurrency` in-flight PUTs over `File.slice()`, a window bounded by `windowChunks`, and handling for `409 ChunkOutOfWindow` (wait) and `409 ResendChunk` (re-send). |

The «نوع» column is a **rendered label, not the enum**: `files.copy` for `Copy`, `resumable ×{N}` for
`Upload` and `Relay`, `S3 export` for `Export`. The comp's vocabulary survives without the enum having
to carry presentation.

Loading and empty states are built now, per M1 §10: skeleton rows at `--row-pad`, and an empty queue
showing a centred `13px` `--muted` line with the «آپلود فایل» button.

## 11. Configuration

| Key | Default | Why this number |
|---|---|---|
| `Transfer:ChunkSizeBytes` | `67_108_864` (64 MiB) | The comp's «اندازه هر chunk: 64 MB». M1 fixed 32 MiB; M3 makes it configurable because the spool and the query budget both key off it. 64 MiB = 256 × 256 KiB, so alignment holds. |
| `Transfer:Concurrency` | `8` | The comp's «تعداد chunk هم‌زمان: 8». |
| `Transfer:WindowChunks` | `8` | Equal to concurrency: no connection can run further ahead than the pool allows. |
| `Transfer:MaxConcurrentJobs` | `3` | Two running plus headroom, matching the comp's steady state. |
| `Transfer:SpoolPath` | — | §13.7. Worst case `MaxConcurrentJobs × (WindowChunks − 1) × ChunkSize` ≈ **1.31 GiB**; startup refuses to run with less than 2× that free. |
| `Drive:QueriesPerMinute` | `6_000` | Half the stated 12,000/60s. §8.2. |
| `Drive:CopySizeCeilingBytes` | `750_000_000_000` | Decimal GB. §8.1. |
| `Quota:SoftCeilingBytes` | `720_000_000_000` | The comp's «در ۷۲۰GB از ۷۵۰GB روزانه». |
| `Quota:ResetLocalTime` / `:TimeZone` | `00:00` / §13.3 | §9. |
| `Jobs:LeaseSeconds` / `:HeartbeatSeconds` | `60` / `15` | §5. |
| `Jobs:MaxAttempts` | `5` | Quota parks excluded (§9). |

**Spool files hold customer file contents.** Mode `0600`, a directory outside `wwwroot` served by no
middleware, excluded from backups, swept on boot.

## 12. Tests that hold this design

Every one of these runs against M1's fake `IDriveClient` except where noted — this machine has no
Docker and tests must never reach Google (M1 §4).

1. **Alignment.** Any non-final Drive write whose length is not a multiple of 262,144 fails.
2. **Reassembly.** Feed a shuffled chunk order into the reassembler; assert the fake `IDriveClient`
   observes strictly ascending, contiguous, aligned ranges and that the reconstructed bytes equal the
   input. This is the test that proves §2.2 is implemented rather than intended.
3. **Window.** A chunk beyond `confirmedIndex + WindowChunks` gets `409 ChunkOutOfWindow` with the
   current `confirmedBytes`, and the spool never exceeds its bound.
4. **Offset trust.** A fake whose `308` reports fewer bytes than were sent causes the next write to
   start at Drive's number, not at the writer's arithmetic.
5. **Sessionless claim.** One worker tick with no `HttpContext` and no ambient tenant claims and
   completes tenant A's job. **This is the direct regression test for the M1 §8 failure** and it must
   assert a non-empty result, because the bug's whole signature is an empty one.
6. **Cross-tenant.** Tenant B cannot read, retry, or cancel tenant A's job through `/api/jobs`, and
   receives no hub message about it.
7. **No ambient tenant.** Architecture test: no current-tenant service and no global query filter in
   Core or Infrastructure.
8. **Copy ceiling.** An 812 GB `Copy` fails pre-flight with `CopyExceedsDailyUploadCeiling` and
   `IDriveClient.CopyAsync` is never called. `convert-to-relay` produces a `Relay` job linked by
   `SupersededByJobId` and leaves the failed row untouched.
9. **Copy idempotency.** A `Copy` re-run after a simulated timeout finds the file by `duJobId` and
   does not issue a second copy.
10. **Rate limit vs quota.** `403 userRateLimitExceeded` → `Queued` with a future `NextAttemptAt` and
    `Attempt + 1`. `403` with a quota reason → `QuotaWait` with `Attempt` **unchanged**.
11. **Redaction.** A non-operator's `/api/jobs` response and every hub message they receive contain no
    Drive file ID, account email, or session URI.
12. **Claim atomicity** — the one that needs a real Postgres, so it runs in CI only: two workers
    against the same table claim disjoint jobs and neither claims twice.

## 13. Before implementation starts

1. **The §2.3 experiment, run first.** Fifteen minutes against a real Drive account, results pasted
   into §2.3. It settles whether the fan-in design is sufficient or whether §2.4 comes back, and it is
   the only item here that blocks the reassembler.
2. **What shape M2's quota tracking takes.** M3 needs `TryReserve(accountId, bytes) → reservation` /
   `Commit` / `Release`, not a bare counter: two jobs both reading "180 GB left" and both starting is
   two jobs parked half-done. If M2 has already shipped a plain counter, the reservation is M3's to
   add and should be scoped now.
3. **Which midnight «ساعت ۰۰:۰۰» means.** Tehran, where the operator is, or Pacific, which is the
   usual anchor for Google's quota day? The panel makes a promise to the user either way; we need the
   value, and we need to know that observation may later replace it with a rolling window (§9).
4. **Copy progress.** The approved comp shows 73% on a `files.copy` row and the API cannot produce it
   (§3.4). Confirm the substitute — indeterminate bar plus elapsed time, same geometry — or commission
   a different treatment.
5. **Confirm the queue screen's «مقصد» column is operator-only.** M1 §1 answer 4 says a customer must
   never learn which account holds their file, which makes `A1 → A2` operator-only. The comp is the
   operator's view; the customer's needs sign-off on hidden-vs-`→ pool`.
6. **A cancel affordance, which the comp does not have.** An unstoppable 812 GB relay started by
   mistake is a real hazard. Proposal: an `×` on the queue row, operator and owner only, with `Copy`
   documented as not interruptible. Needs a yes and a token-level treatment.
7. **The OVH box's spool disk and NIC.** Free space on the target volume and the link speed to Google.
   The §11 defaults assume ~3 GiB of spool headroom and an egress path that is not the constraint;
   both are guesses until someone reads the box.

Items 1 and 2 block the first commit. The rest block the merge.

## 14. Deliberately not in M3

- **Split-file parallel sessions** (§2.4) — the only design that beats a per-session throughput cap,
  deferred behind the §2.3 measurement and an `IResumableWriter` seam that keeps it additive.
- **The `Export` handler.** The type, the row shape and the `TargetDescriptor` column exist from M3's
  first migration so the enum never shifts and the queue renders M6's rows without a schema change;
  the S3 implementation is M6, and no M3 route can enqueue one.
- **A SignalR backplane.** One web process in M3. Required the day there are two (§7).
- **Proxy egress IP selection per chunk.** M6. M3's scheduler has no notion of which IP a connection
  leaves on, and the handoff is emphatic that this would not help quota anyway: «سهمیه ۷۵۰GB به ازای
  اکانت است، نه IP.»
- **Per-tenant fairness in the queue.** M3 orders by `priority, created_at` globally. One tenant
  enqueueing four hundred jobs starves the others, which is invisible at two tenants and obvious at
  twenty. Round-robin by tenant belongs with M5's tenancy work.
- **Scheduled and recurring jobs.** The comp's `nightly-sync #4471` implies a schedule; M3 has no
  scheduler, and that job exists in the mock only as a carrier for the rate-limit error card.
- **Bandwidth throttling per job or per tenant**, and the traffic chart that would justify it — M6.
- **Deleting the source after a copy.** Every M3 transfer is a copy, never a move. The comp's «انتقال
  به A2» reads as "move"; it is not one, and if the owner wants a true move it needs its own decision
  about what happens to the share links pointing at the original.
