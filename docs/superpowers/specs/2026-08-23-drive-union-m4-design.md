# Drive Union — M4: full link control

**Date:** 2026-08-23 · **Status:** design; blocked on §11 before the first line of implementation.
Upstream: M1 §7 (download) and §8 (tenant isolation), both of which this slice extends and neither of
which it may weaken.

## 1. What M4 adds, and the one property it must not break

M1 shipped a link that is public, optionally expiring, optionally capped. M4 turns that into the
thing the brief actually asked for — «کنترل کامل روی نحوه‌ی اشتراک‌گذاری فایل‌ها» — by adding a
password gate, an alias filename, revocation, per-link activity, and the abuse controls a public file
host needs once its links are worth attacking.

**M4 depends only on M1.** It does not need the account pool (M2) or the job queue (M3). If the owner
wants link control before multi-account, this slice can be built second.

The property that must survive every line of this document: **a refused link and a link that never
existed produce the same response.** M1 established it in §7. M4 adds a fifth public state (gated) and
four new ways to get a refusal wrong. §5 is the whole discipline; everything else defers to it.

## 2. Data model changes

```
ShareLink   += PasswordHash?      text          -- null = no password
               PasswordVersion    int  not null default 0
               AliasFileName?     text
               ShowPreviewPage    bool not null default true
               RevokedAt?         timestamptz
               LastDownloadAt?    timestamptz
```

`DownloadEvent` is unchanged from M1: `{ Id, ShareLinkId, OccurredAt, IpHash, UserAgent }`. M4 adds no
columns to it, and §6.3 says why the columns a reviewer will reach for are absent on purpose.

Three index decisions carry weight:

- **`ShareLink.Slug` is unique globally, not per tenant.** A composite unique index on
  `(TenantId, Slug)` is the natural shape in a multi-tenant schema and it is wrong here: the public
  reader has no tenant (M1 §8), so two tenants holding the same slug means `FindBySlugAsync` returns
  two rows and serves whichever the query planner reached first — one tenant's file to another
  tenant's recipients.
- **Slugs are never reused.** Revocation sets `RevokedAt`; it never deletes the row, and slug
  generation must collide-check against every `ShareLink` ever created, revoked ones included.
  Otherwise a recipient's bookmarked `/d/kx91mz` silently starts serving a stranger's file months
  later.
- `DownloadEvent (ShareLinkId, OccurredAt DESC)` for the panel's activity block, and
  `DownloadEvent (OccurredAt)` for the retention sweep in §6.3.

`IsActive` stays as M1 defined it — the read path checks one cheap boolean — and `RevokedAt` is the
audit timestamp beside it. The redundancy is real, so a single domain method `ShareLink.Revoke(now)`
is the only thing permitted to write either field. Deriving `IsActive` from `RevokedAt` in Core and
dropping the column is cleaner, but M1's model is approved and this is not worth reopening it for.

## 3. Password-protected links

### 3.1 How the password is stored

A link password is a **shared secret**, not a user credential. Nobody authenticates as anybody; it is
one string a tenant types once and pastes into a chat alongside the URL. That difference tempts an
obvious shortcut — `HMAC-SHA256(pepper, password)`, one row, one microsecond — and the shortcut is
wrong for one reason that has nothing to do with our threat model:

**People reuse passwords.** The tenant who protects a client deliverable with `Ahmad1360` is
protecting a bank login with it too. A fast hash means anyone who obtains the `ShareLink` table gets
that string back in seconds. We would be leaking our customers' other accounts, not our own files.
Our own files are not what the hash protects anyway — an attacker with the database also has the Data
Protection keys (M1 §5 persists them there) and can decrypt the Google tokens directly. The slow hash
exists entirely to protect the customer from us.

**Decision: `PasswordHasher<ShareLink>` from `Microsoft.AspNetCore.Identity`, format v3
(PBKDF2-HMAC-SHA512, 128-bit salt, 256-bit subkey), `IterationCount` raised from the framework default
to 210,000** to match OWASP's current PBKDF2-HMAC-SHA512 figure. The type is already referenced by
Identity, it is versioned — `VerifyHashedPassword` returns `SuccessRehashNeeded` when the stored
parameters fall behind, and the gate rehashes in place on a successful verify — and it needs no new
package.

Rejected, with reasons:

| Option | Why not |
|---|---|
| `HMAC-SHA256` + server pepper | Fast. A six-character password falls in seconds if the table leaks, and takes the customer's reused password with it. |
| Argon2id | Better against GPUs, but it needs a third-party or native package, and its memory-hardness is a liability *here*: the verify runs on an anonymous public route, so 64 MiB × 20 concurrent guesses is 1.3 GiB of RAM on a box whose actual job is holding streams open. Memory-hard KDFs belong behind a login, not behind a URL anyone can hit. |
| Store it encrypted and compare plaintext | Would let the panel show the password back. See below — that is a feature request, not an accident, and the answer is no. |

