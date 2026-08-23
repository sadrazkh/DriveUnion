# Drive Union — Telegram

**Date:** 2026-08-24 · **Status:** partly built. Three things are **decided**: the transport is our own
`telegram-bot-api --local` (§2.3), the storage is Google Drive with Telegram as delivery only (§2.5), and
2000 MB is the ceiling (§2.1). **Identity, linking and the operator's bot screen have shipped** — §7.1
records what was built and where the code is the newer thought. The transport and both byte-moving
directions are blocked on §14 · **Depends on:** M1, plus a Linux box running the Bot API server, which is
why §4 grows a T0 · §13 lists what this touches in M2–M6 and in plans-and-quotas; it redesigns none of
them, and names three places — M3's disk check, M6's egress measurement and the plans spec's inbound
ceiling — where they now need a change rather than an addition.

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

**A fifth requirement arrived later in the same conversation, and it is not a feature.** Google Drive
stays the storage — «تمام کار ها باید با گوگل درایو بشه» — and Telegram is there so that a customer can
still reach their files when something else cannot: a blocked route to our box, a broken certificate, a
quota day. §2.5 records the storage decision and §2.6 works out honestly which failures the bot actually
survives, because the obvious reading of «اگر گوگل به مشکل خورد» promises more than the design can deliver.

Two product rules arrive from M1 §1 unchanged and constrain every line below:

- **A customer must never learn that a pool of Google accounts exists.** The bot is a new surface with a
  new way to leak it — an error string, a "which account" hint, a file-id-shaped token in a message. The
  bot never says Google, never says Drive, never prints an account short code or email. §5 makes that a
  test rather than a habit.
- **`/d/{slug}` is anonymous, and there is no global query filter** (M1 §8). Every Telegram update
  arrives with no session and no cookie, which is the same shape of problem in a place it has never had
  to hold before. §5 is the whole of that argument.

## 2. What Telegram is for here, and what it can carry — first, because it decides the product

Telegram's Bot API caps file transfer far below what this product exists to hold. The M1 handoff's own
sample data has an 18.4 MB PDF, a 214 GB archive and an 812 GB image, on 5 TB accounts. The bot can
carry the first one. It cannot carry the other two, and no amount of engineering changes that.

**The owner has decided we run our own Bot API server (§2.3).** That moves the outbound ceiling from
50 MB to 2000 MB and the inbound one from 20 MB to the same, which is a factor of forty and twelve
hundred respectively. It moves nothing else. 2000 MB does not carry 214 GB and it does not carry
812 GB, so §3 — what the bot does with a file it cannot carry — is needed exactly as it was written,
with one number changed. A reader arriving later should not conclude that self-hosting made the problem
go away; it made the problem rarer, which is a different thing and is worth less than it sounds.

A feature that silently fails on the product's typical file is worse than one that says up front what it
can carry. So this section establishes the real numbers, marks the confidence of each, and §3 designs
for the truth rather than around it.

### 2.1 The numbers

Read today against the Bot API reference at **Bot API 10.2, dated 14 July 2026**. The changelog carries
no entry from Bot API 7.0 (December 2023) through 10.2 that changes any of these figures. **The version
in that sentence is now also a build input**: the self-hosted server is pinned to a `tdlib/telegram-bot-api`
tag (§2.4.1), and the tag has to implement at least the Bot API version these numbers were read against.
Bumping one without the other is how a spec quietly stops describing what runs.

The right-hand column is the one that governs this deployment. The middle column is kept because §2.4.4's
rollback, §2.4.5's development story and §14's open questions all still refer to it.

| Operation | api.telegram.org | Self-hosted `--local` — **what we run** | Confidence |
|---|---|---|---|
| Bot downloads a file a user sent it (`getFile`) | 20 MB | **no documented limit** — the real ceiling is what the sender's own client could send, one row down | **High** for the cloud figure. Verbatim from the `getFile` description: *"For the moment, bots can download files of up to 20MB in size."* Restated in the Bots FAQ: *"this will only work with files of up to 20 MB in size."* The local server's *"Download files without a size limit"* is verbatim too, but "no documented limit" is not a number, and §3.3 does not treat it as one. |
| Bot sends a file by uploading bytes (`multipart/form-data`) | 50 MB | **2000 MB** | **High.** Bots FAQ, verbatim: *"Bots can currently send files of any type of up to 50 MB in size."* Local-server section, verbatim: *"Upload files up to 2000 MB."* |
| Bot sends a file by handing Telegram an **HTTP URL** | 20 MB; photos 5 MB | unknown, and **now irrelevant** | **Medium, and no longer worth much.** The reference page truncates before its "Sending files" section and I could not read it verbatim from here. Several independent renderings agree on 5 MB / 20 MB, and one adds that `sendDocument` by URL works only for a short list of types (PDF, ZIP, GIF). Whether `--local` lifts the URL cap is **not documented anywhere I could read — needs confirmation** — but §2.2 rejects the URL path on grounds that have nothing to do with its size, so the answer changes no decision. |
| Bot re-sends a file already on Telegram's servers (`file_id`) | no size limit | no size limit | **Medium-high.** Documented under "sending by file_id" with no size clause and no counter-example found. `file_id` is unique per bot and cannot be transferred between bots. **Whether a `file_id` minted against the cloud server stays valid against a local one is not documented and needs confirmation** — §2.4.4 does not wait for the answer, it truncates the cache. |
| What a *user* can send in the first place | 2 GB free / 4 GB Premium | same | **Low-medium — needs confirmation, and it now matters much more than it did.** No official Telegram page was read for this. Third-party sources dated 2026 agree, and grammY's documentation states a local server allows *"downloading files of any size (4000 MB with Telegram Premium)"*, which is consistent. On the cloud API this row was trivia; on a local server it is the actual inbound ceiling, because ours is set below it deliberately. §14.10. |

**Whether 2000 MB is decimal or binary is not stated anywhere I could read**, exactly as 20 MB and 50 MB
were not. Enforce the decimal reading, which is the same choice and the same reasoning as M2 §9 made on
the 750 GB day: if the real limit is binary we leave about 4.6% unused, which is cheap, and the reverse
costs a rejected send after two gigabytes have already moved — the most expensive way in this design to
learn a number.

So, concretely, and these are the values that go in `appsettings.Production.json`:

- **`Telegram:MaxSendBytes` = `2_000_000_000`** — decimal bytes, 2000 MB, the local server's documented
  upload limit taken literally.
- **`Telegram:MaxReceiveBytes` = `2_000_000_000`** — decimal bytes, the same number, but arrived at
  differently and worth being explicit about because it is not forced on us. The local server documents
  *no* download limit, so this ceiling is a **product decision, not an API one**: it is what a non-Premium
  user can send in the first place, and it is the owner's «دو گیگ اندازه کافی هست». A Premium sender
  offering 3 GB is refused with §3.3's panel-uploader message rather than accepted, and that is the
  intended behaviour — the alternative is an inbound path with no bound at all on a shared disk (§2.4.2).
- Development on this machine still runs against the cloud API (§2.4.5), where these keys read
  `50_000_000` and `20_000_000`. **The two environments differ in these values on purpose**, and that is
  precisely why §2.3's rule that no code path knows the numbers is load-bearing rather than tidy.

Two more facts that shape the code rather than the product:

- **What `getFile` returns is not the same kind of thing on the two servers, and the code has to know.**
  Against `api.telegram.org` the result is a `file_path` that becomes the URL
  `https://api.telegram.org/file/bot<token>/<file_path>`, perishable — *"It is guaranteed that the link
  will be valid for at least 1 hour"* — and containing the bot token, which makes it the same class of
  secret as a Drive resumable session URI (M1 §6): never logged, never persisted, never put in a
  response, never handed to a browser.
  **Against the local server it is an absolute path on our own disk**, verbatim from the local-server
  section: *"Receive the absolute local path as a value of the file_path field without the need to
  download the file after a getFile request."* That is not a secret of the same kind — it is a filename —
  but it is still never rendered to anyone, because it names the Bot API working directory and there is
  no reason for a customer or a log line to learn where that is. What replaces the perishable-URL rule is
  a stricter one: **the bytes now exist on our filesystem, so the obligation is to delete them**, and
  §2.4.2 is the whole of that. The URL form is not dead code — it is what development uses — so
  the gateway returns a discriminated result and both branches are tested (§12.14), rather than one branch
  being a comment about the other.
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

**The local server does not rescue it, and it offers a second escape hatch that also does not apply.**
Whether `--local` lifts the 20 MB URL cap is undocumented (§2.1), but the M4 objection is untouched by the
answer: an anonymous fetch of `/d/{slug}/file` still spends a `MaxDownloads`, still writes a
`DownloadEvent`, and still fails outright on a password-protected link. Separately, the local server
advertises *"Upload files using their local path and the file URI scheme"* — hand it `file:///…` instead
of a multipart body. That is genuinely useful to somebody whose bytes are on their own disk. **Ours are
in Drive**, so using it would mean writing up to 2000 MB to disk first in order to avoid streaming
2000 MB through a socket, which is worse on every axis. We stream multipart to loopback and never
construct a `file://` argument — a rule that §2.4.3 turns out to have a second, sharper reason for.

### 2.3 The decision: we run our own Bot API server

The owner's words:

> «نه من سرور دارم و الان سایت رو هم دارم رو سرور بالا میارم دیگه ابری خود تلگرام چرا استفاده کنم؟
> دو گیگ اندازه کافی هست»

There is a server, the panel is going onto it, and 2 GB is a sufficient ceiling for these users. **So the
product runs `tdlib/telegram-bot-api` with `--local` on the OVH box, and `api.telegram.org` is used only
in development (§2.4.5) and as the rollback target of last resort (§2.4.4).**

The rest of this section is what that costs. It is kept in full, and none of it is deleted now that the
answer is known, because a decision whose price has been edited out is a decision nobody can review. §2.4
turns the three items that are real engineering work into something implementable.

Telegram open-sources the Bot API server (`tdlib/telegram-bot-api`). Run with `--local`, the documented
gains are, verbatim:

> "Download files without a size limit." · "Upload files up to 2000 MB." · "Upload files using their
> local path and the file URI scheme." · "Use an HTTP URL for the webhook." · "Use any local IP address
> for the webhook." · "Use any port for the webhook." · "Set *max_webhook_connections* up to 100000."
> · "Receive the absolute local path as a value of the *file_path* field without the need to download
> the file after a getFile request."

What that buys: outbound goes from 50 MB to **2000 MB**, a factor of forty. Inbound goes from 20 MB to
whatever the sender's own client could send — 2 GB, or 4 GB with Premium (§2.1, unconfirmed).

What it commits us to, and none of these is a footnote. The first is an owner decision that is still open;
the middle three are engineering work that §2.4 scopes; the last is a procedure, §2.4.4:

- **An `api_id` / `api_hash` from my.telegram.org, which is not the bot token and is not obtained from
  @BotFather.** The owner asked «مگه نباید یه ربات تو بات فادر بسازه و توکنشو بده» — should it not just be
  a bot in BotFather and its token? — and the two really are separate credentials, so it is worth two
  sentences rather than a footnote:
  - The **bot token** comes from **@BotFather**, identifies **the bot**, and is what the operator pastes
    into the panel (§9.1). Nothing about that changes.
  - The **`api_id` / `api_hash`** come from **my.telegram.org**, are issued against a **personal Telegram
    account** — a phone number, a login code — and identify **an application, not a bot**. The self-hosted
    server needs them because underneath it is TDLib: it speaks **MTProto** straight to Telegram's
    datacenters rather than HTTP to `api.telegram.org`, and every MTProto client must identify itself.
    On the cloud API you never encounter an `api_id` because Telegram's own server is that client.
    Self-hosting makes us that client, which is the whole reason the credential appears.

  The obstacle is not difficulty — it is a two-minute form. It is that the server ends up registered to a
  **person**, and the day somebody else operates this product that registration does not move with it.
  That is why §14.2 stays a blocker: it is an ownership question wearing a configuration question's
  clothes. The `api_id`/`api_hash` are secrets of the same class as the bot token. The server accepts them
  either as `--api-id`/`--api-hash` **or** as the `TELEGRAM_API_ID`/`TELEGRAM_API_HASH` environment
  variables, and it must be the environment variables, delivered by a systemd `EnvironmentFile` at mode
  `0600`: **a secret on a command line is readable by every user on the box through `ps`.** Never in the
  repo, never in the database, never on a panel screen.
- **A C++ build and a service to keep alive.** TDLib as a submodule, CMake, gperf, OpenSSL, zlib. Not
  hard; not nothing; and this machine has no Docker (M1 §4), so it can only be built and verified where
  it will run. §2.4.1 says where the source lives and what builds it; §2.4.5 says what that does to
  testing.
