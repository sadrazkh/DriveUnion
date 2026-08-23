# Drive Union — M5: roles and tenancy

**Date:** 2026-08-23 · **Status:** design proposed; sits on M1, cannot start before it ships; blocked
on §11 before the first line of implementation

## 1. What M5 settles

M1 built one tenant's worth of product and left five questions open that a second customer makes
urgent:

- Who inside a customer's workspace may upload, share, and invite — the three roles the design already
  named.
- What the operator can see and do across all tenants, expressed so that a bug cannot hand that
  authority to a customer.
- How a colleague is invited, how an invitation is accepted, and how it is taken back.
- How much of the operator's 10 TB one customer may consume. M1 §12 flagged this as the missing piece:
  without a cap, the first customer with a NAS to back up eats the pool and every other customer's
  upload starts failing for reasons none of them can see.
- How a tenant comes into existence at all. M1 seeds an operator from config and assumes tenants are
  already there.

The model delta is small. Everything else in this document is about where the checks live.

```
Tenant        { …M1 fields…, StorageQuotaBytes, StorageUsedBytes }
AppUser       { …M1 fields…, TenantId?, IsOperator, Role }
Invitation    { Id, TenantId, Email, NormalizedEmail, Role, TokenHash,
                CreatedByUserId, CreatedAt, ExpiresAt,
                AcceptedAt?, AcceptedByUserId?, RevokedAt? }
```

No new join table. `AppUser.TenantId` stays as M1 wrote it: **one tenant per account.** The design
draws no tenant switcher and no "you are working in…" control, so a membership table would buy a
capability with nowhere to express it, and it would immediately raise "which tenant is this request
about" — the exact ambient question M1 §8 spent its whole argument avoiding. A consultant working for
two customers signs up twice. If this is reversed later, the cost is confined to one class (§4): the
resolution point that turns a principal into a tenantId is the only code that knows the answer comes
from `AppUser`.

## 2. Three roles inside a tenant

The design names them and gives their Persian labels, which the panel keeps verbatim:

| Role | Label | May |
|---|---|---|
| `Viewer` | «فقط مشاهده» | See the tenant's file list and detail panel, download the tenant's files through the panel, see the tenant's share links and their counters, see who else is in the workspace |
| `Uploader` | «آپلودر» | Everything a viewer may, plus upload, delete, transfer (M3), and create / edit / revoke share links |
| `Owner` | «مدیر کل» | Everything an uploader may, plus invite, change a member's role, remove a member, rename the workspace, and see the storage cap card's detail |

Three fixed, strictly nested roles — not a permission grid. They nest because the design's own
descriptions nest («همه‌چیز + مدیریت اکانت‌ها و کاربران» ⊃ «آپلود، انتقال، ساخت لینک» ⊃ viewing), and
because the screen has exactly one role column with three strings in it. A permission grid would need
a UI that does not exist and a migration story for every future verb.

`Uploader` acts on the whole tenant's files, not only on files it uploaded. There is no per-file owner
anywhere in the data model or in the design's file table, and inventing one would put a column on a
screen whose seven grid tracks are already final.

Two rules that are not obvious:

- **A tenant always has at least one owner.** The last owner cannot demote or remove themselves. The
  command returns `409` with `last_owner`. Without this, one careless click produces a workspace whose
  members exist but which nobody can administer, and the only way out is an operator writing SQL.
- **The role enum is persisted, so its values are explicit and validated on read.**

```csharp
public enum TenantRole : byte { Viewer = 10, Uploader = 20, Owner = 30 }
```

Gaps so a fourth role can be inserted without renumbering stored rows. Explicit values because
declaration order must never decide what is in a column. And the comparison `role >= minimum` is
**fail-open on garbage**: a `255` written by a bad migration or a support query is an owner in every
tenant. So the column carries a check constraint on the three legal values, and the handler calls
`Enum.IsDefined` before comparing and denies otherwise.

## 3. Operator vs tenant: authority that cannot leak

M1 §1.4 settled what the split *means*: customers see the same panel scoped down, the account filter,
the daily-quota bars, the Google Accounts screen and the proxy table are operator-only, and a customer
must never learn which Google account holds their file. M5 settles how that is enforced.

