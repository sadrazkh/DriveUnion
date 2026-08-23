# Drive Union — M2: pool and quota

**Date:** 2026-08-23 · **Status:** design draft, follows the approved M1 spec · **Blocks on §13 only
for the four items listed there; the rest can be built against M1 as it stands**

## 1. What M2 is for

M1 makes one Google account work end to end. M2 makes *a pool of them* work, and makes the pool
legible to the operator without leaking a single fact about it to a tenant.

Three things change for the operator: accounts two and three can be connected, every file list spans
all of them, and the daily 750 GB per-account upload ceiling becomes a number the product understands
rather than a surprise 403. One thing deliberately does not change for the tenant: their screens look
exactly as they did in M1. They gain folders and nothing else. No account column, no account chips, no
quota bar, no Drive identifier, no hint that there is more than one backend. M1 §1.4 made that a
product rule; M2 is the first milestone where it is possible to break it, so most of the isolation
work in this document is about the shape of the *response*, not the shape of the query.

M2 also fills the seam M1 left: `IUploadTargetSelector`, which decides which account a new file lands
on.

## 2. Schema delta

M2 adds four tables and seven columns. It renames nothing and drops nothing.

**Changed — `GoogleAccount`:**

| Column | Type | Why |
|---|---|---|
| `ShortCode` | `text not null`, unique | The `A1`/`A2`/`A3` the design uses in every table cell, chip and job row. Assigned once at connect time as `A{count+1}`; **never reused** after a disconnect, because the code appears in old job history and in support conversations. |
| `GoogleUserId` | `text not null`, unique | The stable Google identity of the account, used to refuse connecting the same account twice — see §3. |
| `Priority` | `int not null default 0` | The manual priority order from the settings screen, and the deterministic tie-break for the other two policies. |
| `AcceptsUploads` | `bool not null default true` | Operator-controlled drain switch. Distinct from the quota auto-stop, which is derived and transient. |
| `DailyUploadLimitBytes` | `bigint null` | Per-account override of the pool default. Nullable because the fleet is homogeneous today and probably always; present because §4 cannot promise the 750 GB figure is the same for every account type. |
| `StorageQuotaSyncedAt` | `timestamptz null` | Feeds «آخرین همگام‌سازی …» on the dashboard, and lets the UI mark a stale bar instead of drawing a confident wrong one. |
| `QuotaBlockedUntilUtc` | `timestamptz null` | Set when Google itself refuses an upload for quota reasons. Observed truth outranks our own counter. |
| `ChangesStartPageToken` | `text null` | Captured at connect time, unused in M2. Costs one API call now and saves a full re-list later; see §14. |

`Status` keeps M1's column and gains a fixed set of values: `Healthy`, `NeedsReauth`, `ReadOnly`,
`Removed`. «سالم» on the account card maps to `Healthy`.

M1's `QuotaTotalBytes` / `QuotaUsedBytes` continue to mean *storage*. Everything M2 adds about the
daily upload ceiling is prefixed `DailyUpload`. The two are different quotas with different units,
different reset behaviour and different sources, and one of the easier ways to ship a wrong number
here is to let the word "quota" mean both.