- **The server speaks HTTP only.** A TLS terminator has to sit in front of it for anything remote. On
  this box nginx is already there, so the marginal work is one server block — but it is one more thing
  that can be misconfigured into exposing an unauthenticated Bot API server to the internet, which is a
  full compromise of the bot. §2.4.3 concludes that the correct server block is **no server block**, and
  says what to do on the day that stops being true.
- **Disk.** The local server writes every file it handles to its own directory, and the README documents
  no automatic deletion. A product whose files are measured in gigabytes would fill that volume, and it
  is the same volume M3 §11 already reserves 2 × 1.31 GiB of spool on. Sweeping it is our problem.
  §2.4.2 designs the sweep and changes M3's startup free-space check.
- **`logOut` is a one-way door for ten minutes.** Moving a bot to a local server requires calling
  `logOut` on the cloud API first; after that the bot can log in locally immediately but **cannot return
  to the cloud server for ten minutes**. Being logged in on two servers at once loses updates. So the
  migration is a short, deliberate outage, not a toggle. §2.4.4 is the ordered procedure.

**What the decision does not change is worth stating before anything else, because it is the thing most
likely to be lost.** 2000 MB still does not carry the handoff's 214 GB archive or its 812 GB image. §3's
answer for an oversized file — the send button is absent, the card offers a share link instead, and the
inbound refusal points at the panel's uploader — is required in exactly the form it was written. The
threshold moved from 50 MB to 2000 MB and nothing else about §3 is different. Any future reader who finds
§3 and thinks "but we self-host now, so this is dead code" is wrong, and this paragraph exists to say so.

The seam that made the decision cheap, and that keeps the rollback cheap: **`Telegram:MaxSendBytes` and
`Telegram:MaxReceiveBytes` are configuration, never constants in code**, and no code path anywhere else
knows what they are. The transport is the same kind of seam — `Telegram.Bot` takes a `baseUrl` on
`TelegramBotClientOptions` and exposes a `LocalBotServer` flag for the `getFile` behaviour change, so
`Telegram:ApiBaseUrl` and `Telegram:LocalBotServer` are the only two keys that name which server we are
on. **Both are deployment configuration and neither is a row in `TelegramBotSettings`** (§7): an operator
who can repoint production at the cloud API from a web form is an operator who can do it by accident, and
the recovery from that is §2.4.4's ten-minute door, not a second click.

**Confirm against the installed `Telegram.Bot` version before writing the client** that `LocalBotServer`
exists under that name and that it is what suppresses the `GetInfoAndDownloadFile` URL construction. The
flag is documented, but this spec has not been read against the exact package version the solution will
pin, and a wrong assumption here surfaces as a download attempt against a path that is not a URL.

### 2.4 Running it: the obligations self-hosting creates

Three of §2.3's bullets are real work that nothing in this spec had scoped, and one is a procedure that
has an outage in it. They are here rather than in a runbook because getting any of them wrong is a
security incident or a full disk, and both are cheaper to argue about now.

#### 2.4.1 Where the server lives, and what builds it

The owner asked «چرا c++ نمیشه کنار یا دقیقا همگام با پروژه اصلی باشه؟» — why can the C++ not sit beside,
or exactly in step with, the main project? It can, and it should. A Bot API server whose version is
whatever somebody happened to compile on the box in March is a component nobody can reason about; §2.1's
numbers are read against a Bot API version, and the only way that sentence stays true is if the repo names
the version that runs.

**So the server is vendored into this repository as a pinned submodule, with its build script, its service
unit and its configuration committed beside it.** The layout mirrors the arrangement the sibling repo
already uses for its own native service — a directory under `deploy/` holding a build script, a systemd
unit, an install script and a README — and copies the arrangement only, nothing product-specific:

```
deploy/telegram-bot-api/
  vendor/telegram-bot-api/        git submodule, pinned to a tag, cloned --recursive
                                  (TDLib is itself a submodule of it)
  build.sh                        cmake configure + build + install to /usr/local/bin
  drive-union-bot-api.service     systemd unit: user, working directory, flags, restart policy
  install.sh                      build.sh, then unit install, then the first-run checks of §2.4.5
  nginx-if-it-ever-moves.conf.template   not installed; §2.4.3 explains why it exists unused
  README.md                       the pin, the bump procedure, and the §2.4.4 migration
```

This repository has no `deploy/` directory and no submodules today; this slice creates the first of each,
which is worth knowing before someone is surprised by a `git clone` that needs `--recursive`.