The failure to design against is an ambient god mode — `if (user.IsOperator) return true;` sprinkled
through tenant code paths, so that one wrong default, one over-posted form field, one null-coalescing
mistake promotes a customer to the whole pool. Three rules prevent it:

**1. Operator authority is a different route surface, not a flag tested inside tenant code.** Every
operator screen and endpoint lives under `/operator/*` behind `[Authorize(Policy = "Operator")]`. The
tenant panel's controllers never read `IsOperator` at all — not once. A flag that no tenant-facing code
consults cannot widen any tenant-facing path; it can only open a door that is behind a different door.

**2. The operator never gets an implicit tenantId.** M1 §8's rule survives unchanged: `tenantId` is an
explicit argument on every repository method. When an operator inspects a customer's files, the
tenantId comes from the route — `/operator/tenants/{tenantId}/files` — and is handed to the *same*
repository method a customer's request would call. There is deliberately **no unscoped overload and no
nullable tenantId meaning "all tenants"**. That signature is the Harbora bug wearing a different hat:
one null reference away from being every customer's default. Cross-tenant reads are served by a
separately named read model, `IOperatorTenantReader`, that returns aggregates only — tenant name, member
count, bytes used, file count, last activity — and never file rows across tenants.

**3. The operator is not a member of any tenant, and the role handler has no operator branch.** An
operator's `TenantId` is null and stays null. If the operator needs to act *as* a customer, that is
impersonation, which is not in M5 (§12) precisely because it deserves its own audit trail rather than a
quiet `context.Succeed()` inside a handler everyone reads as being about customers.

## 4. How authorization is expressed in code

Two jobs, two mechanisms, never conflated:

- **What a role may do** is decided by ASP.NET Core authorization policies.
- **Whose rows exist** is decided by the repository signature. `WHERE Id = @id AND TenantId = @tenantId`
  is the isolation mechanism. Tenant B asking for tenant A's file id gets `null` and the controller
  renders 404.

Resource-based authorization (`IAuthorizationService.AuthorizeAsync(User, file, requirement)`) is
deliberately **not** the isolation mechanism, even though it is the textbook answer. It only fires when
a call site remembers to call it; the day someone adds an endpoint and forgets, the check is silently
absent and nothing fails. A method signature that will not compile without a tenantId cannot be
forgotten. Resource handlers may still be used for finer questions later; they are not load-bearing
here.

### The policies

```csharp
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("TenantViewer",   p => p.AddRequirements(new TenantRoleRequirement(TenantRole.Viewer)))
    .AddPolicy("TenantUploader", p => p.AddRequirements(new TenantRoleRequirement(TenantRole.Uploader)))
    .AddPolicy("TenantOwner",    p => p.AddRequirements(new TenantRoleRequirement(TenantRole.Owner)))
    .AddPolicy("Operator",       p => p.AddRequirements(new OperatorRequirement()))
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
```

The fallback policy means a controller added without `[Authorize]` is authenticated-only rather than
open. The public routes M1 owns — `/d/{slug}`, `/d/{slug}/file` — carry an explicit `[AllowAnonymous]`,
as do sign-in, sign-up and invitation acceptance. Forgetting to protect a new panel endpoint now fails
closed; forgetting to open a new public one fails loudly on the first request.

`[Authorize(Roles = "Owner")]` and ASP.NET Identity's role store are **not** used. Identity roles are
global: a principal holding "Owner" holds it everywhere, which in a multi-tenant product is precisely
the bug. Roles here are per-tenant data, and the only thing that reads them is the handler below.

### The handler

```csharp
public sealed class TenantRoleRequirement(TenantRole minimum) : IAuthorizationRequirement
{
    public TenantRole Minimum { get; } = minimum;
}

internal sealed class TenantRoleHandler(ITenantContext tenant)
    : AuthorizationHandler<TenantRoleRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TenantRoleRequirement requirement)
    {
        var membership = await tenant.GetMembershipAsync();   // null for operator and anonymous
        if (membership is null) return;
        if (!Enum.IsDefined(membership.Role)) return;
        if (membership.Role >= requirement.Minimum) context.Succeed(requirement);
    }
}
```

