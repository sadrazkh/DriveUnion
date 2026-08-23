# Drive Union — Plans and quotas

**Date:** 2026-08-24 · **Status:** design proposed; sits on M5, cannot start before it ships; blocked on
§15 before the first line of implementation

## 1. What this settles, and the one thing it deliberately does not

The owner's words:

> «دو گیگ اندازه کافی هست ممکنه برای یوزر ها حتی برای اپلود داخل گوگل درایو هم هر فایل رو محدودیت
> بذارم باید پلن بندی بشه کرد از حجم تا ترافیک تا حجم هر فایل و لینک و دانلود روزانه و ...»

Customer plans: tiers carrying limits on storage, traffic, per-file size, share links and daily
downloads. M1 §12 flagged the half of this that is missing — the cap is scoped into M5, *charging* is
scoped nowhere — and this document scopes the rest of the limiting.

**This spec covers limiting usage. It does not cover charging for it.** No price, no currency, no
invoice, no payment provider, no proration, no dunning, no suspension for non-payment. §12 names the
exact seam a billing system would attach to and stops there, and §12 also names the `Price` column that
is deliberately absent.

Three things follow from that split and they are worth stating before the detail:

- **A plan here is a set of numbers, not a set of features.** Nothing in this document makes password
  links, custom slugs, Telegram or the transfer queue conditional on a tier. Quantities are tiered;
  capabilities are not. Every feature flag is a new authorization surface across four clients (panel,
  `/api/*`, `/d/{slug}`, the bot), and none of them has a customer asking for it.
- **M5 §7 is not redesigned.** The per-tenant storage cap — the conditional single-statement reserve
  before Google is contacted, the release only when Drive confirms deletion, the 409, the over-cap
  tenant that loses uploads only, over-commitment allowed and shown — survives verbatim. This document
  adds no line to it. What it changes is where `StorageQuotaBytes` comes from, and it adds a row saying
  who last changed it and why.
- **Two of the five dimensions the brief names are recommended against**, and §2 argues them down rather
  than dropping them quietly. A limit nobody hits is a column, a support conversation and a bug surface
  for nothing; a limit measured in the wrong unit is worse, because it refuses the wrong customers.

## 2. The dimensions, and the two I recommend against

| Dimension | Named in the brief | Verdict |
|---|---|---|
| Storage bytes | «حجم» | **Keep.** Exists already (M5 §7). The plan supplies the number. |
| Per-file size | «حجم هر فایل» | **Keep.** The cheapest honest limit in the product, and — §4 — the error bar on the traffic limit. |
| Monthly egress ("traffic") | «ترافیک» | **Keep.** The only dimension that bounds what the operator actually pays month to month. Also the least honestly enforceable; §4 says how far. |
| Member seats | not named | **Keep, and flag it.** The one dimension being added rather than inherited. §15.3. |
| Live share links | «لینک» | **Recommend against.** |
| Daily downloads | «دانلود روزانه» | **Recommend against.** |

### Why not a cap on live share links

A share link is a row. It occupies no storage, moves no bytes, and costs the operator nothing until
somebody downloads through it — at which point the *traffic* limit is already counting. A tenant with
ten thousand links over 2 GB of files is cheaper to serve than a tenant with one link over 2 TB.

So a link cap limits the customer's ability to organise their own sharing while limiting nothing the
operator pays for, and it produces the worst class of refusal: «سقف لینک‌ها پر شده» on a screen whose
storage bar reads `4٪`. That is a support ticket every time, for a resource with no cost behind it.

Two things a reviewer will reach for, and where each actually belongs:

- **Enumeration surface.** More live links means a scanner's blind guess at `/d/{slug}` hits more often
  (M4 §7.1 does this arithmetic). The lever for that is slug length, which M4 §11.1 already puts in
  front of the owner with a recommendation of ten characters. It is not a per-tenant quantity and
  pricing it as one would be dishonest about what it protects.
- **Abuse — one tenant minting links in a loop.** That is a rate, not a ceiling. M5 §6 already set the
  precedent by rate-limiting invitations per tenant rather than putting a number on a plan. Link
  creation gets the same treatment if it ever needs it: a per-tenant limiter in M1 §9's rate limiter,
  invisible to anyone behaving normally, and *not* on the pricing page, because nobody wants to buy it.

### Why not a per-tenant daily download count

The brief names it, so it is argued rather than forgotten.

**A download is not a unit of cost.** Five hundred downloads of a 12 KB invoice is six megabytes; five
hundred downloads of a 20 GB video is ten terabytes. A per-tenant daily download cap therefore refuses
the first customer and permits the second, which is backwards from what it is trying to protect. The
traffic limit in §4 measures the same activity in the unit the operator is actually billed in.

It is also not free. Three concrete costs:

1. **A second counter over the same events.** M4 §6.1 keeps `DownloadEvent` exactly 1:1 with
   `ShareLink.DownloadCount`, which is what makes drift between them a bug with an obvious test. A
   per-tenant daily counter over those same events, on a different window, is a second thing that can
   drift, with no equally cheap oracle to check it against.
2. **A sixth refusal state on the public path.** M4 §5's discipline is that unknown, revoked, expired and
   capped produce one byte-identical response. §4 already adds a fifth (over traffic). Each addition is
   another branch that must be proved identical, forever.
3. **It does not bound the worst case.** The first download of the day is under any count limit, and one
   download of a 96 GB file is the event that hurts. A count limit only constrains repetition — which
   the byte counter constrains too, in the right unit.

The tenant keeps the tool that *is* correctly shaped for this: M4's per-link `MaxDownloads`, with M4
§6.2's implied-egress label beside the slider — «۵۰۰ × ۱۸.۴ MB ≈ ۹.۲ GB». That is a per-link egress
budget the customer sets themselves, and M4 §7.2 already declined to grow a second mechanism beside it.

### Why seats, which the brief did not name

Because it is the cheapest limit in the set and the only one customers already expect to be tiered. It
is knowable before anything happens, has no window and no reset, is refused to an Owner who by
definition knows their own plan, and never touches an anonymous path. If the owner does not want to sell
by seat, dropping it costs one column and one check — §15.3.

## 3. Where a plan lives, and why the tenant carries its own numbers

