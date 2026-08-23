# Drive Union — Telegram

**Date:** 2026-08-24 · **Status:** design proposed; blocked on §14 before the first line of
implementation · **Depends on:** M1 only. §13 lists what it touches in M2–M6 and redesigns none of it.

## 1. The brief, and the two sentences it turns into

The owner's words:

> «میخواهم بخش تلگرام هم اضافه کنم یعنی یه ربات داشته باشه که ادمیت توکنشو میده همون اپراتور
> و یوزر میتونه اکانت تلگرامشو رجیستر کنه داخل سایت و فایل هارو مثلا داخل تلگرام بفرسته و اینجا
> براش اپلود بشه یا بخواهد فایل های داخل گوگل درایوش براش داخل تلگرام اپلود و ارسال بشن
> و بخش تلگرامش هم باید خیلی شکیل و تمیز قابلیت مدیریت داشته باشه و بتونه از همونجا هم کاراشو
> بکنه از پنل هم بتونه»

Four requirements, and one of them is a constraint the others have to live inside:

1. **The bot token is the operator's.** One bot for the whole product, configured once, exactly as the
   Google OAuth client is. Not one bot per tenant.
2. **A customer registers their Telegram account on the site.** The panel is where the binding starts.
3. **Both directions.** Send a file to the bot and it lands in the customer's storage here; ask for a
   file that is already here and the bot delivers it in Telegram.
4. **«شکیل و تمیز» — well-made and clean, and usable from the chat as well as from the panel.** In a bot
   that means few commands, obvious state, and no dead ends. It does not mean many features.

Two product rules arrive from M1 §1 unchanged and constrain every line below:

- **A customer must never learn that a pool of Google accounts exists.** The bot is a new surface with a
  new way to leak it — an error string, a "which account" hint, a file-id-shaped token in a message. The
  bot never says Google, never says Drive, never prints an account short code or email. §5 makes that a
  test rather than a habit.
- **`/d/{slug}` is anonymous, and there is no global query filter** (M1 §8). Every Telegram update
  arrives with no session and no cookie, which is the same shape of problem in a place it has never had
  to hold before. §5 is the whole of that argument.

## 2. The size ceiling, first, because it decides the product

Telegram's Bot API caps file transfer far below what this product exists to hold. The M1 handoff's own
sample data has an 18.4 MB PDF, a 214 GB archive and an 812 GB image, on 5 TB accounts. The bot can
carry the first one. It cannot carry the other two, and no amount of engineering changes that.

A feature that silently fails on the product's typical file is worse than one that says up front what it
can carry. So this section establishes the real numbers, marks the confidence of each, and §3 designs
for the truth rather than around it.

### 2.1 The numbers

Read today against the Bot API reference at **Bot API 10.2, dated 14 July 2026**. The changelog carries
no entry from Bot API 7.0 (December 2023) through 10.2 that changes any of these figures.

| Operation | api.telegram.org | Self-hosted server | Confidence |
|---|---|---|---|
| Bot downloads a file a user sent it (`getFile`) | **20 MB** | no documented limit | **High.** Verbatim from the `getFile` description: *"For the moment, bots can download files of up to 20MB in size."* Restated in the Bots FAQ: *"this will only work with files of up to 20 MB in size."* |
| Bot sends a file by uploading bytes (`multipart/form-data`) | **50 MB** | **2000 MB** | **High.** Bots FAQ, verbatim: *"Bots can currently send files of any type of up to 50 MB in size."* Local-server section, verbatim: *"Upload files up to 2000 MB."* |
| Bot sends a file by handing Telegram an **HTTP URL** | **20 MB**; photos **5 MB** | — | **Medium.** The reference page truncates before its "Sending files" section and I could not read it verbatim from here. Several independent renderings agree on 5 MB / 20 MB, and one adds that `sendDocument` by URL works only for a short list of types (PDF, ZIP, GIF). §14.8 asks for one live call to settle it. |
| Bot re-sends a file already on Telegram's servers (`file_id`) | **no size limit** | no size limit | **Medium-high.** Documented under "sending by file_id" with no size clause and no counter-example found. `file_id` is unique per bot and cannot be transferred between bots. |
| What a *user* can send in the first place | **2 GB free / 4 GB Premium** | same | **Low-medium — needs confirmation.** No official Telegram page was read for this. Third-party sources dated 2026 agree, and grammY's documentation states a local server allows *"downloading files of any size (4000 MB with Telegram Premium)"*, which is consistent. It matters only for §2.3. |

**Whether 20 MB and 50 MB are decimal or binary is not stated anywhere I could read.** Enforce the
decimal reading — `20_000_000` and `50_000_000` — which is the same choice and the same reasoning as
M2 §9 made on the 750 GB day: if the real limit is binary we leave about 5% unused, which is cheap, and
the reverse costs a rejected send and a customer who cannot see why.

Two more facts that shape the code rather than the product:

- **A `getFile` result is perishable and it contains the bot token.** The download URL is
  `https://api.telegram.org/file/bot<token>/<file_path>` and the reference says *"It is guaranteed that
  the link will be valid for at least 1 hour."* That URL is the same class of secret as a Drive
  resumable session URI (M1 §6): it is never logged, never persisted, never put in a response, and never
  handed to a browser. `file_path` is not stored.
- **`callback_data` on an inline button is 1–64 bytes.** §8.3 is written around it.

### 2.2 The URL escape hatch does not exist

The obvious idea — do not upload 40 MB from OVH, hand Telegram the public
`https://<domain>/d/{slug}/file` URL and let Telegram fetch it — is worse, not better. Sending by URL
caps at 20 MB rather than 50 MB, and it appears to be restricted to a handful of content types for
`sendDocument`. It also punches a hole through M4: the URL is fetched by an anonymous client that would
consume one of the link's `MaxDownloads`, write a `DownloadEvent` with Telegram's address behind
`IpHash`, and — for a password-protected link — simply fail.

It is named here because it is the first thing a reviewer will suggest, and because the reason it is
wrong is not obvious from the outside.

### 2.3 Running our own Bot API server — a real option, with a real bill

Telegram open-sources the Bot API server (`tdlib/telegram-bot-api`). Run with `--local`, the documented
gains are, verbatim:

> "Download files without a size limit." · "Upload files up to 2000 MB." · "Upload files using their
> local path and the file URI scheme." · "Use an HTTP URL for the webhook." · "Use any local IP address
> for the webhook." · "Use any port for the webhook." · "Set *max_webhook_connections* up to 100000."
> · "Receive the absolute local path as a value of the *file_path* field without the need to download
> the file after a getFile request."