There is no `IsOperator` branch in it. That absence is the single most important line in this document.

### `ITenantContext` — one resolution point, and no empty tenant

```csharp
public interface ITenantContext
{
    Guid TenantId { get; }                        // throws if the caller has no tenant
    bool TryGetTenantId(out Guid tenantId);
    Task<TenantMembership?> GetMembershipAsync(); // role + tenantId, loaded once per request
}
```

`TenantId` is non-nullable and **throws** for an anonymous request, for an operator, and for background
work. It never returns `Guid.Empty`. Harbora's four silent failures in one day all came from an
anonymous request mapping onto an empty tenant and every filtered read coming back plausibly empty; the
fix is not a better default, it is having no representable default. A missing tenant is a fault, and a
fault is loud.

Role and tenantId are **read from the database on the authorized request, not from cookie claims.** The
claims carry identity only. A claim minted at sign-in is stale until the cookie expires, and «ویرایش
دسترسی» in the members table is the button an owner presses when someone leaves the company — it has to
mean *now*. The cost is one indexed lookup, cached in the scoped `ITenantContext` so it happens once per
request regardless of how many policies evaluate. Role changes and removals additionally bump the
user's Identity security stamp so the principal is rebuilt.

The `"Operator"` policy reads `AppUser.IsOperator` from that same per-request lookup for the same
reason. `RequireClaim` was considered and rejected: removing an operator from config would leave a
working cookie behind.

### Two ways a flag gets handed out by accident

- **Over-posting.** Every state-changing endpoint binds to an explicit request DTO, never to an entity.
  `IsOperator`, `TenantId`, `Role` and `StorageQuotaBytes` appear on no DTO reachable by a tenant user.
  `IsOperator` has exactly one writer in the whole codebase: M1's config seeder. §5 pins this with a
  test that posts the field and asserts it did not take.
- **CSRF.** The panel is cookie-authenticated Razor, so a state-changing request an owner's browser is
  tricked into making is an authenticated request. `AutoValidateAntiforgeryTokenAttribute` is registered
  globally, the auth cookie is `SameSite=Lax`, and the Vue islands send the token in an `X-CSRF-TOKEN`
  header — including on the upload endpoints, which write bytes and consume the tenant's cap.

### Status codes

- A viewer POSTing to `/api/uploads` → **403**. They are authenticated and simply lack the capability;
  nothing about another tenant's data is revealed.
- Any request naming an id belonging to another tenant → **404**, never 403. A 403 confirms the id
  exists, which is a cross-tenant existence oracle. Same reasoning as M1 §7's rule that an unknown slug
  and a revoked slug render the identical card.

## 5. Proving tenant B cannot reach tenant A's file

M1 §8 already commits to a cross-tenant test. M5 is the slice where it has to stop being one assertion.

The test lives at the HTTP surface — `WebApplicationFactory<Program>` with a test authentication handler
that issues a principal for a named user — **not** on the repository. The repository is not the thing in
doubt; the thing in doubt is whether some controller, filter, view or new endpoint forgot to route
through it. A unit test on the repository proves the lock works while saying nothing about the door
someone left off its hinges.

The fixture: tenant A with owner `a@example.test`, tenant B with owner `b@example.test`, a `StoredFile`
in A uploaded through M1's fake `IDriveClient`, a `ShareLink` on it, an in-flight `UploadSession` in A.

Then, signed in as B's owner — the *highest* in-tenant role, so the test cannot pass merely because the
role check bit first:

| Request | Expected |
|---|---|
| `GET /api/files/{aFileId}` | 404 |
| `GET /api/files/{aFileId}/download` | 404 |
| `DELETE /api/files/{aFileId}` | 404 |
| `PATCH /api/links/{aSlug}` | 404 |
| `DELETE /api/links/{aSlug}` | 404 |
| `GET /api/uploads/{aSessionId}` | 404 |
| `PUT /api/uploads/{aSessionId}/chunk` | 404 |

The upload-session rows matter most and are the easiest to overlook: a leaked session id is the one
handle that lets an outsider *write* into another tenant's storage and spend their cap.