Measure the verify on the OVH box and keep it near 100 ms of one core. That cost is a
denial-of-service lever, which is why §7.1's per-slug attempt limiter runs **before**
`VerifyHashedPassword`, not after.

**The password is write-only.** The design's masked field (`••••••••••`, links panel) is a state
indicator meaning "a password is set", not a masked value with a reveal button behind it. The panel
can replace it or clear it; it can never read it back. A tenant who forgets the password sets a new
one — which is also the honest UX, because the tenant is not the person who needs to remember it.

Minimum length 6, no composition rules. Composition rules on a secret that sits behind an unguessable
slug and a per-slug attempt limiter are friction with no defensive payoff.

### 3.2 The gate

`GET /d/{slug}` on a live password-protected link renders the gate **instead of** the preview card,
not above it. The handoff says «قبل از کارت، فرم رمز» — before the card — which reads as stacked; it
cannot be. The filename, the size, the thumbnail and the description are exactly what the password was
set to hide. Rendering the preview above the gate would leak all of it to anyone who opens the URL.

Built from components the design already ships:

- Same page shell: `max-width:760px`, `padding:26px 22px 60px`, brand header, FA/EN and theme toggles.
  Same footer note with `abuse@`.
- Same card: `border-radius:18px`, `1px solid var(--line)`, `var(--surface)`, `var(--shadow)`,
  `padding:26px`. **No `.ph` preview band** — a striped placeholder implies content behind it and is
  the wrong signal on a locked door.
- Badge, `--soft` / `--accent-ink`, radius 20, `4px 11px`, `11.5px`:
  «محافظت‌شده با رمز» / "Password protected".
- Title `24px/800`, generic, never the filename: «این فایل با رمز محافظت شده است» /
  "This file is password protected".
- Body `13.5px/1.9` `--muted`, `max-width:56ch`: «رمز را از فرستنده‌ی لینک بگیرید.» /
  "Ask the sender for the password."
- One input, `type="password"`, `dir="ltr"`, monospace `12px`, `1px solid var(--line)`,
  `border-radius:9px`, `background:var(--surface2)`, `padding:8px 11px` — the same field the settings
  panel uses for the password, deliberately, so the two ends of the feature look like each other.
- One button, the public CTA: `padding:15px 34px`, `15px/700`, `border-radius:12px`, `--accent`,
  `box-shadow:0 8px 20px -10px var(--accent)`. «مشاهده فایل» / "Unlock".
- Error line `12px` `--danger`: «رمز نادرست است» / "Incorrect password". No attempt counter — it tells
  an attacker how close the limiter is to firing, and a legitimate recipient does not need one. After
  lockout the text changes to «تعداد تلاش‌ها زیاد است؛ چند دقیقه دیگر تلاش کنید» / "Too many attempts;
  try again in a few minutes".

It is a real `<form method="post" action="/d/{slug}">`, not a `fetch`. M1 claimed the public page is
readable with JavaScript off and that claim has to keep being true when the page becomes interactive.
Antiforgery is validated, standard configuration; success is a **303 to `GET /d/{slug}`** so a refresh
does not re-post the password and the browser's history holds a GET.

One consequence worth stating because it looks like a privacy contradiction with §6.3: the antiforgery
token is a cookie set on an anonymous visitor. **It is issued only on pages that render a form** — a
recipient of an ordinary, non-gated link receives no cookie at all from Drive Union. It is never
logged, never written to `DownloadEvent`, and never joined to anything.

### 3.3 Remembering a passed gate, without a password in a URL

`GET /d/{slug}/file` is a separate request and must be authorized on its own. The password cannot ride
in the URL: query strings land in the reverse proxy's access log, in the `Referer` of any outbound
link on the page, in browser and OS history, in the clipboard when someone shares "the link that works
for me", and in every screenshot. A `?p=` parameter would undo the feature in about a day.

**Decision: a stateless, purpose-scoped, time-limited Data Protection token in a path-scoped cookie.**

```
Set-Cookie: du_gate=<protected>; Path=/d/{slug}; Max-Age=14400;
            HttpOnly; Secure; SameSite=Lax
```

- **Payload:** `{ shareLinkId, passwordVersion, issuedAt }`, protected with
  `provider.CreateProtector("DriveUnion.LinkGate.v1").ToTimeLimitedDataProtector()` and a four-hour
  lifetime. Data Protection rather than a hand-rolled HMAC because M1 §5 already persists DP keys to
  the database, so the token survives a redeploy and works across instances; a private key for this
  one feature would have the same persistence problem and someone would forget it.
- **`shareLinkId` in the payload** — a cookie lifted from link A cannot be replayed against link B,
  independently of the `Path` scoping. `Path` is a convenience (the browser sends one cookie per link,
  not thirty), not the control.
- **`passwordVersion`** is an integer on `ShareLink`, incremented whenever the password is set,
  changed, or cleared. A token whose version does not match the row is rejected. Without it, "I
  changed the password" would lock out nobody, which is the only reason anyone changes one.