**Changed — `StoredFile`:** adds `FolderId uuid null` (null = the tenant's root) and
`FolderPath text not null default '/'`, the denormalised path of the containing folder. See §7 for why
the path is denormalised and what that costs.

**Changed — `UploadSession`:** no columns. Its existing `BytesConfirmed` becomes the source of every
quota increment (§4), and it gains an index on `(GoogleAccountId, Status)` because the selector reads
in-flight bytes per account on every upload request.

**New:**

```
Folder          { Id, TenantId, ParentFolderId?, Name, Path, CreatedAt, DeletedAt? }
FolderMapping   { FolderId, GoogleAccountId, DriveFolderId, CreatedAt }   PK (FolderId, GoogleAccountId)
AccountUploadDay{ GoogleAccountId, QuotaDate, BytesConfirmed, UpdatedAt } PK (GoogleAccountId, QuotaDate)
PoolSettings    { Id (=1), UploadPolicy, AutoStopNearQuota, DailyLimitBytes, SoftStopBytes,
                  RoundRobinCursorAccountId?, UpdatedAt, UpdatedByUserId }
```

`Folder` carries `TenantId` and is reached only through repository methods that take an explicit
`tenantId`, per M1 §8. `FolderMapping` and `AccountUploadDay` deliberately carry no `TenantId` —
like `GoogleAccount`, they describe the operator's pool, not anyone's content. `PoolSettings` is a
single global row, not per tenant: the pool is the operator's and so is the policy that fills it.

One migration. Backfill is three statements: `ShortCode = 'A1'` for M1's account, `FolderPath = '/'`
for every existing file, `PoolSettings` seeded with the design's defaults. `AccountUploadDay` starts
empty, so on deploy day the dashboard under-reports the day's uploads. That is correct and
self-correcting by the next reset; do **not** backfill it from `StoredFile.CreatedAt`, which counts
only uploads that finished and would therefore be wrong in the direction that matters.

## 3. Connecting the second and third account

The flow is M1's, run again. Four things about running it a second time:

**`prompt=consent&access_type=offline` is not optional.** Google returns a `refresh_token` only on the
first authorisation for a given user/client pair unless consent is forced. Connecting account two
while the operator's browser already holds a Google session, without `prompt=consent`, yields an
access token and no refresh token — and the account looks perfectly connected for about an hour. This
is the single most likely way to lose a day to M2.

**Refuse duplicates on identity, not on email.** Gmail treats `archive.main@gmail.com`,
`archive.main+cold@gmail.com` and `archivemain@gmail.com` as the same mailbox, so an email comparison
does not prevent connecting the same account twice — which would double-count 5 TB of pool capacity
and let the selector "balance" a file onto the account it just came from. After the token exchange,
request `openid email` alongside the drive scope and key on the id token's `sub`, stored as
`GoogleUserId`. If for any reason the id token is unavailable, Drive's own
`GET /drive/v3/about?fields=user(emailAddress,permissionId),storageQuota` returns a `permissionId` on
the user resource that serves the same purpose. Adding `openid` and `email` does not change M1 §2's
verification story: `drive` is already the restricted scope that governs it, and those two are not
sensitive.

**`about.get` requires `fields`.** Drive v3 rejects it without one. The same call gives us
`storageQuota { limit, usage, usageInDrive, usageInDriveTrash }`, which is where
`QuotaTotalBytes`/`QuotaUsedBytes` come from. `limit` is **absent** when the account has unlimited or
pooled storage, which is possible on some Workspace editions — in that case the used-space bar has no
denominator and must render as a plain usage figure with no bar, and the selector's free-space test
(§5) treats the account as unbounded. Handle the missing field; do not default it to zero, which would
make the account look full and quietly remove it from the pool.

**Consent-screen consequences.** While the OAuth consent screen is in *Testing*, each connected
account must be listed as a test user, and M1 §2's seven-day refresh-token expiry now costs three
reconnections a week instead of one. Google also documents a limit on the number of live refresh
tokens per account per client (50, with the oldest invalidated beyond that) — irrelevant in
production at three accounts, but a real footgun during development, where re-running the connect
flow repeatedly is normal. If accounts start silently needing re-auth during a heavy development day,
this is the first thing to check.

**Disconnect.** «قطع اتصال» means two different things depending on whether the account still holds
files, and the confirmation dialog says which one it is doing:

- **Zero files:** full disconnect. Revoke at `https://oauth2.googleapis.com/revoke`, clear both
  protected token columns, set `Status = Removed`. The `ShortCode` stays allocated.
- **One or more files:** read-only. `Status = ReadOnly`, `AcceptsUploads = false`, tokens retained.
  The account stops receiving uploads and stops counting toward pool capacity in the sidebar, and
  every existing `/d/{slug}` served from it keeps working.

The second case exists because a button that revokes a token also breaks every public link backed by
that account, turning a share link into a 500 rather than a 404, with no way back. Evacuating an
account requires `files.copy`, which is M3. Until then, read-only is the honest maximum. The card
shows a «فقط خواندنی» badge in place of «سالم» so the state is visible without opening anything.

«تازه‌سازی توکن» forces a refresh grant immediately and rewrites `AccessTokenExpiresAt`. Its only real
job is letting the operator confirm a re-consent actually took, rather than finding out an hour later.

The card subtitle «scope: drive · توکن معتبر تا ۵۹ دقیقه دیگر» is computed client-side from the
absolute `AccessTokenExpiresAt` the API sends, not from a server-rendered minute count that is stale
on arrival. Past expiry it reads «منقضی — تازه‌سازی خودکار در آپلود بعدی» rather than counting
backwards, because an expired access token is a non-event: M1's single-flight refresh handles it.

## 4. The 750 GB/day counter

### What is counted

**Bytes Google confirmed, not files that completed.** A 300 GB upload abandoned at 90% consumes 270 GB
of the day's allowance and never produces a `StoredFile` row. Counting completed files would therefore
under-report exactly on the days when the number matters most.

The increment happens in the chunk handler, in the same transaction that advances
`UploadSession.BytesConfirmed`, and the amount is the *delta*:
`newConfirmed − UploadSession.BytesConfirmed`. On the resume path (M1 §6.4), where Drive reports the
confirmed range in a `308` and we may re-learn a range we already recorded, the delta is zero and
nothing is double counted. This is why M2 needs no new column on `UploadSession`: M1 already tracks
the only number the counter needs.

The write is one statement, so there is no read-modify-write race between concurrent chunks:

```sql
insert into account_upload_day (google_account_id, quota_date, bytes_confirmed, updated_at)
values (@account, @day, @delta, now())
on conflict (google_account_id, quota_date)
do update set bytes_confirmed = account_upload_day.bytes_confirmed + excluded.bytes_confirmed,
              updated_at = now();
```

`@day` is computed at confirmation time, so a chunk acknowledged one second after the reset counts
against the new day. Accounting is exact except across a process crash that lands between Drive's
acknowledgement and our commit *and* spans the reset boundary, which can misattribute at most one
chunk — 32 MiB against 750 GB, noise.

M3's `files.copy` adds to the same table through the same method. See the unknowns below for why we
count copies at all.

### When it resets

**We do not run a reset job, and that is deliberate.** The counter resets because the date key
changes; nothing has to happen at midnight for it to be correct. A nightly reset job that fails to run
would leave every account looking exhausted and stop all uploads, and it would fail silently.

The day key is `TimeZoneInfo.ConvertTime(clock.UtcNow, resetZone).Date`, using M1's `IClock` so it is
testable. `Quota:ResetTimeZone` is configuration, defaulting to `America/Los_Angeles`.

**Google's reset is not local midnight, and it is probably not midnight anywhere we control.** What is
publicly documented is that the limit clears "within 24 hours"; the two behaviours consistent with
that and with widely reported field experience are (a) a fixed daily window anchored to Google's own
service day, commonly reported as US Pacific, and (b) a rolling 24-hour window from the upload that
hit the cap. **I could not verify which is true from here, and this spec does not assert one.** The
design is built to be correct either way:

- Our counter uses a fixed window anchored to `Quota:ResetTimeZone`. If Google is actually rolling,
  our number is a conservative estimate near the boundary, not a permit.
- The authority is Google's own refusal. When an upload fails with a quota 403, we set
  `QuotaBlockedUntilUtc = max(nextResetInstant, now + 24h)` and mark the day counter at the ceiling so
  the bar reads 100%. That is the interpretation Google's own guidance supports, and it is correct
  under both semantics.
- No upload is ever discarded because of a reset. A Drive resumable session lives about a week (M1
  §6), so an upload that runs out of daily allowance pauses and resumes tomorrow against the same
  session URI. Nothing about the reset needs to be predicted accurately for the product to work; the
  prediction only drives the copy the UI shows.

Consequently the design's «شروع خودکار ساعت ۰۰:۰۰ بعد از ریست سهمیه» is not shipped literally.
The UI renders the computed next-reset instant in the panel's timezone — «ادامه پس از ریست سهمیه —
۱۰:۳۰» if the reset zone is Pacific and the panel is Tehran. A hardcoded ۰۰:۰۰ is wrong by ten and a
half hours, and it is wrong in the direction that makes the operator think the product is broken.

### The ladder

Per account, with the pool defaults:

| | Bytes | Meaning |
|---|---|---|
| warn colour | ≥ 80% | Bar turns `--warn`. Advisory. |
| danger colour | ≥ 95% | Bar turns `--danger`. Advisory. |
| soft stop | 720 GB (96%) | `AutoStopNearQuota` on: the selector stops choosing this account. |
| hard limit | 750 GB | Google's ceiling. We should never reach it. |

The design gives the 80/95 colours and the 720/750 switch as unrelated facts on two different screens;
stated as one ladder they are coherent — the bar goes red *before* the account stops, so the stop is
never a surprise.

`Quota:DailyLimitBytes` is configured as an absolute byte count (`750000000000`), never as a "GB"
number multiplied in code. See §9 for why that constant is decimal while file sizes are formatted
binary.

**What the soft stop does and does not do.** It removes the account from the selector's candidate set.
It does **not** cancel in-flight uploads. Killing a 90%-complete 200 GB upload to protect 30 GB of
headroom is strictly worse than finishing it, and the 30 GB gap between the stop and the limit exists
precisely so in-flight work can land. A target is chosen once, at `POST /api/uploads`, and pinned to
the `UploadSession`; it is never re-selected, because the resumable session URI belongs to that
account.

When every account is stopped, `POST /api/uploads` returns `503` with `Retry-After` and
`{ "error": "no_upload_target", "retryAfterUtc": ... }`. For an operator the body also names the
blocked accounts. For a tenant it does not, and the message is «آپلود موقتاً در دسترس نیست — تا ساعت
۱۰:۳۰ دوباره تلاش کنید»: a tenant learning that "A1 is at quota" learns that there is an A1. Queuing
instead of refusing is M3.

### What we do not know, stated plainly

1. **Reset semantics** — fixed-window vs rolling, and the anchor timezone. Addressed above.
2. **Decimal or binary.** 750 "GB" could be 750×10⁹ or 750×2³⁰, a 7% difference. We enforce the
   smaller (decimal); if the real limit is binary we leave 7% unused, which is cheap. The reverse
   costs 403s.
3. **Whether consumer accounts share the figure.** The 750 GB/day limit is documented in Google
   Workspace administrator material. Whether consumer Google One accounts are governed by the same
   number is widely assumed and not something I can confirm. This is why `DailyUploadLimitBytes` is a
   per-account column and why M1 §11.1's Workspace-vs-consumer question is repeated in §13.
4. **Whether server-side `files.copy` bytes count against it.** The design's failed-jobs card asserts
   a 750 GB ceiling on `files.copy`, which implies the two are related, and copies are commonly
   reported to consume the destination account's daily allowance. We count them (M3 wiring, M2
   plumbing) because over-counting costs throughput and under-counting costs a hard stop mid-copy.