**The list is generated, not hand-written.** The test injects `EndpointDataSource` from the test host,
selects every endpoint under `/api/` whose route template contains an id or slug parameter, and asserts
each one is either in an explicit, commented allow-list (anonymous, or genuinely tenant-agnostic like
`/api/me`) or covered by a case above. A new endpoint added in M6 turns the test red until someone
classifies it. This is more machinery than a hand-written list and it is the only version that is still
true in six months.

Three more tests hold the surrounding line:

- **The tension test, in the same class so it is visible:** anonymous `GET /d/{slug}` on A's link returns
  **200** in the same fixture where B's panel read of the same file returns 404. This is M1 §8's pair of
  tests written as one story, because the two requirements pull against each other and a future
  "simplification" will break exactly one of them.
- **No ambient filter.** Reflect over the `DbContext` model and assert `GetDeclaredQueryFilters()` is
  empty for every entity type. Reintroducing a global `TenantId` filter as a convenience becomes a red
  test instead of a silent behaviour change that 404s every public link in the product.
- **No empty tenant, no promotion by post.** `ITenantContext.TenantId` throws for an anonymous principal
  and for an operator principal. And `POST /account/register` with `isOperator=true` and `role=Owner`
  added to the form and to the JSON body creates a user whose `IsOperator` is false and whose role came
  from the server.

## 6. Invitations

An owner invites by email. That is the whole flow the design implies with «ویرایش دسترسی» and a member
list; nothing else is drawn, so nothing else is built.

**Creating.** `POST /api/members/invitations` with `{ email, role }`, policy `TenantOwner`. The server
generates 32 bytes from `RandomNumberGenerator`, base64url-encodes them into the URL
`{PublicBaseUrl}/invite/{token}`, and stores **only the SHA-256 hash**. The invitations table is a table
of live credentials; a database dump, a support query pasted into a ticket, or a logged result set must
not be a set of working keys — the same reasoning that put M1's refresh tokens behind Data Protection.
SHA-256 rather than a password hash because the token is 256 bits of entropy with nothing to brute
force, and the lookup must be one indexed read.

**Lifetime: 7 days.** Long enough to survive a weekend and a spam folder, short enough that a forwarded
link found in an old mailbox is dead. It is also the same "about a week" as M1's Drive resumable
sessions, so the product has one such number rather than two.

**The token is shown to the owner once, right after creation, with a copy button** — the same
`dir="ltr"` monospace field pattern the design uses for the share-link address. This is not only
convenience: the product must work before an SMTP provider exists (§11), and after that first render the
token is unrecoverable because only its hash was stored. Same contract as an API key.

**Accepting.** `GET /invite/{token}` is anonymous. If the token hashes to a row that is unaccepted,
unrevoked and unexpired, the page shows the workspace name, the invited email and the offered role, and
either a sign-in form or a sign-up form with the email field **pre-filled and read-only**. Anything else
— unknown, expired, revoked, already accepted — renders one identical card, «این دعوت معتبر نیست»,
built from the public page's existing components. One card for all four cases, so the page is not an
oracle for which tokens ever existed.

**Acceptance is bound to the invited address.** The accepting account's `NormalizedEmail` must equal the
invitation's. A mismatch renders «این دعوت برای نشانی دیگری صادر شده است» and does not say which
address. If the token alone were sufficient, an invitation would be a bearer token to a customer's entire
file list — and it travels by email, which is forwarded, quoted into tickets and archived forever. The
failure mode is silent: nobody notices the wrong person joined, and the owner's member list names
someone who is not who they think.

The counterargument is real. Owners mistype addresses, people have two accounts, and shared mailboxes
exist. So the mismatch screen offers exactly one action: ask the owner to re-send to *this* address,
which it displays so the user can copy it. There is deliberately no self-service way to retarget an
invitation, because retargeting is the control being enforced.

**An invited address that already belongs to another tenant is refused at creation time**, with «این
نشانی در فضای کاری دیگری عضو است» — a consequence of the one-tenant-per-account decision in §1, and it
has to fail at invite time rather than at accept time so the owner learns it while they still have the
person's attention.

**Clicking a link sent to an address is proof of controlling it.** An account created through the invite
flow has its email marked confirmed on acceptance. A second confirmation round trip would prove nothing
that the click did not already prove.