- **No `Domain` attribute** — host-only, so the cookie never reaches a sibling subdomain.
- **`SameSite=Lax`, not `Strict`.** The recipient typically arrives at `/d/{slug}` by clicking a link
  in a messenger; that is a cross-site top-level navigation, which `Strict` would strip, showing the
  gate again to someone who unlocked it five minutes ago. `Lax` sends it. The download itself is a
  same-site navigation from our own page either way.
- **Nothing is bound to IP or User-Agent.** IP binding breaks a phone that switches from Wi-Fi to
  mobile data halfway through a 4 GB download — the resume lands on a different IP and gets a gate. UA
  binding breaks a download manager that does not copy the browser's UA. Neither is a real defence: an
  attacker who has the cookie is on the recipient's machine, and someone on the recipient's machine
  has the password too.
- **The cookie is re-issued on every successful authorized response**, including the streamed one (the
  header goes out with the 200, before the body). Four hours is therefore sliding, so a series of
  resumed range requests on a 214 GB file works indefinitely as long as they are under four hours
  apart. Without this, a large gated download that pauses overnight resumes into a gate.

Accepted risk, stated plainly: the cookie is a bearer credential for one link for up to four hours.
Whoever holds it has what whoever holds the password has. The password protects against strangers with
the URL, not against someone with the recipient's browser.

**Authorization order on both public routes, and it is not interchangeable:**

1. Resolve the slug. Miss → the unavailable card (§5).
2. Check `IsActive`, `ExpiresAt`, `MaxDownloads`. Any failure → the unavailable card.
3. Only now, if `PasswordHash is not null`, check the gate cookie; no valid cookie → render the gate.

Step 2 before step 3 is the whole point. A revoked password-protected link that rendered a gate would
tell a scanner "this slug was real once", and would tell a recipient whose access was deliberately
revoked that the file still exists.

### 3.4 What the gate changes about counting a download, and what it must not

M1's rule stands: **increment when the request carries no `Range` header, or when its range starts at
byte 0.** It exists so that one viewer scrubbing a video does not burn twenty of a customer's five
hundred downloads, and it is a unit test, not a comment.

The gate does not change what a download is. Two things around it need saying, and one is a narrowing
of the M1 rule that M4 owns because M4 owns the cap:

- **A failed gate never counts, and never writes a `DownloadEvent`.** Counting happens after
  authorization, not before.
- **`Range: bytes=0-0` does not count.** Media players and download managers routinely probe with a
  single-byte range to learn `Content-Length` and `Accept-Ranges`, then issue the real request. Under
  M1's rule as written, the probe starts at byte 0 and counts, and then the real download counts again
  — one playback, two downloads. The rule narrows to: *no `Range` header, or a range that starts at
  byte 0 and spans more than the first byte.* This is strictly tighter than M1's rule and is a fifth
  case in the same unit test.

Two further mechanics, because M4 is where the cap becomes load-bearing:

- **The cap check and the increment are one atomic statement**, not a read then a write:

  ```sql
  UPDATE "ShareLinks" SET "DownloadCount" = "DownloadCount" + 1, "LastDownloadAt" = @now
  WHERE "Id" = @id AND ("MaxDownloads" IS NULL OR "DownloadCount" < "MaxDownloads")
  ```

  Zero rows affected means the cap is reached — render the unavailable card. Read-modify-write loses
  increments on a popular link and makes `MaxDownloads` advisory; the design puts `۲۴۱/۵۰۰` in front
  of the customer, so it has to be true.
- **The increment happens before the first byte leaves.** A 200 cannot be un-sent, so the reservation
  must precede the stream. If the Drive request fails *before any byte is copied*, decrement and
  refuse; after the first byte, never. A client that closes the connection at 3% is indistinguishable
  from one that finished, and a decrement-on-abort rule would hand anyone unlimited downloads for
  free.

## 4. Alias filename

The toggle is «پنهان‌کردن نام اصلی فایل» with the subtitle «نمایش نام مستعار به گیرنده». The design
ships the toggle and no field for the name, so the field is specified here from the pattern the design
already established one section above: when the toggle is on, a text input appears directly beneath
its row, exactly as the password field appears beneath the password row — same
`1px solid var(--line)`, `border-radius:9px`, `background:var(--surface2)`, `padding:8px 11px`,
`12px`. Unlike the password field it is `dir="auto"`, because Persian aliases are the normal case here.

**Where the alias replaces the real name:** the `<h1>` on the public card, the page `<title>`, any
`og:title`, and the `Content-Disposition` of `/d/{slug}/file`. Nowhere in the panel — the tenant sees
the real name with the alias beneath it in `11px` `--muted` monospace.