5. **The exact 403 `reason` string for the daily cap.** Drive documents `userRateLimitExceeded`,
   `rateLimitExceeded`, `dailyLimitExceeded`, `quotaExceeded` and `storageQuotaExceeded`, but which one
   arrives when the 750 GB ceiling is hit is not something I can verify without a live account at
   quota. The handler therefore classifies by behaviour: a 403 or 429 on an upload path whose reason is
   not a known transient rate-limit reason is treated as a quota stop, and **the reason string is
   logged verbatim**. Tighten the mapping in the first week from real log lines rather than from a
   guess encoded now.

None of the five change the shape of the design. All five change a constant or a string match.

## 5. Choosing a target — `IUploadTargetSelector`

```csharp
public sealed record UploadTargetRequest(Guid TenantId, long SizeBytes, Guid? FolderId);

public interface IUploadTargetSelector
{
    Task<UploadTarget> SelectAsync(UploadTargetRequest request, CancellationToken ct);
}
```

`TenantId` does not influence the choice in M2 — the pool is shared and undifferentiated — but it is
on the request because it belongs in the audit line for every selection, and because M5's per-tenant
caps will need it. Adding it later means touching the call site and every test; it costs nothing now.
M1's stub satisfies this signature by returning the only account.

Selection is two stages: an eligibility gate that no policy can override, then the policy.