```
Plan              { Id, Code, Name, StorageBytes, MaxFileBytes, MonthlyEgressBytes,
                    MaxMembers, IsRetired, SortOrder, CreatedAt }

Tenant           += PlanId?, PlanAppliedAt?,
                    MaxFileBytes, MonthlyEgressBytes, MaxMembers,
                    TrafficWindowKey, TrafficUsedBytes
                    -- StorageQuotaBytes and StorageUsedBytes are M5 §7's, unchanged

TenantEgressDay   { TenantId, UsageDate, Bytes, UpdatedAt }   PK (TenantId, UsageDate)

TenantQuotaChange { Id, TenantId, ChangedAt, ChangedByUserId, PlanCodeBefore?, PlanCodeAfter?,
                    Field, OldValue, NewValue, Reason }
```

`Plan` carries **no `TenantId`**. It is the operator's catalogue, like `GoogleAccount` (M1 §5),
`PoolSettings` (M2), `EgressRoute` (M6 §11) and `TelegramBotSettings` (Telegram §7), and it is covered by
M5 §5's operator-only route test for the same reason.

### The plan is a template; the tenant carries its own effective limits

Assigning a plan **copies** its four numbers onto the `Tenant` row. Nothing on any enforcement path ever
joins to `Plan`. Editing a plan changes nothing for any existing tenant until someone re-applies it, and
the operator screen says so in words.

This is the decision a reviewer should argue with first, so here is the case both ways.

**Against copying:** one edit ought to fix everybody, and copies drift. That is a real cost and it is
paid on purpose.

**For copying, three reasons:**

1. **Overrides are not an edge case.** A customer negotiates 3 TB on a 1 TB tier; that is the normal
   shape of selling to businesses. Once the tenant row must be able to hold a number that differs from
   its plan, a mixed model — some limits from the plan, some from the tenant — is the worst of both, and
   every read has to know which is which.
2. **M5 §10 already predicted this.** It left exactly one seam: `StorageQuotaBytes` is written by one
   command, `SetTenantStorageQuota(tenantId, bytes, reason)`, callable only from the operator surface.
   A plan is a caller of that command. If the enforcement path read through a plan instead, that seam
   would have been designed for nothing and there would be two writers of a customer's cap.
3. **A cap is a promise to one customer.** «چرا سهمیه‌ام کم شد» has to be answerable with a row naming a
   person, a time and a reason. A template edit cannot produce that per tenant, and a pricing experiment
   that silently moves a paying customer's ceiling is how their uploads start failing on a Tuesday.

The payoffs show up twice more: retiring a plan (`IsRetired`) hides it from new assignment while every
tenant on it keeps working, because their numbers are on their own row; and M5 §7's operator
over-commitment figure stays `sum(Tenant.StorageQuotaBytes)` — the same query it already was, unaffected
by anything happening in the catalogue.

### No nullable "unlimited"

All four limit columns on `Tenant` are non-nullable, for M5 §7's reason: a nullable cap meaning
unlimited is one migration default away from every tenant being uncapped, and nothing looks wrong until
the pool is full. A "no practical limit" tier is a large explicit number, and on storage that number is
meaningless above the pool anyway — the pool is the real ceiling and M5 §7 already decided the operator
sees the over-commitment rather than the product preventing it.

### One default, not two

M5 §8 seeds a new tenant's cap from `Tenancy:DefaultStorageQuotaBytes`. That setting is **replaced** by
`Plans:DefaultPlanCode`, and the storage number moves into that plan's row. Two configuration keys that
can disagree about what a new customer gets is a bug waiting for the day they do.

## 4. Enforcement, dimension by dimension, and where honesty runs out

| Dimension | Known when | Enforced where | Reservation | Refusal |
|---|---|---|---|---|
| Storage | before bytes move | `POST /api/uploads`, M5 §7 unchanged | yes — reserve / settle / release | `409 tenant_quota_exceeded` |
| Per-file size | before bytes move | `IUploadCoordinator.BeginAsync` | no — see §5 | `409 file_too_large_for_plan` |
| Traffic | only as bytes move | admission at `/d/{slug}/file` and at the bot's send; counted during | **impossible** — see below | the identical 404 card, or `409 tenant_traffic_exceeded` on `/api/*` |
| Seats | before anything happens | invitation creation | one conditional statement | `409 member_limit_reached` |

### 4.1 Storage — nothing new

M5 §7 reserves in one conditional UPDATE in the same transaction that creates the `UploadSession`,
counts in-flight sessions, settles on the final chunk, releases only when Drive confirms a deletion, and
refuses with 409 rather than 413 or 507. All of that stands. The only change is that the number in
`storage_quota_bytes` arrived from a plan and its history is in `TenantQuotaChange`.

### 4.2 Per-file size — the cheapest honest limit, and where the check lives

The declared `sizeBytes` on `POST /api/uploads` is known before Google is contacted, so the check is one
comparison next to M5 §7's reserve, inside the same transaction.

**It lives in `IUploadCoordinator.BeginAsync`, not in the controller.** There are already three callers
of the upload path — the panel's `uploadPanel` island, `/api/uploads`, and the Telegram inbound bridge
(Telegram §3.3) — and M3's transfer and M6's export will add more. A check in one controller is a check
three other entry points do not have, and the missing one will be the one that matters.

M5 §7's second half already covers the dishonest client: a declared size is a claim, so the chunk
endpoint counts what it forwards and aborts the session the moment the total exceeds what was reserved,
and a `Content-Range` total that disagrees with the session's reserved size is a 400. The per-file limit
inherits that for free, because it is enforced by refusing to *reserve* more than `MaxFileBytes` — the
mid-stream mechanism that stops a 100 GB body declared as 1 MB is the same one M5 already specified. One
new comparison, no new machinery.

Telegram inbound is bounded by Telegram's own 20 MB ceiling (Telegram §3.3), which is below any plausible
per-file limit — but the check still runs, because the ceiling is Telegram's number and not ours, and
Telegram §3.3 already refuses to trust a third party's `file_size`.

Refusal is `409`, not `413`. Same argument M5 §7 made: 413 is about this request's entity being too large
for the endpoint, and `POST /api/uploads` carries a sixty-byte JSON body. The condition is about state,
not about this request's size.

### 4.3 Traffic — what actually happens, and three things that cannot be pretended

**What counts: bytes this box sent to a client on the tenant's behalf.** Concretely — the copy into
`Response.Body` on `/d/{slug}/file` (M1 §7), the same copy on the panel's authenticated
`GET /api/files/{id}/download`, and the outbound leg of a Telegram document send (Telegram §13's
`EgressSample.Direction = ToTelegram`).

**What does not count, and why each will look like a bug to somebody:**