What that buys: outbound goes from 50 MB to **2000 MB**, a factor of forty. Inbound goes from 20 MB to
whatever the sender's own client could send — 2 GB, or 4 GB with Premium (§2.1, unconfirmed).

What it costs, and none of these is a footnote:

- **An `api_id` / `api_hash` from my.telegram.org, which is issued against a personal Telegram account,
  not against a bot.** That is an ownership question before it is a technical one: whoever's account
  issues it is the person the server is registered to. §14.2.
- **A C++ build and a service to keep alive.** TDLib as a submodule, CMake, gperf, OpenSSL, zlib. Not
  hard; not nothing; and this machine has no Docker (M1 §4), so it can only be built and verified where
  it will run.
- **The server speaks HTTP only.** A TLS terminator has to sit in front of it for anything remote. On
  this box nginx is already there, so the marginal work is one server block — but it is one more thing
  that can be misconfigured into exposing an unauthenticated Bot API server to the internet, which is a
  full compromise of the bot.
- **Disk.** The local server writes every file it handles to its own directory, and the README documents
  no automatic deletion. A product whose files are measured in gigabytes would fill that volume, and it
  is the same volume M3 §11 already reserves 2 × 1.31 GiB of spool on. Sweeping it is our problem.
- **`logOut` is a one-way door for ten minutes.** Moving a bot to a local server requires calling
  `logOut` on the cloud API first; after that the bot can log in locally immediately but **cannot return
  to the cloud server for ten minutes**. Being logged in on two servers at once loses updates. So the
  migration is a short, deliberate outage, not a toggle.

**Recommendation: the cloud API for the first slice, and self-hosting as a later, separately-decided
change.** Two reasons. First, 2000 MB still does not carry a 214 GB file, so self-hosting does not
change the *design* — §3's answer for an oversized file is needed either way, and only its threshold
moves. Second, the threshold is one configuration value and one client base URL: `Telegram.Bot` takes a
`baseUrl` on `TelegramBotClientOptions` and exposes a `LocalBotServer` flag for the behaviour change in
`getFile`. Building against the cloud API and moving later is a config change plus a `file_id` cache
invalidation (§4), not a rewrite.

The seam that keeps it cheap: **`Telegram:MaxSendBytes` and `Telegram:MaxReceiveBytes` are configuration,
never constants in code**, and no code path anywhere else knows what they are.

## 3. What happens to a file that is too big, in each direction

This is the section the owner has to agree with, because it is the visible compromise.

### 3.1 Outbound — the bot hands over a link, and it decides before anyone waits

`StoredFile.SizeBytes` is known before a single byte moves. So the decision is made when the file card is
*rendered*, not when the button is pressed:

- **Under `Telegram:MaxSendBytes`:** the card shows «ارسال فایل». Pressing it queues an outbox item and
  the document arrives in the chat.
- **At or over it:** the «ارسال فایل» button **is not rendered**, and the card's second line reads
  «بزرگ‌تر از سقف تلگرام — با لینک بفرستید». The «ساخت لینک» button is right beside it.

Absent, not disabled. M5 §7 already set that rule — a capability you do not have is absent, a condition
you can fix is disabled — and a file's size is not a condition the user can fix.

**The link is not a consolation prize, and the copy should not apologise for it.** A share link is what
this product is sold on (M1 §7): streamed, ranged, resumable, revocable, capped, and counted. A 3 GB
file delivered as a link is a *better* outcome for the recipient than a 3 GB file delivered as a Telegram
document, because it resumes. The bot says what it is doing in one line and moves on.

### 3.2 Outbound, the free lunch: cache the `file_id`

When we do upload bytes, Telegram's response carries a `file_id`. Re-sending that file to any chat is
then `sendDocument` with the `file_id` — **no size limit, no bytes leaving OVH, no Drive read, no
egress, and no daily-quota consumption**. It is the single largest performance decision in this slice
and it is free.

`TelegramFileId` (§7) caches it, keyed on `(StoredFileId, BotUserId)`. Keyed on the bot because a
`file_id` is unique per bot and cannot be transferred to another one: pointing the panel at a different
token must produce a cache *miss*, not a wrong send.

Two consequences a reviewer should be told about rather than discover:

- **A cached re-send writes no `DownloadEvent` and does not move `ShareLink.DownloadCount`,** because no
  byte left this box and no link was involved. The counters stay honest about what they mean (M4 §6.1);
  they simply do not count this.
- **The bytes stay on Telegram's servers for as long as Telegram keeps them.** That is the property the
  cache exploits, and it is also a data-residency fact the product did not previously have. §14.7.

### 3.3 Inbound — 20 MB, and an honest bridge for everything else

A user sends the bot a file. The update carries `document.file_size` (or `video`/`audio`/`photo`
equivalents) before we fetch anything, so again the decision is made before anyone waits:

- **Under `Telegram:MaxReceiveBytes`:** `getFile`, then stream the response body straight into a Drive
  resumable session through `IUploadCoordinator`. No spooling — the M1 §6 rule holds without effort here,
  because at 20 MB the whole thing is a single final chunk and the 256 KiB alignment rule does not apply.
- **At or over it:** one reply naming the limit and linking to `{PublicBaseUrl}/Files/Upload`, which is
  the panel's chunked uploader and already carries 96 GB files.

`file_size` is optional in the API and may be absent. Treat absent as unknown, **and enforce the ceiling
with a byte counter on the copy anyway**, aborting the session past it. A declared size is a claim
(M5 §7 makes the same argument about `POST /api/uploads`), and here the claim comes from a third party.

**A token-authenticated one-time upload URL — so the phone can upload without signing in — is explicitly
not built.** It would be a bearer credential that writes into a tenant's storage and spends their cap,
which M5 §5 already identifies as the single most dangerous handle in the product. It deserves its own
design, not a paragraph in this one. §15.

## 4. Decomposition

Telegram depends on M1 and nothing else. It can be built at any point after M1 ships; §13 says what
changes if M2–M5 are already there.