**Eligibility.** An account is a candidate when all hold:

1. `Status = Healthy` and `AcceptsUploads = true` and `QuotaBlockedUntilUtc` is null or past.
2. `QuotaTotalBytes − QuotaUsedBytes − inFlight(account) ≥ SizeBytes + 1 GiB`, where `inFlight` is
   `sum(SizeBytes − BytesConfirmed)` over that account's active `UploadSession` rows. The 1 GiB
   headroom is there because `QuotaUsedBytes` is a cached figure refreshed every few minutes (§8) and
   can lag by whatever landed since. Accounts with no `storageQuota.limit` skip this test.
3. `dayBytes(account) + inFlight(account) + SizeBytes ≤ ceiling`, where the ceiling is the soft stop
   when `AutoStopNearQuota` is on and the hard limit otherwise.

Rule 3 has an obvious hole: a 812 GB file — the design has one — can never satisfy it against a
750 GB ceiling, so no account would ever be eligible and the upload could never start. **The
resolution is not to special-case oversized files, it is that the rule is about starting, not
finishing.** For `SizeBytes > ceiling`, rule 3 is replaced by `dayBytes(account) + inFlight(account) <
ceiling` — the account merely has to have room to make progress today. The upload runs until the day's
allowance is gone, the chunk handler gets a quota refusal or the selector's own ceiling is crossed, and
the client's resume path picks it up after the reset. A 812 GB file takes two days. This also means we
never have to know whether Google permits a single file larger than the daily limit in one shot
(§4, unknown 3) — the design does not depend on the answer.

**Reservations are a heuristic, not an entry.** `inFlight` prevents three concurrent uploads from all
choosing the same "most free" account, which is the failure the naive version has. It is not
accounting: only confirmed bytes reach `AccountUploadDay`, and they are attributed to the day they were
confirmed, not the day the session opened.

**Policy**, applied to the eligible set:

| `UploadPolicy` | Rule | Note |
|---|---|---|
| `most_free` (default) | Maximise free bytes after `inFlight`; ties broken by `Priority`, then `ShortCode`. | The design's «بیشترین فضای خالی». |
| `round_robin` | Order eligible accounts by `ShortCode`, take the successor of `PoolSettings.RoundRobinCursorAccountId`, advance the cursor in the same transaction that inserts the `UploadSession`. | Round-robin is by **file count**, which is what «پخش متناوب بین اکانت‌ها» says and is a genuine footgun: alternating a 4 KB file and a 200 GB file balances nothing. The card's subtext stays as designed; `most_free` stays the default. |
| `manual` | Lowest `Priority` first; first eligible wins. | The design's «اول A1 تا پر شود، بعد A2». Fill, don't spread. |