- **Upload egress to Google.** Browser → OVH → Google means an upload produces outbound bytes too, and
  they cost the operator. They are not charged to the traffic meter, because a customer who uploads
  500 GB and never shares it has not used half a terabyte of "traffic" in any sense that word carries,
  and charging one upload against two dimensions is a support conversation with no defensible answer.
  The storage limit is what prices storing a file; the traffic limit is what prices distributing it.
- **A cached Telegram `file_id` re-send.** Telegram §3.2's whole point: no bytes leave this box. Telegram
  §13 already warns that this will read as an accounting bug to whoever sees the chart first. The meter
  counts what moved, and nothing moved.
- **M3's `files.copy`.** Server-side at Google; no bytes cross this box. Also, per M5 §7, a transfer does
  not change tenant storage either.
- **M6 §9's S3 export.** Operator-initiated today (M6 §12.4). If it ever becomes customer-facing with
  per-tenant destinations, it moves the tenant's bytes off the box and must be metered; that is a seam,
  named here so M6 does not have to rediscover it.

**Three admissions, in the order a reviewer will find them.**

**(a) The figure is our own byte counting, and it undercounts.** M6 §10 chose in-process accounting over
host NIC counters and stated plainly that the number excludes TLS and HTTP framing, retransmits, and
everything else on the box. The traffic limit inherits that weakness exactly. Two consequences worth
writing down rather than discovering: the tenant's meter always reads *less* than what the operator is
billed for, which is the right direction to be wrong in for a number that refuses service; and a
customer disputing their usage is disputing our arithmetic, not a third party's, so the daily rows in
`TenantEgressDay` are the only evidence there will ever be.

There is a second, smaller loss: bytes counted in memory but not yet flushed are lost across a process
restart. §6 bounds it at one minute of the box's uplink — a number nobody knows yet, because M6 §12 is
still asking what the port can do. The direction of that error is also undercount.

**(b) A 214 GB download cannot be un-sent halfway through.** There is no atomic check here and this
document will not pretend otherwise. Three options were on the table:

1. *Refuse a download whose file size would exceed the remaining allowance.* Dishonest: a ranged or
   resumed request does not move `SizeBytes`, so the arithmetic is wrong in both directions, and it
   refuses a customer with plenty of allowance because one large file happened to be the next one.
2. *Kill the stream at the boundary.* The recipient gets a truncated file, their resume truncates again
   at the same byte, and they blame the customer who sent it.
3. **Admission control at the start, byte accounting during, and a stated overshoot.** Chosen.

So: before Drive is contacted, the resolve asks whether the tenant is **already** over. Over → refused.
Under → the transfer runs to completion and is allowed to carry the meter past the limit.

**The overshoot is bounded by `MaxFileBytes × the number of transfers admitted in the same instant`,**
and that is the strongest argument for keeping the per-file dimension at all: it is not only a product
tier, it is the error bar on the traffic limit. It is not bounded by one file, because N requests can
pass the threshold test concurrently — which is what M4 §7.1's per-link (6) and global (200) concurrency
limiters bound in practice, and why they are worth having for a second reason now.

**(c) The refusal applies to continuations too, and that is inherited behaviour rather than a new
cruelty.** The admission check runs on **every** GET to `/d/{slug}/file`, counted or not. The
alternative — checking only requests that count as downloads under M1 §7's rule — is a limit any client
defeats with one header (`Range: bytes=1-` forever), which is not a limit. The cost is that a visitor
who is 90% through a transfer when the tenant crosses the line gets a truncated body and the unavailable
card on resume. That is exactly what already happens when a link is revoked mid-download: M4 §5 says
revocation takes effect on the next request and accepted this. The traffic refusal inherits an accepted
behaviour instead of inventing one.

**The traffic meter and the download counter are different questions and must not be merged.**
`DownloadCounting.CountsAsDownload` exists so that one viewer scrubbing a video does not burn twenty of
a customer's five hundred downloads — it answers "was this a person taking the file". The traffic meter
answers "how many bytes left the box". So a range continuation counts **zero downloads and N bytes**,
and a one-byte player probe counts zero of both because it never reaches Drive (the HEAD `Probe` action
is answered from the ticket). Conflating them would either bill a scrubber twenty downloads or meter a
resumed 214 GB transfer at zero.

### 4.4 Seats

`MaxMembers` counts current members **plus pending, unrevoked, unexpired invitations** — the same
principle M5 §7 applies to storage when it counts in-flight upload sessions. Because pending invitations
are counted, an acceptance can never overshoot; only two owners inviting simultaneously at
`MaxMembers − 1` can, and that is closed by making the count and the insert one conditional statement.

M5 §6's cap of twenty pending invitations per tenant stays, unchanged and independent. One is an abuse
control against a spam cannon on the operator's mail domain; this is a product quantity. They answer to
different questions and should not be collapsed into one number.

## 5. Reserve-then-commit: the rule, and the three answers that are not it

The product has the pattern twice — M5 §7's storage bytes, and `IPublicLinkReader.TryReserveDownloadAsync`
/ `RecordDownloadAsync` / `ReleaseDownloadAsync` in `PublicLinkReader.cs`, whose comments state the
reasoning better than a restatement would. Every limit added here either follows it or says why not.

| Limit | Reservation | Why |
|---|---|---|
| Storage | **Yes**, M5's | Two concurrent uploads can spend the same free bytes, and the amount is known at reserve time. |
| Per-file size | **No** | It is a predicate on one immutable declared value, not a claim on a shared counter. Nothing another request does can make a 3 GB file bigger. There is no slot to race for. |
| Seats | **No — one conditional statement** | Two requests *can* race, but the amount is always exactly one and the commit is the same write as the check. A reservation would add a second state (a held seat) with nothing to release it. |
| Traffic | **Cannot** | The amount is unknown until it is spent. |

The traffic row deserves its sentence. Reserving `SizeBytes` up front and refunding the difference is the
obvious repair and it is worse than the threshold test: it refuses a customer with ample allowance
because one large file is in flight, and it makes the meter briefly hold a number that no byte ever
matched — which is the number the panel is showing and the number a dispute would be about. So the
traffic limit does not reserve, and §4.3(b) states the overshoot instead of hiding it behind machinery.

**The rule for whoever adds a fifth dimension:** if two concurrent requests can spend the same unit, it
needs a reservation. If the amount is unknown until it has been spent, it cannot have one — and the
spec must state the overshoot bound rather than imply a check that is not there.

## 6. Counters, windows, and resetting without a job