| # | Slice | Contents |
|---|---|---|
| **T1** | **Linked, and both directions work** | Operator's bot token and its panel screen, webhook + polling transports, account linking and unlinking, `/start` `/help` `/files` `/quota` `/unlink`, the file card, send-under-ceiling with `file_id` caching, link-over-ceiling, receive-under-ceiling, the outbox and its drainer, per-chat and global rate limiting, stranger handling |
| T2 | Doing the work from the chat | «ساخت لینک», «لینک‌ها» with revocation, «حذف», `/search`, pagination, FA/EN from `language_code`, `setMyCommands` per language |
| T3 | Operations | `getWebhookInfo` health card, delivery counters, blocked/deactivated classification surfaced in the panel, per-tenant outbox caps tuned against real traffic, the retention sweeps |

T1 is the only slice worth shipping alone, in the same sense M1 is: it satisfies the owner's two headline
sentences, and it is where a mistake is a security incident rather than a missing button.

## 5. Tenant isolation, where there is no session at all

Every Telegram update arrives with no cookie, no principal and no tenant. The only identity in it is a
numeric id that anyone in the world can cause the bot to see, simply by messaging it. This is the same
shape as `/d/{slug}` — which M1 §8 is entirely about — and it is worse in one respect: `/d/{slug}` reads
one row by an unguessable slug, whereas a Telegram update wants to reach a whole tenant's file list.

### 5.1 One place turns a chat id into a tenant

`ITelegramIdentityReader` is the sibling of `IPublicLinkReader`, and its absence of a tenant parameter is
load-bearing for the same reason:

```csharp
public sealed record TelegramIdentity(Guid AppUserId, Guid TenantId, TenantRole Role);

public interface ITelegramIdentityReader
{
    /// Null when this Telegram user is bound to nobody. There is no other answer, and no
    /// overload that takes a tenantId — there is nobody to take one from.
    Task<TelegramIdentity?> ResolveAsync(long telegramUserId, CancellationToken ct);
}
```

Everything downstream takes the resolved `TenantId` as an explicit argument, into the same
`IFileCatalog` / `IShareLinkService` / `IUploadCoordinator` methods a browser request calls. There is no
Telegram-specific repository with a wider scope, no ambient tenant, and no `Guid.Empty` anywhere near
this path.

**`TelegramAccount` deliberately carries no `TenantId` column** (§7). It holds `AppUserId`, and the
resolver joins to `AppUser` for the tenant and the role on every update. A denormalised tenant on the
mapping row would go stale the day a user moves or is removed, and it would go stale silently. This is
M5 §4's rule — role and tenant are read from the database on the request, never from a cached claim —
applied one layer down.

### 5.2 Key on the sender, not on the chat

`message.from.id` and `message.chat.id` are equal in a private chat and different everywhere else.
Binding on `chat.id` means a *group's* id could become bound, and then every member of that group reads
the tenant's files.

So: **the binding is on `from.id`, and T1 answers only in private chats.** Any update whose
`chat.type` is not `private` gets one message — «این ربات فقط در گفتگوی خصوصی کار می‌کند» — and is
otherwise discarded. That also sidesteps Telegram's privacy-mode rules entirely, and §14.1 asks for the
bot to be configured at BotFather so it cannot be added to groups at all. Belt and braces: even in a
private chat, the handler asserts `chat.id == from.id` before replying.

### 5.3 What an unlinked chat gets

**One string, always the same one**, whether the sender was never linked, was unlinked yesterday, or
belonged to a panel user who has been removed:

> «این ربات مخصوص کاربران Drive Union است. برای اتصال، از بخش تنظیمات پنل خود شروع کنید.»

This is M4 §5's four-identical-refusals discipline in a new place, and it is worth spelling out why it
applies. A bot that answers "your account was disconnected" to one stranger and "unknown account" to
another is an oracle for *which Telegram accounts are customers of this service*. Anyone can message the
bot; the entire Telegram user-id space is enumerable at whatever rate the rate limiter allows.

The cost of the uniform string is that a legitimate customer whose link was removed sees a generic
message. That cost is paid elsewhere: **unlinking always sends one farewell message at the moment it
happens** (§6.3), so the person learns why from the event, not from the steady state.

### 5.4 Callback data is client-supplied, and is never an authorization

An inline button's `callback_data` comes back to us from a client we do not control. A crafted callback
naming another tenant's `StoredFileId` must produce nothing. So the handler **re-resolves every id
through the tenant-scoped repository** — `IFileCatalog.GetAsync(tenantId, fileId)` — and a null answer
renders the same "this file is not available" card as a random GUID. That is exactly M5 §4's rule that a
cross-tenant id gets 404 and never 403, restated where the client is a chat.

### 5.5 The pool never appears

M1 §1.4 is absolute and the bot is a new way to break it. No message the bot sends may contain a Google
account email, a Drive file id, an account short code (`A1`), a resumable session URI, or a count of
accounts. When the pool has no eligible upload target, the bot renders M2 §4's **tenant** string —
«آپلود موقتاً در دسترس نیست» — never the operator one that names the blocked accounts.

§12.3 makes this a test on the raw outbound string, not on a typed object, for the reason M2 §12.4 gives:
the bug being guarded against is a message template gaining a field, and a typed assertion would not
notice.

## 6. Account linking

### 6.1 The plain shape, and why it is not enough here

The usual design is a deep link carrying a one-time, short-lived, single-use token:
`https://t.me/<bot>?start=<token>`, the bot receives `/start <token>`, the server looks it up and writes
the binding. The parameter format is documented and generous enough: *"A-Z, a-z, 0-9, \_ and - are
allowed … The parameter can be up to 64 characters long"*, which fits 32 random bytes as base64url
(43 characters) with room to spare.

The problem is what a token in a URL on a screen actually is. The realistic leak is not an attacker
intercepting it — it is **a customer screenshotting their settings page into a support conversation**,
which happens in the first month of every product. Whoever sees that screenshot within the token's
lifetime can bind *their* Telegram account to *that customer's tenant*, and from then on reads every file
the tenant owns. Shortening the lifetime narrows the window; it does not close it, because the screenshot
and the "it's not working" message arrive together.

### 6.2 The flow that is built: two legs, and the write happens on the authenticated one

1. **Panel.** On «تنظیمات», the customer presses «اتصال حساب تلگرام». The server generates 32 bytes from
   `RandomNumberGenerator`, base64url-encodes them, and stores **only the SHA-256 hash** in
   `TelegramLinkToken` with `ExpiresAt = now + 10 minutes`. Only the hash, for M5 §6's reason: an
   invitations-shaped table is a table of live credentials, and a database dump or a result set pasted
   into a ticket must not be a set of working keys. SHA-256 rather than a password hash because the token
   is 256 bits of entropy with nothing to brute-force and the lookup must be one indexed read.