The cursor is a real persisted column with a row lock rather than a `count % n` trick, because two
simultaneous requests deriving the same modulus both pick A1 and "round-robin actually alternates"
stops being testable.

**Falling through is allowed, and for `manual` it is the point.** If the policy's first choice is not
eligible, the next candidate in the same ordering is taken. No upload is refused because the policy
pointed at a full account while another had room. Only an empty eligible set produces
`no_upload_target`. `manual` is the policy most likely to produce one, since it concentrates the day's
uploads on a single account's allowance — worth saying in the settings card's subtext, and worth
knowing before someone selects it and reports a bug.

Deterministic tie-breaks throughout, so the selector is unit-testable without a database.

## 6. The union view

**The union is not a fan-out.** The file list is `StoredFile` rows from Postgres, filtered by
`GoogleAccountId`; the panel never calls `files.list` on N accounts to build a page. That is a
deliberate constraint, not an optimisation: fanning out costs N round trips per page against the
12,000-queries-per-60-seconds budget M1 §9 already reserves for M3, cannot be paginated coherently
across accounts, and would return files the tenant does not own. Since every file in these accounts got
there through Drive Union, our table is the authoritative view of them. What that gives up — drift if
the operator edits from Drive's own web UI — is discussed in §14.

So "one file list spanning all accounts" is a `WHERE` clause, and the account chip is a filter, not a
merge.

**Chips.** Account chips are a radio group («همه اکانت‌ها» plus one per connected account, labelled
`{ShortCode} {Label}` — «A1 archive.main»); «فقط لینک‌دار» and «بزرگ‌تر از ۱۰GB» are independent
toggles ANDed with it. `Label` defaults at connect time to the local part of the account email, and is
editable. The ≥10 GB threshold is 10 GiB, consistent with how sizes are displayed (§9).

**Sorting.** Default is modified descending, matching the reference. Folders are **not** pinned to the
top: the reference's folder row sits sixth, in its correct chronological position, and pinning would
contradict the sort the user chose. Name sorting uses an ICU collation for Persian
(`"fa-x-icu"`); if the deployed Postgres image lacks ICU, the column falls back to `C` collation and
Persian names sort by code point. Verify against the actual image before the folder work is called
done — it is a one-line difference and an unpleasant one to discover from a screenshot.

**Pagination** is keyset on `(sortKey, Id)`, base64 in `cursor`. Not offset: the list changes under the
user constantly.

**In-flight uploads appear in the list.** The reference shows `backup-2026-08.tar.zst` with «در حال
آپلود» in the modified column, `—` for link and `۰` downloads. Those rows come from the tenant's
active `UploadSession` rows, fetched as a separate small query and merged into the **first page only**
— they are few, bounded by upload concurrency, and interleaving them into the keyset query would
corrupt the cursor. Their account column shows the target the selector picked, which is how the
operator watches the upload policy actually behave.

**Search** — the header's «جست‌وجو در همه‌ی اکانت‌ها…» — is `ILIKE` on name, tenant-scoped, backed by
a `pg_trgm` index. If `CREATE EXTENSION pg_trgm` is not permitted on the target database, drop the
index; at the design's 14,286 rows a sequential `ILIKE` is imperceptible, and the query text does not
change.

**What a tenant gets.** Not a hidden column — a different type.

- `FileRowOperatorDto` has `AccountShortCode`; `FileRowTenantDto` does not. Two DTOs and two query
  models, not one DTO with conditional serialisation, because runtime-conditional JSON is exactly the
  kind of thing that fails silently after an unrelated refactor. The tenant's query model has no
  `account` property, so `?account=A1` from a tenant is not "rejected" — it is unbindable and
  therefore invisible, which also means the API does not confirm that the parameter means anything.
- `GET /api/accounts` is operator-only, `403` for a tenant.
- The subtitle «نمای یکپارچه از ۲ اکانت · ۱۴٬۲۸۶ آیتم» is operator copy; a tenant sees the item count
  alone. The search placeholder becomes «جست‌وجو در فایل‌ها…». The sidebar's `2 accounts · 10 TB` and
  its pool quota card render server-side only when `IsOperator`.
- In the details panel, «اکانت» and «شناسه درایو» are operator-only rows. M1 §7 already forbids the
  Drive file ID from appearing in any response; a truncated `1aB…9Zk` is still a Drive identifier and
  still tells a tenant that a Drive is involved. A tenant's panel keeps «مسیر» and «ساخته شده».