Storage, per-file size and seats have no window. Traffic has one, and M2 §4 already settled how a window
works in this product: **the counter resets because the date key changes, not because a sweeper ran.** A
nightly reset job that fails leaves every tenant looking exhausted and refuses every download in the
product, silently. Nothing here runs at midnight.

### The window

**A calendar month in one configured timezone**, `Plans:UsageTimeZone`, defaulting to `Asia/Tehran`. The
key is `yyyy-MM` computed as `TimeZoneInfo.ConvertTime(clock.GetUtcNow(), usageZone)`, through M1's
clock abstraction so it is testable.

This is deliberately **not** M2's `Quota:ResetTimeZone`. That one is Pacific because it is a guess at
*Google's* service day and it answers to Google's authority (M2 §4). This one is the customer's month and
it answers to the customer. Two settings, because they will disagree and the day one is changed to match
the other is the day both are wrong.

Not a rolling thirty days: a rolling window cannot answer "when does this free up" with a date. The
honest answer would be "it depends what you downloaded thirty days ago", and §9 makes a date the whole
point of the refusal message.

Not the tenant's sign-up anniversary — *yet*. The panel can tell everybody «تا ۱ شهریور» and the
operator's cross-tenant view has one common period. **The seam is one function**,
`UsageWindow(tenant, now) → (start, end, key)`, which today returns the calendar month and would later
return a billing period. §15.4 asks the owner to decide now, because the daily rows survive either
choice but the denormalised counter's key does not.

### The counter

`Tenant.TrafficUsedBytes` is denormalised beside `Tenant.TrafficWindowKey`, for the reason M1 §5
denormalised `ShareLink.DownloadCount` and M5 §7 denormalised `StorageUsedBytes`: it is read on the hot
path. `TenantEgressDay` is the audit trail behind it.

The write is one statement, and the reset falls out of it:

```sql
UPDATE tenants
   SET traffic_window_key = @window,
       traffic_used_bytes = CASE WHEN traffic_window_key = @window
                                 THEN traffic_used_bytes + @delta
                                 ELSE @delta
                            END
 WHERE id = @tenantId;
```

A write in a new window replaces rather than adds. A read whose stored key does not match the current
one reads zero:

```sql
SELECT CASE WHEN traffic_window_key = @window THEN traffic_used_bytes ELSE 0 END
```

The first byte of the month rolls the window. No job, nothing scheduled, nothing to fail.

### Collection, and the boundary crossed mid-transfer

A counting stream decorator around the three copy sites of §4.3 increments an in-process per-tenant
bucket. This is M6 §10's `CountingStream` with one extra dimension; **it does not need M6 to ship**,
because the one site the traffic limit cannot do without — the copy into `Response.Body` on
`/d/{slug}/file` — is M1 code that exists today. §13 keeps them independent for that reason.

A hosted service flushes the buckets once a minute, writing the `TenantEgressDay` upsert and the `Tenant`
UPDATE above **in one transaction**, so the two can only disagree through a lost transaction — which
makes a reconciliation discrepancy a real bug rather than expected noise (§11).

The day key and the window key are computed **at flush time, per bucket**, exactly as M2 §4 computes its
day at confirmation time. A six-hour download that starts on the 31st and finishes on the 1st therefore
contributes to both months in the proportion actually moved in each. No transfer is ever "attributed" to
a window; only bytes are, and only after they moved. A transfer that began under the limit and crossed a
boundary mid-flight simply starts filling the new month.

The flush is **sessionless background work with no `HttpContext`**. Per M2 §10's rule it goes through a
repository with no tenant parameter at all — the bucket carries the tenant id, the repository is not
asked to guess one — and per M4 §6.3's rule it gets a test asserting a **non-zero** result, because a
flusher that writes nothing looks exactly like a flusher that had nothing to write. This codebase has
already paid for that lesson once.

### Retention

`TenantEgressDay` is one row per tenant per day. Keep **400 days**, matching M2 §10's `AccountUploadDay`
for the same reason — "what did we actually serve in Mordad" should be answerable through a season — and
delete beyond it with a single statement in the same background service. Same non-zero-delete test.

## 7. Supply and demand: when the pool is the binding constraint

M2 §4's 750 GB/day per Google account is a **supply** limit on the operator's side. Everything in this
document is a **demand** limit on the customer's side. They can refuse the same upload for entirely
unrelated reasons and the two refusals must never be confused.

- Over the plan's storage cap → `409 tenant_quota_exceeded` (M5 §7). The customer's problem, actionable
  by them, true regardless of our supply.
- No eligible Google account → `503 no_upload_target` with `Retry-After` (M2 §4). Our problem, and the
  tenant-facing string «آپلود موقتاً در دسترس نیست — تا ساعت ۱۰:۳۰ دوباره تلاش کنید» never names an
  account, because a tenant told "A1 is at quota" has learned that there is an A1.

**The plan check runs first.** A tenant who is both over their cap and facing an empty pool gets the 409,
not the 503 — because the 409 is true either way and tells them something they can act on, while the 503
promises a retry time that will not help them. This is a test (§14.9), not a comment.

**A plan does not create capacity, and no customer-facing screen may imply it does.** This is M6 §2's
discipline applied to a new surface: there, the rule is that adding a proxy IP cannot raise a 750 GB
ceiling and the UI must not suggest it. Here, selling a 5 TB plan does not add 5 TB to a 10 TB pool. The
place that tension is legitimately visible is the operator's screen (§11), where M5 §7 already renders
over-commitment in `--warn`.

**And the interaction nobody will notice until it happens:** one tenant on a large plan, uploading at
full speed, can consume an entire account's 750 GB day by themselves and deny every other tenant. M2's
selector spreading across accounts and M2 §4's 30 GB soft-stop reserve are the only things standing in
the way, and neither was designed for this. Per-tenant daily *upload* fairness does not exist and is not
built here — it is a scheduling problem and M3's queue is where it would live. It is named in §16 rather
than left to be discovered from a support ticket.

## 8. Checking a limit where there is no signed-in user

M1 §8 is the load-bearing decision: **no global query filter, `tenantId` as an explicit argument.** Every
counter added here is read on paths that include anonymous ones — `/d/{slug}` has no session, and the
Telegram webhook has none either — so the rule has to be restated for a limit rather than for a row.

**A limit on an anonymous path is scoped by the row, not by the principal.**

`IPublicLinkReader` has no tenant argument on any method and must never gain one; `PublicLinkReader.cs`
says why in a comment that should not need re-litigating. What changes is that it starts **returning** a
tenant, which it already does in spirit — `PublicDownloadTicket` carries `GoogleAccountId` and
`DriveFileId`, both strictly more sensitive than a tenant id, and both stay server-side.