2. The panel renders the deep link as a button **and as a QR code**, because the common case is a desktop
   panel and a phone Telegram, and typing a 43-character token is not a thing anyone will do.
3. **Bot.** `/start <token>`. The server hashes the parameter, finds an unconsumed, unexpired row, records
   `PresentedTelegramUserId`, `PresentedChatId` and `PresentedAt` on it, and replies with a **six-digit
   confirmation code** and one line: «اگر این را از صفحه‌ی تنظیمات خودتان باز نکرده‌اید، این پیام را
   نادیده بگیرید.» **Nothing is bound yet.**
4. **Panel.** The card now shows a six-digit input. The customer types what the bot sent. That POST is
   authenticated, antiforgery-protected, and carries a session — so the row that finally appears in
   `TelegramAccount` is written by a request that already proved who the panel user is.

**What this buys, stated precisely:** possession of the deep link alone gets a stranger a six-digit code
and nothing else, because completing the binding requires reaching the settings page of the account being
bound. The screenshot attack in §6.1 stops working. The cost is one extra step — the same gesture as any
two-factor prompt — and one screen state.

Mechanics that matter:

- **Consumption is one conditional statement.** `UPDATE … WHERE Id = @id AND ConsumedAt IS NULL AND
  ExpiresAt > @now` returning exactly one affected row, in the same transaction that inserts
  `TelegramAccount`. Two simultaneous confirmations produce one binding and one «این درخواست معتبر
  نیست». Same shape as M5 §6's invitation acceptance.
- **The code is stored hashed too**, salted with the token row id. Six digits is 10⁶ and a table read
  should not be a working code.
- **Five attempts, then the token dies** and the panel offers a fresh one. The code is not the primary
  control — the authenticated session is — so a short attempt budget costs nothing.
- **One Telegram account per panel user, and one panel user per Telegram account.** Both directions are
  unique indexes. A `/start <token>` from a Telegram id that is already bound elsewhere gets «این حساب
  تلگرام قبلاً به یک حساب دیگر متصل است»; a customer who already has a link sees «قطع اتصال» rather than
  a second «اتصال». Proposal, mirroring M5 §1's one-tenant-per-account decision and for the same reason:
  the resolver returns one answer, and there is no chat-level switcher anywhere in the design to express
  anything else. §14.6.
- **Unused tokens are swept**, and the sweeper's non-zero-delete test from M4 §6.3 applies: a sweeper that
  deletes nothing must not look like a sweeper that had nothing to do.

### 6.3 Unlinking, and what happens when a panel user is removed

**Unlinking works from both ends and does exactly one thing: it deletes the identity mapping.** No file
is touched, no link is revoked, nothing the customer created goes away.

- **From the panel:** «قطع اتصال» on the settings card, with a confirmation that says what will stop
  working.
- **From the bot:** `/unlink`, with an inline confirm. The owner asked that the customer be able to do
  their work from the chat; being able to leave is part of that.

Either way, **the last thing that happens is one farewell message** — «اتصال این حساب تلگرام به Drive
Union قطع شد.» — before the row disappears. A chat that simply stops answering is the failure mode this
product keeps refusing to ship, and it is also what makes §5.3's uniform stranger string acceptable.

**When the panel user is removed**, the mapping goes with them. That is expressed as one command,
`UnlinkTelegramForUser(userId, reason)`, which deletes the row *and* queues the farewell, and it is
called from user deletion, from M5's member removal, and from tenant deletion (once per member). The FK
also carries `ON DELETE CASCADE`, as a backstop for a direct SQL delete — but a cascade alone is silent,
and silence is the thing being designed against.

**A role change does not touch the link.** A user demoted from Uploader to Viewer keeps their binding and
loses the buttons, because §5.1 reads the role from the database on every update rather than caching it
on the mapping row.

## 7. Data model

Six tables. Nothing existing is renamed, dropped, or given a new meaning.

```
TelegramBotSettings { Id (=1), BotTokenProtected, BotUsername?, BotUserId?,
                      UpdateSource, WebhookPathSegment?, WebhookSecretProtected?,
                      WebhookRegisteredAt?, UpdatedAt, UpdatedByUserId }

TelegramAccount     { Id, AppUserId (unique), TelegramUserId (unique), ChatId,
                      Username?, DisplayName?, LanguageCode?,
                      LinkedAt, LastSeenAt, DeliveryStatus, BlockedAt? }

TelegramLinkToken   { Id, AppUserId, TokenHash, ConfirmationCodeHash?,
                      PresentedTelegramUserId?, PresentedChatId?, PresentedAt?,
                      ConsumedAt?, Attempts, CreatedAt, ExpiresAt }

TelegramOutbox      { Id, TenantId, ChatId, Kind, StoredFileId?, Payload jsonb,
                      Status, Attempt, NextAttemptAt?, ErrorCode?, ErrorDetail?,
                      CreatedAt, SentAt? }

TelegramFileId      { StoredFileId, BotUserId, FileId, FileUniqueId, SizeBytes, CachedAt }
                      PK (StoredFileId, BotUserId)

TelegramUpdateSeen  { UpdateId, ReceivedAt }   PK (UpdateId)
```

The decisions inside that, in the order they will be questioned:

- **`TelegramBotSettings` is a single global row with no `TenantId`**, like M2's `PoolSettings`. The bot
  is the operator's. Three secrets live in it — the token, the webhook secret, and the random webhook
  path segment — and all three are encrypted with the same `ITokenProtector` that protects the Google
  refresh tokens, under M1 §5's rule that the Data Protection keys are persisted to the database.
- **`TelegramAccount` has no `TenantId`.** §5.1 explains why, and the absence is the same kind of
  load-bearing absence as `GoogleAccount` having none.
- **`TelegramOutbox.TenantId` is not nullable**, exactly like `Job.TenantId` (M3 §4). The drainer is
  sessionless and the row is the only tenant identity it has. There is no system-owned outbox item.
- **`TelegramFileId` is keyed on the bot** (§3.2).
- **`TelegramUpdateSeen` is the dedup table.** Telegram redelivers an update when the webhook answers
  non-2xx or times out, and a redelivery must not upload the same file twice or send the same document
  twice. This is the Telegram analogue of M3 §3.3's `duJobId` probe: the retry is not a hypothetical, it
  is the documented behaviour. Insert-on-conflict-do-nothing; a conflict means "already handled, answer
  200 and stop". Rows older than 7 days are swept.