## 7. Folders

M1 shipped a flat list and deferred folders here so they could be designed once against a pool instead
of twice. The pool is what makes them interesting.

**A folder is logical.** Files in one folder are placed by the upload policy and therefore land on
different Google accounts. A Drive folder cannot span accounts, so a Drive folder cannot be the folder.
`Folder` is our row; `FolderMapping` records the Drive folder that materialises it on each account,
created lazily the first time a file lands in it there. Under M1's per-tenant root that means logical
`/reports/2026/Q3` becomes `DriveUnion/{tenant-slug}/reports/2026/Q3` on each account that holds any
of its contents, and on no others.

A consequence worth having: **creating a folder makes no Drive call.** It cannot fail, costs no API
quota, and works while every account is at its ceiling.

**Materialising a folder on an account is a create-if-absent, and Drive allows duplicate names.** Two
concurrent uploads into a new folder will both check, both find nothing, and both create — leaving two
Drive folders with the same name and half the files invisible under each. Check-then-create is not
safe here. Wrap the per-`(FolderId, GoogleAccountId)` ensure in a Postgres advisory transaction lock
keyed on the pair, with the unique PK on `FolderMapping` as the backstop. The ensure is recursive:
mapping a folder requires its parent mapped first. Creation is
`POST /drive/v3/files` with `{ name, mimeType: "application/vnd.google-apps.folder", parents: [parentId] }`;
existence is `files.list` with
`q = name = '…' and '<parent>' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false`.
Drive v3 no longer permits a file to have multiple parents, which is why each mapping is a single
`DriveFolderId` and not a set.

**Path is denormalised onto `StoredFile.FolderPath`** so the details panel's «مسیر» row and the
subtree queries need no recursive join. The cost is explicit: renaming or moving a folder must rewrite
every descendant's `FolderPath`. **Rename and move are not in M2** — folders are created, files are
placed in them, and that is all — so the cost is deferred, but whoever adds rename must add the rewrite
in the same change or the panel starts showing paths that no longer exist.

**The folder row in the table.** Size is `SUM(SizeBytes)` over the subtree, one query per page using
`FolderPath LIKE '/x/y/%'` against an index on `(TenantId, FolderPath text_pattern_ops)`; Drive does
not report folder sizes, so this number is ours. Modified is the subtree's maximum, falling back to the
folder's `CreatedAt` for an empty folder.

**The account column for a folder deviates from the reference**, which shows a plain `A1`. A logical
folder's contents can sit on several accounts, so the cell shows `A1` when the whole subtree is on one
account and «۲ اکانت» when it is not. The reference's mock folder simply happened to be
single-account. For a tenant the column does not exist, so the question does not arise.

## 8. The dashboard

M2 introduces the dashboard route with the account-cards row, the page title and the sync timestamp —
and nothing else. The active-jobs and failed-jobs cards need M3's `jobs` table, the top-links card is
M4's download analytics, and the traffic chart is M6. The grid is written so those four slot in at
their designed positions without relayout. A milestone that ships a dashboard with four empty cards is
worse than one that ships a dashboard with one full one.

The dashboard is operator-only in M2. Tenants land on Files, as in M1.

**The account cards** are the reference's, with the two bars: «فضای مصرفی» from
`QuotaUsedBytes`/`QuotaTotalBytes`, «آپلود امروز» from `AccountUploadDay`/effective limit. Card grid is
`repeat(auto-fit, minmax(320px, 1fr))` rather than the reference's `repeat(2, 1fr)`: with two accounts
it renders identically, and with three it produces a correct three-up row instead of a lone card on a
second line. Verified sane to six accounts; there is no hard cap on account count, but each one is
another trip through Google's consent screen and another line in the sidebar.

**Storage figures are refreshed by `AccountQuotaSyncService`**, a `BackgroundService` that every five
minutes calls `about.get?fields=storageQuota,user(emailAddress)` per non-removed account and writes
`QuotaTotalBytes`, `QuotaUsedBytes`, `StorageQuotaSyncedAt`. Three accounts × 12/hour = 864 calls a
day, against a budget of 12,000 per *minute*.

«آخرین همگام‌سازی ۲ دقیقه پیش» is the **minimum** `StorageQuotaSyncedAt` across accounts, rendered
relative. If any account has not synced in fifteen minutes the timestamp renders in `--warn` and names
the account, because a stalled sync makes every bar on the page a confident lie and there is otherwise
no symptom.

**The sidebar pool card** («سهمیه آپلود امروز · `918 / 1500 GB`») sums today's confirmed bytes over
sums of the effective per-account limits. Its **fill** is the pool ratio; its **colour** is driven by
the worst single account, not by the pool average. A pool at 61% with A1 stopped at 96% is a green bar
over a blocked account, and green is the wrong answer to "why did my upload fail".