Concretely, three small changes and no new shape:

- `LookUpAsync` joins the `Tenant` row it already reaches through `StoredFile`, so the traffic check is a
  column comparison on rows the query already loaded. **No extra round trip**, which M4 §5 requires: the
  refusal must cost the same work as every other refusal, and an extra query on the "it existed" branch
  is the only timing difference large enough to measure over the internet.
- `ShareLinkAvailability` gains one value for the log and the owner's panel — the same role
  `PublicLinkResolution.Reason` already plays. `ResolveForDownloadAsync` returns `null` when the tenant is
  over, so the controller has one fewer branch it could forget, and the visitor gets the identical card.
- `PublicDownloadTicket` gains `TenantId`, used only to attribute the bytes the stream is about to move.

**The tenant id must not leak.** It means nothing to a visitor, but two slugs sharing one is a
correlator that says "same customer" — precisely the cross-link joining M4 §6.1 designed the `IpHash`
to prevent. It appears in no response body, header or URL, and the test asserts that on the raw bytes
rather than on a deserialised object, per M2 §12.4.

**Telegram:** nothing new. `ITelegramIdentityReader.ResolveAsync(telegramUserId)` already turns a chat
into a `TenantId` in one place, with no overload that takes one, and everything downstream receives it
explicitly (Telegram §5.1). The traffic check is one more downstream consumer of that resolved id.
Callback data stays untrusted (Telegram §5.4): a limit is checked against the tenant the *resolver*
produced, never against anything a callback claimed.

**Sessionless work:** the flusher and the reconciliation legitimately touch every tenant, so they go
through a repository with **no tenant parameter at all** (M2 §10), and both are tested for a non-zero
result.

## 9. What the customer sees

A limit that refuses without naming itself, or without saying how long until it frees, is a support
ticket every time. Four surfaces, and one of them is deliberately told nothing.

### 9.1 The panel

**One colour ladder, four meters.** M5 §7 already set it and borrowed it from M2's daily-quota bar:
`--accent` below 80%, **`--warn` at ≥80%, `--danger` at ≥95%**, with M2 §9's rule that the colour follows
the *rounded* integer so that what you see is what triggered. Nothing here invents a fifth rule.

What M5 §7 defined only for storage is the 100% state, so it is completed:

| Meter | At 100% |
|---|---|
| Storage | Bar full `--danger`; header's «آپلود فایل» **disabled** (M5 §7, unchanged). |
| Traffic | Bar full `--danger`; **nothing is disabled** — uploads, deletes and the panel all still work. A banner on «لینک‌های اشتراک». |
| Seats | Bar full `--danger`; «دعوت همکار» **disabled**, and only an Owner sees it at all. |

Disabled, never absent, in both cases — M5 §7's own rule, restated by Telegram §3.1: a capability you
lack is absent, a condition you can fix is disabled. An owner over their seat limit *has* the capability
to invite and can fix the condition.

**The sidebar keeps the storage card only.** Its job is to explain the disabled upload button and it
occupies the one free slot. Traffic has no control on that screen to explain.

**A new card on «تنظیمات», «پلن و مصرف», at `grid-column: span 2`** — the span the proxy card and M5 §9's
members card already establish. Card chrome, heading at `14px/700`, `12.5px` body, `var(--row-pad)` and
the per-row bottom border all reused verbatim. Three rows, each a label, a monospace value and the 6px
bar:

- «فضای مصرفی» — `124 / 500 GB`
- «ترافیک این ماه» — `310 / 1000 GB`, subline «تا ۱ شهریور»
- «اعضا» — «۳ از ۵»

That subline is the reason the card exists: the panel answers "how long until it frees" before anybody
asks. Per M5 §2's role table the card's *detail* — plan name, the four numbers, the change history — is
Owner-only; Uploaders and Viewers see the sidebar bar, which is what explains a disabled button to them.

**Digits follow M2 §9**: counts in Persian digits, quantities carrying a unit in Latin monospace. Which
surfaces a small inconsistency worth fixing while it is cheap — M5 §7 renders its cap card as
«۱۲۴ / ۵۰۰ GB», Persian digits on a quantity with a unit, against M2 §9's own examples (`612 / 750 GB`,
`3.42 / 5 TB`). The new card follows M2 §9, and M5's card should be brought into line when it is built.
Two cards on one screen using two digit systems for the same kind of number is exactly what the rule
exists to prevent.

**Over the traffic limit, the links table carries a banner:**

> «سقف ترافیک ماهانه‌ی پلن شما پر شده — لینک‌های عمومی تا ۱ شهریور دانلود نمی‌شوند.»

**Over the storage cap after a downgrade** (§10), the sidebar card's subline says it in words rather than
only in red:

> «حجم مصرفی بیشتر از سقف پلن فعلی است — آپلود جدید تا آزادسازی فضا ممکن نیست.»

**A signed-in member keeps downloading their own files when the tenant is over its traffic limit**, and
those bytes still count. This is a product decision with a real counter-argument, so both are stated.
For: locking a customer out of their own data over a usage meter is a different kind of act from
stopping public distribution, and it is the kind that ends a customer relationship; the limit exists to
bound distribution cost, and a member fetching a file must first be signed in and inside M5's role
model. Against: a customer could distribute by handing out member accounts — which requires giving
strangers the workspace's whole file list, is a much larger thing to do than pasting a link, and is
priced by the seat limit. §15.5 puts this in front of the owner.

### 9.2 `/api/*`

`409 Conflict` throughout, with M5 §7's body shape and one added field so a client can tell which limit
fired:

```json
{ "error": "tenant_quota_exceeded",   "limit": "storage",  "capBytes": …, "usedBytes": …, "requestedBytes": … }
{ "error": "file_too_large_for_plan", "limit": "file",     "maxFileBytes": …, "requestedBytes": … }
{ "error": "tenant_traffic_exceeded", "limit": "traffic",  "capBytes": …, "usedBytes": …, "windowEndsAtUtc": … }
{ "error": "member_limit_reached",    "limit": "members",  "maxMembers": …, "usedSeats": … }
```

Every body with a window carries `windowEndsAtUtc`, so no client has to compute the date the message
needs.