**Acceptance is single-use and race-proof.** The accept command updates
`WHERE Id = @id AND AcceptedAt IS NULL AND RevokedAt IS NULL AND ExpiresAt > @now`, requires exactly one
row affected, and creates the membership in the same transaction. Two simultaneous clicks produce one
membership and one «این دعوت معتبر نیست».

**Revoking.** `DELETE /api/members/invitations/{id}`, policy `TenantOwner`, sets `RevokedAt`. The row is
kept — "did we ever invite this person" is a question owners ask — but revoked and expired invitations
disappear from the members table, which shows only what is actionable: current members and pending
invitations.

**Rate limits.** At most 20 pending invitations per tenant, and at most 10 invitation emails per hour per
tenant, through the ASP.NET Core rate limiter M1 already configures in §9, partitioned by tenantId rather
than by IP. This endpoint sends attacker-chosen-recipient mail from the operator's domain; unbounded, it
is a spam cannon whose first casualty is the domain's deliverability, which then breaks password reset
for every paying customer.

**Mail.** M5 introduces `IEmailSender` in Core, an SMTP implementation in Infrastructure, and a
collecting fake in tests — the same shape as `IDriveClient`, and for the same reason: no test may reach
a mail server. Provider and credentials are an owner decision (§11).

## 7. Per-tenant storage caps

`Tenant.StorageQuotaBytes` is the ceiling; `Tenant.StorageUsedBytes` is the counter. Both non-nullable.
A nullable cap meaning "unlimited" is the ambient-god-mode shape again — one migration default away from
every tenant being uncapped, and nothing would look wrong until the pool was full.

### What counts

The sum of `SizeBytes` over the tenant's live `StoredFile` rows, **plus the declared size of every
in-flight `UploadSession`.** In-flight sessions must be counted, or ten parallel 60 GB uploads into a
500 GB cap each pass the check and land at 600 GB. That bug appears only under a real user with a real
connection, which is to say in production.

`StorageUsedBytes` is denormalised on `Tenant` for the same reason `DownloadCount` sits on `ShareLink`:
the sidebar reads it on every page render. The authoritative figure is the query above; a reconciliation
recomputes it and logs any discrepancy rather than silently correcting it, because a discrepancy means a
transition was missed and that is worth seeing. M5 runs it at startup and on demand from the operator
panel; when M3 brings a job runner, it becomes a daily job there.

Three transitions, and nothing else touches the counter:

1. **Reserve** at `POST /api/uploads`, before Google is contacted.
2. **Settle** when the final chunk completes — the reservation is replaced by the size Drive reports.
3. **Release** on session failure, expiry or abort, and on file deletion.

Two things that are easy to get wrong:

- **A transfer between Google accounts (M3) does not change tenant usage.** It changes
  `StoredFile.GoogleAccountId`. If M3 implements it as a copy, the operator's pool pays twice and the
  tenant pays once, because the customer did not ask for two copies.
- **Bytes are released when Google confirms they are gone, not when the user clicks delete.** `DELETE`
  sets `DeletedAt` (hiding the file immediately) and calls Drive's `files.delete` in the same request;
  the counter drops only on confirmation. If Drive fails, the row stays pending purge and the bytes keep
  counting until a retry succeeds, with the sidebar card's subline reading «در حال آزادسازی». Releasing
  optimistically means the tenant's counter says free while the operator's pool says full, and the
  operator eats the difference silently.

`Tenant.StorageUsedBytes` and `GoogleAccount.QuotaUsedBytes` measure different things and are never
reconciled against each other. The first is what customers own; the second is what Google is holding,
which is larger by exactly the pending purges and M3's copies.

### When it is enforced

**Before the bytes are sent, twice, for two different threats.**

The honest client is stopped at `POST /api/uploads`, before the resumable session is opened with Google.
Before, not after: refusing afterwards orphans a session on Google's side, and the client deserves to
learn "no" from the round trip that costs one request rather than after it has pushed 32 MiB. The
reservation is a single conditional UPDATE, in the same transaction that creates the `UploadSession`:

```sql
UPDATE tenants
   SET storage_used_bytes = storage_used_bytes + @size
 WHERE id = @tenantId
   AND storage_used_bytes + @size <= storage_quota_bytes
```