**The alias never changes the file's type.** If the alias carries no extension, the real file's
extension is appended; if it carries a different one, the real extension is appended anyway
(`report.doc` on a PDF becomes `report.doc.pdf`). `Content-Type` always comes from
`StoredFile.MimeType`. Two reasons: a recipient who receives `photo.pdf` containing JPEG bytes has a
broken download and blames us, and a tenant who can choose an arbitrary extension while we serve an
arbitrary content type has a small, free malware-delivery affordance on a public host.

**Sanitisation, before storage and again before the header.** The alias is a tenant-controlled string
that ends up in an HTTP response header on an anonymous route, which makes it the highest-risk string
in this slice:

- Reject `CR`, `LF`, and `NUL`. Header injection is the actual severity here; everything else is
  cosmetic.
- Strip path separators (`/`, `\`) and the `..` sequence.
- Strip Unicode bidirectional overrides and embeddings — **U+202A–U+202E, U+2066–U+2069, U+200E,
  U+200F**. This is not theoretical decoration: an override before `gpj.exe` renders as `exe.jpg` in a
  download list, and this is a Persian-language product where a filename containing bidi marks looks
  entirely normal to a reviewer.
- Strip other C0/C1 control characters, collapse whitespace, trim leading and trailing dots and spaces
  (a trailing dot or space is a Windows filename trap).
- Cap at 120 characters after sanitisation; if nothing survives, reject the save with a field error
  rather than silently falling back to the real name — a silent fallback means a tenant believes the
  real name is hidden when it is not.

**`Content-Disposition`.** Persian filenames are the common case (`دفترچه-راهنما-نسخه۳.pdf` is in the
design's own sample data), so the header must be RFC 6266/5987, not a raw UTF-8 `filename=`:

```
Content-Disposition: attachment; filename=file.pdf; filename*=UTF-8''%D8%AF%D9%81...
```

Build it with `Microsoft.Net.Http.Headers.ContentDispositionHeaderValue` and `SetHttpFileName`, which
emits both forms and ASCII-sanitises the fallback. Do not hand-format the header; hand-formatting is
how the CR/LF above gets back in.

**`ShowPreviewPage`** («صفحه پیش‌نمایش — به‌جای دانلود مستقیم») interacts with both features and is
specified here because the panel it lives on is this slice's. When it is off, `GET /d/{slug}` streams
the file directly instead of rendering the card. Combined with a password that would leave the gate
nowhere to live, so: **with a password set and preview off, `GET /d/{slug}` renders the gate until a
valid gate cookie is present, and streams directly once it is.** The "render or stream" decision is
one function, and both the counting rule of §3.4 and the alias `Content-Disposition` apply to whichever
route emits bytes.

## 5. Revoke, and the four refusals that must look the same

Revoking is `ShareLink.Revoke(now)` — `IsActive = false`, `RevokedAt = now`. Not a delete: the
`DownloadEvent` rows and the `DownloadCount` are the tenant's record of what happened before they
pulled the plug, and §2 needs the row to keep the slug reserved for ever.

It takes effect on the next request, which means **nothing on the public path may be cached**.
`GET /d/{slug}` and `GET /d/{slug}/file` both send `Cache-Control: no-store` (`private, no-store` on
the stream). A CDN or a corporate proxy holding a preview page defeats revocation; holding a 214 GB
body defeats the download cap entirely. This costs the offload a CDN would give, which is a cost M1
already accepted — the product's stated promise is «دانلود مستقیم و استریم‌شده از سرور ما».

While we are here: `/d/*` sends `X-Robots-Tag: noindex, nofollow` and `robots.txt` disallows `/d/`.
M1 §7 argued for server-rendered HTML partly on SEO grounds; that argument was about rendering quality
and server-side language negotiation, not about wanting private links in a search index. A «لینک
اختصاصی» that Google has indexed is not one.

### The four refusals

**Unknown slug · revoked · expired · download cap reached — one identical response.** M1 established
it; M4 must not erode it while adding three new ways to reach it.

Identical means:

- **The same status code: 404 for all four.** Not 410 for expired, not 403 for the cap. A distinct
  status is the cheapest possible oracle and the easiest to add by accident, because 410 genuinely is
  the semantically correct code for "expired" and someone will suggest it in review.
- **The same body, byte for byte.** No slug echoed, no filename, no size, no expiry date, no "expired
  3 days ago", no thumbnail. The only variable is FA vs EN, which the requester controls through
  `Accept-Language`/`?lang=` and therefore cannot learn anything from.
- **The same headers.** No `Set-Cookie` on one branch and not another, no differing `Cache-Control`,
  no `Vary` difference.
- **The same work.** The refusal is decided from the `ShareLink` row alone — before any join to
  `StoredFile`, before any Drive call, before any thumbnail fetch. Constant-time comparison of
  database lookups is not a thing worth pretending to do; not performing an extra network round trip
  on the "it existed" branch is, and it is the only timing difference large enough to measure over the
  internet.

The card, per the handoff's «همان کارت با آیکن خنثی، عنوان "این لینک دیگر در دسترس نیست"، بدون CTA»:
the 18px card, the 230px band filled with `var(--surface2)` instead of the `.ph` stripes and holding
one `48px` `--muted` glyph `⊘`, title `24px/800` «این لینک دیگر در دسترس نیست» / "This link is no
longer available", and one body line:

> «ممکن است منقضی شده باشد، به سقف دانلود رسیده باشد، یا لغو شده باشد.»
> "It may have expired, reached its download limit, or been revoked."

That sentence is a security control, not copy. It names every cause without identifying which one,
which is what lets support answer «چرا؟» honestly without the page becoming an oracle. No metadata
bar, no CTA, and **no card footer** — the footer on the live card carries the slug and the download
count, and the count would leak.

### What a change here leaks

Three concrete regressions, so that a future reviewer can price the suggestion before accepting it:

1. **410 for expired, 404 for unknown.** A scanner now separates slugs that were ever real from noise.
   Combined with a six-character slug space (§7.1), that is a target list — and it also tells a
   recipient whose access was deliberately revoked that the file is still there, which is a social
   problem as much as a technical one.
2. **"This link expired on ۱۴۰۵/۰۵/۳۱".** Confirms the slug exists and hands over a timestamp that
   correlates with the tenant's activity. A handful of these across a tenant's links is a usable
   picture of when that business ships things.
3. **Filename or thumbnail on the cap-reached card.** The download cap exists so that the 501st person
   gets nothing. A document's title is frequently the sensitive part — `Q3-Report-Final.pdf`,
   `contract-v7.docx` — so showing it gives away most of what the cap was protecting.

## 6. Download analytics

### 6.1 What is recorded

**One `DownloadEvent` per counted download, and nothing else.** Range continuations, resumes, probes,
gate attempts and gate passes write no rows. A download manager pulling a 214 GB file with eight
parallel ranges must produce one event, not eight, or «۲۴۱ بار دانلود شده» on the public card is a lie
and the events table grows with seek behaviour instead of with usage. The consequence is a pleasant
one: `DownloadEvent` stays exactly 1:1 with `ShareLink.DownloadCount`, so the denormalised counter M1
introduced is verifiable by a `COUNT(*)` and any drift is a bug with an obvious test.

`IpHash` — the shape matters more than it looks:

```
IpHash = HMAC-SHA256(key: Analytics:IpPepper, message: shareLinkId || '\0' || normalizedIp)[0..16]
```

- **The pepper lives in configuration, never in the database.** M1 puts the Data Protection keys in
  the database, so using Data Protection here would mean a single database dump reverses every hash. A
  config secret makes a database-only leak useless.
- **`shareLinkId` is mixed in**, so the same visitor produces a different hash on every link. Within
  one link we can still see "the same client took this 400 times", which is the only question abuse
  response actually asks. Across links and across tenants nothing joins, so no visitor graph exists to
  be built, subpoenaed, or leaked.
- **Call it a pseudonym, not anonymisation.** IPv4 is 2³² values; anyone holding the pepper can
  brute-force a hash back to an address in seconds. What this buys is real but bounded: the panel and
  the API can never show a raw address, and the events table alone is inert. Claiming more than that
  on a privacy page would be false.
- Rotating the pepper severs every historical hash from every new one and breaks unique counts across
  the boundary. Treat it as permanent.

`normalizedIp` depends on a piece of configuration that fails silently if it is missed:
`ForwardedHeadersOptions` must list the reverse proxy in `KnownProxies`/`KnownNetworks`. The default
trusts loopback only, so behind nginx in a container every request appears to come from the container
gateway — one `IpHash` for the entire internet, and, worse, a per-IP rate limiter that is actually a
single global bucket. Nothing errors; the numbers are just wrong.

`UserAgent` is truncated to 200 characters and stored as received. It is the highest-entropy field in
the row, so the panel never shows it raw — the tenant sees a coarse class (Desktop / Mobile / Bot)
derived from it. The raw string stays available to the operator, because an abuse report that says "a
scripted client took this 4,000 times" needs it.

### 6.2 What the panel shows

Made real, from indicators the design already draws:

- **File details panel** — the three monospace indicators `۲۴۱ / ۵۰۰ دانلود`, `انقضا ۱۲ روز`,
  `رمزدار`. The third is currently decorative; M4 makes it reflect `PasswordHash is not null`.
- **Links table** — a `--soft`/`--accent-ink` pill reading «رمز» after the file name when a password
  is set, and the alias on a second line in `11px` `--muted` monospace when one is set. The `وضعیت`
  column gains «باطل‌شده» in `--muted` alongside the design's existing «فعال» / «نزدیک سقف» /
  «غیرفعال».
- **Dashboard** — «لینک‌های پربازدید امروز» is a `COUNT(*)` over `DownloadEvent` for the current day,
  grouped by link, top three. This is the only place the events table is read on a hot path; the
  `(ShareLinkId, OccurredAt DESC)` index covers it.
- **New: an activity block in the link settings aside**, above the two buttons, and no new screen. A
  three-cell strip using the public card's separator trick (`gap:1px` over a `--line` background,
  cells `var(--surface)`, `padding:13px 15px`, label `11px --muted`, value `13.5px/700` monospace):
  «۷ روز اخیر» total, «یکتا» distinct `IpHash` count, «آخرین دانلود» relative time from
  `LastDownloadAt`. Beneath it, seven bars reusing the dashboard traffic chart's markup at half height
  (`48px` container, `gap:5px`, `border-radius:4px 4px 0 0`), `--soft` with the tallest bar in
  `--accent`.
- **Beside the cap slider**, the implied egress in `11px` `--muted`: «۵۰۰ × ۱۸.۴ MB ≈ ۹.۲ GB». A
  label, not a mechanism — see §7.2.
- **The public card footer** keeps «۲۴۱ بار دانلود شده» as designed, showing `DownloadCount` only. It
  must not become `۲۴۱/۵۰۰`: publishing the remaining headroom invites a race to exhaust it, and it
  tells a recipient something about the tenant's intent that the tenant did not choose to say.
  (Whether the total should be public at all is §11.6.)

### 6.3 What is deliberately not recorded

This is a decision, not an omission. Drive Union's customers share links with clients, contractors,
lawyers and journalists. A per-download log of who fetched what and from where is a subpoena target and
a breach liability, and it buys nothing the counts do not already buy for abuse response. So:

- **No raw IP address**, in any table, log line, API response, or exported report. The application
  never writes one. (The reverse proxy's own access log is a separate decision with a separate
  retention number — §11.3.)
- **No `Referer`.** It reveals where a link was shared: the private group, the internal wiki URL, the
  client's unreleased staging site. That is the sender's and the recipient's context and we have no
  product use for it.
- **No geolocation, country, or ASN.** Deriving it means either a third-party lookup — data leaving
  the German box, which the handoff explicitly forbids for fonts and means here too — or shipping and
  updating a geo database, for a column nothing in the design displays.
- **No third-party analytics, no tracking pixel, no external script on `/d/*`.** The public card
  promises «بدون تبلیغ، بدون انتظار»; a beacon would make that a half-truth.
- **No cookie on a link that does not need one.** Only gated links set cookies (§3.2, §3.3), and
  neither cookie's value is recorded anywhere.
- **No byte-level or per-range logging.** Counted downloads only.
- **No account or identity of the recipient.** There isn't one and M4 does not invent one.
- **No indefinite retention.** `DownloadEvent` rows older than **180 days** are deleted by a nightly
  sweeper. `ShareLink.DownloadCount` and `LastDownloadAt` survive, so the tenant's «۲۴۱ دانلود» stays
  correct for ever while the per-event detail expires. The aggregate is a product feature; the detail
  is a liability with a shelf life.

The sweeper runs with **no HTTP context and no tenant**. M1 §8's whole argument was that a global query
filter turns sessionless work into work that reads an empty table and reports success — the sweeper
would run nightly, delete nothing, log nothing, and be discovered a year later. M1's explicit
`tenantId` arguments make this a compile error instead, provided the sweeper's repository method is
written without one. It is a deletion path, so it also gets a test that asserts a non-zero delete count
against seeded old rows; a sweeper that deletes nothing must not be able to look like a sweeper that
had nothing to do.

## 7. Abuse controls

M1 ships a per-IP rate limit on `/d/*` and calls it "the only anonymous, expensive, publicly-guessable
route in the product". That limiter counts requests. The three things that hurt a public file host are
not request rates.

### 7.1 What M4 adds

**1. A per-slug gate-attempt limiter, keyed by slug rather than by IP.** Ten failed verifications per
slug per fifteen minutes; over the limit the gate re-renders with the lockout message and
`VerifyHashedPassword` is never called, so a guessing campaign cannot also be a CPU denial-of-service
via §3.1's deliberately slow hash. Keyed by slug because a distributed guessing attack is precisely
what a per-IP limiter cannot see. In-memory is sufficient on a single box, and a restart resetting the
window is an acceptable loss. The trade-off is real and accepted: an attacker can lock a legitimate
recipient out of one link for fifteen minutes. That is why the window is short, and why the links table
surfaces «۱۰ تلاش ناموفق در ۱۵ دقیقه‌ی اخیر» to the tenant, who is the person who can actually respond
by revoking and re-sharing.

**2. A global miss-rate circuit breaker against slug enumeration.** The design's slugs are six
lowercase alphanumerics: 36⁶ ≈ 2.18 × 10⁹. With ten thousand live links, roughly one guess in 218,000
hits something. A hundred hosts each running at M1's per-IP limit find a live link about every
thirty-six minutes — cheap enough that someone will do it. So: count `/d/*` requests that resolve to a
refusal, globally, per minute. Above a threshold (start at 100/min and tune against real traffic), add
a fixed **one-second delay to every refusal response** and alert the operator.

A delay rather than a block, because IP-blocking is useless against a botnet and blocks a corporate NAT
for everyone behind it, while a second per miss destroys a scanner's throughput and costs a human who
mistyped a link exactly one second. **The delay applies to all four refusal states, never only to
unknown slugs** — applying it selectively would rebuild the §5 oracle out of a defence.

**3. A concurrency cap on public streams — the one M1 genuinely lacks.** The scarce resource is not
requests per minute, it is simultaneous multi-gigabit streams; three requests can pull 600 GB while
sitting far under any request-rate limit. `AddConcurrencyLimiter` on `/d/{slug}/file`, partitioned per
link (start at 6) and globally (start at 200, sized to the box's egress and revisited in M6 when the
traffic chart is real). Over the limit returns **503 with `Retry-After`, not the unavailable card** —
this is a capacity signal, and dressing it as a link state would tell a legitimate recipient their
working link is dead.

**4. An operator kill-switch.** The public card publishes `abuse@yourdomain.com`, which is a promise
that someone can act. The operator gets the ability to revoke any link and mark the underlying
`StoredFile` blocked, across tenants, from the existing links table. Keep the abuse contact a
`mailto:` — a report form on a public page is a spam target that would need its own captcha, which
§7.2 rejects.

### 7.2 What is over-engineering at this stage

- **A CAPTCHA on the gate.** Every usable one is a third-party script, which contradicts the handoff's
  no-foreign-CDN rule and the card's own «بدون تبلیغ، بدون انتظار». The per-slug limiter already bounds
  guessing.
- **Malware scanning of uploads.** A real obligation for a public host eventually, and genuinely not
  now: a paid API means customer data leaving Germany, and ClamAV over a 214 GB file is hours of CPU
  per upload. The interim control is the operator kill-switch plus a working `abuse@`. This is deferred
  on cost, not dismissed on principle — see §11.2.
- **IP reputation, Tor and VPN blocking, per-country blocking, bot fingerprinting, a WAF ruleset.**
  Each blocks real users — a product sold in Persian to people who routinely reach the internet through
  a VPN cannot treat a VPN as a signal — for an attack the limiters above already price.
- **Signed, expiring URLs for `/d/{slug}/file` on non-gated links.** The slug is already the secret;
  signing moves the secret without shrinking it.
- **A per-link egress budget in bytes.** `MaxDownloads × SizeBytes` is that budget, already exists, and
  is already in the design. It becomes useful by being *displayed* next to the slider (§6.2), not by
  growing a second mechanism that can disagree with the first.
- **A CDN in front of `/d/*`.** Named here because it is the most likely well-meaning suggestion: it
  breaks revocation, the download cap, and the counting rule in one move (§5).

## 8. The anonymous path, again

Everything M4 adds to `/d/*` runs without a session, and three of them are now *writes*:
`DownloadCount`, `DownloadEvent`, and the rehash-on-verify from §3.1.

M1 §8's design holds — no global query filter, explicit `tenantId` arguments, and a separate
`IPublicLinkReader` with no tenant concept — and M4 extends the public seam rather than reaching around
it:

- `IPublicLinkReader.FindBySlugAsync(slug, ct)` returns everything §3.3's authorization order needs
  from one row: state, expiry, cap, `PasswordHash`, `PasswordVersion`, `AliasFileName`,
  `ShowPreviewPage` — with the `StoredFile` join deferred until after the refusal decision (§5).
- A matching `IPublicLinkWriter` with three methods — `TryReserveDownloadAsync`, `RecordDownloadAsync`,
  `UpdatePasswordHashAsync` — none of which take a `tenantId`, because there is nobody to take one
  from.
- M1's anonymous integration test that fetches `/d/{slug}` and expects 200 is extended to cover the
  gate POST and the streamed file, so the M4 code paths are all exercised with no session present.

## 9. Frontend surface

Two of M1's five islands grow; no new island and no new screen.

| Island | M4 additions |
|---|---|
| `linkSettings` | Password toggle + write-only field, alias toggle + field, preview-page toggle, revoke with confirmation, the activity block of §6.2 |
| `publicDownload` | The gate card, the unavailable card, and the states that pick between them |

Revocation is `POST /api/links/{slug}/revoke`, deliberately not a field on `PATCH /api/links/{slug}`.
It is irreversible in effect — the slug is burned for ever (§2) — and it should not be reachable by a
partial update that a stray save could carry. In the panel it is the outline button in `--danger` the
design already draws, behind a confirmation that names the file and says the address will never be
reusable.

`GET /api/links/{slug}/activity?days=7` feeds the activity block. It returns counts and a coarse device
breakdown; it never returns `IpHash` values, because a hash the tenant can see is a pseudonymous
identifier the tenant can correlate, which is the thing §6.3 declined to build.

The gate and the unavailable card are server-rendered Razor, not island content. `publicDownload` stays
what M1 made it — the theme and language toggles — so the page still works with JavaScript off.

## 10. Tests that hold these lines

1. All four refusal states — unknown, revoked, expired, cap-reached — return 404 with byte-identical
   bodies and identical header sets. Parameterised, comparing the full response.
2. A revoked *password-protected* link renders the unavailable card, not the gate. (§3.3's ordering.)
3. A gate cookie minted for link A does not authorize link B.
4. Changing a link's password invalidates an outstanding gate cookie. (`PasswordVersion`.)
5. Counting: no `Range` counts; `bytes=0-` counts; `bytes=500-` does not; `bytes=0-0` does not; a
   failed gate does not.
6. Fifty concurrent requests against a link with `MaxDownloads = 10` yield exactly 10 successes and
   `DownloadCount = 10`.
7. `Content-Disposition` for a Persian alias round-trips through `ContentDispositionHeaderValue`,
   contains no CR or LF, and carries both `filename` and `filename*`.
8. An alias containing U+202E is rejected before storage.
9. Anonymous `GET /d/{slug}`, `POST /d/{slug}`, and `GET /d/{slug}/file` all succeed with no session
   and no authenticated user. (M1 §8's line, re-asserted against every new route.)
10. No `DownloadEvent` column value parses as an `IPAddress`. Crude, and it is the regression guard
    that catches the day someone "temporarily" logs a raw address.
11. The retention sweeper deletes seeded 200-day-old rows and returns a non-zero count.

## 11. Before implementation starts

Six things from the owner. The first blocks slug generation; the fourth blocks the first analytics
write; the rest block launch, not the first commit.

1. **Slug length for newly generated links.** The design shows six characters (`/d/kx91mz`), which is
   36⁶ ≈ 2.18 × 10⁹ — a hundred scanning hosts find a live link roughly every thirty-six minutes at ten
   thousand live links (§7.1). Ten characters makes the same botnet take about a hundred and fifteen
   years, and changes nothing except the width of a monospace string the design puts on screen in five
   places. Recommendation: ten. Custom slugs stay the tenant's choice either way, with a warning in the
   panel when one is shorter than eight characters.
2. **`abuse@` — who receives it, and the takedown SLA.** The public card promises the address; a
   German-hosted public file host needs a named human behind it and a response time. This is also where
   the deferred malware-scanning question from §7.2 belongs, along with whether hash-matching against
   known-bad content lists is a legal requirement in the jurisdictions this will serve. That is a
   programme, not a checkbox, and it needs the owner's answer rather than an engineering guess.
3. **Two retention numbers.** `DownloadEvent` at 180 days (§6.3) is a proposal, not a decision. The
   reverse proxy's own access log is the second, because nginx logs raw IPs by default and the
   application's privacy stance means nothing if the box beside it keeps them for ever. Whatever the
   numbers are, the sweeper's schedule must not be overridable by an untracked deployment override — a
   retention setting that quietly keeps everything is indistinguishable from a sweeper that runs and
   finds nothing to do.
4. **A home for `Analytics:IpPepper`.** Thirty-two random bytes that must live in configuration rather
   than the database (§6.1) and must never be rotated casually. Whatever secret store the OVH
   deployment uses, this is its first tenant.
5. **The link password policy.** Proposed: minimum six characters, no composition rules. Anything
   stronger is friction on a secret that gets pasted into a chat next to the URL, and whose real defence
   is the per-slug limiter.
6. **Whether the public card keeps «۲۴۱ بار دانلود شده».** It is in the approved design and it tells
   every recipient how widely the file was shared — some customers will read that as a leak.
   Recommendation: keep it, and add a per-link toggle later if anyone objects. But the answer is wanted
   now, because removing it after launch is a visible change to a design the owner signed off.

## 12. Deliberately not in M4

Per-recipient links and named recipient access — that is an identity feature, and M5's roles are for
members of a tenant, not for the people they send files to. Email or SMS one-time codes on the gate,
for the same reason plus infrastructure that does not exist yet. Download notifications («کسی فایل شما
را دانلود کرد»), which are genuinely wanted and would turn §6's deliberately thin event log into a
notification stream feeding an email pipeline nothing has built.

Watermarking, view-only preview with downloading disabled, and any form of document DRM: all of them
are defeated by a screenshot, and none survive the fact that `/d/{slug}/file` must stream real bytes.

Geography, ASN, referrer source and funnel reporting in analytics (§6.3 says why). Raw `IpHash` values
in any API the tenant can reach. Per-link egress budgets in bytes, CAPTCHAs, malware scanning, IP
reputation, and signed URLs for non-gated links (§7.2). A CDN in front of `/d/*` (§5).

Bulk operations across links, link collections or folders, custom per-tenant domains, and public upload
or request-a-file links. Per-tenant download-cap billing, which M1 §12 already flagged as unscoped
everywhere — M4 makes it more pressing, because a link with `MaxDownloads = ∞` on a 214 GB file is now
a fully supported, fully controllable way for one customer to move a hundred terabytes of someone
else's egress.