**Not `402 Payment Required`**, which is the tempting one and is wrong twice. It asserts the fix is money
when the fix may be waiting or deleting; and this spec does not do money at all (§1), so a status code
that announces a bill exists would be a lie the API tells before the product does. If money is later
scoped, 402 becomes correct for exactly one condition — a tenant suspended for non-payment — and that is
a different thing from a limit.

Not `429`: nothing here is a rate. Not `507`: M5 §7 already rejected it as a 5xx that proxies and generic
client code will retry, reasonably reading it as our fault.

### 9.3 `/d/{slug}` — the visitor is told nothing

**The same 404, the same body byte for byte, the same headers, the same work.** M4 §5 defines four
identical refusals — unknown, revoked, expired, cap-reached — and this is the fifth. There is no new
status code, no new card, no hint.

Two consequences, both deliberate:

**The card's sentence does not change.** M4 §5 calls «ممکن است منقضی شده باشد، به سقف دانلود رسیده باشد،
یا لغو شده باشد» a security control rather than copy, because it names every cause without identifying
which one. A fourth cause would name the *customer's account state* on a public page — telling a stranger
that this business is on a metered plan and has hit its ceiling, which is commercial information about
someone who did not choose to publish it. The trade is real and should be recorded rather than
discovered: the sentence is now slightly incomplete as an explanation, in exchange for not publishing a
customer's plan state. Every remedy available to a recipient is identical in all five cases — ask the
sender — and the sender is told the truth in the panel banner, because the sender is the only person who
can act. §15.6 asks the owner to agree to this.

**The refusal is decided before any extra work.** §8's join means the check is a comparison on a row the
resolve already loaded. No Drive call, no second query, nothing that makes the "it existed" branch
measurably slower than the "it never existed" branch.

### 9.4 The bot

The bot speaks to a **linked** user, who is a member of the tenant. That is the whole difference between
it and `/d/{slug}`: the bot has an identity, so it may name the limit.

`/quota` already exists in Telegram §8.1 as "storage used against the tenant's cap". It renders all three
meters and the reset date. A refused send says which limit and when it frees:

> «سقف ترافیک ماهانه پر شده — تا ۱ شهریور»

The «ارسال فایل» button stays present and is not removed. Telegram §3.1 makes a size ceiling *absent*
because the user cannot fix it; a traffic overage is a condition that clears on a known date, so the
button remains and the reply explains — the same absent-versus-disabled line M5 §7 drew.

One inconsistency a user will notice, so the bot pre-empts it: a file that was already sent once is
re-sent from its cached `file_id` and **is not refused**, because it moves no bytes and refusing it
enforces nothing (§4.3). The limit refuses what it meters. The reply on the refused file names the limit,
so the difference has an explanation attached to it rather than looking arbitrary.

Every refusal still ends somewhere, per Telegram §8.2's no-dead-ends rule: the traffic refusal offers
«فایل‌ها», and nothing in it mentions a Google account, an account count or the pool (Telegram §5.5).

## 10. Plan assignment and change

**Where it is set:** `/operator/tenants/{tenantId}`, the route surface M5 §3 established — `tenantId`
from the route, handed to the same repository a customer's request would call, no unscoped overload and
no nullable tenantId meaning "all tenants". Two commands write a tenant's effective limits and nothing
else does:

```
SetTenantPlan(tenantId, planCode, reason)
SetTenantQuotaOverride(tenantId, field, value, reason)
```

Both operator-only, both writing a `TenantQuotaChange` row. `SetTenantStorageQuota` from M5 §10 becomes
the storage-shaped special case of the second, rather than a third writer.

Overrides exist because a negotiated customer is the normal case and a product that cannot express one
forces the operator to invent a fake plan per customer. The override is on the tenant row where the plan
numbers already are, so nothing on the enforcement path learns that overrides exist.

**On sign-up**, a tenant gets `Plans:DefaultPlanCode` (§3). §15.7 is where the owner decides what a
stranger gets on a 10 TB pool, which is the number that decides whether open sign-up survives.

**Upgrade** is immediate: numbers copied, audit row written, in-flight uploads unaffected because they
already reserved (M5 §7 — a session already reserved finishes even if the cap moves under it).

**Downgrade when the tenant is already over the new limit.** Three options; two are not options:

- *Delete the customer's files.* No.
- *Refuse the downgrade.* Also no — it makes the operator's own commercial action impossible, and it is
  the "pretending they fit" the brief warns about.

**The choice: the lower number is stored, the tenant is over it, and being over a cap is a state the
product already has.** M5 §7 defined it — uploads stop, nothing is deleted, downloads and links and the
panel keep working, the bar is full in `--danger`, and the way out is deleting files, which requires the
panel, which still works. A downgrade produces that state deliberately instead of accidentally.

**The one-line rule that covers all four dimensions: a downgrade constrains the next action, never an
existing one.**

| Dimension | On downgrade below current usage |
|---|---|
| Storage | Over-cap state. Uploads refused; nothing deleted; the sidebar says so in words (§9.1). |
| Per-file size | **Existing larger files are untouched.** The limit is on the act of uploading, not on possession. A stored file keeps downloading and keeps sharing. Anything else means a pricing change deletes or hides customer data. |
| Traffic | Already-spent bytes in the current window do not change; the tenant may be instantly over for the rest of the month. Same refusal, and the message's date is the answer. |
| Seats | Existing members are not removed and pending invitations are not revoked — silently cancelling an invitation someone is about to accept is a surprise with no upside. New invitations are refused until the count fits. |

**The operator sees the resulting overage before confirming**, on the downgrade screen: the storage
overage in bytes and the file count that would have to go, the traffic figure already spent this month,
and the seat count. An operator who downgrades a customer without seeing that is going to hear about it
from the customer.

**Retiring a plan** sets `IsRetired`: it disappears from new assignment and every tenant on it keeps
working, because their numbers live on their own row (§3).

## 11. The operator's side

M5 §7 already put used-against-cap per tenant and the sum of caps against the pool on the operator's
tenant list, with **over-commitment allowed and displayed** rather than prevented — caps are ceilings,
not reservations, and requiring `sum(caps) ≤ 10 TB` would make every new sign-up wait on a capacity
purchase. That is carried through unchanged, `--warn` and «تعهدشده: ۱۴ TB از ۱۰ TB» included, and the
figure stays `sum(Tenant.StorageQuotaBytes)` — a query unaffected by anything happening in the plan
catalogue, which is one of §3's payoffs.

Extended to the new dimensions, with one refusal:

- **Traffic gets no pool comparison.** The operator's egress ceiling is a bandwidth number, not a stored
  quantity, and M6 §3 says plainly that nobody yet knows what this box's uplink can do — M6 §12 is still
  asking. So the screen shows two figures side by side and labels them honestly: **actual** egress this
  month across all tenants, from M6 §10's measurement, beside the **sum of sold allowances**, marked as
  sold rather than reserved. Sizing the first against the port is M6's open question and this document
  does not pretend to answer it.
- **`IOperatorTenantReader`** (M5 §3) gains plan code, traffic used this window, and seats used. It stays
  what M5 made it: aggregates only, never file rows across tenants.
- **`TenantQuotaChange` is shown on the tenant's operator page.** This is a deliberate, narrow tension
  with M5 §12, which declined an audit-log screen. It is not one: it is one table on one page, and it
  exists because a quota is a commercial promise and «چرا سهمیه‌ام عوض شد» must be answerable without a
  support engineer writing SQL. The general audit log M5 declined stays declined.
- **Reconciliation.** M5 §7 recomputes `StorageUsedBytes` and **logs a discrepancy rather than silently
  correcting it**, because a discrepancy means a transition was missed and that is worth seeing.
  `TrafficUsedBytes` is reconciled the same way against `SUM(TenantEgressDay)` over the window. Because
  §6's flush writes both in one transaction, a mismatch there is a real bug and not expected noise —
  which is the only reason the check is worth running.

## 12. Where money attaches, and where this stops

M5 §10 left one seam and no more. This document widens it by one command and no more.

**The seam:** `SetTenantPlan` and `SetTenantQuotaOverride` (§10) are the only writers of a tenant's
effective limits, both operator-only, both audited. A billing system becomes a second caller of those
two commands, and it reads three meters that already exist for other reasons: `StorageUsedBytes`,
`TrafficUsedBytes` per window (with `TenantEgressDay` behind it for any window a billing period wants),
and the seat count. Nothing else has to exist for money to attach.

**There is no `Price` column on `Plan`, deliberately.** It is the single column that would turn this into
a billing table, and a price with no engine behind it is a number on a screen that nobody honours. When
money is scoped, the price lives with the thing that charges it.

Four questions would have to be answered before the first line of billing code, and none of them is
answered here:

1. What happens to a customer's files when they stop paying. M5 §10 already said this is a legal and
   product question before it is a code one, and it is still the hardest one in the product.
2. Currency, and whether a payment provider is reachable at all for this business in the jurisdictions
   it serves. That is a business-formation question, not an integration.
3. Invoice and tax obligations, which decide what has to be stored and for how long — and which
   interact with M4 §6.3's deliberately short retention on `DownloadEvent`.
4. Monthly or annual, and whether a plan change mid-period is prorated — which is the question that
   decides whether `UsageWindow()` (§6) can stay a calendar month.

## 13. Decomposition

| # | Slice | Contents | Depends on |
|---|---|---|---|
| **P1** | **Plans, and the two limits that are simply true** | `Plan`, the tenant's effective columns, `TenantQuotaChange`, `SetTenantPlan` / `SetTenantQuotaOverride`, the operator's assignment and downgrade-preview screens, per-file size enforced in `IUploadCoordinator.BeginAsync`, storage's number sourced from the plan, the «پلن و مصرف» card, the four `409` bodies | M5 |
| P2 | Traffic | `TenantEgressDay`, the denormalised window counter and its one-statement reset, the counting-stream wrap on the three copy sites, the admission check on `/d/{slug}/file` and in the bot, the flusher and its retention, reconciliation, the links-table banner and the reset date | P1 |
| P3 | Seats and the operator's cross-tenant view | `MaxMembers` and its conditional insert at invitation creation, the seat meter, `IOperatorTenantReader`'s new figures, actual-versus-sold egress, the plan catalogue screen with retirement | P1 |

**P1 is the only slice worth shipping alone**, in M1's sense: it turns M5's single unexplained number
into a named tier with a history, and it adds the per-file limit the owner's message asks for most
directly. It introduces no counter, no window and no new refusal on an anonymous path.

**P2 does not depend on M6.** The one wrap site the traffic limit cannot do without is M1's copy into
`Response.Body` on `/d/{slug}/file`, which exists today. If M6 has shipped, P2 reuses its
`CountingStream` and adds a dimension; if it has not, P2 builds the decorator and M6 §10 later reuses it
in the other direction. Neither ordering costs anything.

**P3 depends on P1 only** and can be built before or after P2.

## 14. Tests that hold these lines

In the spirit of M1 §8's two and M5 §5's suite. Every one runs against the fake `IDriveClient` and fake
`ITelegramClient`; nothing here reaches Google or Telegram.

1. **The per-file limit is in the coordinator, not the controller.** `IUploadCoordinator.BeginAsync`
   called directly with a size above `MaxFileBytes` refuses, and no `UploadSession` row and no Drive
   resumable session exist afterwards. Plus the same refusal reached through the Telegram inbound path.
2. **A refused upload spends nothing.** After a `file_too_large_for_plan` refusal, `StorageUsedBytes` is
   unchanged — the storage reservation and the size check are in one transaction and both roll back.
3. **The fifth refusal is identical to the other four.** Over-traffic `/d/{slug}` and `/d/{slug}/file`
   return 404 with a body byte-identical to unknown, revoked, expired and cap-reached, and an identical
   header set. Parameterised alongside M4 §10.1's cases so a future addition has to join the list.
4. **No tenant id on the public path.** No `/d/*` response body or header, as a raw string, contains the
   tenant's GUID in any casing or encoding. Asserted on the string, per M2 §12.4 — the bug being guarded
   against is a DTO gaining a property.
5. **The window resets with nothing running.** `TimeProvider` at 23:59 on the last day of the month in
   `Plans:UsageTimeZone` reads N; at 00:01 it reads 0; no background service was started. M2 §12.3's
   test, restated for a denormalised counter.
6. **A boundary crossed mid-transfer splits.** Bytes flushed either side of the month boundary land in
   two `TenantEgressDay` rows and in the correct window counter, with the old month's total intact.
7. **A cached `file_id` re-send meters zero.** Telegram §12.7's test, extended: no `TenantEgressDay` row
   and no change to `TrafficUsedBytes`.
8. **Overshoot is bounded and admission is not.** A tenant at 99% starting a counted download completes
   it and lands over the limit; the next request to `/d/{slug}/file` — counted or a range continuation —
   gets the unavailable card.
9. **The plan check outranks the pool.** A tenant over their storage cap, with every Google account
   soft-stopped, gets `409 tenant_quota_exceeded` and not `503 no_upload_target`.