One row affected means the reservation held. Check-then-act in C# loses this race; the database wins it
in one statement.

The **dishonest** client is stopped mid-stream. A declared `sizeBytes` is a claim, and a pre-check
against a claim is not a security control — declare 1 MB, upload 100 GB. Every byte passes through OVH
by M1 §6's design, so the chunk endpoint counts what it forwards and aborts the session the moment the
total would exceed what was reserved. Each chunk's `Content-Range` total is also checked against the
session's reserved size and a mismatch is a 400.

Refusal is **`409 Conflict`** with `{ error: "tenant_quota_exceeded", capBytes, usedBytes,
requestedBytes }`. Not 413, which is about this request's entity being too large for the endpoint
regardless of state. Not 507, which is a 5xx and gets retried by proxies and by generic client code that
reasonably reads 5xx as "our fault, try again".

Two behaviours around the edges:

- **A session already reserved finishes even if the cap is lowered under it.** Killing a 90%-complete
  200 GB upload to enforce a cap change is a worse outcome than a temporary overage; the cap blocks the
  next upload instead.
- **An over-cap tenant loses uploads only.** Downloads, links, deletes and the panel keep working, and
  nothing is deleted automatically. The way out of an overage is deleting files, which requires the
  panel; breaking reads to enforce a storage cap punishes the customer for the only action that fixes it.

### What the panel shows as it fills

The sidebar's bottom slot is free for a tenant — «سهمیه آپلود امروز» is the operator's per-account daily
Drive quota and is operator-only under M1 §1.4 — so the cap card takes it, reusing that card's markup
exactly: `border:1px solid var(--line); border-radius:12px; padding:12px; background: var(--surface2)`,
an `11.5px` label, a monospace value, a `6px` bar.

- Label «فضای مصرفی», value `۱۲۴ / ۵۰۰ GB` in monospace.
- Fill colour uses the threshold rule the design already states for the daily-quota bar: `--accent`
  below 80%, **`--warn` at ≥80%, `--danger` at ≥95%**. Reused rather than invented, so the product has
  one set of thresholds.
- At or over 100% the bar is full in `--danger` and the header's «آپلود فایل» button is **disabled** —
  `opacity:.5; cursor:not-allowed`, no colour change, since the handoff has no disabled token. This is a
  state we are adding; it is not in the design.

Disabled is right here and wrong for roles. A viewer does not get a greyed-out upload button — for a
viewer the control is **absent**, because a role is not a temporary condition and a greyed control reads
as an upsell. An over-cap owner *has* the capability and the condition will pass, so showing the control
disabled is honest.

The `uploadPanel` island may pre-check the selected files against the remaining cap for immediate
feedback, but the pre-check is a courtesy and can be stale; the server's 409 is the rule, and the island
must render it correctly even when its own pre-check passed.

On the operator side, the tenant list shows used-against-cap per tenant and the sum of caps against the
pool. **Over-commitment is allowed and displayed**, not prevented: caps are per-customer ceilings, not
reservations, and requiring `sum(caps) ≤ 10 TB` would make every new sign-up wait on a capacity
purchase. When the sum exceeds the pool the figure renders in `--warn` — «تعهدشده: ۱۴ TB از ۱۰ TB».

## 8. Sign-up, and how a tenant comes to exist

M1 §1.2 says customers sign up and use the panel, so sign-up is the front door and **a tenant is created
by its first user.** One anonymous form — workspace name, email, password — and one transaction creating
`Tenant` plus `AppUser` with `TenantId` set and `Role = Owner`.

**The tenant slug is generated, not derived from the name.** M1 §5 uses it as the Drive folder
`DriveUnion/{tenant-slug}/`, and a name-derived slug has to change when the customer renames their
workspace, at which point the Drive folder either gets renamed under live uploads or quietly diverges
from the name it was supposed to mirror. It is also a transliteration problem with no good answer for
«شرکت آلفا». So `Tenant.Slug` is an 8-character token from M1's existing `ISlugGenerator` at a different
length, `Tenant.Name` is free text and renameable by the owner, and no screen in the design shows a
tenant slug to anyone.