The dashed add-account card's copy is computed, not written: «افزودن اکانت سوم — ظرفیت کل به ۱۵TB و
سهمیه روزانه به ۲.۲۵TB می‌رسد» projects `(n+1) × limit` and `(n+1) × dailyLimit`, and is only valid
while every connected account has the same `limit`. When they differ, the copy drops the projection
and reads «افزودن اکانت — ظرفیت کل و سهمیه روزانه با هر اکانت جدید بالا می‌رود». The Persian ordinal
(سوم/چهارم/…) comes from a small lookup for 3–10; beyond that, «افزودن اکانت بعدی».

**The settings screen** in M2 renders two cards: «سیاست آپلود» (the three radio cards, live) and
«کارایی انتقال» containing only the auto-stop switch «توقف خودکار نزدیک سهمیه — در ۷۲۰GB از ۷۵۰GB
روزانه». The chunk-count and chunk-size sliders belong to M3 and the proxy card to M6, and neither is
rendered — a disabled control that never becomes enabled is worse than an absent one. The «کارایی
انتقال» card is kept, sparse, so its heading and grid position are final and M3 only adds to it.

## 9. Units, digits and colour

These look like nitpicks and are the difference between matching the handoff and nearly matching it.

**Storage formats binary, daily upload formats decimal, and this is forced rather than chosen.** A 5 TB
Google plan reports `limit = 5,497,558,138,880` bytes — 5 TiB — so binary formatting renders exactly
the design's `5 TB` while decimal renders `5.5 TB`. The daily figure is stated by Google as a round
"750 GB" and is configured as `750000000000`, so decimal formatting renders exactly the design's
`750 GB` while binary renders `698 GB`. Each formatter is chosen to reproduce the source's own number.
File sizes and the ≥10 GB filter follow the storage convention (binary), matching Drive's own UI. The
enforcement constant is an absolute byte count in configuration so no "GB" multiplication ever happens
in the upload path.

**Percentages round, and the colour follows the rounded number.** The reference labels 1.08/5 TB as
`22٪` (21.6 rounded) and 3.42/5 TB as `68٪` (68.4 rounded), so display rounds. Deriving the colour from
the same rounded integer means what you see is what triggered — the alternative, thresholding on the
raw ratio, shows an `80٪` label on an accent-coloured bar. The effective thresholds become 79.5% and
94.5%, about 4 GB early on a 750 GB budget. Bar *width* uses the exact ratio.

**The 80/95 rule applies to the used-space bar too**, not only to the daily bar the design annotates. A
full account is a harder failure than a rate-limited one, and the design simply had no near-full
account in its mock data (68% and 22%) for the state to appear in.