**It is explicitly not part of `dotnet build`.** No `.csproj`, no entry in `DriveUnion.slnx`, no MSBuild
`Exec` task that shells out to CMake. Three reasons, and the first is decisive: this machine is Windows
with no Docker (M1 §4), so a Linux C++ service cannot be built here at all, and wiring it into the
solution build would make the entire solution unbuildable on the machine the product is developed on — in
exchange for producing a binary that machine could not run. Second, the build is on the order of an hour
and several gigabytes of RAM (TDLib's compilation is the expensive part), which is not something a
`dotnet build` should ever do. Third, the two artefacts have different lifecycles: the panel is deployed
often, the Bot API server on a pin bump.

**What builds it, then:** `deploy/telegram-bot-api/build.sh`, run on the box, once per pin bump. A Linux
CI job that compiles the pinned tag and publishes the binary as an artefact is the better answer and is
worth adding as soon as there is a second deployment — it turns a bump from an hour of the live box's CPU
into a file copy. It is named here so that its absence reads as a decision rather than an oversight.

**What the pin buys, and what it costs.** It buys a deployment that can be reproduced and a spec that can
be checked: the version running is the version the repo names, `telegram-bot-api --version` on the
operator health card (§9.1) can be compared against it, and §2.1's figures describe the software that is
actually answering. It costs a manual bump, and the bump is a decision rather than a chore — **the pinned
tag must implement at least the Bot API version §2.1 was read against (10.2), and a bump that moves any
figure in §2.1 changes §2.1.** That link is the point of pinning; without it, Telegram ships a release and
this document becomes fiction with no event to mark the moment.

#### 2.4.2 The disk, which is the binding constraint

This is not an operational footnote. The owner's words:

> «من جا نداره سرورم جا داشتم که از تلگرام و گوگل درایو استفاده نمیکردم»

There is no room on the box; if there were room, neither Telegram nor Google Drive would be in this
product at all. So a Bot API server that leaves every file it touches on disk is close to disqualifying,
and everything below is a precondition for the feature working rather than housekeeping around it.

**What is actually known about that directory**, separated from what is assumed, because the difference
decides how much disk one send costs:

- **Layout is documented.** Files live in a per-bot subdirectory of the working directory, named by the
  bot's user id — verbatim from the server's own migration guidance: *"locate the bot's subdirectory in
  the working directory of the old server by the bot's user ID, move the subdirectory to the working
  directory of the new server"*. So the path to sweep is `<working dir>/<bot user id>/`, and §9.1's byte
  count is a walk of one directory.
- **No automatic deletion is documented anywhere I could read**, and two open upstream issues ask exactly
  this question (`tdlib/telegram-bot-api` #303 «how often does it clear downloaded files?» and #402) with
  no answer visible in either. Treat "the bot deletes them or nobody does" as the operating assumption; it
  is the only assumption whose failure mode is a wasted sweep rather than a full volume.
- **Three things I could not establish from here, and did not guess.** They go to §14.10 rather than into
  a number in this spec:
  1. **Does an upload get staged on disk?** When we stream a multipart body to the server — or hand it a
     `file://` path, which §2.2 says we never will — does it **copy** the bytes into the working directory
     before sending them to Telegram, or read them through? This is the difference between one outbound
     send costing **zero** disk and costing **one full copy** of the file, and with a 2000 MB ceiling that
     is the difference between a feature and an outage.
  2. **When is an inbound file written?** On update receipt, or only when `getFile` is called? One
     secondary source says only on `getFile`, which is what §11.3's control assumes. If it is eager, then
     "we never call `getFile` for an unbound chat" bounds our *work* but not our *disk*, and any stranger
     can push two gigabytes onto this box by messaging the bot. The design below does not depend on the
     answer — the sweep is not keyed on files we know about — but the risk assessment does.
  3. **Is a cloud-minted `file_id` valid against a local server?** §2.4.4 does not wait for this; it
     truncates the cache.

**Delete the local copy the moment the send succeeds. There is no waiting period.** This is the owner's
own proposal and it is right, and it is worth stating sharply because the belief it replaces is a
plausible one that would quietly become a retention setting nobody could justify later:

- **Wrong:** "keep it a minute, and the user has to forward it to Saved Messages first."
- **Right:** the instant `sendDocument` returns 200, **Telegram holds the bytes** — that is what a
  successful send means — and the response carries a `file_id` that re-sends them to any chat, for ever,
  with no size limit, no Drive read and no local copy (§3.2). The recipient does nothing. Nobody forwards
  anything. There is no window during which the local file is still load-bearing, because the local file
  was never what the recipient was reading from.

So the local copy has exactly one purpose — being the bytes of an in-flight operation — and it stops
having that purpose at a moment the code can observe precisely.

**The peak for one delivery, stated as a number somebody can check against the box.** Delivering a 2000 MB
file means: read 2000 MB from Drive, stream it as a multipart body to the local server, the server writes
it into `<working dir>/<bot user id>/`, sends it to Telegram, and we delete it. **Peak occupancy is one
copy — 2000 MB — held for the duration of the upload and no longer**, and it is *zero* if the answer to
unknown (1) above is that the server streams uploads through rather than staging them. Budget one copy. An
inbound 2000 MB file is the same arithmetic in the other direction, and unlike the outbound case it is
certain rather than assumed, because `getFile` hands us a path to a file that demonstrably exists.
§2.5 keeps this bounded: the bytes are only ever passing through, because Drive is where they live.

**Four mechanisms, in the order they act:**

0. **A free-space pre-flight, before the operation starts.** No byte-moving operation begins unless free
   space on the volume is at least `size + Telegram:WorkDirHeadroomBytes`. Beginning a 2 GB transfer onto a
   volume that cannot hold 2 GB fails at 98%, having done all the work, read all the bytes out of Drive,
   and filled the disk on the way out. The check runs at claim time in the drainer (§11.5) and at accept
   time inbound (§3.3); over the line the item stays queued and the chat gets §11.2's honest queue message.
   This is the mechanism the owner's constraint actually needs — the sweep cleans up, but only a pre-flight
   prevents.
1. **Delete on success, immediately, in a `finally`.** Outbound: the moment `sendDocument` returns, whether
   it returned 200 or an error. Inbound: the moment the Drive resumable session commits or fails. Both
   directions, both outcomes — a `finally`, not an `if (success)`, because the failure path is the one that
   leaves gigabytes behind.
2. **`TelegramWorkDirSweeper`, every minute, for the crash path only.** A `BackgroundService` in the app,
   not a cron entry and not a `find -delete` in the systemd unit, for one reason: **a shell one-liner has
   no test**. It deletes anything under `<working dir>/<bot user id>/` whose mtime is older than
   `Telegram:WorkDirMaxAgeMinutes`, default **30** — comfortably past the longest legitimate hold, which is
   one ceiling-sized transfer and its retries. Every minute rather than nightly, because a nightly sweep of
   a directory that can gain 2 GB per message is not a sweep.
3. **A watermark that outranks the age rule.** Below `Telegram:WorkDirMinFreeBytes` the sweeper deletes
   oldest-first regardless of age and the bot stops accepting byte-moving work in both directions until
   free space is back above the mark. Deleting a file that is 5 minutes old is destructive — it may be an
   in-flight transfer, which will then fail — and that is the correct trade: a failed transfer is one
   error message, a full volume takes Postgres and M3's spool down with it.

**What proves the sweep is working, and why the answer is not the one M4 §6.3 gives.** M4's rule —
a sweeper that deletes nothing must not look like a sweeper that had nothing to do — was written for a
sweeper whose deletions are the *normal* path. Here rule 1 does the normal work, so in a healthy
production the sweeper **should** find nothing, and a delete count of zero is the good state. Applying
M4 §6.3 naively would give an alarm that fires every minute. So the discipline is split:

- **In the test suite**, M4 §6.3 applies literally: seed old files, run the sweeper, **assert a non-zero
  delete count** (§12.16). Filesystem sweepers fail silently more easily than table sweepers do — a wrong
  path, a permissions error, or a `Directory.Exists` that returns false all produce exactly zero deletions
  and no exception — so the test is what proves the code can delete at all.
- **In production**, the health signal is not the delete count, it is **the directory's total size, which
  should sit at or near zero**. §9.1 renders bytes, file count, oldest file age and the last sweep's two
  numbers. **A non-zero directory size sustained across several minutes is the alarm**, in `--warn`, beside
  §9.1's rising `pending_update_count`, and it means rule 1 has stopped running.

**Permissions, which will bite before anything else does.** The server runs as its own unprivileged user;
the app runs as the panel's user; **the app has to delete files the server wrote**. That is a group on the
working directory with `setgid` so new files inherit it, and a umask on the service that leaves group
write on. The directory holds customer file contents exactly as M3's spool does, so it inherits M3 §11's
rules verbatim: outside `wwwroot`, served by no middleware, excluded from backups, restrictive mode. And
one rule that is new and points the other way: **the Bot API server's user must not be able to read M3's
spool directory.** They share a volume, not a directory, and §2.4.3 explains why that distinction is a
security boundary rather than tidiness. Also read `--help` on the box for the working-directory and
temporary-directory options and set **both** explicitly: a temp directory left on a small root volume is
the same problem discovered in a worse place.

**The arithmetic, and what to do when it does not fit — which on this box it may not.** M3 §11 reserves
2 × 1.31 GiB ≈ **2.62 GiB** of spool on this volume. Telegram's worst case is
`MaxConcurrentTransfers × (MaxSendBytes + MaxReceiveBytes)`: at the §11.5 default of 2 concurrent
transfers and a 2000 MB ceiling each way, that is **8 GB**, and it is **16 GB** if the answer to unknown
(1) above is that uploads are staged. Call it 11–19 GB together with M3's spool, on a box whose owner says
has no room.

**If it does not fit, the answer is not a smaller sweeper — it is a smaller ceiling.**
`Telegram:MaxSendBytes` and `MaxReceiveBytes` are configuration precisely so that this is a one-line
response rather than a redesign: set them to what the disk can actually hold with
`MaxConcurrentTransfers = 1`, and §3's over-the-ceiling branch — the share link outbound, the panel
uploader inbound — carries everything above it. **A 500 MB ceiling on a small disk is a working product;
a 2000 MB ceiling on a full disk is an outage.** This is the second time in this spec that §3 turns out
not to be dead code, and it is the more important of the two.

Concretely, **the startup check enforces the arithmetic**: the app refuses to start unless free space on
the volume covers M3's spool reservation plus
`MaxConcurrentTransfers × (MaxSendBytes + MaxReceiveBytes) + WorkDirHeadroomBytes`. **This is a change to
M3 §11's check, not an addition beside it** — M3's 2 × 1.31 GiB was computed when nothing else on the box
wrote gigabytes to that volume, and leaving the two checks independent means each one passes while the sum
does not. M3 §13.7 already asks somebody to read the box's free space; that question now has a much larger
answer and is restated as §14.9.

**One coupling worth naming out loud.** Deleting the file ourselves is only possible because the server is
on the same box. If it ever moves to another host, the local-path result of `getFile` is not merely
inconvenient, it is unusable — and the HTTP fallback is not a clean substitute either, since the upstream
issue tracker carries reports of `/file/bot<token>/<file_path>` returning 404 against a self-hosted server
in non-local mode (`tdlib/telegram-bot-api` #540; the reporter notes the local-mode path works precisely
because it is a local path). Moving the server off the box therefore means either running it non-local —
which gives back the 20 MB download limit and undoes the entire decision — or building a byte transport
between the two hosts. That is not a reason to avoid the optimisation; it is a reason the "same box"
assumption is load-bearing and should be written on the runbook rather than remembered.

#### 2.4.3 The front door, and why the answer is that there is not one

The server speaks plain HTTP. The instinct is to put nginx in front of it with TLS, since nginx is already
on the box. **The correct configuration is no server block at all**, and the reasoning is worth following
because the instinct is not stupid — it is just solving a problem we do not have.

Nothing outside this box needs to reach the Bot API server. Our app calls it over loopback, and — because
`--local` permits *"Use an HTTP URL for the webhook"*, *"Use any local IP address for the webhook"* and
*"Use any port for the webhook"* — the server calls **us** back over loopback too (§10.2). Both legs are
already inside the machine. A TLS terminator would add a public attack surface in exchange for encrypting
traffic that never leaves the kernel.

So:

- **The server binds to loopback only, on port 8081.** The port is documented — *"By default the Telegram
  Bot API server is launched on the port 8081, which can be changed using the option `--http-port`"* — but
  **the option that sets the bind address is not named on the documentation page and must be read from
  `telegram-bot-api --help` on the box**, which is where the pinned tag's real option list lives anyway.
  Whatever it is spelled, it is `127.0.0.1`: not `0.0.0.0`, not the box's public address. The systemd unit
  carries `IPAddressDeny=any` / `IPAddressAllow=localhost` as a second lock that does not depend on
  anybody having spelled a flag right.
- **No nginx `server` block, no `location`, no `proxy_pass` naming port 8081.** The committed
  `nginx-if-it-ever-moves.conf.template` (§2.4.1) is deliberately not installed. It exists so that the day
  the server moves to a different host the person doing it starts from a reviewed block rather than a
  search result, and it carries the rules below.
- **The firewall denies 8081 inbound** anyway, because a bind address is one typo away from a public
  listener and the two controls fail independently.

**Why this is not over-caution: an exposed Bot API server is a total compromise, and worse than that
phrase usually means.** Two reasons, and the second is specific to `--local`:

1. **The bot token is in the URL path** — `POST /bot<token>/sendMessage` — which is the only authentication
   the server has. Anything that can reach the port and has seen one URL is the bot. That also means
   **nothing may log these URLs**: not nginx's access log, not our `HttpClient` logging, not an exception
   message. The token in a log file is the token, and this is the one place in the product where a
   credential travels in a path rather than a header.
2. **In local mode the server will read arbitrary files from this box.** *"Upload files using their local
   path and the file URI scheme"* is a documented feature, and a feature that reads any path the server's
   user can read. §2.2 already decided we never construct a `file://` argument; that decision does not
   protect us, because the danger is somebody else constructing one. Reachability plus the token is
   therefore not only "send messages as the bot" — it is **arbitrary file read as the server's user**,
   which is why that user is unprivileged, owns only its own working directory, and specifically cannot
   read M3's spool (§2.4.2). Verbatim in the template: an internet-reachable Bot API server is a full
   compromise of the bot *and* a file-read primitive on the host.

If it ever does have to be reachable, the template's rules are: TLS 1.2+ only; an explicit `server_name`
and never `default_server`; `allow` the calling host and `deny all`; a client certificate or a shared
secret header on top of the token, because the token alone is a credential that appears in logs; and
`access_log off` for the proxied location with the reason written in a comment above it.

#### 2.4.4 The migration, as an ordered procedure with a named outage

`logOut` on the cloud API is irreversible for ten minutes, and a bot logged in on two servers loses
updates. So this is a change window, not a configuration flip, and it is written as steps because the
order is the thing that matters.

**Rehearse first, and rehearse with a different bot.** Create a throwaway bot in @BotFather, point the
built server at it, and take it all the way through: `logOut`, local login, `getMe`, `setWebhook`, one
file each way. This is the only way to discover that the server does not start, or that the `api_id` is
wrong, or that the working directory is unwritable, **before** the real bot is inside the ten-minute door.
Without the rehearsal the rollback in step 9 is the discovery mechanism, and it is a bad one.

Preconditions: §2.4.1 built and installed, §2.4.2's directory and sweeper deployed, §2.4.3's binding
verified from outside the box, `api_id`/`api_hash` in place (§14.2), and the rehearsal green.

1. **Stop the outbox drainer** (`Telegram:UpdateSource` moved to a paused state, or the service stopped).
   Nothing should be mid-send when the transport changes underneath it.
2. **`deleteWebhook` against the cloud API**, with `drop_pending_updates` **false**. It must be false here
   and at every later step. Updates arriving during the window are held by Telegram — `getUpdates`,
   verbatim: *"Incoming updates are stored on the server until the bot receives them either way, but they
   will not be kept longer than 24 hours"* — which is far more headroom than a ten-minute outage needs.
   Dropping them is the one way to turn that outage into lost customer files.
3. **Let the queue settle.** Any queued item stays queued; it will be sent by the new server.
4. **`logOut` against the cloud API. This is the irreversible step, and the outage starts here.**
5. **Truncate `TelegramFileId`** — every row, both bot ids. Whether a cloud-minted `file_id` is valid
   against a local server is undocumented (§2.1) and the cost of being wrong is a *wrong send*, which
   §3.2 explicitly designed the cache key to prevent. Truncating costs one re-upload per file that gets
   asked for again, and is correct whichever way the undocumented answer falls.
6. **Flip `Telegram:ApiBaseUrl` to `http://127.0.0.1:8081/`, `Telegram:LocalBotServer` to true, and the
   two size keys to §2.1's values.** Restart the app.
7. **`getMe` against the local server.** This is the proof that the token logged in locally; nothing after
   it is worth attempting until it answers.
8. **`setWebhook`** with `http://127.0.0.1:<kestrel port>/telegram/{fresh segment}`, a fresh secret, and an
   explicit `allowed_updates` (§10.2), then **`getWebhookInfo`** and read `last_error_message` — empty is
   the goal, and it is the same field §9.1 renders verbatim. Restart the drainer.
9. **Verify end to end**: one file in, one file out, and the working directory observed growing and then
   swept back to zero. If any of it fails, **the rollback is `setWebhook` back on the cloud API, and it is
   not available until ten minutes after step 4.** That window is the whole risk of this procedure and it
   is why step 0 is a rehearsal on a throwaway bot.

**The outage, named:** from step 2 to a green step 8, the bot is silent. Budget **ten minutes** and treat
anything under thirty as within expectations. Nothing else in the product is affected — the panel,
`/d/{slug}` and every transfer keep running, because the bot is a surface on top of them and not
underneath them. Customers see a bot that does not answer for a few minutes and then does; no message is
lost, provided step 2's `drop_pending_updates` was false.

**Moving between two local servers later is a different and much gentler procedure**, and it is documented,
so it is quoted rather than paraphrased. The blunt version is `logOut`: *"If the bot is logged in on more
than one server simultaneously, there is no guarantee that it will receive all updates. To move a bot from
one local server to another you can use the method logOut to log out on the old server before switching to
the new one."* The version that keeps state is better: *"remove the bot's webhook using the method
deleteWebhook, then use the method close to close the bot instance. After the instance is closed, locate
the bot's subdirectory in the working directory of the old server by the bot's user ID, move the
subdirectory to the working directory of the new server and continue sending requests to the new server as
usual."* That is also the second confirmation of §2.4.2's directory layout — per-bot, named by the bot's
user id. **`close`'s own error behaviour — it is reported to return 429 for the first ten minutes after a
launch — is not quoted here and needs confirmation** before anyone builds a runbook step on the timing.

#### 2.4.5 What can be verified on this machine, and what cannot

This machine is Windows, has no Docker (M1 §4), and the panel runs here directly. A Linux C++ service can
be built and verified only where it will run. That is not a small caveat for a slice whose transport is
now that service, so:

- **Nothing in the test suite talks to a Bot API server, cloud or local.** `ITelegramBotGateway` — the
  seam that shipped, §7.1 — is to Telegram what `IDriveClient` is to Google in M1 §4, and every test in
  §12 runs against the fake. Self-hosting does not change this and must not be allowed to argue for an
  integration test that needs the box.
- **The fake's default shape is the local server's, not the cloud's.** Its `getFile` returns an absolute
  path by default and a URL only where a test opts in. The branch that runs in production is the branch
  the suite exercises by default; the reverse arrangement is how a production-only bug gets written.
- **Development on this machine runs against `api.telegram.org` with a throwaway bot, polling (§10.1),
  with the cloud size limits in `appsettings.Development.json`.** There is no alternative — there is no
  local server here to talk to. The consequence is a permanent asymmetry between development and
  production in three specific places: the size ceilings, the `getFile` result shape, and the existence of
  the working directory. Those three are where a production-only defect will come from, and naming them is
  most of the defence.
- **The build is verified on the box, by a checklist rather than by xUnit**: `build.sh` completes, the
  service starts and stays up across a reboot, `telegram-bot-api --version` matches the pin, the bind
  address is loopback as seen from another host, `getMe` answers, and a file each way leaves the working
  directory empty within a minute. That checklist lives in `deploy/telegram-bot-api/README.md` and is run
  after every pin bump, not only the first time.
- **The box's RAM is a real prerequisite and nobody has read it.** TDLib's compilation is memory-hungry
  enough that a small VPS needs swap or a reduced build parallelism to finish at all. §14.9.

### 2.5 Drive is the storage. Telegram is delivery. — decided

The question was whether Telegram should hold the bytes, given §2.4.2's «من جا نداره سرورم». It is
answered:

> «تمام کار ها باید با گوگل درایو بشه… نیاز به فایل ایدی هم حتی نیست شما یا فایلی رو میفرستی که اپلود
> میشه رو گوگل درایو یا یه فایلی رو درخواست میکنی که برات تو تلگرام ارسال میشه ولی بازم تو گوگل درایو
> موجود هست پس نکته ای وجود نداره»

**Every byte a customer owns lives in the Drive pool, exactly as M1 and M2 designed. Nothing is ever only
in Telegram.** A file sent to the bot is uploaded to Drive; a file requested from the bot is read from
Drive and delivered — and it is still in Drive afterwards, which is the owner's «پس نکته ای وجود نداره».

That one decision removes three separate risks that a Telegram-as-storage design would have carried, and
they are worth naming so nobody re-opens them: the bot token never becomes the key to the library, so
losing or deleting the bot costs a feature rather than every file; Telegram's absent retention guarantee
stops mattering, because nothing depends on it; and using a bot as unlimited storage — tolerated rather
than permitted — is not something this product does. «ریسک های خود تلگرام قابل پذیرشه کسی انتظاری نداره
ازش», and the reason nobody has to expect anything of it is that nothing is entrusted to it.

It also means §3's over-the-ceiling branch is a *delivery* gap and never a storage gap. A 214 GB file the
bot cannot carry is not a file the product cannot hold; it is a file the product hands over as a link.
`TelegramFileId` stays, demoted to what it always was underneath: a performance cache for repeat sends
(§3.2), with no role in whether a file exists.

### 2.6 «اگر گوگل به مشکل خورد» — the escape hatch, and exactly which failures it survives

§2.5 gave the bot a second purpose the brief did not originally have:

> «اگر یه روزی مثلا مشکلی خوردیم ما بتونیم از تلگرام به عنوان بکاپ استفاده کنیم یعنی هدف اینه اگر گوگل به
> مشکل خورد یوزر بیاد فایلاشو مستقیم ربات براش تو تلگرام بفرسته یا هرلحظه به هردلیلی که لازم بود و یه مدت
> داشته باشه»

That is an availability requirement, not a convenience feature, so it gets what an availability
requirement deserves: a list of failures with an honest answer for each rather than a reassuring
adjective.

**The hole to confront first.** A delivery reads the file from Drive. If Drive cannot be read, the bot
cannot deliver either — the two paths are not independent, they share their first and most fragile leg.
Anyone who reads «اگر گوگل به مشکل خورد… ربات براش بفرسته» as "the bot works when Google does not" will
find out otherwise at the worst possible moment.

| What has failed | Panel and `/d/{slug}` | Bot delivery | Why |
|---|---|---|---|
| The customer cannot reach our box — filtering, a bad route, a slow or hostile link | Down for them | **Works** | The customer never talks to our box. Bytes go Drive → our box → Telegram → their client. **This is the failure the feature genuinely fixes**, and given where these customers are it is likely the common one. |
| Our HTTP surface is broken but the process is alive — nginx, an expired certificate, a bad Razor deploy | Down | **Works**, if the drainer runs | Different path, different port, no nginx and no TLS in front of it (§2.4.3). |
| The box is down | Down | Down | The bot is a surface *on* the box, not beside it. |
| Drive API returning 5xx globally | Downloads fail | **Fails** | Same leg. No help — and the refusal must say something true rather than the same «موقتاً در دسترس نیست» in two places. |
| One pool account disconnected, or its refresh token revoked (M2) | That account's files fail | **Fails, for those files** | Same leg. Files on other accounts are fine either way. |
| The 750 GB/day upload ceiling is spent (M3 §8.1) | Uploads blocked, downloads fine | **Works** | A delivery is a `files.get?alt=media` read, and reads do not spend the upload quota. Real value on a quota day. |
| `403 userRateLimitExceeded` on reads (M3 §8.2) | Throttled | Throttled | Same leg, same backoff. |
| The file was delivered before and its `file_id` is cached (§3.2) | — | **Works with no Drive call at all** | The one case where Telegram really is a second copy — opportunistic, not a guarantee. |

**So: Telegram is a second *path*, not a second *copy*.** It takes the customer's network and our HTTP
surface out of the equation. It does not take Drive out, and nothing in this slice can, because §2.5 put
all the bytes in one place on purpose.

**Except in one way, and it is the owner's actual answer.** «و یه مدت داشته باشه» — and let them have it
for a while. Once a file has been delivered, **the bytes are sitting in the customer's own Telegram chat,
in the customer's hands**. That copy survives our box, our Drive account, and us. It costs nothing to
provide and nothing to keep, and it is a real backup — with two limits that should be said out loud: it
exists only for files that were actually delivered, and it lasts only as long as the customer keeps the
message.

**Which puts it in direct conflict with §3.4**, where the same conversation asked for the delivery message
to delete itself after a minute. A message deleted after a minute is not a backup. Both requests are
reasonable and they are opposites; §3.4 proposes the resolution and §14.8 puts it back to the owner rather
than settling it for them.

**If a second copy is wanted that depends on neither Drive nor the customer, it does not come from this
slice, and pretending otherwise would be the expensive kind of optimism.** The product already has the
seam: M6 §9's export writes a file to S3-compatible storage — a second location, under our control, with
its own credentials and its own failure domain. That is the honest answer to «اگر گوگل به مشکل خورد». It
is one milestone away and it should not be confused with what the bot does.

**What T1 owes this section, concretely:**

- `/files` and the file card have to work when the panel is unreachable *to that customer*, which is most
  of the value in the table above. Nothing in the bot may require the customer to open the panel first,
  except linking — which by definition already happened.
- **The bot's failure messages must distinguish "this file cannot be fetched right now" from "you have no
  files" from "you are not linked".** From a chat, three different «خطایی رخ داد» look identical, and this
  is precisely the moment a customer needs to know which one they are looking at. §8.2's no-dead-ends rule
  already asks for it; this section is why it matters more than it looks.
- **Nothing anywhere may imply the bot is independent of Google.** Not `/help`, not the settings card, not
  a refusal string. The product's promise is a second path, and the copy should promise exactly that.

### 2.7 One bot or two — and why splitting the bots does not split the infrastructure

The owner proposed a split:

> «میتونی این دوتا تیکه رو جدا کنیم یعنی یه بات باشه فقط برای ارسال فایل ها از سمت گوگل درایو به تلگرام
> که خب نیاز داره به سرور، و یه ربات که با همون توکن عادی کار میکنه برای منیج کردن اکانت یوزر یا حتی
> ارسال فایل از تلگرام برای اپلود روی گوگل درایو»

**The load-bearing correction first, because the split is proposed on a premise that is not true.** The
delivery bot is described as the one that «نیاز داره به سرور» — needs the server — with the management bot
running on an ordinary token beside it. But **one `telegram-bot-api` instance serves many bots**: the
working directory is organised per bot, keyed by the bot's user id, which §2.4.4's documented move
procedure states directly. Adding a second token to the running server costs a second `setWebhook` and
nothing else. There is no second machine, no second build, no second unit, no second sweep.

So splitting the bots is a **product** decision, not an infrastructure one, and the only technical question
it raises is per-bot: **which endpoint does each token talk to?** Each token can be logged in on exactly
one server at a time — that is what §2.4.4's `logOut` enforces — so this is a real choice with a real cost
to change later.

**Shape 1 — both bots on the local server.** Two tokens, one server. Both get 2000 MB in both directions.
Costs are entirely in the product, listed below.

**Shape 2 — delivery bot local, management bot on the cloud API.** The literal proposal. **The cost that
must not be missed: a bot on the cloud API can download only 20 MB (§2.1).** «ارسال فایل از تلگرام برای
اپلود روی گوگل درایو» — the inbound job the owner assigned to exactly this bot — would therefore be capped
at 20 MB, and raising it later means `logOut` and the whole of §2.4.4 for a bot customers are already
using. In exchange the saving is **zero**, because the local server is running anyway for the other bot.

**Shape 3 — one bot doing everything, on the local server.** What §1–§13 designs.

**Recommendation: Shape 3 for T1. Shape 1 later if it earns its keep. Not Shape 2, in any circumstance.**
The reasons, in the order they matter:

- **A bot cannot message a user who has never started a chat with it.** This is the decisive one. With two
  bots the customer must press Start on the delivery bot as well as link in the panel, and until they do,
  every delivery silently has nowhere to go. That is a dead end of exactly the kind §8.2 forbids, and it
  is discovered by the customer rather than by us.
- **«شکیل و تمیز» is harmed directly.** Two bots with overlapping abilities is a maze. Someone will send a
  file to the delivery bot and get a refusal whose reason — wrong bot — is invisible from inside the chat.
- **The infrastructure saving is zero**, per the correction above.
- **The good reason to split is already solved.** The real argument for two identities is that a delivery
  backlog should not make account management unresponsive. §11.2's round-robin and §11.5's transfer slots
  already separate byte-moving work from chat replies *inside one bot*; splitting would solve it a second
  time with a second identity and a second onboarding step.
- **`file_id` is per bot (§3.2)**, so two bots means two caches and, for any file both ever send, two
  uploads of the same bytes.

**If the split is wanted anyway, this is exactly what it costs**, and the point of writing it now is that
none of it is a rewrite:

- `TelegramBotSettings` gains `Role` (`Delivery | Management`) and stops being the single row with `Id = 1`
  that §7 describes. One migration.
- `ITelegramIdentityReader.ResolveAsync` takes `(botUserId, telegramUserId)`. **§5's isolation argument
  survives untouched, and it is worth being precise about why:** the argument was never "there is one
  bot", it was "the resolver takes no tenant parameter and there is no ambient tenant" (§5.1). Adding
  `botUserId` makes the key *narrower*; it does not widen the scope. §5.2 through §5.5 are unchanged.
- **The customer links once, not twice.** The binding is between a panel user and a Telegram *user id*,
  and that is the same person on both bots, so §6.2's flow runs exactly once. What is genuinely per-bot is
  chat state — whether they have started that bot, and whether they have blocked it — so `DeliveryStatus`
  and `BlockedAt` move out of `TelegramAccount` into `TelegramBotChat { TelegramAccountId, BotUserId,
  HasStarted, DeliveryStatus, BlockedAt? }`. A seventh table. `ChatId` does not move: in a private chat it
  equals the user id, which is the same number for both bots.
- Two webhook endpoints, two path segments, two secrets, two rate-limiter policies. §10.2's controls apply
  twice and §12.11 and §12.13 gain a second case each.
- The settings card (§9.2) shows two connection states and has to explain why there are two. That is the
  part that cannot be made «تمیز», and it is the honest reason the recommendation is one bot.

§14.8 asks for the choice with this list attached.

## 3. Delivery in both directions — the ceiling, the cache, and the message afterwards

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

**The threshold is now `2_000_000_000` and the branch is unchanged.** Two things follow, in opposite
directions, and both are worth saying:

- **The visible win from §2.3 is exactly here.** Everything between 50 MB and 2000 MB — which is most of
  what a person actually sends: a video, a design file, a photo set, an installer — moves from "here is a
  link" to "here is the file", in the chat, as a document. That is what the owner bought, and it is the
  only place in the design where they will see it.
- **Deciding at render time matters more now, not less.** Under the cloud API a wrong decision cost fifty
  megabytes and a few seconds. Now it costs two gigabytes read out of Drive, several minutes of a transfer
  slot (§11.5), and a failure the user watched happen. A button that cannot succeed must not be drawn, and
  `SizeBytes` is sitting right there.

The test for this costs nothing despite the size, and §12.6 should not be weakened on the grounds that
2 GB is awkward to fixture: `SizeBytes` is metadata, the fake `IDriveClient` never produces bytes, and the
assertion is that `SendDocumentAsync` was never reached.

**The link is not a consolation prize, and the copy should not apologise for it.** A share link is what
this product is sold on (M1 §7): streamed, ranged, resumable, revocable, capped, and counted. A 3 GB
file delivered as a link is a *better* outcome for the recipient than a 3 GB file delivered as a Telegram
document, because it resumes. The bot says what it is doing in one line and moves on.

### 3.2 Outbound, the free lunch: cache the `file_id`

When we do upload bytes, Telegram's response carries a `file_id`. Re-sending that file to any chat is
then `sendDocument` with the `file_id` — **no size limit, no bytes leaving OVH, no Drive read, no
egress, no daily-quota consumption, and — the part that matters most on this box — no disk**. It is the
single largest performance decision in this slice and it is free.

**A ceiling of 2000 MB makes this worth forty times what it was worth at 50 MB**, and it changes what the
cache is for. Under the cloud API it saved a few seconds and fifty megabytes. Now the first send of a 2 GB
file is a Drive read, a multipart upload, minutes of a transfer slot (§11.5) and up to 2 GB of working
directory (§2.4.2); every send after it is one API call with a 64-byte argument, returning in
milliseconds. It is also what makes §2.4.2's delete-on-success safe rather than reckless: the reason we
can drop the local copy the instant the send returns is that the `file_id` in that same response is a
permanent handle to the bytes.

`TelegramFileId` (§7) caches it, keyed on `(StoredFileId, BotUserId)`. Keyed on the bot because a
`file_id` is unique per bot and cannot be transferred to another one: pointing the panel at a different
token must produce a cache *miss*, not a wrong send. **Whether a `file_id` also survives a move between
Bot API servers is undocumented (§2.1)**, which is why §2.4.4 truncates the table during the migration
rather than reasoning about it — the cache-miss-not-wrong-send property is exactly what makes truncating
a cheap answer.

Three consequences a reviewer should be told about rather than discover:

- **A cached re-send writes no `DownloadEvent` and does not move `ShareLink.DownloadCount`,** because no
  byte left this box and no link was involved. The counters stay honest about what they mean (M4 §6.1);
  they simply do not count this.
- **The bytes stay on Telegram's servers for as long as Telegram keeps them.** That is the property the
  cache exploits, and it is also a data-residency fact the product did not previously have. §14.7.
- **It is a performance cache and nothing more, and §2.5 is why that sentence is here.** A `file_id` is
  never the only handle to a file's bytes; the file is in Drive, always. So a cache miss, a truncation
  (§2.4.4), a rotated bot, or a `file_id` Telegram no longer honours all cost exactly one re-upload and
  can never cost a file. That is what lets §2.4.2 delete the local copy without ceremony and what lets
  the migration truncate the whole table rather than reason about it.

### 3.3 Inbound — 2 GB, an honest bridge above it, and bytes that are on our disk whether we like it or not

A user sends the bot a file. The update carries `document.file_size` (or `video`/`audio`/`photo`
equivalents) before we fetch anything, so again the decision is made before anyone waits:

- **Under `Telegram:MaxReceiveBytes`** (`2_000_000_000`, §2.1): `getFile`, then into a Drive resumable
  session through `IUploadCoordinator`. What sits between those two steps is not what it was, and §2.1's
  local-path result is why.
- **At or over it:** one reply naming the limit and linking to `{PublicBaseUrl}/Files/Upload`, which is
  the panel's chunked uploader and already carries 96 GB files. **This branch did not become dead code.**
  It now catches a Premium sender's 3 GB file rather than an ordinary 25 MB one, and it still catches
  everything the product actually exists to hold.
- **And there is a second ceiling now, which is the tenant's and not Telegram's.** The plans-and-quotas
  spec puts a per-file limit in `IUploadCoordinator.BeginAsync`, and it reasoned that the Telegram bridge
  would never trip it because Telegram's own inbound cap was 20 MB. At 2000 MB that is no longer true and
  **the plan's limit is usually the one that refuses first** (§13). The two refusals must not be the same
  sentence: "Telegram cannot carry this" and "your plan does not allow a file this large" have different
  next actions, and one of them is a link to the panel's uploader that would also refuse.

**The "no spooling" claim has to be withdrawn, and replaced with something better.** The original text
said `getFile` then stream the response body straight into Drive, no spooling, M1 §6's rule holding
without effort because 20 MB is a single final chunk. Both halves of that stop being true:

- **The file is spooled whether we like it or not, and not by us.** On a local server `getFile` returns an
  absolute path, which means the Bot API server has already written the bytes into its working directory
  (§2.4.2). There is no streaming-response-body option to prefer — there is no response body. We open a
  `FileStream` on a file that already exists.
- **At 2 GB it is not one final chunk.** M1 §6's 256 KiB alignment rule and M3's chunked writer both apply
  in full: reading from the local path and writing 256 KiB-aligned ranges into the resumable session, with
  the last chunk the only unaligned one. This is the ordinary upload path the product already has, fed
  from a file rather than from a request body, which is if anything simpler than what was written before.

**What that costs and what it buys, stated plainly rather than spun:**

- **It costs disk** — up to `MaxReceiveBytes` per in-flight inbound file, on a box that has none. §2.4.2's
  pre-flight check runs *before* `getFile`, not after: there is no point asking the server to materialise
  two gigabytes we cannot hold. Over the line, the item stays queued and the chat is told.
- **It buys a resume that the streaming design could not have.** A file on disk can be re-read. If the
  Drive session fails at 80%, the retry starts from Drive's confirmed prefix (M1 §6) against the same local
  file, instead of asking the user to send two gigabytes again. That is a real improvement and it exists
  only because the bytes are local.
- **It buys a real size instead of a claimed one.** `FileInfo.Length` on the local path is the truth. The
  byte counter below stays anyway — it costs nothing and it is the only defence if the local-path branch
  is ever swapped back for the URL branch (§2.1) — but the primary check moves earlier and gets harder.
- **It obliges us to delete it**, in a `finally`, on both outcomes. §2.4.2 rule 1.

`file_size` is optional in the API and may be absent. Treat absent as unknown, **and enforce the ceiling
with a byte counter on the copy anyway**, aborting the session past it. A declared size is a claim
(M5 §7 makes the same argument about `POST /api/uploads`), and here the claim comes from a third party.
With a local path there is a second, better check available before any byte is read — the file's actual
length — and the design uses both: the length gates the pre-flight, the counter guards the copy.

**A token-authenticated one-time upload URL — so the phone can upload without signing in — is explicitly
not built.** It would be a bearer credential that writes into a tenant's storage and spends their cap,
which M5 §5 already identifies as the single most dangerous handle in the product. It deserves its own
design, not a paragraph in this one. §15.

### 3.4 The delivery message deletes itself — with a button, not with a one-minute timer

> «اون ربات که فایل هارو از گوگل درایو داره میاره تو تلگرام داخل ربات بعد یک دقیقه حتی پیامش پاک بشه»

The intent is right and worth building: a chat should not silently become an archive of everything the
customer has ever asked for, and a document sitting in a chat is a copy in a place they may not want it.
The number is the problem.

**Two things are wrong with sixty seconds, and the second is the serious one.**

1. **A minute is shorter than the download.** A 2000 MB document takes about three minutes at 100 Mbit/s
   and closer to half an hour on an ordinary phone connection. A fixed 60-second delete lands in the middle
   of nearly every large transfer. **Whether deleting a message interrupts a client that is already
   downloading its file is not documented and I could not establish it — it must be observed, not assumed
   (§14.10).** The design treats it as though it does, because that is the failure mode that loses the file.
2. **It deletes §2.6's backup.** «یه مدت داشته باشه» — let them have it for a while — *is* the message.
   The one genuine second copy in this design is the bytes sitting in the customer's chat, and a
   sixty-second timer removes it. Both requests came from the same conversation and a short fixed timer
   cannot satisfy both.

**What the API permits.** A bot may delete its own outgoing messages in a private chat, and **a message
can only be deleted if it was sent less than 48 hours ago**; `deleteMessages` handles up to 100 at once and
skips what it cannot delete rather than failing. This is mirrored consistently across client-library
documentation but was **not read verbatim from `core.telegram.org` here — medium-high confidence**, and it
is on §14.10's list. Two consequences follow whichever way it is confirmed: any configured lifetime is
**clamped below 48 hours**, and a lifetime the code cannot honour is refused at configuration time rather
than discovered later as a silent no-op.

**What is not available, so that nobody designs around it.** Telegram does not tell a bot when a recipient
has finished downloading a document — there is no delivery receipt and no download callback. So the shape
that would actually be correct, "start the timer when the transfer completes", **cannot be built**. Any
design that claims to know the download finished is guessing, and this one does not.

**So:**

- **An explicit «دریافت کردم، پاک کن» button on the delivery message.** The only party who knows the
  download finished is the customer. One tap, the message goes, the chat stays clean, and nothing vanishes
  underneath a transfer. This is the default, and it is the whole of the feature in T1.
- **An optional timer, `Telegram:DeliveryMessageTtlMinutes`, default `0` — never.** Non-zero values are
  clamped to 2820 minutes (47 h). Whenever a timer is armed **the message says so**: «این پیام تا ۳۰ دقیقه
  دیگر پاک می‌شود». A message that disappears without warning is the same failure as a chat that stops
  answering, which §6.3 already refuses to ship.
- **If a timer is used, the floor is not a minute.** The minimum is the time the file plausibly takes to
  arrive: `max(10 minutes, SizeBytes ÷ 1 Mbit/s)` per message, which is about four and a half hours for
  2000 MB and ten minutes for anything small. That is a guess at a customer's link speed and it is written
  here as a guess; it is a much better one than sixty seconds.
- **Deletion is an outbox item**, `Kind = DeleteMessage`, with `NextAttemptAt` set to the deadline — not
  an in-memory timer, which does not survive the restart that a deploy makes routine. It also means the
  delete passes through §11.1's per-chat limiter like every other call.
- **Per-file opt-in is T2**, as «ارسال و حذف خودکار» on the card. T1 ships the button and the off-by-default
  timer.

**One honesty requirement that the settings card has to respect.** Deleting the message removes it from the
chat. **It does not remove the bytes from Telegram's servers**, and the `file_id` stays valid — which is
exactly what §3.2's cache depends on. Auto-delete is tidiness and chat privacy; it is not a data-residency
control, and §9.2 must not let a customer read it as one. §14.7's line is unchanged by this feature.

**Recommendation: the button in T1, the timer off by default, per-file opt-in in T2.** It gets the intent —
the chat does not become an archive — without deleting a download in progress, without deleting §2.6's
backup, and without the product asserting knowledge of a moment it cannot observe. §14.8 puts the
timer-versus-button choice, and its conflict with the backup story, back to the owner.

## 4. Decomposition

Telegram depends on M1 and nothing else *in this repository*. Since §2.3 it also depends on a Linux box
running a built Bot API server, which is not C# and cannot be produced here (§2.4.5) — so the decomposition
grows a slice at the front that contains no application code at all.

| # | Slice | Contents |
|---|---|---|
| **T0** | **The transport exists** | `deploy/telegram-bot-api/` — pinned submodule, `build.sh`, systemd unit, loopback binding, the `EnvironmentFile` carrying `api_id`/`api_hash`, working directory and its permissions, the §2.4.5 checklist, and the §2.4.4 migration rehearsed on a throwaway bot. No C#. It is listed as a slice rather than a prerequisite because it is a week's worth of ways to be wrong and because **it is where this feature's deployment risk lives** — every failure in it is invisible from the panel |
| **T1** | **Linked, and both directions work** | Operator's bot token and its panel screen, webhook + polling transports, account linking and unlinking, `/start` `/help` `/files` `/quota` `/unlink`, the file card, send-under-ceiling with `file_id` caching, link-over-ceiling, receive-under-ceiling, the «دریافت کردم، پاک کن» button and the off-by-default delete timer (§3.4), the outbox and its drainer, the transfer-slot bound (§11.5), **the free-space pre-flight and `TelegramWorkDirSweeper` (§2.4.2)**, per-chat and global rate limiting, stranger handling |
| T2 | Doing the work from the chat | «ساخت لینک», «لینک‌ها» with revocation, «حذف», `/search`, pagination, per-file «ارسال و حذف خودکار» (§3.4), FA/EN from `language_code`, `setMyCommands` per language |
| T3 | Operations | `getWebhookInfo` health card, working-directory size and sweep counters, delivery counters, blocked/deactivated classification surfaced in the panel, per-tenant outbox caps tuned against real traffic, the retention sweeps |

**The disk work is in T1, not T3, and that is deliberate.** T3 is where "the retention sweeps" would
naturally sit, and on the cloud API that would have been right. With a local server on a box with no
space, a T1 that ships without the pre-flight and the sweeper does not degrade gracefully — it fills the
volume M3's spool and Postgres are on, and takes the product down rather than the feature.

T1 is the only application slice worth shipping alone, in the same sense M1 is: it satisfies the owner's
two headline sentences, and it is where a mistake is a security incident rather than a missing button.
T0 ships before it and can be verified independently, which is the main argument for separating them.

**Part of T1 has already shipped, and it is the part that did not need the transport decision.** Identity,
linking, unlinking and the operator's bot-settings screen are built (§7.1) — §5, §6, §7's first three
tables and §9 — against `ITelegramBotGateway` with an implementation that honestly delivers nothing. That
ordering was luck rather than planning, but it is the ordering to keep: **everything in T1 that decides
which tenant a chat may read was buildable before anyone knew which Bot API server we would run**, and it
is now testable independently of a component this machine cannot even compile (§2.4.5). What remains of T1
is the transport itself, the outbox and its drainer, the file card, and both byte-moving directions.

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

**This shipped as designed, and gained two refusals the design had not named** (§7.1): a sender whose
panel user has no tenant, and a sender whose panel user is operator staff, both resolve to `null` and get
§5.3's stranger string. Operator staff have no tenant to answer out of, and the bot is a customer's
surface — so the only alternative to `null` would have been `Guid.Empty`, which is the exact failure M1 §8
is written about. The role is read as `Owner` for every linked customer until M5 adds a column, in one
method rather than inlined, so that the day it exists this is the only line that changes.

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
  deletes nothing must not look like a sweeper that had nothing to do. **As built it sweeps consumed rows
  too** (§7.1), which is right — after consumption a row's only job was to have been the thing that could
  not be consumed twice, and the binding it produced outlives it.
- **Pressing «اتصال» twice replaces the request rather than adding one.** `StartAsync` deletes the
  user's unconsumed tokens before minting the new one, so "the pending request" is unambiguous for the
  confirming POST and there are never two live deep links for one account. `Attempts` is deliberately not
  reset when a deep link is re-presented to the bot, or the five-guess budget becomes unbounded (§7.1).

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
                      CreatedAt, SentAt?, SentMessageId? }

TelegramFileId      { StoredFileId, BotUserId, FileId, FileUniqueId, SizeBytes, CachedAt }
                      PK (StoredFileId, BotUserId)

TelegramUpdateSeen  { UpdateId, ReceivedAt }   PK (UpdateId)
```

The decisions inside that, in the order they will be questioned:

- **`TelegramBotSettings` is a single global row with no `TenantId`**, like M2's `PoolSettings`. The bot
  is the operator's. Three secrets live in it — the token, the webhook secret, and the random webhook
  path segment — and all three are encrypted with the same `ITokenProtector` that protects the Google
  refresh tokens, under M1 §5's rule that the Data Protection keys are persisted to the database.
- **Which Bot API server we are talking to is deliberately *not* in this table.** `Telegram:ApiBaseUrl`
  and `Telegram:LocalBotServer` are deployment configuration (§2.3), alongside the two size keys. Putting
  them in `TelegramBotSettings` would put a dropdown on a web form whose wrong value is §2.4.4's
  ten-minute door, and whose right value nobody can verify from a browser. The operator screen *displays*
  which server is in use (§9.1); it cannot change it.
- **`TelegramAccount` has no `TenantId`.** §5.1 explains why, and the absence is the same kind of
  load-bearing absence as `GoogleAccount` having none.
- **`TelegramOutbox.TenantId` is not nullable**, exactly like `Job.TenantId` (M3 §4). The drainer is
  sessionless and the row is the only tenant identity it has. There is no system-owned outbox item.
- **`TelegramOutbox.SentMessageId` is what makes §3.4 possible**, and it is the reason the column exists
  rather than being derivable. A `SendDocument` item records the message id Telegram returns; a
  `Kind = DeleteMessage` item carries that id in `Payload` with `NextAttemptAt` set to the deadline. The
  queue is therefore the timer, which is the point — an in-memory timer does not survive the deploy that
  restarts the process, and «پیامش پاک بشه» failing silently after a release is precisely the class of bug
  this spec keeps designing against.
- **`TelegramFileId` is keyed on the bot** (§3.2).
- **`TelegramUpdateSeen` is the dedup table.** Telegram redelivers an update when the webhook answers
  non-2xx or times out, and a redelivery must not upload the same file twice or send the same document
  twice. This is the Telegram analogue of M3 §3.3's `duJobId` probe: the retry is not a hypothetical, it
  is the documented behaviour. Insert-on-conflict-do-nothing; a conflict means "already handled, answer
  200 and stop". Rows older than 7 days are swept.
- **`DeliveryStatus`** is `Active | Blocked | Deactivated`, set from the two 403 reasons in §11.4. It is
  what the settings card renders as «مسدود شده در تلگرام», and it is what stops the outbox retrying into
  a wall for ever.

### 7.1 What has actually been built, and where the code is the newer thought

Three of these six tables now exist, in migration `20260823214553_TelegramIdentityAndLinking`:
`TelegramAccount`, `TelegramLinkToken` and `TelegramBotSettings`, with `ITelegramIdentityReader`,
`ITelegramLinkService`, `ITelegramBotSettingsStore`, `ITelegramOperatorView` and `TelegramController`
behind them. **`TelegramOutbox`, `TelegramFileId` and `TelegramUpdateSeen` land with the transport, so
"one migration" was wrong: it is two, and the split is the right one** — the identity half has no
dependency on which Bot API server we end up talking to, which is what let it ship while §2.3 was still
being decided.

Where the implementation and this document disagree, the implementation is the later thought and this is
the record of it:

- **`TelegramBotSettings` shipped without `UpdateSource`, `WebhookPathSegment`, `WebhookSecretProtected`
  and `WebhookRegisteredAt`.** Correct: all four describe a registration that cannot exist before there is
  a transport, and a nullable column nothing writes is a column that gets misread as "not configured yet"
  when it means "not built yet". They belong to the transport migration.
- **`BotUserId` is parsed from the token rather than fetched with `getMe`** — it is the digits before the
  colon — and this is better than the design for a reason §3.2 depends on. The `file_id` cache is keyed on
  the bot, so the key must be knowable and must change when the bot changes: parsing gives it with no
  network call, before any transport exists, and it moves the moment an operator pastes a *different*
  bot's token, which is exactly the cache miss §3.2 asked for. It also survives a @BotFather token
  *rotation* for the same bot, because the prefix is the bot's id and not the secret — so rotating a
  leaked token does not invalidate the cache. `getMe` remains the authoritative check and arrives with the
  transport (§9.1); until then the shape is validated at the form, which turns "the bot never answers"
  into "this is not a token" on the screen where it can be corrected.
- **The outbound seam shipped as `ITelegramBotGateway`, not `ITelegramClient`.** One method today,
  `TrySendMessageAsync`, with `UnconfiguredTelegramBotGateway` returning false and saying so rather than
  pretending a farewell was delivered. Everywhere this document says `ITelegramClient` — §10.1's polling
  loop, §11.1's rate-limit buckets, §12's fake — read `ITelegramBotGateway`: it is the single outbound
  seam and the transport slice widens it rather than adding a second one. The rate limiters go in front of
  *it*, and the point of §11.1 was never the name but that there is exactly one place to put them.
- **The resolver refuses two cases the design did not name**, and both are right. A sender whose panel
  user has no tenant, and a sender whose panel user is operator staff, resolve to **null** — the stranger
  reply — because the bot is a customer's surface and operator staff have no tenant to answer with.
  `TelegramLinkService.StartAsync` refuses the same two cases at link time, so the row never gets in and
  the resolver's check is the backstop for one that did.
- **`TenantRole` is read as `Owner` for every linked customer until M5 lands**, in one method rather than
  inlined, and that is an accurate reading rather than a placeholder: with no role column, every member of
  a tenant can do everything the panel offers a tenant. §13's M5 note is what changes when the column
  arrives.
- **Linking gained four mechanics worth keeping.** At most one live request per user, because `StartAsync`
  deletes the previous unconsumed rows — so pressing the button twice replaces the link rather than
  leaving two that work. `Attempts` is **not** reset when a deep link is re-presented, or five guesses
  becomes as many as the guesser likes. The sweeper deletes **consumed** rows as well as expired ones,
  since a consumed row's only job was to be unconsumable twice. And expiry is deliberately kept out of the
  SQL predicate in the consuming `UPDATE`, because SQLite stores a `DateTimeOffset` as text and will not
  compare one — the same reason `PublicLinkReader` gives, and it keeps one rule rather than one per
  database.
- **`/telegram/link` is its own page for now**, not the «تنظیمات» card of §9.2, because that screen is
  M5's and does not exist yet. The card moves when it does; nothing in the flow changes with it.

**One gap against this design, which is a defect rather than a decision.** `TelegramStartRequest` carries
the sender's `Username`, `DisplayName` and `LanguageCode`, and `PresentAsync` receives them — but nothing
persists them, so `TelegramAccount` is written with all three null. §9.2's linked card is specified to
show the display name and `@username` and will show neither, and §8.4's language selection has nothing to
read. The values exist at the door and are dropped one step before the row that wants them.

`TelegramBotSettings` is seeded with its single row, empty, at `UpdatedAt = UnixEpoch`, which the read
model translates back to "never saved" rather than showing 1970 on a screen.

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
  `sendChatAction: upload_document` immediately and then the document. At a 2000 MB ceiling "immediately
  and then" hides several minutes, and the paragraph after this list is about that.
- **ساخت لینک** — `IShareLinkService.CreateAsync` with the configured defaults, replying with the URL in
  a monospace span so it is one tap to copy. Expiry and cap are not editable from the chat in T1: a form
  in a chat is four messages, and this is the kind of feature that turns a clean bot into a maze. §14.5.
- **لینک‌ها (۲)** — this file's links with their state, and «باطل کردن» each, through
  `IShareLinkService.RevokeAsync`. Two-step confirm, because M4 §2 makes revocation permanent: the slug
  is burned for ever.
- **حذف** — `IFileCatalog.DeleteAsync`, two-step confirm, Uploader and above under M5 §2.

**A send that takes minutes, which is what a 2000 MB ceiling means, needs three things the 50 MB design
did not.** All three are consequences of §2.3 that the card would otherwise get wrong:

1. **`sendChatAction` has to be repeated.** A chat action is documented as lasting about five seconds. Sent
   once at the start of a four-minute upload, the chat shows "uploading" for five seconds and then looks
   idle for the rest — the exact appearance of a broken bot that §8.2's third rule exists to prevent. The
   drainer re-sends it roughly every four seconds for the life of the transfer. It is cheap and it is not
   a message, so it does not spend §11.1's per-chat budget.
2. **The card is edited twice, not continuously.** On claim it becomes «در حال آماده‌سازی…»; when the send
   returns it becomes the delivered state with §3.4's «دریافت کردم، پاک کن». **No percentage**, and the
   reason is not laziness: §11.1 allows one message operation per chat per second, an edit is a message
   operation, and a progress bar that respects that limit updates once a second for four minutes — sixty
   times the cost of the two edits that carry the same information. M3 §3.4 made the same call about copy
   progress for a different reason and reached the same place.
3. **The failure has to land on the card, not as a new message.** A transfer that fails after three minutes
   edits the card to say so and offers «تلاش دوباره» and «ساخت لینک». §2.6 is why the wording matters: the
   customer needs to be able to tell "Drive is unreachable" from "you are over the ceiling".

Digits follow M2 §9's rule, which the panel already holds to: **counts in Persian digits, quantities
carrying a unit in Latin** — «۳ روز پیش» and «لینک‌ها (۲)» against `18.4 MB`. At the new ceiling the card's
second line renders sizes up to `2.0 GB`, which is the same rule and worth a fixture.

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
  `TelegramFileId` cache key (§3.2). **It arrives with the transport, and until then the screen gets both
  values without it** (§7.1): the bot id is parsed out of the token, and the `@username` is typed by the
  operator and validated against Telegram's own 5–32 character rule. That is enough to build a working
  deep link, which is why linking could ship before any transport existed — but it is **not** proof the
  token works, and the screen must not imply that it is until `getMe` is there to say so.
- **Update mode** — webhook or polling (§10). In webhook mode «ثبت وبهوک» calls `setWebhook` with a
  freshly generated secret, a fresh path segment and an explicit `allowed_updates`; «حذف وبهوک» calls
  `deleteWebhook`.
- **Health, most of which is Telegram's own answer.** `getWebhookInfo` returns `url`,
  `pending_update_count`, `last_error_date`, `last_error_message`, `ip_address` and `max_connections`.
  Rendering `last_error_message` **verbatim** is the single most useful thing on the page: it is Telegram
  saying why it could not reach us, in its own words, and paraphrasing it throws away the only diagnosis
  available. Beside it, our own four numbers — updates processed in 24 h, outbox depth, sends failed in
  24 h, and linked accounts as a bare count.
- **The Bot API server itself, which is new and which nothing else in the product can see.** Four
  read-only facts: which endpoint we are configured against (`Telegram:ApiBaseUrl`, displayed and **not
  editable** — §7), the running `telegram-bot-api --version` next to the tag the repo pins (§2.4.1), and
  the working directory's **total bytes, file count and oldest file age** (§2.4.2). Beside them, free
  space on the volume against the startup check's requirement, because that number is shared with M3's
  spool and is the one that ends the feature when it runs out.
- **Two alarms, not one.** A rising `pending_update_count` is what a broken webhook looks like from the
  outside while everything on this box appears perfectly healthy. **A working directory whose size stays
  above zero across several minutes** is what a stopped delete-on-success looks like while everything
  about the bot appears perfectly healthy. Both in `--warn`; the second is the one that fills the disk.
- **`logOut` and `close` are not buttons on this screen, and that is deliberate.** A mis-tap would be
  §2.4.4's ten-minute outage, reachable by anyone with the operator role and no way back. They are runbook
  steps, executed deliberately, on a box, by someone who has read §2.4.4.

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
- The data-residency line from §14.7, above the button rather than below it. **It says that a file sent
  through the bot passes through Telegram's servers and stays cached there. It must not say that deleting
  the message undoes that** — §3.4 removes a message from a chat and nothing else, and a card that implies
  otherwise is a privacy promise the product cannot keep.
- **One line about what the bot is for beyond convenience**, per §2.6: it is a second way to reach your
  files when you cannot reach the panel. Worded so it never implies independence from Google, because it
  is not independent of Google and the design cannot make it so.

**Linked:** the Telegram display name and `@username`, the date it was linked, and «قطع اتصال». **Never
the numeric Telegram id** — it is an identifier the customer has no use for and support does not need on
a screen. When `DeliveryStatus` is not `Active` the card reads «مسدود شده در تلگرام» with one line about
unblocking, because the fix is on the customer's phone and nowhere else (§11.4).

**When the operator has configured no bot at all**, the card says so plainly instead of drawing a button
that fails. Same rule M2 §8 applies to the settings screen's unbuilt sliders: a control that cannot work
must not be rendered as though it can.

## 10. Updates: webhook or long polling

Both are documented, both work, and they fail differently.

**Read this section knowing that §2.3 removed most of it.** The constraints below are the *cloud* API's,
and they are what development on this machine lives with (§2.4.5). In production the webhook is registered
against our own server, which documents away four of the five: *"Use an HTTP URL for the webhook"*, *"Use
any local IP address for the webhook"*, *"Use any port for the webhook"*, *"Set max_webhook_connections up
to 100000"*. §10.1 and §10.2 are rewritten around that below; the list is kept because it is what the
rollback target and the development environment still obey.

**Webhook against `api.telegram.org`** — the verified constraints:

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

The original argument was that the OVH box already terminates HTTPS on 443 for the panel and for
`/d/{slug}`, so the webhook's one hard prerequisite was already paid for. **That argument is now moot in
the best possible way: against our own server there is no prerequisite at all.** The webhook URL is
`http://127.0.0.1:<kestrel port>/telegram/{segment}` — plain HTTP, an arbitrary port, a loopback address,
all three explicitly permitted by the local-server documentation — so the update never crosses the
internet, never touches nginx, never touches TLS, and never involves a certificate that can expire at
three in the morning. That is a straightforward reduction in the number of things that can break.

Three more reasons specific to this product, unchanged by any of it:

- **A webhook is a controller action.** No `BackgroundService`, no lease, no restart recovery, and no
  second long-running thing that can silently do nothing. M2 §10 and M3 §5 are both written about that
  exact failure — a sessionless worker that reports success over an empty result set — and not creating
  one is worth a lot here.
- **A poller must be a singleton across the deployment.** Two instances calling `getUpdates` produce
  `409 Conflict: terminated by other getUpdates request`, and the symptom is intermittently missing
  messages, which is invisible in development and maddening in production. Enforcing single-instance
  polling means a lease, which is M3's machinery imported into a slice that otherwise does not need it.
- **Latency.** An update arrives when it happens rather than on the next poll.

**Do not raise `max_connections`.** The local server permits *"max_webhook_connections up to 100000"*,
which is an invitation to point a hundred thousand concurrent POSTs at a single Kestrel process on a box
that has no disk. Leave it at the cloud default of 40. A configuration key whose maximum is that far
outside anything sane should be treated as a hazard, not a feature.

**What the webhook costs, honestly.** This machine has no public HTTPS and no local Bot API server
(§2.4.5), so nobody can run the bot here without either a tunnel or the other transport. Therefore:

**Both are implemented, behind one configuration key** — `Telegram:UpdateSource = Webhook | Polling` —
and **polling is what runs in development.** That is not indecision. The polling client is a thin loop
over `ITelegramClient`, and without it the bot cannot be exercised on the machine the product is
developed on — the same constraint that put `IDriveClient` in Core (M1 §4). Production uses the webhook.
Switching is one setting plus one `setWebhook`/`deleteWebhook` call, both of which the operator screen
exposes as buttons.

### 10.2 The webhook endpoint is the product's fourth anonymous surface — now reachable only from the box

After `/d/{slug}`, `/d/{slug}/file` and `/accounts/callback`. In production it differs from those three in
one important way: **it is bound to loopback and no nginx location routes to it**, so the set of clients
that can reach it is "processes on this machine" rather than "the internet". That is a large reduction and
it changes exactly one control below. It does not make the endpoint safe by itself — anything on the box
that can open a socket can still POST an update, and the Bot API server is not the only process there —
so the secret token remains *the* control rather than a formality. It gets:

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
- **The IP control changes, and this is the one place self-hosting genuinely improved it.** Against the
  cloud API an allow-list was rejected as a primary control: the documented subnets `149.154.160.0/20` and
  `91.108.4.0/22` are explicitly subject to change, and the box sits behind a proxy whose forwarded headers
  have to be trusted correctly for a source address to mean anything at all — M4 §6.1 and `Program.cs`'s
  `DriveUnion:TrustedProxies` comment describe how quietly that goes wrong. Against our own server the
  allow-list is one entry, `127.0.0.1`, it will never change, and **the forwarded-header problem disappears
  entirely because nginx is not in the path**. So `Telegram:TrustedSubnets` stops being an empty
  defence-in-depth key and becomes a real, enforced control set to loopback — while the secret token stays
  primary, because a second process on the box also comes from `127.0.0.1`. In development, against the
  cloud API, the key returns to being empty and the old reasoning applies unchanged.

### 10.3 Answer 200 immediately; move bytes elsewhere

Telegram redelivers on a non-2xx or a timeout. A `sendDocument` inside the webhook handler would hold the
request open and be redelivered — and the redelivery would send the file twice. **At 50 MB that argument
was strong; at 2000 MB it is not an argument, it is arithmetic**: the handler would be open for minutes,
guaranteeing the redelivery, and each redelivery would start its own multi-gigabyte transfer.

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

Both live in one place, in front of the single outbound seam — **`ITelegramBotGateway`, which is what
shipped; §7.1** — so no call site can route around them.

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
- **We never call `getFile` for an unbound chat**, and the stakes went up by a factor of a hundred. A
  stranger sending a 2 GB file must not be able to make this box pull 2 GB and write it to a volume that
  has no room (§2.4.2), and doing the identity check before the fetch is the whole of that control.
  **With one caveat that has to be verified rather than assumed:** if the local server writes an incoming
  file to disk when the update arrives rather than when `getFile` is called, this control bounds our work
  but not our disk, and the only thing standing between a stranger and the volume is §2.4.2's watermark.
  §14.10 asks the question; until it is answered, the watermark is not optional.
- The dedup table (§7) also bounds the cost of a redelivery storm.

### 11.4 The two 403s worth naming

A user who blocks the bot produces `403 Bot was blocked by the user` on the next send; a deleted account
produces `403 Forbidden: user is deactivated`. Both set `TelegramAccount.DeliveryStatus`, stop the outbox
retrying, and surface on the customer's settings card as «مسدود شده در تلگرام» — which is the only place
that fact is useful, because the fix is on the customer's phone.

Everything else is logged **verbatim** and retried with backoff. M3 §8.2's discipline applies: tighten the
classifier in the first week from real log lines rather than from a mapping guessed now.

### 11.5 A 2 GB send is not a message, and the drainer has to know the difference

Everything above §11.4 rate-limits *messages*. A 50 MB `sendDocument` was near enough to a message that
the distinction did not matter. A 2000 MB one is not, and four things change. None of them changes §11.1's
two buckets, which is worth stating first: **the per-chat 1/s and global 25/s limits are unaffected**,
because they count API calls, and one upload is one call however long it takes. What a long upload
consumes is not rate budget — it is a worker, a connection, an uplink and a disk (§2.4.2).

- **The drainer needs a concurrency bound it did not have.** `Telegram:MaxConcurrentTransfers`, default
  **2**, limiting outbox items that move bytes; short items — text, cards, callbacks, `DeleteMessage` —
  are not counted against it and are never blocked behind one. Without the split, twenty queued deliveries
  saturate the uplink, the disk and every worker at once, and the chat replies that would explain what is
  happening are stuck behind the transfers causing it. Two, for M3 §11's reason: enough for the steady
  state, with the box's uplink and §2.4.2's arithmetic both sized against it.
- **The claim query's fairness ordering now matters much more.** §11.2's round-robin was written when a
  backlog meant seconds of other tenants' latency. One tenant queueing fifty 2 GB deliveries occupies both
  transfer slots for hours, so the ordering `(least-recently-served tenant, created_at)` is applied to the
  transfer slots specifically, not only to the queue as a whole.
- **The queue bound has to count bytes, not just items.** `Telegram:MaxQueuedPerTenant = 50` at the old
  ceiling was 2.5 GB of pending work; at the new one it is 100 GB, which is days. So a second bound,
  `Telegram:MaxQueuedBytesPerTenant`, default **20 GB**, and whichever is reached first produces §11.2's
  «چند درخواست در صف دارید» answer. A bound in items only is not a bound.
- **The HTTP timeout must be per-operation.** `HttpClient`'s default 100-second `Timeout` covers the whole
  request including the body, so a 2 GB upload dies at 100 seconds regardless of progress. `ITelegramClient`
  therefore sets `Timeout.InfiniteTimeSpan` on the client and applies a `CancellationToken` deadline per
  call: seconds for a `sendMessage`, and a generous size-derived budget for a transfer. This is a one-line
  bug that produces a feature which works in every test and fails on every real file.

**And retries get cheaper attention than they did.** M3's `Jobs:MaxAttempts = 5` is right for work that
costs a request. A byte-moving Telegram item costs its own size twice over on every attempt — once read
out of Drive, once pushed to the server — so five attempts on a failing 2 GB delivery is 20 GB of Drive
reads and egress that M6 will bill, for something that has already failed four times. **Byte-moving kinds
get `Telegram:MaxTransferAttempts = 3`; everything else keeps 5.** §11.1's 429 park still does not spend an
attempt, for the reason it never did.

## 12. Tests that hold these lines

Every one runs against a fake `ITelegramBotGateway` (§7.1 — the design called it `ITelegramClient`) and
M1's fake `IDriveClient`. **Nothing in this suite reaches Telegram or Google**, for M1 §4's reason, and
§2.4.5 adds that this holds for the local Bot API server too: no test talks to one, and the fake's default
`getFile` shape is the local server's.

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
14. **Both `getFile` shapes.** The fake returns an absolute local path by default (§2.4.5) and a URL only
    where a test opts in. The local-path case opens the file, streams 256 KiB-aligned ranges into the fake
    `IDriveClient`, and **deletes the file**; the URL case streams the response body. Neither branch may be
    unreachable, because production runs one and development runs the other.
15. **The local copy is deleted on failure too.** A `getFile` whose Drive session then throws still leaves
    the working directory empty. Asserted on the filesystem, not on a mock, because §2.4.2 rule 1 is a
    `finally` and a mock cannot tell a `finally` from an `if`.
16. **The sweeper deletes something.** Seed files older than `WorkDirMaxAgeMinutes`, run one pass, assert a
    **non-zero** delete count and non-zero bytes (M4 §6.3). Plus the inverse that matters more here: a
    directory of *fresh* files is left alone, so the backstop cannot eat an in-flight transfer.
17. **The pre-flight refuses before it starts.** With free space reported below `size + headroom`, no
    `getFile` is called and no Drive session is opened; the item stays queued and the reply is the
    queue-full string. The point is that nothing was read out of Drive.
18. **Transfer slots and fairness under large items.** With `MaxConcurrentTransfers = 2`, three queued
    deliveries run two at a time; a text reply queued behind them is sent immediately rather than waiting
    (§11.5); and tenant B's single delivery is not behind tenant A's fifty.
19. **The delete-message item survives a restart.** A `Kind = DeleteMessage` row with a future
    `NextAttemptAt` is picked up by a freshly constructed drainer and deletes the recorded
    `SentMessageId` — the regression test for §3.4's "the queue is the timer".
20. **A configured TTL beyond the API's limit is refused at startup**, not silently truncated at run time
    (§3.4).
21. **The bound row keeps the sender's profile.** A `/start` carrying a `@username`, a display name and a
    `language_code`, confirmed through the panel, produces a `TelegramAccount` with all three set. This is
    the regression test for §7.1's one known defect: the values arrive on `TelegramStartRequest`, are
    read by `PresentAsync`, and are currently dropped before the row is written — so §9.2's card shows a
    blank name and §8.4 has no language to read. It is a two-column addition to `TelegramLinkToken` plus
    two assignments, and it is worth a test because nothing else in the product fails when it is wrong.
22. **Operator staff and tenant-less users cannot link and cannot resolve.** Both legs refuse (§7.1), and
    the resolver's refusal is asserted separately from the link service's — they guard different moments
    and a test that only covers one would let the other be removed as redundant.

## 13. What this touches in M2–M6 and in plans-and-quotas, and redesigns in none of them

- **M2 — pool and quota.** Nothing in the bot chooses a Google account; inbound uploads go through
  `IUploadCoordinator` and therefore through `IUploadTargetSelector`. The one thing inherited is M2 §4's
  `503 no_upload_target`, and the bot must render the **tenant** string, never the operator one that
  names blocked accounts (§5.5).
- **M3 — queue and worker.** The Telegram outbox is a separate table for the reasons in §10.3. **The seam
  if they are ever merged:** `Job.Type` gains `TelegramSend` and the chat id rides in
  `Job.TargetDescriptor`, which M3 §4 already reserved as `jsonb` for exactly this kind of addition. The
  fairness rule of §11.2 would have to move with it.
  **And one genuine contradiction, which is not a seam and has to be fixed in M3's code:** M3 §11 refuses
  to start with less than 2 × 1.31 GiB free on the spool volume, a number computed when nothing else on the
  box wrote gigabytes there. The Bot API working directory is on that volume and can hold
  `MaxConcurrentTransfers × (MaxSendBytes + MaxReceiveBytes)` — 8 GB at the defaults. **Two independent
  checks each pass while the sum fails**, which is the worst arrangement available. §2.4.2 replaces them
  with one check over both reservations, and M3 §13.7's "read the box's disk" question is restated with a
  much larger number as §14.9.
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
- **M6 — egress, and self-hosting broke the measurement.** The original note said both legs move bytes
  over the box's uplink, so they belong in M6 §10's `CountingStream` with one new `EgressSample.Direction`
  value, `ToTelegram`. **With a local Bot API server that is no longer what our counter measures.** Our
  `CountingStream` now wraps a multipart upload to `127.0.0.1`; the bytes that actually leave the box are
  sent afterwards by the `telegram-bot-api` process, which M6 cannot instrument at all. Inbound is worse:
  the download from Telegram is performed entirely by that process and never passes through our code.
  What to do about it, since M6 has not been written yet and this is cheap to get right:
  - **Keep counting at our `CountingStream`, and rename what it means in the code and on the chart.** For
    outbound it is a faithful 1:1 proxy for the uplink bytes the server then sends, but it is measured on
    loopback and a comment must say so, or somebody will later "fix" it by excluding loopback traffic.
  - **Inbound has no proxy and must not be invented.** The honest options are the server's own statistics
    port if it has one — **needs confirmation from `--help` (§14.10)** — or an interface counter, and
    M6 §10's chart should show inbound-from-Telegram as unmeasured rather than as zero. A zero on a chart
    is a claim.
  - A `file_id` re-send still contributes **zero**, which is the point of §3.2 and will look like an
    accounting bug to whoever reads the chart first.
  - M6 §9's export is also, per §2.6, the only real answer to «اگر گوگل به مشکل خورد», which raises its
    priority for reasons that have nothing to do with egress.
- **Plans and quotas — three agreements and one sentence that self-hosting made false.** That spec landed
  after §2.3 was decided and reasons from the old numbers in one place:
  - **The contradiction, and it is load-bearing rather than cosmetic.** Its §4.2 says *"Telegram inbound
    is bounded by Telegram's own 20 MB ceiling (Telegram §3.3), which is below any plausible per-file
    limit"*, and concludes the per-file check on the inbound bridge is a formality that runs anyway. **It
    is now 2000 MB (§2.1), which is above most plausible per-file limits**, so the check is the thing that
    actually refuses, and its refusal is a message a customer will see routinely rather than never. The
    conclusion in that spec — run the check in `IUploadCoordinator.BeginAsync` rather than in a controller
    — is unchanged and is now doing real work. Its sentence about the ceiling needs correcting; this spec
    cannot edit that file.
  - **A single «ارسال فایل» can now spend 2 GB of a tenant's monthly traffic**, where it could spend
    50 MB. That makes the bot the cheapest way in the product to exhaust a traffic allowance, and it means
    the plan's reserve-then-commit has to happen at **drainer claim time**, in the same pre-flight as
    §2.4.2's free-space check and §11.5's transfer slot — not when the button is pressed, which may be
    hours earlier. Three checks, one place, before any byte is read out of Drive.
  - **Its §9.4 and this spec's §3.1 already agree and should stay that way**: a size ceiling makes
    «ارسال فایل» *absent* because the customer cannot fix it; a traffic overage leaves the button
    *present* and explains, because it clears on a known date. §3.4's «دریافت کردم، پاک کن» is neither —
    it is on the delivered message, not on the card.
  - **Its §4.3 meters `ToTelegram` at our `CountingStream`**, which is the same loopback-measurement
    problem the M6 note above describes: the number is right as a 1:1 proxy and is measured somewhere that
    will look wrong to whoever reads the code. A cached `file_id` re-send meters zero in both specs, which
    is correct and which both already warn will read as a bug.

## 14. Before implementation starts

Nine things are needed from the owner and two are engineering tasks. **Items 1 and 2 block the transport,
which is now the whole of what is left of T1** — identity, linking and the operator's screen have already
shipped without them (§7.1), which is the strongest evidence that the seam between the two halves was
drawn in the right place.

**Three of the original questions are now answered and are recorded rather than asked**, so that a reader
does not go looking for an open decision that has been made: **the transport** is our own
`telegram-bot-api --local` (§2.3); **what stores the bytes** is Google Drive, with Telegram as delivery
only (§2.5); and **2000 MB** is an acceptable ceiling. What survives from the transport question is item 2,
which was always the harder half of it.

1. **The bot token from @BotFather**, and three settings alongside it: privacy mode **on**, group
   membership **disabled** (`/setjoingroups`), and the bot's display name and description. §5.2 answers
   only private chats, and a bot that *can* be added to a group will be.
2. **Whose personal Telegram account issues the `api_id`/`api_hash`.** This is not the bot token and it
   does not come from @BotFather — §2.3 sets out the distinction, and the short version is that the
   self-hosted server is TDLib underneath, speaks MTProto rather than HTTP, and every MTProto client must
   identify itself as an *application* registered to a *person*. **The obstacle is not difficulty; it is a
   two-minute form at my.telegram.org.** The obstacle is that whoever's phone number issues it is who the
   server is registered to, and that registration does not move if somebody else ever operates this
   product. It blocks the first commit because nothing about the local server can be built or rehearsed
   without it.
3. **Confirm §3's answer for an oversized file, in words.** Outbound: the bot hands over a share link,
   decided before anyone waits, with the «ارسال فایل» button simply absent. Inbound: a link to the
   panel's uploader. This is the single most visible compromise in the slice and it should be agreed
   rather than discovered. **Self-hosting moved the threshold from 50 MB to 2000 MB and changed nothing
   else about this answer** — 2000 MB does not carry the 214 GB archive or the 812 GB image — so the
   question is unchanged and only its number is different.
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
   card should say it in one line before they press «اتصال». **§2.5 makes this smaller than it was** —
   nothing is *stored* in Telegram, only passed through and cached — and §3.4 does not shrink it further,
   because deleting a chat message does not remove bytes from Telegram's servers.
8. **Three answers in one conversation, and two of them disagree.** They are grouped because deciding them
   separately is how a contradiction ships:
   - **One bot or two?** §2.7 recommends **one**, and corrects the premise the split was proposed on: one
     `telegram-bot-api` instance hosts both tokens, so splitting the bots saves no infrastructure and
     costs a second «Start» the customer must press before any delivery can reach them. If two are still
     wanted, §2.7 lists exactly what changes — and **not the shape where the management bot stays on the
     cloud API**, because that caps «ارسال فایل از تلگرام برای اپلود روی گوگل درایو» at 20 MB.
   - **The delivery message: a button or a timer?** §3.4 recommends a «دریافت کردم، پاک کن» button, with
     the timer off by default. One minute is shorter than a 2 GB download and would delete the message
     mid-transfer.
   - **And the contradiction to settle out loud:** «بعد یک دقیقه پیامش پاک بشه» and «یه مدت داشته باشه»
     cannot both be true. §2.6 shows that the only real second copy in this design **is** the message in
     the customer's chat; deleting it after a minute deletes the backup that was asked for. Pick which
     matters more, or accept §3.4's answer, which is that the customer decides per message.
9. **The box: free disk and RAM, as numbers.** M3 §13.7 already asked for the spool volume's free space
   against a 2.62 GiB reservation. Self-hosting makes that question much larger and much more urgent:
   §2.4.2's arithmetic needs M3's spool **plus** `MaxConcurrentTransfers × (MaxSendBytes + MaxReceiveBytes)`,
   which is 8 GB at the defaults, on a box whose owner has said «من جا نداره». **If the number is smaller
   than the requirement, the answer is a smaller `Telegram:MaxSendBytes`, not a smaller sweeper** — and
   that is a product decision, because it moves files from «ارسال فایل» to «با لینک بفرستید». RAM matters
   separately and only once: TDLib's compilation is memory-hungry enough that a small VPS may need swap to
   finish the build at all (§2.4.5).

**Two engineering tasks rather than owner decisions, and they come first.**

10. **The fifteen-minute verification, now with a longer list.** One live `curl` against a real bot token
    settles §2.1's medium-confidence rows: send a 30 MB file by HTTP URL and record the error, send a
    60 MB file by multipart and record the error, `getFile` a 25 MB document and record the error. Those
    three are against the **cloud** API and can be done today, before any server exists. Then, once T0 is
    built, the questions only a local server can answer — and this spec deliberately guessed at none of
    them:
    - Does an upload get **staged on disk**, or streamed through? This is the difference between one
      2000 MB delivery costing zero disk and costing a full copy (§2.4.2).
    - Is an inbound file written to disk **on update receipt or only on `getFile`**? If eagerly, a stranger
      can push bytes onto the box by messaging the bot, and §11.3's control does not cover it.
    - Does a **cloud-minted `file_id` work against the local server**? §2.4.4 truncates the cache either
      way; the answer only tells us whether that was necessary.
    - **`deleteMessage`'s real limits** — the 48-hour window is mirrored across client libraries but was
      not read verbatim from `core.telegram.org` here — and, by observation rather than documentation,
      **whether deleting a message interrupts a client that is mid-download** (§3.4).
    - Does the server expose a **statistics port** carrying byte counters? §13's M6 note has no honest
      inbound measurement without one.
    - Capture `telegram-bot-api --help` verbatim into `deploy/telegram-bot-api/README.md`, which settles
      the working-directory, temp-directory and bind-address option names that §2.4.2 and §2.4.3 have to
      leave open.

    Paste the raw responses into this file under a `§2.1 findings` heading.
11. **Rehearse the migration on a throwaway bot before touching the real one** (§2.4.4). It is the only
    step that turns `logOut`'s ten-minute one-way door from a risk into a formality, and it costs one
    @BotFather bot and an afternoon.

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
- **Telegram as a storage backend.** Decided against in §2.5: Drive holds every byte, Telegram carries
  copies. It is listed here rather than left implicit because it is the natural next idea once the bot is
  working and the disk is full, and because the three risks it would import — the bot token becoming the
  key to the library, an absent retention guarantee, and a use Telegram tolerates rather than permits —
  are avoided entirely by a decision that is one sentence long and easy to erode.
- **Automatic failover between the local server and the cloud API.** It sounds like a small resilience
  win and it is not available at all: §2.4.4's `logOut` is a ten-minute one-way door, so "fall back to the
  cloud" is a ten-minute outage and a truncated `file_id` cache, in both directions. The rollback exists
  as a deliberate, rehearsed procedure and must never be wired to a health check.
- **Exposing the Bot API server beyond loopback.** §2.4.3 — the token travels in the URL path and, in
  local mode, the server will read arbitrary files off the box. The nginx template exists unused so that
  the day this stops being true, somebody starts from a reviewed configuration.
- **Handing the server a `file://` path.** §2.2: our bytes are in Drive, so using it would mean writing
  the file to disk in order to avoid streaming it, which is worse on every axis on a box with no disk.
- **Running the panel and the Bot API server on different hosts.** §2.4.2's delete-on-success and §3.3's
  local-path read both assume one filesystem. Splitting them means either a byte transport between hosts
  or a non-local server, and a non-local server undoes §2.3 entirely.
- **Progress percentages on a long send.** §8.2 — an edit is a message operation, §11.1 allows one per
  chat per second, and a progress bar would spend sixty rate-limit slots to say what two edits already
  say.