`StorageQuotaBytes` is set from `Tenancy:DefaultStorageQuotaBytes` (proposed: 50 GB — §11 item 1).

**Email confirmation gates uploading, not signing in.** `SignIn.RequireConfirmedAccount` stays false; an
unconfirmed owner sees the panel with a banner, and `POST /api/uploads` returns `403 email_unconfirmed`.
The thing that must be attributable to a real person is the bytes hosted on the operator's Google
accounts and the links served from the operator's domain — that is the abuse surface. Reading an empty
panel is harmless. It also means the product is usable the day before SMTP is wired, which is the same
reason invitations degrade to a copyable URL.

**Sign-up is anonymous and creates rows**, so `POST /account/register` is rate-limited per IP through
M1 §9's limiter, and Identity's unique email index gives one tenant per address.

**No HTTP path sets `IsOperator`.** Operators come from M1's config seeder and nowhere else. §5 pins it.

## 9. What the screens become

The design's «دسترسی کاربران پنل» table was drawn for an operator team: its third column is «اکانت‌ها»,
holding `A1, A2` — which of the operator's Google accounts each teammate may work with. In this product
that column cannot exist. M1 §1.3 makes the accounts the operator's, and M1 §1.4 makes it a hard rule
that **a customer must never learn which Google account holds their file**. A column listing them would
break that rule on the first render, and there is nothing weaker to put in its place: a customer's user
has no relationship to accounts at all, because placement is chosen by M2's upload policy on the
operator's side.

So the table keeps its geometry and changes its meaning:

- The card **moves off the «اکانت‌های گوگل» screen**, which is operator-only, onto the tenant's
  **«تنظیمات»** screen as the first card at `grid-column: span 2` (the proxy card already establishes
  that span in the design). It has to move somewhere, and a customer's settings screen is otherwise
  empty — upload policy, transfer tuning and proxies are all operator-only.
- Card chrome, heading («دسترسی کاربران پنل», `14px/700`), grid tracks
  `minmax(0,1fr) 150px 130px auto`, `12.5px` body, `var(--row-pad)` and the per-row bottom border are
  all kept verbatim.
- **Column 3 becomes «وضعیت»**, which is what makes invitations visible without adding a second table:
  «فعال» for a member, and for a pending invitation «دعوت — ۶ روز» in monospace `11.5px` `--muted`. The
  column keeps its monospace treatment honestly, because the handoff assigns monospace to numbers.
- Column 4 keeps «شما» in `--muted` for the current user and «ویرایش دسترسی» in `--accent-ink` for
  others, plus «لغو دعوت» in `--danger` for a pending invitation.
- «ویرایش دسترسی» turns the role cell into a `select` styled as the design's inputs
  (`border:1px solid var(--line); border-radius:9px; font-size:12.5px`) that saves on change. No modal —
  a three-option choice does not need a dialog, and the handoff contains no dialog anywhere. While a row
  is in edit mode the action column also offers «حذف» and «انصراف»; «حذف» becomes «تأیید حذف» in
  `--danger` for four seconds rather than opening a confirmation dialog that would have to be invented.
- The card header gains a primary «دعوت همکار» button, styled like the header's «آپلود فایل»
  (`background: var(--accent)`, `border-radius:9px`, `padding:8px 16px`, `13px/600`). New affordance,
  named as new.
- Uploaders and viewers see the same card read-only: no invite button, no action links. They can see who
  their colleagues are, which is unremarkable information about their own workspace.

Elsewhere in the shell, for a tenant user:

- Navigation is فایل‌ها / صف انتقال / لینک‌های اشتراک / تنظیمات. «اکانت‌های گوگل» is gone under M1 §1.4,
  and the design's «داشبورد» is entirely operator content — account cards, OVH egress, Drive-specific job
  errors — so a tenant lands on فایل‌ها. Designing a customer dashboard belongs to whichever slice
  designs it, not to this one.
- The brand subtitle at `11px` `--muted` shows the workspace name instead of `2 accounts · 10 TB`, which
  is the operator's inventory and means nothing to a customer.
- The quota card becomes §7's storage cap card.
- The user card's role line shows «مدیر کل» / «آپلودر» / «فقط مشاهده»; an operator sees «اپراتور».
  Operator and tenant owner get different words on purpose — one term for two different authorities is
  how a support conversation ends up granting the wrong one.