- **`DeliveryStatus`** is `Active | Blocked | Deactivated`, set from the two 403 reasons in §11.4. It is
  what the settings card renders as «مسدود شده در تلگرام», and it is what stops the outbox retrying into
  a wall for ever.

One migration. `TelegramBotSettings` is seeded with its single row, empty.

## 8. The bot's surface

«شکیل و تمیز» in a chat means: few commands, one screen, and every message ends somewhere.

### 8.1 Commands

Six, registered through `setMyCommands` so Telegram renders the menu button. Commands are documented as
*"up to 32 characters"* using *"Latin letters, numbers and underscores"*; all six are far inside that.

| Command | Unlinked | Linked |
|---|---|---|
| `/start` | §5.3's single string, or the linking flow when it carries a token | The home card |
| `/files` | §5.3 | The 10 newest files, one card each, «بیشتر» to page |
| `/search <متن>` | §5.3 | The same list, filtered by name (T2) |
| `/quota` | §5.3 | Storage used against the tenant's cap (M5 §7); before M5, file count and total bytes |
| `/help` | The same as `/start` | Two lines: what the bot can carry, and what it hands over as a link |
| `/unlink` | §5.3 | Confirm, then §6.3 |

**There is no `/upload`.** Uploading is "send the bot a file", which is what the owner asked for and what
a person does without being told.

`/help` carries the size ceilings **on purpose**. They are the one thing a user will otherwise discover
by failing, and a bot that states its limits before it is asked is the difference between "well-made" and
"broken".

### 8.2 The file card

Everything else is inline keyboards hanging off one card, so there is one screen and nothing to memorise:

```
📄 گزارش-فصلی-۱۴۰۵.pdf
18.4 MB · ۳ روز پیش

[ ارسال فایل ]   [ ساخت لینک ]
[ لینک‌ها (۲) ]  [ حذف ]
```

- **ارسال فایل** — under the ceiling only (§3.1). Queues an outbox item; the chat gets
  `sendChatAction: upload_document` immediately and then the document.
- **ساخت لینک** — `IShareLinkService.CreateAsync` with the configured defaults, replying with the URL in
  a monospace span so it is one tap to copy. Expiry and cap are not editable from the chat in T1: a form
  in a chat is four messages, and this is the kind of feature that turns a clean bot into a maze. §14.5.
- **لینک‌ها (۲)** — this file's links with their state, and «باطل کردن» each, through
  `IShareLinkService.RevokeAsync`. Two-step confirm, because M4 §2 makes revocation permanent: the slug
  is burned for ever.
- **حذف** — `IFileCatalog.DeleteAsync`, two-step confirm, Uploader and above under M5 §2.

Digits follow M2 §9's rule, which the panel already holds to: **counts in Persian digits, quantities
carrying a unit in Latin** — «۳ روز پیش» and «لینک‌ها (۲)» against `18.4 MB`.

**No dead ends** is three concrete rules, not an aspiration:

1. Every message that is not the home card ends with a «فایل‌ها» button.
2. A slow action edits the message it started from rather than appending new ones, so a chat does not
   fill with progress. `answerCallbackQuery` is called on every callback without exception — a button
   that spins for ever is the most common way a bot looks broken.
3. Every refusal names the next action. Too big names the link. Not linked names the panel. No files
   names the upload page.

### 8.3 Callback data, in 64 bytes

`callback_data` is 1–64 bytes, which does not fit two GUIDs as text (36 characters each). So a GUID is
encoded as 22-character base64url of its 16 bytes, and the verb is one character:
`s.{22}` is 24 bytes for "send file X", leaving room for a second id when a callback needs one.

And, from §5.4: **callback data is never trusted.** Every id in it is re-resolved through a
tenant-scoped repository before anything happens.

### 8.4 Language

Persian by default, matching the panel's `dir="rtl" lang="fa"` shell. `message.from.language_code` picks
English when it is not `fa`, and `setMyCommands` is registered per `language_code` so even the menu is
right. This is the same FA/EN decision M1 §7 made for the public page, made once more in the one other
place the product speaks to someone who is not looking at the panel.

## 9. The panel side

Two audiences, two screens, and both already have a home.

### 9.1 The operator: the token, and nothing about content

The bot token is the same shape of problem as the Google client secret, and it gets the same answer
rather than a second one. `IGoogleOAuthCredentialStore` already establishes the discipline in this
codebase, and it is followed exactly:

- A read model that returns everything **except** the secret — `HasToken` is the only thing the browser
  ever learns. `StoredGoogleOAuthClient`'s own comment says why: *"a screen that could print the secret
  is a screen that eventually will: into an HTML source view, a browser cache, a bug report
  screenshot."*
- One accessor to the plaintext, reached only by the code that actually talks to Telegram.
- `ITokenProtector` for encryption at rest, under M1 §5's rule that the Data Protection keys live in the
  database.
- Saving with the field left empty **keeps the stored secret**, so correcting the update mode does not
  require fetching the token out of BotFather again.

**One deliberate difference: the bot token lives in a row, not in a file.**
`FileGoogleOAuthCredentialStore`'s own comment names the file as the weaker choice — *"A
`GoogleOAuthClient` single-row table would remove even that"* — and Telegram is where that weakness
actually bites rather than merely being noted. A registered webhook is bound to a token *and* a secret
*and* a path segment; losing the file on a redeploy does not cost one re-paste, it leaves Telegram
POSTing to a URL this process no longer recognises, and every customer's bot goes quiet with nothing in
any log to say so. The Google store itself is not changed by this slice.

The screen is `/telegram`, `[Authorize(Policy = Operator)]`, taking one of the nav slots currently
rendered as a disabled «در نسخه‌ی بعدی» placeholder:

- **The token field.** Write-only, showing «ذخیره شده» when one is present, with a link to @BotFather and
  the two-line instruction for obtaining one. The screen must come up and be useful when nothing is
  configured — the same requirement `AccountsController` already meets for Google.
- **«تأیید توکن»** → `getMe`. It is the only proof a token works, and both values it returns are stored:
  the `@username` is what every customer's deep link is built from (§6.2), and the bot id is the
  `TelegramFileId` cache key (§3.2).
- **Update mode** — webhook or polling (§10). In webhook mode «ثبت وبهوک» calls `setWebhook` with a
  freshly generated secret, a fresh path segment and an explicit `allowed_updates`; «حذف وبهوک» calls
  `deleteWebhook`.
