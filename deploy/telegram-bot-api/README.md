# The self-hosted Telegram Bot API server

Drive Union runs its own `tdlib/telegram-bot-api` instead of `api.telegram.org`. The cloud API
caps a bot at **20 MB inbound and 50 MB outbound**; the owner needs 2 GB. Running our own moves
that to **2000 MB in both directions**. Everything in this directory is what that costs.

The decision, and the full price of it, is the Telegram spec's §2.3. This file is how you carry
it out.

> **None of this has been run.** It was written on a Windows machine with no Docker, where a
> Linux C++ service cannot be built or started (spec §2.4.5). The shell scripts are syntax-clean
> and `sweep-workdir.sh` has had its safety checks and its delete path exercised against a fake
> directory tree; nothing else here has executed anywhere. The systemd units have never been
> parsed by systemd. Treat the first run as the first run. Where a flag or a behaviour was not
> confirmed, this file says so in those words rather than guessing — [What is not
> verified](#what-is-not-verified) is the list.

---

## What is in here

| File | What it is |
|---|---|
| `PIN` | The version, as one commit SHA. Everything else refuses to act on anything but this. |
| `fetch-source.sh` | Clone the pinned source, prove it is the pin, prove it implements Bot API 10.2. |
| `build.sh` | Preflight (RAM, disk, toolchain, destination), then CMake. Refuses before the hour, not after. |
| `install.sh` | User, directories, environment file, units, and the checks that turn a wrong config into a refusal. |
| `drive-union-bot-api.service` | The server. Unprivileged, loopback, hardened, secrets from an environment file. |
| `drive-union-bot-api.env.example` | Every key, no values. The real one lives in `/etc` and never in the repo. |
| `sweep-workdir.sh` | The disk sweep. Dry-run by default. |
| `drive-union-bot-api-sweep.service` / `.timer` | The sweep, every minute, as the service user. |
| `nginx-if-it-ever-moves.conf.template` | **Not installed.** The correct nginx config is none; this is the reasoning and the block for the day that changes. |

Nothing here is part of `dotnet build`. There is no `.csproj`, no entry in `DriveUnion.slnx`,
no MSBuild `Exec` shelling out to CMake — wiring an hour-long Linux C++ build into the solution
would make the whole solution unbuildable on the machine the product is developed on, in
exchange for a binary that machine could not run.

---

## The shape of it

```
  panel (Kestrel)  ──HTTP, loopback──▶  telegram-bot-api  ──MTProto, outbound──▶  Telegram
        ▲                                     │
        └────────HTTP webhook, loopback───────┘
```

Three things follow from that picture, and they are the three that people get wrong:

- **Telegram never connects to us.** Self-hosted, the webhook POST comes from a process on this
  box to another process on this box. The socket to Telegram is one we opened. So there is no
  public endpoint to expose and no TLS certificate to obtain.
- **The bytes land on our disk.** `getFile` against a local server returns an absolute path on
  this filesystem, not a URL. That is the optimisation and it is also the [disk
  problem](#the-disk-sweep).
- **Same box is load-bearing.** Move the server to another host and that absolute path becomes
  unusable, and the HTTP fallback is reported to 404 against a self-hosted server in non-local
  mode (`tdlib/telegram-bot-api` #540). Running it non-local instead gives back the 20 MB
  download limit and undoes the whole decision.

---

## The pin

`PIN` names one commit:

```
UPSTREAM_URL   https://github.com/tdlib/telegram-bot-api.git
PINNED_COMMIT  adfd7f6a8e990272851777eeb3ae0def4216f161
               "Update version to 10.2."  ·  2026-07-14
BOT_API_VERSION 10.2
```

### A commit, and a manifest, rather than a tag or a submodule

The spec's §2.4.1 says "a pinned submodule, pinned to a tag". Both halves turned out to be
unavailable, so this is neither, and the reasons are worth having written down:

**There is no tag.** `tdlib/telegram-bot-api` publishes none. Its releases page says there are
no releases and its tag feed is empty; upstream's own install instructions are
`git clone --recursive …` off the default branch with no pin at all. So the choice is a commit
SHA or nothing, and "whatever somebody compiled on the box in March" is the thing this directory
exists to prevent.

The commit chosen is not arbitrary: it is the commit that raised the implemented Bot API version
to 10.2, which is the version the spec's §2.1 figures were read against. It touches two files
and changes `10.1` to `10.2` in both.

**A manifest rather than a submodule**, for four reasons in descending order of weight:

1. **A submodule records a SHA and nothing else.** With no tags upstream, `git submodule status`
   would show forty hex characters. The thing that actually matters here — *which Bot API version
   is this, and does it still match §2.1?* — would not be in the repository at all. `PIN` carries
   the version, the date and the commit subject beside the SHA, and `fetch-source.sh` checks the
   source against the declared version and **fails** if they disagree. That check is the whole
   value of pinning, and a submodule cannot express it.
2. **It would push a C++ tree onto every .NET developer.** `telegram-bot-api` carries TDLib as
   its own submodule; a superproject submodule makes `git clone --recursive` mandatory for
   everyone, forever, to fetch a few hundred megabytes of source that only one Linux box can
   build. This repository has no submodules today.
3. **The failure mode of a forgotten `--recursive` is silence.** An empty submodule directory
   makes the build fail with a CMake message about a missing subdirectory. A fetch script that
   runs, reports what it cloned and verifies it is a louder mechanism at the moment it matters.
4. **`vendor/` is git-ignored here**, so a deployment checkout that has built the server does not
   show several gigabytes of untracked objects in `git status`.

What the manifest gives up: nothing in `git status` tells you the source tree has drifted off the
pin. That is recovered where it counts — `build.sh` re-verifies the checkout against `PIN` before
compiling and refuses on a mismatch, and warns if the tree has uncommitted modifications.

### Checking that the pin still matches Bot API 10.2

Three checks, in increasing cost:

```bash
# 1. Does the source that is here declare 10.2?  Offline, instant, no build.
./fetch-source.sh --verify-only
#    Reads two independent places:
#      CMakeLists.txt              project(TelegramBotApi VERSION 10.2 LANGUAGES CXX)
#      telegram-bot-api.cpp        parameters->version_ = "10.2";
#    Both must agree with PIN's BOT_API_VERSION or it is a hard failure.

# 2. Does the BINARY that is installed declare 10.2?  After a build.
telegram-bot-api --version
#    Compare against PIN. The operator health card (spec §9.1) renders exactly this
#    comparison, which is why Telegram:PinnedBotApiVersion is a configuration key.

# 3. Has upstream moved?  One network round trip, changes nothing.
./fetch-source.sh --check-upstream
```

### Bumping the pin — and why it is a decision, not a chore

**The pinned commit must implement at least the Bot API version §2.1 was read against, and a
bump that moves any figure in §2.1 changes §2.1.** That link is the point of pinning. Without
it, Telegram ships a release and the spec quietly becomes fiction with no event to mark the
moment.

```bash
./fetch-source.sh --check-upstream          # prints the range and how to read it
git -C vendor/telegram-bot-api fetch origin
git -C vendor/telegram-bot-api log --oneline adfd7f6a8e99..origin/HEAD
```

Read that range for a commit titled **`Update version to X.Y.`** — that is how upstream marks a
Bot API version change, and it is the same commit shape as the current pin.

- **No such commit in the range** — a maintenance bump. Edit `PINNED_COMMIT`,
  `PINNED_COMMIT_SUBJECT` and `PINNED_COMMIT_DATE` in `PIN`, leave `BOT_API_VERSION`, rebuild,
  re-run the [verification checklist](#verifying-it-is-serving).
- **There is one** — stop. Read
  <https://core.telegram.org/bots/api-changelog> for what changed between 10.2 and the new
  version, then re-read the spec's §2.1 table against the reference. If any of the five figures
  moved, §2.1 is edited in the same change as `PIN`. Then bump `BOT_API_VERSION` too, and
  `Telegram:PinnedBotApiVersion` in `appsettings.Production.json`.

There is no automation for this and there should not be. Nothing else on this box has a version
that a document's numbers depend on.

**The better long-term answer, named so its absence reads as a decision:** a Linux CI job that
compiles the pinned commit and publishes the binary as an artefact. It turns a bump from an hour
of the live box's CPU into a file copy. Worth doing as soon as there is a second deployment.

---

## `api_id` and `api_hash`

This is the credential that only self-hosting needs, and it is the one people expect not to
exist. It is **not** the bot token and it does **not** come from @BotFather.

| | Bot token | `api_id` / `api_hash` |
|---|---|---|
| From | @BotFather | <https://my.telegram.org> |
| Identifies | **the bot** | **an application**, registered to a **person** |
| Issued against | a bot | a personal Telegram account — a phone number and a login code |
| Lives in | the panel, encrypted in the database | `/etc/drive-union-bot-api/bot-api.env`, mode 0600 |

**Why it exists at all.** The self-hosted server is TDLib underneath. It speaks **MTProto**
directly to Telegram's datacenters rather than HTTP to `api.telegram.org`, and every MTProto
client must identify itself as a registered application. On the cloud API you never meet an
`api_id` because Telegram's own server is that client. Self-hosting makes *us* that client,
which is the entire reason the credential appears.

**Getting them** is a two-minute form: log in at my.telegram.org with a phone number, open *API
development tools*, fill in an application name and description, and it returns an `api_id` (a
number) and an `api_hash` (32 hex characters).

**The obstacle is not the form.** It is that the server ends up registered to a *person*, and
the day somebody else operates this product that registration does not move with it. That is an
ownership decision wearing a configuration decision's clothes, and it is why the spec's §14.2
blocks rather than defers. Decide whose account issues it before typing anything.

Both are secrets of the same class as the bot token. They go in the environment file and nowhere
else — never in the repository, never in the database, never on a panel screen, and **never on
the command line**, because an argument is readable by every user on this box through `ps`. The
unit relies on the server's own documented fallback to the `TELEGRAM_API_ID` and
`TELEGRAM_API_HASH` environment variables for exactly that reason.

---

## Building

### What to install

Upstream's dependency list, verbatim: *OpenSSL, zlib, C++17 compatible compiler (Clang 5.0+,
GCC 7.0+, MSVC 19.1+, Intel C++ Compiler 19+), gperf (build only), CMake (3.10+, build only)*.

```bash
# Debian / Ubuntu
apt-get install -y build-essential cmake gperf libssl-dev zlib1g-dev git

# RHEL / Alma / Rocky
dnf install -y gcc-c++ make cmake gperf openssl-devel zlib-devel git
```

### Time, RAM and disk — read this before starting

**The single most likely way this goes wrong is an OOM kill at 90%, after most of an hour of
CPU.** `build.sh` checks for it before it starts rather than after.

- **Time.** On the order of an hour; TDLib is nearly all of it. That figure is the spec's
  estimate and **nobody has timed it on this box**.
- **RAM.** TDLib's own README, verbatim: *"clang 6.0 with libc++ required less than 500 MB of
  RAM per file and GCC 4.9/6.3 used less than 1 GB of RAM per file"* — **per parallel compiler
  process**. `make -j$(nproc)` on a four-core box therefore asks for about 4 GB of compiler
  alone, and the kernel kills it. `build.sh` computes a job count that fits in
  `MemAvailable + SwapFree` minus 512 MB of headroom, and refuses if not even one job fits.
  **Swap counts.** A build that runs into swap is slow and finishes, which is the trade you want.
- **Disk.** Several GB of objects. `build.sh` requires 5 GiB free by default — **an estimate,
  not a measurement**. Override with `DUBOTAPI_BUILD_MIN_FREE_GIB=n`. This is the same volume
  the working directory and M3's transfer spool live on, so filling it during a build takes the
  panel down with it.

If the box cannot spare the memory even with swap, the answer is to build elsewhere and copy the
binary over — that is the CI job named above, arriving early.

### Doing it

```bash
cd /path/to/DriveUnion/deploy/telegram-bot-api

./fetch-source.sh          # clone at the pin, verify the pin, verify Bot API 10.2
./build.sh --preflight-only   # every check, nothing compiled — do this first
./build.sh                 # the hour
```

`build.sh` is re-runnable: the build directory is reused, so a second run after a failure
resumes. `--clean` throws it away. `--jobs N` overrides the computed parallelism and warns if
you have gone above what the memory supports.

---

## Installing

```bash
sudo ./install.sh --panel-user <the user Kestrel runs as>
```

That creates the `dubotapi` system user and group, `/var/lib/drive-union-bot-api/{work,tmp}` at
mode `2770`, `/etc/drive-union-bot-api/bot-api.env` from the example at mode `0600` root-owned,
and the three units — and then **refuses to go further because every value in the environment
file is empty**. Fill it in:

```bash
sudoedit /etc/drive-union-bot-api/bot-api.env
sudo ./install.sh --panel-user <user> --start
```

`install.sh` will not start anything until all of these hold, and each refusal names itself:

- every required key has a value — an empty `DUBOTAPI_HTTP_IP` does not mean "the default", it
  means the argument disappears and the server falls back to accepting connections on **any**
  local IPv4 address, which is this box's public one;
- `DUBOTAPI_HTTP_IP` is a loopback address;
- `DUBOTAPI_WORK_DIR` and `DUBOTAPI_TEMP_DIR` are inside the unit's `ReadWritePaths=` — systemd
  does not expand variables there, so these can drift apart, and when they do the server starts,
  listens, looks perfectly healthy and gets `EACCES` on the first file that arrives;
- `DUBOTAPI_HTTP_PORT` matches the unit's `SocketBindAllow=` — same class of drift, different
  symptom: the unit starts and every connection is refused.

### Two things `install.sh` deliberately does not do

**It does not touch the firewall.** Changing a box's packet filter from an installer is how a
remote session ends. Do it yourself — it is the second, independent lock on the listener:

```bash
ufw deny 8081/tcp
# or
firewall-cmd --permanent --remove-port=8081/tcp && firewall-cmd --reload
```

**It does not write anything into `/etc/nginx`,** and nothing here ever will. See
[The front door](#the-front-door-there-is-not-one).

### Permissions

Three things have to be true together, and missing any one of them breaks delete-on-success in
a way the sweeper then hides by cleaning up half an hour late:

1. the directories are `2770`, `dubotapi:dubotapi` — the setgid bit is what makes new files
   inherit the group;
2. the unit sets `UMask=0002`, so the server does not strip group-write off each file it creates;
3. **the panel's user is in the `dubotapi` group** — `install.sh --panel-user <name>` does this,
   and the panel's process must restart before a new group membership takes effect.

Pointing the other way, and just as important: **the `dubotapi` user must not be able to read
M3's transfer spool.** They share a volume, not a directory. In `--local` mode the server
implements *"Upload files using their local path and the file URI scheme"*, which is an
arbitrary-file-read primitive scoped to whatever this user can read — so it reads almost nothing.
Keep the spool `0700` under the panel's own user and group.

---

## Configuration keys

**Nothing in this directory edits `appsettings.json`.** Add these by hand;
`install.sh` prints the block with your real values substituted.

### `appsettings.Production.json`

| Key | Value | Why |
|---|---|---|
| `Telegram:ApiBaseUrl` | `http://127.0.0.1:8081/` | The only key that names which server we are on. Deployment configuration, **never a row in `TelegramBotSettings`** — an operator who can repoint production at the cloud API from a web form can do it by accident, and the recovery is a ten-minute one-way door. |
| `Telegram:LocalBotServer` | `true` | `Telegram.Bot`'s flag for the `getFile` behaviour change. **Confirm this name against the installed package version before writing the client** — the flag is documented, but not read against the exact version this solution will pin, and a wrong assumption surfaces as a download attempt against a path that is not a URL. |
| `Telegram:MaxSendBytes` | `2000000000` | Decimal bytes. 2000 MB, the local server's documented upload limit taken literally. |
| `Telegram:MaxReceiveBytes` | `2000000000` | Decimal, and *not* forced on us — the local server documents no download limit, so this is a product decision: what a non-Premium sender could send in the first place. |
| `Telegram:WorkDirPath` | `/var/lib/drive-union-bot-api/work` | For the §9.1 health card's byte count, the sweeper, and delete-on-success. **Key name proposed here, not in the spec.** |
| `Telegram:WorkDirMaxAgeMinutes` | `30` | Matches `DUBOTAPI_SWEEP_MAX_AGE_MINUTES`. Keep them equal. |
| `Telegram:PinnedBotApiVersion` | `"10.2"` | So §9.1 can render the running `--version` beside the pin. **Proposed here, not in the spec.** |
| `Telegram:PinnedCommit` | `adfd7f6a…` | Same. **Proposed here.** |

Three more keys have **no honest default from here**, because all three are read off this box's
real free space, which nobody has (spec §14.9):

| Key | How to arrive at it |
|---|---|
| `Telegram:MaxConcurrentTransfers` | Spec §11.5's default is `2`. It is the multiplier in everything below. |
| `Telegram:WorkDirHeadroomBytes` | Slack above a transfer's own size in the pre-flight free-space check. |
| `Telegram:WorkDirMinFreeBytes` | The watermark below which the sweeper deletes oldest-first regardless of age and the bot stops accepting byte-moving work. |

The arithmetic they have to satisfy, which is also the app's startup check: free space must cover
M3's spool reservation (2 × 1.31 GiB ≈ **2.62 GiB**) plus
`MaxConcurrentTransfers × (MaxSendBytes + MaxReceiveBytes) + WorkDirHeadroomBytes`. At the
defaults that is **8 GB** of Telegram, or **16 GB** if the server turns out to stage uploads on
disk — call it 11–19 GB with M3's spool, on a box whose owner has said «من جا نداره».

**If it does not fit, the answer is a smaller ceiling, not a smaller sweeper.** Set
`MaxSendBytes` / `MaxReceiveBytes` to what the disk can actually hold with
`MaxConcurrentTransfers = 1`. The spec's §3 over-the-ceiling branch — a share link outbound, the
panel's uploader inbound — carries everything above it. A 500 MB ceiling on a small disk is a
working product; a 2000 MB ceiling on a full disk is an outage.

### `appsettings.Development.json`

Development on the Windows machine runs against `api.telegram.org` with a throwaway bot, because
there is no local server there to talk to. So it differs **on purpose**: `ApiBaseUrl` absent,
`LocalBotServer` false, `MaxSendBytes` `50000000`, `MaxReceiveBytes` `20000000`. That asymmetry
— the size ceilings, the `getFile` result shape, and whether a working directory exists at all —
is where a production-only defect will come from, and naming it is most of the defence.

---

## Verifying it is serving

Run after the first install **and after every pin bump**, not only the first time.

```bash
# 1. The unit is up and stays up.
systemctl status drive-union-bot-api
systemctl restart drive-union-bot-api && sleep 5 && systemctl is-active drive-union-bot-api

# 2. The version running is the version the repo names.
telegram-bot-api --version        # compare with PIN

# 3. It is on loopback — from this box…
ss -ltn 'sport = :8081'           # must show 127.0.0.1:8081, never 0.0.0.0 or *

# 4. …and, the check that actually proves it, FROM ANOTHER HOST.
#    This must time out or be refused. If it answers, stop the service now.
#    Run this from your laptop, not from the box:
curl -m 5 http://<the box's public address>:8081/

# 5. It answers over loopback. Replace <token> with a real bot token.
#    Do not paste this command anywhere afterwards — the token is in the URL.
curl -s http://127.0.0.1:8081/bot<token>/getMe

# 6. It survives a reboot.
reboot   # then repeat 1–3

# 7. A file each way leaves the working directory empty within a minute.
watch -n2 'du -sh /var/lib/drive-union-bot-api/work; find /var/lib/drive-union-bot-api/work -type f | wc -l'

# 8. Capture the real option list into this file, once, on the box.
telegram-bot-api --help
```

Step 8 matters: the option names used in the unit were read out of the pinned source rather than
from any documentation page, which is better than guessing but is not the same as reading the
binary's own help.

---

## The disk sweep

> «من جا نداره سرورم جا داشتم که از تلگرام و گوگل درایو استفاده نمیکردم»
> — there is no room on the server; if there were, neither Telegram nor Drive would be here.

The Bot API server writes every file it handles into `<working dir>/<bot user id>/` and never
removes it. That is not an oversight to work around: reading the **complete option list of the
pinned source**, there is no option for automatic deletion, expiry, cleanup or retention. Two
upstream issues ask exactly this (#303, #402) with no answer. Nothing but us deletes anything.

With a 2000 MB ceiling in both directions, an unswept directory is a full volume, and a full
volume takes Postgres and M3's spool down with it. This is not housekeeping.

### Four mechanisms, and which of them lives here

| | Mechanism | Where it lives |
|---|---|---|
| 0 | Free-space pre-flight before any transfer starts | the panel (spec §2.4.2) |
| 1 | **Delete on success, immediately, in a `finally`** — the normal path | the panel |
| 2 | Age-based sweep for the crash path | **here**, and also in the panel |
| 3 | Free-space watermark, oldest-first regardless of age | the panel only |

Rule 1 is the one that does the work: the instant `sendDocument` returns 200, Telegram holds the
bytes and the local copy has no purpose. There is no waiting period and no retention setting.

### Why there is a timer here at all, when the spec says otherwise

Spec §2.4.2 puts the sweep in the panel as a tested `BackgroundService`, explicitly *not* a cron
entry, on the grounds that a shell one-liner has no test. That is right, and this does not
replace it. This runs for the two cases the in-app sweeper structurally cannot:

1. **The app is not running.** The most likely moment for this directory to hold gigabytes is
   right after the panel crashed mid-transfer — which is exactly when its `BackgroundService` is
   not running either.
2. **The app does not exist yet.** This server gets built, started and rehearsed against a
   throwaway bot before any Telegram C# ships. Files land on the disk during that rehearsal.

And it is not a one-liner: `--dry-run` prints, per file, what it would remove, which is a test a
person runs in one command against the live directory. If the in-app sweeper ships and one
mechanism is preferred, `systemctl disable --now drive-union-bot-api-sweep.timer` and this
becomes a manual tool.

It deliberately does **not** implement mechanism 3. Deleting a five-minute-old file is
destructive — it may be an in-flight transfer — and that decision needs to know what is in
flight. The panel knows; an unattended `find` at 3am does not.

### How it is safe by construction

- **Dry run is the default**, and there is no configuration that changes that. `--delete` appears
  in exactly one place in the whole deployment: one word in
  `drive-union-bot-api-sweep.service`.
- **Every target must resolve inside `/var/lib/drive-union-bot-api`**, checked after symlink
  resolution, with a second independent refusal list of system directories and a minimum path
  depth. All of those refusals were exercised.
- **`find -P … -xdev -mindepth 1 -type f -mmin +N`.** `-P` never follows a symlink, so a symlink
  planted in the working directory cannot lead it out; `-type f` means it can only ever unlink a
  regular file — never a directory, never a symlink itself; `-xdev` means a filesystem mounted
  underneath is not its business. Empty per-bot subdirectories are left alone on purpose: the
  server has them open.
- **It runs as `dubotapi`, not root**, with `PrivateNetwork=true` — a cleanup job with network
  access is a cleanup job that can exfiltrate what it is about to delete.
- **GNU `find` is required and checked.** BusyBox `find` has no `-printf`, and the way it fails
  is by finding nothing and reporting a clean sweep.

### Reading its log line

```
sweep mode=delete age_min=30 dirs=2 removed_files=0 removed_bytes=0 (0 B) \
      remaining_files=0 remaining_bytes=0 (0 B) free_bytes=38114168832 (38.1 GB) failed=0
```

Printed every run, including when nothing was removed — a sweeper that deleted nothing must not
look identical to one that never ran. But the two counts mean **opposite** things here:

- **`removed_*` is the crash-path count, and zero is the good state.** Rule 1 does the normal
  work, so in healthy production this sweeper should find nothing. An alarm on zero deletions
  would fire every minute of a healthy year. (This is where M4 §6.3's rule does *not* apply
  literally — it applies literally in the test suite, where a seeded old file must produce a
  non-zero count, because that is what proves the code can delete at all.)
- **`remaining_bytes` is the health signal.** It should sit at or near zero. **A non-zero
  remaining size sustained across several minutes is the alarm**, and it means delete-on-success
  has stopped running.
- **`failed>0` is almost always the group.** See [Permissions](#permissions).

```bash
journalctl -u drive-union-bot-api-sweep -f            # watch it
sudo -u dubotapi ./sweep-workdir.sh                   # dry run, by hand, right now
systemctl list-timers drive-union-bot-api-sweep.timer # is it even firing
```

---

## The front door: there is not one

**The correct nginx configuration for this server is no configuration.** `install.sh` writes
nothing into `/etc/nginx` and never will.

The instinct to put nginx in front of it is not stupid — the server speaks plain HTTP and nginx
is already on this box. It is just solving a problem we do not have. Every leg is already inside
the machine: the panel calls the server over loopback, and because `--local` permits *"Use an
HTTP URL for the webhook"*, *"Use any local IP address for the webhook"* and *"Use any port for
the webhook"*, the server calls the panel's webhook back over loopback too. A TLS terminator
would add public attack surface in order to encrypt traffic that never leaves the kernel.

**Is a public route needed for anything? No.** Not for the webhook — Telegram does not deliver
it; our own server does, from this box. Not for file downloads — those are the panel's `/d/{slug}`
routes, which already have their own public surface and their own controls. Not for Telegram —
that connection is outbound MTProto, dialled by us. The only scenario that changes the answer is
the server moving to a different host, and [that move is expensive for unrelated
reasons](#the-shape-of-it).

**Why this is not over-caution.** An unauthenticated Bot API server reachable from the internet
is a **total compromise of every bot on it**, and worse than that phrase usually means:

1. **The bot token is the only authentication and it is in the URL path** —
   `POST /bot<token>/sendMessage`. Anything that can reach the port and has seen one URL *is* the
   bot. Which is also why **nothing may log these URLs**: not an nginx access log, not
   `HttpClient` logging, not an exception message. A token in a log file is the token.
2. **In `--local` mode the server will read arbitrary files off this box.** `file://` upload is a
   documented feature and it reads any path the server's user can read. Drive Union never
   constructs such an argument, and that protects nothing — the danger is somebody *else*
   constructing one. So reachability plus the token is not just "send messages as the bot", it is
   **arbitrary file read on this host**.

Three independent locks, and they fail separately: `--http-ip-address 127.0.0.1`, the unit's
`SocketBindDeny=any` / `SocketBindAllow=8081`, and the firewall rule you add by hand.

`nginx-if-it-ever-moves.conf.template` is committed and deliberately not installed, so that the
day this stops being true somebody starts from a reviewed block instead of a search result. Its
rules: TLS 1.2+, an explicit `server_name` and never `default_server`, `allow` the calling host
and `deny all`, a client certificate or a shared secret header **on top of** the token, and
`access_log off` for the proxied location with the reason written above it.

---

## Migrating an existing bot off the cloud API

**`logOut` against the cloud API is irreversible for ten minutes.** A bot logged in on two
servers loses updates. So this is a change window with a real outage in it, not a configuration
flip, and the order is the thing that matters.

### Rehearse first, on a different bot

Create a throwaway bot in @BotFather, point the built server at it, and take it all the way
through: `logOut`, local login, `getMe`, `setWebhook`, one file each way. This is the only way to
discover that the server does not start, or that the `api_id` is wrong, or that the working
directory is unwritable, **before the real bot is inside the ten-minute door.** Without it, the
rollback in step 9 is the discovery mechanism, and it is a bad one.

Preconditions: built and installed, the sweep timer running, the loopback binding verified **from
another host**, `api_id`/`api_hash` in place, and the rehearsal green.

### The window

**Steps 2 through a green step 8. Budget ten minutes; treat anything under thirty as within
expectations. This is an outage: the bot does not answer.** Nothing else in the product is
affected — the panel, `/d/{slug}` and every transfer keep running, because the bot sits on top of
them rather than underneath. No message is lost, provided step 2 gets `drop_pending_updates`
right.

1. **Stop the outbox drainer.** Nothing should be mid-send when the transport changes underneath
   it.
2. **`deleteWebhook` against the cloud API, with `drop_pending_updates` = `false`.** It must be
   false here and at every later step. Telegram holds updates for the bot for up to 24 hours,
   which is far more headroom than ten minutes needs. **Dropping them is the one way to turn this
   outage into lost customer files.**
3. **Let the queue settle.** Anything still queued stays queued; the new server will send it.
4. **`logOut` against the cloud API. This is the irreversible step. The outage starts here.**
5. **Truncate `TelegramFileId`** — every row, both bot ids. Whether a cloud-minted `file_id` is
   valid against a local server is undocumented, and the cost of being wrong is a *wrong send*.
   Truncating costs one re-upload per file that is asked for again, and is correct whichever way
   the undocumented answer falls.
6. **Flip the configuration**: `Telegram:ApiBaseUrl` to `http://127.0.0.1:8081/`,
   `Telegram:LocalBotServer` to `true`, and the two size keys to 2000 MB. Restart the app.
7. **`getMe` against the local server.** This is the proof that the token logged in locally.
   Nothing after it is worth attempting until it answers.
8. **`setWebhook`** at `http://127.0.0.1:<kestrel port>/telegram/<fresh segment>` with a fresh
   secret and an explicit `allowed_updates`, then **`getWebhookInfo`** and read
   `last_error_message` — empty is the goal. Restart the drainer.
9. **Verify end to end**: one file in, one file out, and the working directory observed growing
   and then swept back to zero.

**If any of it fails, the rollback is `setWebhook` back on the cloud API — and it is not
available until ten minutes after step 4.** That window is the whole risk of this procedure, and
it is why the rehearsal is not optional.

### Moving between two local servers later

Much gentler, and documented rather than inferred: `deleteWebhook`, then `close` to shut the bot
instance down, then move `<old working dir>/<bot user id>/` to the new server's working directory
and carry on. (That is also the second confirmation that the working directory is laid out
per-bot, named by the bot's user id.) **`close`'s own error behaviour — it is reported to return
429 for the first ten minutes after a launch — is not confirmed**, so do not build a runbook step
on that timing without checking it.

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| `bad interpreter: /usr/bin/env bash^M` | The scripts arrived with CRLF line endings — almost always because they were copied out of a Windows checkout rather than cloned on the box. `sed -i 's/\r$//' *.sh` fixes it; cloning on the box avoids it. The repository's `.gitattributes` normalises to LF *in the repository*, which does not help a file copied out of a Windows working tree. |
| Build stops with `Killed` and nothing else | The OOM killer. `./build.sh --jobs 1`, or add swap. |
| Unit fails with an error naming `SocketBindDeny` | systemd older than 249. Remove those two lines from the unit; the bind address and the firewall are the other two locks. `install.sh` warns about this before installing. |
| Unit is `active`, every request refused | `DUBOTAPI_HTTP_PORT` and `SocketBindAllow=` disagree. `install.sh` checks this. |
| Unit is `active`, `EACCES` on the first file | `DUBOTAPI_WORK_DIR` is outside the unit's `ReadWritePaths=` — `ProtectSystem=strict` made everything else read-only. `install.sh` checks this. |
| Unit restarts five times then stays failed | Usually a wrong `api_id`/`api_hash`. That is the burst limit working. `journalctl -u drive-union-bot-api -n 50` |
| `ss` shows `0.0.0.0:8081` | `DUBOTAPI_HTTP_IP` is empty or wrong. **Stop the service now** — every bot on it is compromised by anyone who can reach the port. |
| Sweep reports `failed>0` | The panel's user is not in the `dubotapi` group, or the setgid bit is gone. See [Permissions](#permissions). |
| `remaining_bytes` stays above zero for minutes | Delete-on-success in the panel has stopped. This is the alarm that fills the disk. |
| `--verify-only` says the source is not at the pin | Somebody built from a moved `HEAD`. `./fetch-source.sh` puts it back. |

```bash
journalctl -u drive-union-bot-api -f
journalctl -u drive-union-bot-api-sweep --since '1 hour ago'
sudo ./install.sh --verify
```

---

## What is not verified

Nothing in this directory has run on Linux. Beyond that, these are the specific places where a
statement here rests on something other than observation. They are listed rather than smoothed
over, because a fabricated flag in a deployment script fails at 3am on somebody else's server.

**Read from the pinned source, not from documentation, and not confirmed against a binary.**
The option names in the unit — `--local`, `--dir`, `--temp-dir`, `--http-ip-address`,
`--http-port`, `--max-connections`, `--verbosity`, `--http-stat-port`,
`--http-stat-ip-address` — were read out of the option parser at commit `adfd7f6a`. Only
`--local`, `--http-port`, `--api-id` and `--api-hash` appear on any documentation page. Run
`telegram-bot-api --help` on the box and reconcile.

**Not tested anywhere, by anybody.**
- The systemd units have never been parsed by systemd. `systemd-analyze verify` them first.
- `SocketBindDeny=` / `SocketBindAllow=` on this binary. Documented, untried.
- Whether `--http-ip-address 127.0.0.1` actually restricts the listener as the help text implies.
- The 5 GiB build-space requirement is an estimate. So is "about an hour".
- `sweep-workdir.sh`'s **symlink** behaviour. Its refusal list, its dry run, its delete path and
  its idempotent re-run were exercised against a fake tree; the symlink case could not be,
  because this machine cannot create symlinks. `find -P` not following them is documented GNU
  behaviour, not something observed here.

**Undocumented, and nobody has looked.** All of these change how much disk one transfer costs or
how much of the spec is right, and none of them was guessed at:
- Does an upload get **staged on disk** or streamed through? Zero disk per delivery, or one full
  copy — with a 2000 MB ceiling that is the difference between a feature and an outage.
- Is an inbound file written **on update receipt or only on `getFile`**? If eagerly, a stranger
  can push two gigabytes onto this box by messaging the bot.
- Is a **cloud-minted `file_id` valid against a local server**? The migration truncates the cache
  either way; the answer only says whether that was necessary.
- What the **statistics port** actually reports. It exists — `--http-stat-port` is in the option
  list — which is more than the spec knew. Whether it carries byte counters is unchecked, and
  M6's inbound measurement has no honest source without it.
- Whether the 2000 MB limit is **decimal or binary**. Everything here enforces decimal, which
  leaves ~4.6% unused if it is binary; the reverse costs a rejected send after two gigabytes have
  already moved.

**Where this deviates from the Telegram spec, deliberately.** Three places, each argued above:
a manifest and a commit SHA rather than a submodule and a tag ([The pin](#the-pin)); a sweep
timer beside the in-app sweeper rather than instead of it ([The disk sweep](#the-disk-sweep));
and **no `IPAddressDeny=any` / `IPAddressAllow=localhost`** in the unit. The spec's §2.4.3 asks
for those two directives as a second lock on the bind address. They would take the server off the
air completely: `IPAddressAllow`/`Deny` filter all socket traffic in both directions, and this
process is an MTProto client whose whole job is holding connections open to Telegram's
datacenters on the public internet. Restricting it to loopback leaves a server that starts,
listens, and can never log a bot in. The unit carries that reasoning in a comment so nobody
"fixes" the omission.