**Hiding a control is not authorization.** Every hidden control's endpoint is independently protected by
its policy, and §5's suite includes a viewer POSTing to `/api/uploads` and getting 403 with no button
anywhere on their screen.

Capabilities are evaluated **once, on the server**. The Razor view injects `IAuthorizationService`, and
the islands receive `{ canUpload, canManageLinks, canManageMembers }` as props rather than a role string
they would have to re-interpret. Two implementations of one permission matrix drift, and the drift is
invisible until it is a support ticket.

## 10. Where billing attaches, and where this stops

Billing is not in scope and is specified nowhere. M5's job is to leave one seam and no more.

The seam is that **`Tenant.StorageQuotaBytes` is written by exactly one command,
`SetTenantStorageQuota(tenantId, bytes, reason)`, callable only from the operator surface.** Nothing in
M5 reads a price, computes a plan, or knows that money exists. Whatever bills later becomes a second
caller of that one command. `StorageUsedBytes` and its reconciliation are the only metering input such a
system would need.

Everything else that word implies — plans, proration, payment, invoices, dunning, what happens to a
customer's files when they stop paying — is unanswered here, and the last of those is a legal and
product question before it is a code one.

## 11. Before implementation starts

Five things are needed from the owner. The first two block the first commit of real code.

1. **The default storage cap, and whether over-committing the pool is acceptable.** Proposal: 50 GB
   default, and yes to over-commitment with the operator warning in §7. The number is config, but the
   answer decides whether self-serve sign-up can be open at all — an uncapped or generously capped open
   sign-up on a 10 TB pool is a single afternoon away from being full.
2. **Outbound email.** SMTP host and credentials or a provider, a From address on the operator's domain,
   and SPF/DKIM records. Invitations, password reset and email confirmation all depend on it. Until it
   exists, invitations work by copied URL and confirmation gates upload only — which is a deliberate
   degradation, not a permanent design.
3. **Is sign-up open on day one, or operator-provisioned?** Proposal: open, rate-limited, with
   confirmation gating upload. If the answer is a closed beta, `/account/register` is switched off by
   config and an operator "create tenant" screen is built — that screen exists only if the answer is
   "closed".
4. **May one person belong to more than one tenant?** Proposal: no, per §1. This shapes the data model
   now; reversing it later is confined to `ITenantContext` but it is not free.
5. **The abuse mailbox and the terms the sign-up form must present.** The public download page already
   prints `abuse@yourdomain.com` (design §7). Once strangers can create tenants that publish links from
   the operator's domain, that address has to be real and reaching someone, and the sign-up form needs a
   terms URL to link to.

## 12. Deliberately not in M5

- **Billing in every form** — plans, payment, invoices, dunning, seat counting. §10 leaves the seam and
  stops.
- **Operator impersonation** — "view this customer's panel as they see it". It is the natural next
  request from support and it must not be a `context.Succeed()` inside the role handler; it needs its own
  route, its own banner and its own audit trail.
- **Per-file and per-folder permissions inside a tenant**, and sharing a file with one named colleague.
  The design has one role per user and no owner column on a file.
- **Custom roles and permission grids.** Three nested roles, as drawn.
- **Multi-tenant membership and a tenant switcher.**
- **The «اکانت‌ها» column from the design's table.** Removed, not deferred: it cannot exist in this
  product without breaking M1 §1.4.
- **Two-factor authentication.** Identity ships TOTP and it is not wired here. A tenant owner's password
  is the only thing between a stranger and a customer's entire file store, and this should be scoped
  before customers with real data arrive — the same honest flag M1 §12 raised about billing.
- **SSO, SAML and OIDC.**
- **Trash, restore and a retention window** for deleted files. Delete is final once Drive confirms.
- **Per-tenant egress or bandwidth caps.** M5 caps stored bytes only. A tenant with one 200 GB file
  behind a link that goes viral costs the operator transit, not storage, and nothing in this document
  stops it. Worth a slice; not this one.
- **An audit log screen.** M5 records who invited whom and when, and keeps revoked invitations, but there
  is no surface that reads it.