- **Health, most of which is Telegram's own answer.** `getWebhookInfo` returns `url`,
  `pending_update_count`, `last_error_date`, `last_error_message`, `ip_address` and `max_connections`.
  Rendering `last_error_message` **verbatim** is the single most useful thing on the page: it is Telegram
  saying why it could not reach us, in its own words, and paraphrasing it throws away the only diagnosis
  available. Beside it, our own four numbers — updates processed in 24 h, outbox depth, sends failed in
  24 h, and linked accounts as a bare count.
- **A rising `pending_update_count` is the one alarm worth drawing**, in `--warn`. It is what a broken
  webhook looks like from the outside while everything on this box appears perfectly healthy.

**Nothing on this screen names a customer.** No chat ids, no Telegram usernames, no filenames, no list of
who has linked what. §15 declines that directory explicitly.

### 9.2 The customer: one card on «تنظیمات»

M5 §9 moves the members card onto the tenant's settings screen and notes that the screen is otherwise
empty for a customer. This card is its neighbour.

**Not linked:**

- Two lines saying what it does — send the bot a file and it lands here, ask for a file and it comes back
  — and one line saying what it cannot carry, with the number read from `Telegram:MaxSendBytes` rather
  than typed into the copy.
- «اتصال حساب تلگرام», which reveals the deep-link button, the QR code and the six-digit input of §6.2.
- The data-residency line from §14.7, above the button rather than below it.

**Linked:** the Telegram display name and `@username`, the date it was linked, and «قطع اتصال». **Never
the numeric Telegram id** — it is an identifier the customer has no use for and support does not need on
a screen. When `DeliveryStatus` is not `Active` the card reads «مسدود شده در تلگرام» with one line about
unblocking, because the fix is on the customer's phone and nowhere else (§11.4).

**When the operator has configured no bot at all**, the card says so plainly instead of drawing a button
that fails. Same rule M2 §8 applies to the settings screen's unbuilt sliders: a control that cannot work
must not be rendered as though it can.

## 10. Updates: webhook or long polling

Both are documented, both work, and they fail differently.

**Webhook** — the verified constraints:

- HTTPS is mandatory: *"A webhook requires SSL/TLS encryption, no matter which port is used. It's not
  possible to use a plain-text HTTP webhook."* TLS 1.2 or newer only.
- *"We currently support the following ports: 443, 80, 88 and 8443. Other ports are not supported and
  will not work."*
- IPv4 only. Telegram posts from *"subnets 149.154.160.0/20 and 91.108.4.0/22"*, documented as subject
  to change.
- `secret_token`: *"A secret token to be sent in a header 'X-Telegram-Bot-Api-Secret-Token' in every
  webhook request, 1-256 characters. Only characters `A-Z`, `a-z`, `0-9`, `_` and `-` are allowed."*
- `max_connections`: *"1-100. Defaults to 40."* `allowed_updates`, `drop_pending_updates` and a fixed
  `ip_address` are also settable.

**Long polling** — `getUpdates`, `limit` 1–100 defaulting to 100, `timeout` in seconds; *"This method
will not work if an outgoing webhook is set up."*

### 10.1 The recommendation: webhook, for this deployment

The OVH box in Germany already terminates HTTPS on 443 for the panel and for `/d/{slug}`, on a real
domain with a real certificate. **The one hard prerequisite of a webhook is already paid for**, which is
usually the whole argument against it.

Three more reasons specific to this product:

- **A webhook is a controller action.** No `BackgroundService`, no lease, no restart recovery, and no
  second long-running thing that can silently do nothing. M2 §10 and M3 §5 are both written about that
  exact failure — a sessionless worker that reports success over an empty result set — and not creating
  one is worth a lot here.
- **A poller must be a singleton across the deployment.** Two instances calling `getUpdates` produce
  `409 Conflict: terminated by other getUpdates request`, and the symptom is intermittently missing
  messages, which is invisible in development and maddening in production. Enforcing single-instance
  polling means a lease, which is M3's machinery imported into a slice that otherwise does not need it.
- **Latency.** An update arrives when it happens rather than on the next poll.

**What the webhook costs, honestly.** This machine has no public HTTPS, so nobody can run the bot locally
without either a tunnel or the other transport. Therefore:

**Both are implemented, behind one configuration key** — `Telegram:UpdateSource = Webhook | Polling` —
and **polling is what runs in development.** That is not indecision. The polling client is a thin loop
over `ITelegramClient`, and without it the bot cannot be exercised on the machine the product is
developed on — the same constraint that put `IDriveClient` in Core (M1 §4). Production uses the webhook.
Switching is one setting plus one `setWebhook`/`deleteWebhook` call, both of which the operator screen
exposes as buttons.

### 10.2 The webhook endpoint is the product's fourth anonymous surface

After `/d/{slug}`, `/d/{slug}/file` and `/accounts/callback`. It gets:

- **An unguessable path.** `/telegram/{WebhookPathSegment}`, where the segment is 32 random bytes,
  base64url, generated on registration and stored encrypted. Obscurity is not the control; it keeps the
  route out of scanners' logs and off the panel's route list.
- **The secret token, compared in fixed time.** `X-Telegram-Bot-Api-Secret-Token` against the stored
  value with `CryptographicOperations.FixedTimeEquals`. Missing or wrong → **401, and nothing is
  processed or logged beyond a counter.** This is the control.
- **A request body size limit.** Updates are small — a document update carries metadata, not bytes — so a
  few hundred kilobytes is generous. Without it the endpoint is an anonymous unbounded POST.
- **Its own rate limiter policy**, `DriveUnion.TelegramWebhook`, added beside the two in
  `DriveUnionRateLimits`.
- **No IP allow-list as the primary control.** The documented subnets are explicitly subject to change,
  and the box sits behind a proxy whose forwarded headers have to be trusted correctly for the source
  address to mean anything at all — M4 §6.1 and `Program.cs`'s `DriveUnion:TrustedProxies` comment
  already describe how quietly that goes wrong. An optional `Telegram:TrustedSubnets` exists as defence
  in depth, empty by default.

### 10.3 Answer 200 immediately; move bytes elsewhere

Telegram redelivers on a non-2xx or a timeout. A 50 MB `sendDocument` inside the webhook handler would
hold the request open for a minute and be redelivered — and the redelivery would send the file twice.