10. **A plan is a template.** Editing a `Plan` row changes no tenant's effective limits, and re-applying
    it writes `TenantQuotaChange` rows for each field that moved.
11. **A downgrade deletes nothing.** Downgrading below current usage leaves the file count unchanged,
    leaves `GET /d/{slug}` at 200, and refuses the next `POST /api/uploads` with 409.
12. **Seats do not overshoot.** Two simultaneous invitations at `MaxMembers − 1` produce one invitation
    and one `member_limit_reached`.
13. **Sessionless flush and sweep write something.** The flusher with no `HttpContext` writes tenant A's
    bucket, asserted non-empty; the 400-day sweep over seeded old rows returns a non-zero delete count.
14. **The new endpoints are classified.** M5 §5's generated endpoint test must place every route added
    here on the explicit allow-list or cover it with a cross-tenant case, or turn red.

## 15. Before implementation starts

Seven things are needed from the owner. The first three block the first commit.

1. **Is money in scope, and when?** It decides whether §12's seam is a seam or a fiction, and it decides
   how much of P3 is worth building — a catalogue with retirement and history is right if billing is
   months away and over-built if plans are only ever an operator capacity tool. The recommendation in
   this document is to build the limits now and decide money separately, because every limit here is
   worth having on its own and none of them needs a price to work.

2. **The plan names and the actual numbers** — per tier: storage bytes, per-file bytes, monthly egress
   bytes, seats. These are not guessed here and no placeholder is written anywhere in the code. Three
   constraints to reason with:
   - The sum of storage over-commits a 10 TB pool by design (M5 §7), so the question is not "do they
     fit" but "how many of them can plausibly fill at once".
   - **Per-file size is the error bar on the traffic limit** (§4.3b). A tier whose per-file limit is a
     large fraction of its monthly traffic limit will visibly overshoot, and a customer will screenshot
     it. Keeping per-file well below monthly traffic is what makes the traffic meter look accurate.
   - **«دو گیگ» — which dimension was that?** The message reads as a per-file limit («هر فایل رو
     محدودیت بذارم»), and 2 GB would be an unusually small *storage* cap for a product whose upload path
     is built for 96 GB files. Confirm which, because the two lead to completely different plans.

3. **Which dimensions ship.** Confirm the recommended four — storage, per-file, monthly traffic, seats —
   and confirm the two declines. Both declines are dimensions the brief named: **live share links** and
   **daily downloads**, argued down in §2. Seats is the one dimension being added that the brief did not
   name; if selling by seat is not wanted, it costs one column and one check to drop.

4. **The usage window.** Proposal: calendar month in `Plans:UsageTimeZone`, defaulting to `Asia/Tehran`,
   deliberately separate from M2's Google-facing `Quota:ResetTimeZone`. If invoices will later run from
   each customer's sign-up anniversary, say so now: `UsageWindow()` is the one function that changes and
   the daily rows survive either way, but the denormalised window key does not, and migrating a month of
   live counters is not free.

5. **Over the traffic limit, does a signed-in member keep downloading their own files?** Proposal: yes,
   and the bytes still count (§9.1). Public and Telegram delivery stop; the panel does not. There is a
   real abuse counter-argument and it is stated there. This is a product call, not a technical one.

6. **Does the public card's sentence stay unchanged?** Proposal: yes (§9.3). It means the card no longer
   names every possible cause, which is a deliberate erosion of a property M4 §5 built on purpose, in
   exchange for not telling strangers about a customer's plan state. The owner should agree to that
   rather than discover it from a support conversation.

7. **The default plan for self-serve sign-up.** M5 §11.3 already asks whether sign-up is open at all.
   This adds the number that goes with the answer: if sign-up is open, the default plan is what any
   stranger gets on a 10 TB pool, and — as M5 §11.1 put it — a generous open sign-up is a single
   afternoon away from a full pool.

## 16. Deliberately not in scope

- **Charging money, in every form** — prices, invoices, payment providers, proration, dunning, trials,
  coupons, grace periods, overage pricing, pay-as-you-go, and any `Price` column. §12 leaves the seam
  and stops.
- **Self-serve plan change by the customer.** There is no checkout. A plan change is an operator action
  until money is scoped, and the panel offers no "upgrade" button that would have nowhere to go.
- **A suspended tenant.** There is no suspended state and no account-level disable. The limits refuse
  specific actions; nothing here turns a customer off.
- **Per-tenant live-link caps and per-tenant daily download caps.** Declined in §2 with the reasoning,
  not deferred — both were in the brief and both are the wrong shape for what they would protect.
- **Per-tenant bandwidth throttling.** M6 §13 already declined it, on the grounds that a shaper needs a
  policy saying whose traffic to slow and by how much, and that is a billing and SLA question. This
  document supplies a limit, not a shaper: a tenant over their traffic allowance is refused, never
  slowed.
- **Per-tenant concurrency as a plan dimension.** M4 §7.1's per-link (6) and global (200) stream limiters
  stay what they are — capacity and abuse controls, revisited in M6 when the traffic chart is real. They
  happen to bound §4.3's overshoot, which is a benefit rather than a reason to sell them.
- **Per-tenant daily upload fairness against M2's 750 GB/day accounts.** §7 names the exposure plainly:
  one large tenant can consume an account's day alone. It is a scheduling problem and M3's queue is
  where it would live, not a plan dimension.
- **Ingress as a metered dimension.** §4.3 charges upload egress to Google against storage, not traffic.
- **Per-plan feature flags** — password links, custom slugs, Telegram, transfers or S3 export gated by
  tier. Quantities are tiered here; capabilities are not (§1). Every flag is a new authorization surface
  across four clients and none of them has a request behind it.
- **Per-plan retention** — "free tier files are deleted after 30 days". It is a deletion policy on
  customer data, it needs its own design, and M5 §12 already declined trash and restore.
- **A customer-visible usage history** beyond the current window: no month-over-month chart, no CSV
  export, no per-link egress attribution. `TenantEgressDay` makes all of them possible later; none is
  built.
- **A hard pool-level ceiling on the sum of tenant storage.** M5 §7 decided over-commitment is allowed
  and shown, and nothing here reverses it.
- **Enforcement of anything against `GoogleAccount.QuotaUsedBytes`.** M5 §7 is explicit that the tenant
  counter and the Google counter measure different things and are never reconciled against each other;
  a plan limit reading the second would be measuring the operator's pending purges and M3's copies.
</content>
</invoke>