**Digits: counts are Persian, quantities carrying a unit are Latin.** That is the rule the reference
follows everywhere it is consistent — «۲۴۱ دانلود», «۱۴٬۲۸۶ آیتم», «۷۶» in the downloads column against
`18.4 MB`, `3.42 / 5 TB`, `612 / 750 GB`, `68٪`. (The proxy table's «۶۰٪» contradicts it; that card is
M6's and can be brought into line then.) Dates in the panel are Jalali via
`System.Globalization.PersianCalendar`, formatted server-side.

## 10. Tenant isolation and sessionless work

M1 §8 removed global query filters and made `tenantId` an explicit argument. M2 adds the first code in
the product that runs with no tenant at all, and the first data that legitimately has none.

**`AccountQuotaSyncService` and the retention sweep are sessionless.** They resolve their own
`IServiceScope`, have no `HttpContext`, and read `GoogleAccount` and `AccountUploadDay` — both of which
are un-tenanted by design. There is no filter for them to trip over precisely because M1 refused to
introduce one; this is the milestone where that decision pays. The rule to hold: **anything reachable
from a `BackgroundService` must go through a repository with no tenant parameter at all**, so the
question can never be answered wrongly. An integration test runs the sync with no `HttpContext` and
asserts it touched every account — the failure mode being guarded against is not an exception, it is a
sweeper that reports success over an empty result set.

**Retention.** `AccountUploadDay` grows by one row per account per day — roughly a thousand rows a year
at three accounts. Keep 400 days so "was A1 really at quota on the 14th?" is answerable through a
season, and delete beyond it with a single statement in the same background service, once a day. There
is nothing to tune and no separate job to schedule.

**The quota numbers are never exposed to a tenant in any form** — not in `/api/accounts` (403), not as
an aggregate, not in an error body. A tenant told "the pool is at 96%" has learned there is a pool.

## 11. API surface

M2 adds, changes or restricts the following. Everything under `/api/accounts`, `/api/settings/pool` and
`/operator/*` is operator-only and returns `403` otherwise.

| Route | Change |
|---|---|
| `GET /operator/accounts/connect` → `/callback` | New. Browser redirects, not JSON. Single-use `state` bound to the session and validated before the token exchange — the callback is effectively an anonymous GET and M1 §8's lesson applies to it directly. |
| `GET /api/accounts` | New. Per account: `shortCode`, `email`, `label`, `status`, `priority`, `acceptsUploads`, `storage { usedBytes, totalBytes?, syncedAt }`, `dailyUpload { usedBytes, limitBytes, softStopBytes, resetsAtUtc }`, `accessTokenExpiresAt`. |
| `POST /api/accounts/{id}/refresh-token` | New. Forces a refresh grant. |
| `POST /api/accounts/{id}/read-only`, `DELETE /api/accounts/{id}` | New. The two disconnect paths of §3; `DELETE` returns `409` with a file count when the account is not empty. |
| `GET /api/files` | Adds `account`, `folder`, `hasLink`, `minSize`; `cursor` becomes keyset. Two response DTOs (§6). |
| `POST /api/folders` | New. `{ name, parentFolderId? }`. No Drive call. |
| `GET`/`PUT /api/settings/pool` | New. Upload policy, auto-stop switch, per-account priorities. |
| `POST /api/uploads` | Response gains `accountShortCode` for operators only. New failure: `503 no_upload_target` with `Retry-After`. |

## 12. Tests that hold the line

Five, in the spirit of M1 §8's two:

1. **The gate outranks the policy.** Two accounts, A1 at 719 GB today with `Priority = 0`, A2 at
   100 GB. Under `manual`, a 2 GB upload goes to A2.
2. **A resume counts nothing twice.** Confirm 10 GB, restart the session, re-learn the same confirmed
   range from Drive, assert `AccountUploadDay.BytesConfirmed` is still 10 GB.
3. **Nothing resets the counter.** With `IClock` at 23:59 in the reset zone the day reads N; at 00:01
   it reads 0, with no background job having run.
4. **The tenant response is clean at the byte level.** A tenant's `/api/files` body, as raw JSON,
   contains no account short code, no account email and no Drive file ID. Asserted on the string, not
   on the deserialised object — the bug this catches is a DTO gaining a property, and a typed
   assertion would not notice.
5. **Sessionless sync reaches everything.** `AccountQuotaSyncService` invoked with no `HttpContext`
   updates every non-removed account, asserted by count.

## 13. Before implementation starts

1. **Workspace or consumer, for every account in the pool.** Carried unanswered from M1 §11.1 and now
   load-bearing twice over: it decides whether `storageQuota.limit` is even present (§3) and whether
   750 GB/day is the right default (§4).
2. **The panel's timezone, and confirmation that the reset copy shows the real instant.** The design
   says «ساعت ۰۰:۰۰»; if Google's reset anchor is US Pacific and the operator is in Tehran, the honest
   string is «۱۰:۳۰». Confirm the operator would rather see the true time than the round number.
3. **Disconnect semantics for an account that still holds files.** §3 proposes read-only retention over
   token revocation, on the grounds that revoking breaks every live public link with no path back.
   This is a product call, not a technical one.
4. **Whether a third account is being added at M2 time, and its plan size.** It decides whether the
   dashed card ships with the projection copy or the generic copy, and it is the only way to exercise a
   three-way round-robin against something real.

## 14. Deliberately not in M2

`files.copy`, the `jobs` table, the background worker, parallel chunks, SignalR progress, and the
«صف انتقال» screen entirely (M3) — which is why an upload with no eligible target is *refused* with a
retry time rather than queued.

Folder rename, folder move, folder delete, and moving a file between folders. M2 creates folders and
places files in them; §7 states what the denormalised path will cost whoever adds rename.

Rebalancing an existing pool. There is no way in M2 to move a file off a full account, because moving
between accounts *is* `files.copy`.

Reconciliation with Drive's own state. Nobody but Drive Union writes to these accounts, so our table is
the truth; if the operator edits from Drive's web UI, rows drift and nothing notices. `changes.getStartPageToken`
is captured per account at connect time anyway (§2) so that a later reconciliation can start from a
known point instead of paying for a full re-list of 14,000 files across three accounts.

Per-tenant storage caps and any attribution of pool consumption to a tenant (M5). `AccountUploadDay`
has no tenant column and is not the place to add one.

The proxy/egress table and the traffic chart on the dashboard (M6). Note that the settings screen's own
subtitle already says the necessary thing — «سهمیه ۷۵۰GB به ازای اکانت است، نه IP» — and M2's quota
model is the reason that sentence is true: the counter is keyed on `GoogleAccountId` and nothing about
it is per-connection.