So, one rule: **short replies inline, byte-moving work through the outbox.**

- A text reply, a card, a callback acknowledgement — all measured in tens of milliseconds. Sent inline,
  and the handler returns 200.
- A document send, or an inbound file upload — queued to `TelegramOutbox`, and the handler returns 200
  immediately, having first sent a one-line acknowledgement so the chat is never silent.

**The outbox is deliberately not M3's `Job` table**, even where M3 exists. Two reasons: Telegram sends
are rate-limited on a dimension `Job` has no column for (the chat id, §11.1), and the whole slice is
buildable on M1 alone, which it stops being the moment it depends on M3's schema. §13 gives the seam if
the two are ever merged.

The drainer is one `BackgroundService`, claiming with `FOR UPDATE SKIP LOCKED` in the same shape as
M3 §5, sessionless, with `TelegramOutbox.TenantId` as its only source of tenant identity. M3 §12.5's test
applies verbatim: the sessionless drain must assert a **non-empty** result, because the bug's whole
signature is an empty one.

## 11. Rate limits and abuse

Telegram throttles bots, and the owner's concern is exact: one customer with a thousand files must not
make the bot look broken for everybody else. The verified figures, from the Bots FAQ:

- One chat: *"avoid sending more than one message per second. We may allow short bursts that go over
  this limit, but eventually you'll begin receiving 429 errors."*
- Groups: no more than 20 messages per minute (not relevant — §5.2).
- Bulk: approximately 30 messages per second overall.
- A 429 carries `retry_after` in the response's `ResponseParameters`.

### 11.1 Two buckets in front of every outbound call

- **Per chat: 1 message per second.**
- **Globally: 25 per second**, not 30. The same reasoning as M3 §5's `DriveQueriesPerMinute = 6000`
  against a stated 12,000 budget: leave headroom and never learn where the ceiling is from a 429.

Both live in one place, in front of `ITelegramClient`, so no call site can route around them.

**A 429 is obeyed, not retried.** `retry_after` parks the outbox item until that instant and **does not
increment `Attempt`**. That is M3 §9's rule for a quota park, and it is right for the same reason: a
backlog that exhausts its retry budget on flood control fails for a reason no user could understand.

### 11.2 The outbox drains round-robin by tenant

Not FIFO. One tenant queueing four hundred sends must not put every other tenant behind them.

**This is a deliberate divergence from a sibling spec.** M3 §14 declines per-tenant fairness in the job
queue, on the grounds that it is invisible at two tenants and obvious at twenty. Here it is not optional,
because the shared resource is a *single bot identity* with a global ~30/s ceiling: one tenant's backlog
is directly every other tenant's latency, from the second tenant onwards. The claim query orders by
`(least-recently-served tenant, created_at)`.

**And the queue is bounded.** At most `Telegram:MaxQueuedPerTenant` items pending per tenant, starting at
50. Over it, the bot answers «چند درخواست در صف دارید — پس از اتمام دوباره تلاش کنید» and enqueues
nothing. A bounded queue with an honest message beats an unbounded one that looks like it is working.

### 11.3 A public bot receives messages from anyone

- **An unbound sender gets at most 3 replies per hour**, plus a global cap on stranger replies per
  minute. Beyond either, the update is consumed — so the offset or the webhook advances — and **nothing
  is sent**. Silence, not an error: an error is a reply, and a reply is the resource being abused.
- **We never call `getFile` for an unbound chat.** A stranger sending a 20 MB file must not be able to
  make this box pull 20 MB, and doing the identity check before the fetch is the whole of that control.
- The dedup table (§7) also bounds the cost of a redelivery storm.

### 11.4 The two 403s worth naming

A user who blocks the bot produces `403 Bot was blocked by the user` on the next send; a deleted account
produces `403 Forbidden: user is deactivated`. Both set `TelegramAccount.DeliveryStatus`, stop the outbox
retrying, and surface on the customer's settings card as «مسدود شده در تلگرام» — which is the only place
that fact is useful, because the fix is on the customer's phone.

Everything else is logged **verbatim** and retried with backoff. M3 §8.2's discipline applies: tighten the
classifier in the first week from real log lines rather than from a mapping guessed now.

## 12. Tests that hold these lines

Every one runs against a fake `ITelegramClient` and M1's fake `IDriveClient`. **Nothing in this suite
reaches Telegram or Google**, for M1 §4's reason.

1. **The uniform stranger reply.** Never-linked, unlinked, and belonged-to-a-deleted-user all produce the
   byte-identical string of §5.3.
2. **Cross-tenant callback.** A callback naming tenant A's file id, arriving from tenant B's linked chat,
   produces the same "not available" card as a random GUID, and `IFileCatalog.GetAsync` was called with
   B's tenant id.
3. **Nothing about the pool leaks.** No outbound message body matches a Google account email, a Drive
   file id shape, or an `A[1-9]` short code. Asserted on the raw string, per M2 §12.4.
4. **Linking is single-use and race-proof.** Two simultaneous confirmations produce one
   `TelegramAccount`.
5. **A forwarded deep link cannot complete a binding.** `/start <token>` from a Telegram id other than
   the panel user's records `Presented` and returns a code; no `TelegramAccount` row exists until the
   authenticated panel POST arrives.
6. **The ceiling is enforced before the wait.** A file above `Telegram:MaxSendBytes` never reaches
   `SendDocumentAsync` — the fake records no upload — and the reply contains a link.
7. **A cached `file_id` re-send** makes no `IDriveClient` call, writes no `DownloadEvent`, and leaves
   `ShareLink.DownloadCount` unchanged.
8. **Dedup.** The same `update_id` delivered twice performs the action once.
9. **Fairness.** 100 queued items for tenant A and 1 for tenant B: B's item is sent within the first few,
   not after 100.
10. **429 parks without spending a retry.** `retry_after` sets `NextAttemptAt` and leaves `Attempt`
    unchanged.
11. **The webhook secret is the control.** A POST with a missing or wrong
    `X-Telegram-Bot-Api-Secret-Token` gets 401, processes nothing, and the comparison is fixed-time.
12. **Sessionless drain.** The drainer with no `HttpContext` sends tenant A's queued item, asserting a
    **non-empty** result (M3 §12.5).
13. **The webhook route is classified.** M5 §5's generated endpoint test must place it on the explicit,
    commented allow-list as anonymous and tenant-agnostic, or turn red.

## 13. What this touches in M2–M6, and redesigns in none of them

- **M2 — pool and quota.** Nothing in the bot chooses a Google account; inbound uploads go through
  `IUploadCoordinator` and therefore through `IUploadTargetSelector`. The one thing inherited is M2 §4's
  `503 no_upload_target`, and the bot must render the **tenant** string, never the operator one that
  names blocked accounts (§5.5).
- **M3 — queue and worker.** The Telegram outbox is a separate table for the reasons in §10.3. **The seam
  if they are ever merged:** `Job.Type` gains `TelegramSend` and the chat id rides in
  `Job.TargetDescriptor`, which M3 §4 already reserved as `jsonb` for exactly this kind of addition. The
  fairness rule of §11.2 would have to move with it.
- **M4 — link control.** «ساخت لینک» creates a link with M4's defaults. Two interactions:
  a **password-protected** link sent into a chat is a link the recipient cannot open, so the bot's link
  message must say a password is set and must **never** contain it (M4 §3.1: the password is
  write-only). And M4 §5's four-identical-refusals rule gains a fifth consumer — which T1 satisfies by
  the cheapest possible route: **the bot does not resolve arbitrary slugs at all**, so it cannot become
  an oracle for which ones exist.
- **M5 — roles and tenancy.** The bot's capabilities are M5's three roles, read from the database on
  every update (§5.1). Viewer: browse, `/quota`, send. Uploader: adds upload, create link, revoke,
  delete. Owner: the same as Uploader in the chat — invitations and member management are not chat work.
  M5 §5's generated route test must classify the webhook (§12.13).
- **M6 — egress.** Both legs move bytes over the box's uplink, so they belong in M6 §10's `CountingStream`
  with one new `EgressSample.Direction` value, `ToTelegram`. One enum value and two wrap sites, named
  here so M6 does not have to rediscover it. Note that a `file_id` re-send contributes **zero**, which is
  the point of §3.2 and will look like an accounting bug to whoever reads the chart first.

## 14. Before implementation starts

Seven things are needed from the owner. The first two block the first commit of real code.

1. **The bot token from @BotFather**, and three settings alongside it: privacy mode **on**, group
   membership **disabled** (`/setjoingroups`), and the bot's display name and description. §5.2 answers
   only private chats, and a bot that *can* be added to a group will be.
2. **Cloud API or self-hosted Bot API server** — which is the size question, and the only one that
   changes what customers experience. The recommendation is the cloud API for T1, with §2.3's costs
   itemised. If self-hosting is wanted, the blocking sub-question is **whose personal Telegram account
   issues the `api_id`/`api_hash`**, because that is an ownership decision, not a configuration one.
3. **Confirm §3's answer for an oversized file, in words.** Outbound: the bot hands over a share link,
   decided before anyone waits, with the «ارسال فایل» button simply absent. Inbound: a link to the
   panel's uploader. This is the single most visible compromise in the slice and it should be agreed
   rather than discovered.
4. **May the bot delete and revoke?** T1 proposes yes, behind two-step confirmations, because «بتونه از
   همونجا هم کاراشو بکنه» asks for it. Against: a destructive action in a chat has no undo and a mis-tap
   is one pixel away, and M4 §2 makes revocation permanent — the slug is burned for ever.
5. **Default expiry and download cap for a link created from the chat.** The panel has a form; the chat
   deliberately does not (§8.2). A link minted from a phone with no expiry is a link nobody revisits.
6. **One Telegram account per panel user, or several?** Proposal: one, per §6.2, mirroring M5 §1. A person
   with a work phone and a personal phone picks one or signs up twice. Reversing this later is confined
   to the resolver, but it is not free.
7. **The privacy line, which is new and is not a technicality.** M4 §6.3 refused geolocation partly to
   keep customer data from leaving the German box, and the handoff forbids a foreign CDN even for fonts.
   A file sent through the bot **passes through Telegram's servers and stays cached on them** — that is
   precisely the property §3.2's `file_id` cache exploits. That is a real change to the product's
   data-residency story, it belongs in the terms M5 §11.5 already asks for, and the customer's settings
   card should say it in one line before they press «اتصال».

An eighth item is an engineering task rather than an owner decision, and it should be the first thing
anyone does: **one live `curl` against a real bot token to settle §2.1's medium-confidence rows** — send
a 30 MB file by HTTP URL and record the error, send a 60 MB file by multipart and record the error,
`getFile` a 25 MB document and record the error. Fifteen minutes, and it converts three inherited numbers
into three observed ones. Paste the raw responses into this file under a `§2.1 findings` heading.

## 15. Deliberately not in scope

- **Groups, channels, forum topics, and the bot being added to anything.** T1 is private chats only
  (§5.2). Everything about group semantics — privacy mode, per-group rate limits, who in a group speaks
  for a tenant — is a second product.
- **Inline mode** (`@bot query` from inside any chat). It is the natural next request and it puts a
  tenant's filenames one keystroke away from being typed into somebody else's conversation.
- **A token-authenticated one-time upload URL**, so a phone could upload without signing in. Declined
  with its reason in §3.3: it is a bearer credential that writes into a tenant's storage and spends their
  cap.
- **Per-tenant bots.** One bot, the operator's, as the brief says.
- **Telegram as a sign-in method** (the Login Widget, `login_url`). Linking proves a Telegram account
  belongs to a panel user; it does not become a credential. That is an authentication decision with its
  own threat model and it must not arrive as a side effect of this one.
- **Download notifications** — «کسی فایل شما را دانلود کرد». M4 §12 already declined it; a bot makes it
  tempting, and it would turn M4 §6's deliberately thin event log into a notification stream.
- **Folders in the chat** (M2's), **the transfer queue in the chat** (M3's), **typing a link password
  into the chat** (M4's — a password typed into a chat is a password in a chat log), and **member
  management in the chat** (M5's).
- **Editing a link's expiry, cap, alias or password from the bot.** A form in a chat is four messages and
  a state machine; the panel already has the screen.
- **Sending a folder, a multi-select, or a generated archive.** Each one is a byte-moving job with no
  size story better than §3's.
- **Payments, Telegram Stars, Mini Apps and Web Apps.** A Mini App would put the whole panel inside
  Telegram and is a genuinely interesting idea; it is also a second frontend, and this product has one.
- **Any operator screen listing which customers have linked Telegram accounts.** The operator's health
  page shows counts and never a chat id, a username, or a filename. A cross-tenant directory of
  customers' messenger identities is a privacy surface with no product use behind it.
