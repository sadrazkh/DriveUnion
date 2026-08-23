# Drive Union — M6: network tuning

**Date:** 2026-08-23 · **Status:** design drafted; §4–§7 blocked on the measurement in §3, the whole
slice blocked on §12 · **Depends on:** M1 (upload path, `IDriveClient`, Data Protection), M2
(`PoolSettings`, `AccountUploadDay`), M3 (`Job`, the fan-in writer, the spool), M5 (operator-only
enforcement)

## 1. What M6 is

Four things the design puts on one settings screen, one dashboard card, and one queue row:

| Piece | Where the design shows it | Section |
|---|---|---|
| Multiple egress IPs / upstream proxies with a traffic-share weight | Settings, «IPهای واسط و پروکسی» | §4–§7 |
| Concurrent chunk count and chunk size as operator settings | Settings, «کارایی انتقال» | §8 |
| S3 export, the fifth job type | Queue, `invoices-2025.7z · S3 export · A2 → S3` | §9 |
| The seven-day OVH egress traffic chart | Dashboard, «ترافیک خروجی سرور OVH» | §10 |

They are grouped because the siblings deferred them here, not because they are one feature. §8 and §10
are small and useful whatever happens. §9 is a product question. §4–§7 are the expensive, unproven
part, and §3 argues they should not be built first — or, on current evidence, possibly at all.

Every surface in M6 is **operator-only**, per M1 §1.4 and M5 §10: the proxy table, the transfer sliders
and the dashboard's egress chart are all things a customer must never see. Any endpoint M6 adds has to
be classified in M5's tenant-isolation route test, which turns red until someone does.

## 2. The sentence that must survive contact with the UI

The handoff states it under the proxy table:

> «فقط برای دور زدن throttling مسیر شبکه — سهمیه ۷۵۰GB به ازای اکانت است، نه IP.»

and again in its closing notes:

> «سهمیه‌ی ۷۵۰GB روزانه به ازای هر اکانت است، نه IP … UI نباید القا کند که افزودن IP سهمیه را بالا
> می‌برد.»

**The 750 GB/day ceiling is a property of the Google account.** M2's data model is why that sentence is
true rather than merely asserted: `AccountUploadDay` is keyed `(GoogleAccountId, QuotaDate)` and there
is no column in it that an IP address could occupy. Adding a source address or a proxy cannot raise the
ceiling. The only thing that raises it is adding a Google account — the accounts screen's dashed card,
«افزودن اکانت سوم … سهمیه روزانه به ۲.۲۵TB می‌رسد», which is M2's.

Egress routes exist for exactly one hypothesis: that the *network path* between this box and Google is
shaped, and that a different path is faster. §3 is about how weak that hypothesis currently looks.

Four rules keep the UI honest. The last one is a test.

1. The subtitle under «IPهای واسط و پروکسی» ships verbatim, not paraphrased into something softer.
2. «سهم ترافیک» is a share of *our upload traffic*, never a share of quota. It is a routing weight and
   the column header's tooltip says so.
3. Adding, enabling or reweighting a route changes **no** number on the dashboard's quota bars, the
   sidebar's «سهمیه آپلود امروز» card, or the accounts screen. Nothing on the proxy card computes a
   capacity figure, and the «+ افزودن IP» button gets no capacity promise to mirror the accounts
   screen's dashed card.
4. A test asserts the rendered proxy settings card contains no `GB` or `TB` token. It is a silly test
   and it will hold the line for years, because the pressure to add "adds 750 GB/day" copy to that card
   is precisely the pressure this section exists to resist. A job parked in «صبر سهمیه» must likewise
   never surface "add an IP" as a remedy.

**Numerals.** M2 §12 set the panel's convention — Persian digits for counts, Latin monospace for
measured quantities and identifiers, `68٪` for percentages — and noted that the design's proxy table
breaks it with «۶۰٪», leaving the fix to this slice. Taking it: the share column renders `60٪`,
Latin digits with the Persian percent sign, matching the account cards. Address and latency stay Latin
monospace (`51.xx.xx.14`, `6 ms`); «محل» and «وضعیت» stay Persian words.

## 3. The evidence against building §4 first

### The arithmetic

Two accounts × 750 GB/day = 1.5 TB/day of upload the product is *permitted* to perform.

- Spread over 24 hours: **≈ 17 MB/s ≈ 139 Mbit/s**. A 1 Gbit port carries that seven times over
  alongside customer downloads. In this mode the account quota is the constraint and no amount of
  egress tuning makes anything faster.
- Burned in a four-hour window: **≈ 104 MB/s ≈ 833 Mbit/s**, plus downloads on the same wire. That
  saturates a 1 Gbit port.

So the value of §4–§7 depends on a question nobody has answered: does the operator intend to spread
uploads across the day or burst them? §12.1. And if the answer is "burst", note what actually follows:
**additional addresses on the same NIC add no capacity.** They share one physical port. Only a proxy on
a *different machine with a different uplink* adds a path.

Read the design's own table with that in mind. `51.xx.xx.14 (OVH GRA)` is Gravelines, France;
`148.xx.xx.7 (OVH FRA)` is Frankfurt, Germany; `185.xx.xx.92` is the Netherlands. Three datacentres
cannot be three local addresses on one box. **The design's table is mostly proxies**, which is why §5
treats `WebProxy` as the primary mechanism and source-IP binding as the secondary one.

### M3's writer makes it worse

M3 §2.2 settled the upload topology, and it is not the one the mock implies. The parallelism is on the
**browser → OVH** leg: N concurrent `PUT`s into an ordered reassembler. The **OVH → Google** leg is
*exactly one sequential writer per resumable session*, because Drive's resumable protocol acknowledges
a single contiguous prefix and has no equivalent of S3 multipart.

The consequence for this slice is direct and unwelcome: **a single upload cannot use two egress routes
at once.** One writer, one connection at a time. Routes can alternate between chunks; they cannot add
up. So for the product's headline case — one enormous file going to Drive — a proxy list does nothing
for aggregate throughput.

Where multiple routes *can* run concurrently is narrower: across M3's `MaxConcurrentJobs` (default 3)
simultaneous jobs, across `Relay`'s N concurrent source-side `Range` GETs, across the S3 export's read
side (§9), and across `/d/{slug}/file` downloads. §4 is designed around that reality rather than around
the mock.

M3 §2.2 also states the OVH → Google leg has "a 3 ms RTT to Google's edge … and no reason to be the
constraint" — and cites the handoff's own proxy table for that 3 ms. That figure is currently an
assumption inherited from mock data. §7 is what turns it into a measurement.

### What a real diagnosis looks like

"Is this path throttling or account quota?" has a decision procedure. Skipping it is what turns this
feature into cargo cult.

| Observation | Account quota | Path shaping | Our own box / uplink |
|---|---|---|---|
| Upload **fails** with a Drive 403 carrying a quota reason | yes | no | no |
| Uploads run at a steady but unimpressive rate, no errors | no | maybe | maybe |
| One stream is slow; N *independent* streams scale near-linearly | no | yes — per-flow shaping | no |
| N streams do not scale; aggregate flat near a round number | no | yes — per-IP aggregate cap | yes |
| A large non-Google sink is *equally* slow from this box | no | no | yes |
| A second machine is fast **at the same moment, same account** | no | **yes** | no |

The first row is the one that matters most: **the daily cap presents as an error, not as slowness.**
Google refuses the write; it does not gradually squeeze throughput. "Uploads got slow" is therefore
never quota, and "uploads started failing at 04:00" is never the network. M2 already counts bytes per
account per day and M3 already parks such a job in `QuotaWait` rather than failing it, so the panel
knows which of the two it is looking at without anyone guessing.

Rows three and four are the ones that decide §4, and they are *different hypotheses*. Per-flow shaping
is already answered without proxies: M3 runs three concurrent jobs, so three flows leave this IP
already. Egress routes only pay off under the **per-IP aggregate** hypothesis — that Google, or a
transit provider, caps the sum of all our flows from one address. That is a narrow claim and nothing
observed so far supports it.

*Uncertainty, stated plainly:* the exact `reason` string Drive returns for the 750 GB/day ingest cap is
not asserted here. M3 §4 already routes "403 with a daily-upload-limit or quota reason" to `QuotaWait`;
the M6 contribution is to capture the raw body of the first real rejection and write the mapping from
evidence rather than from memory.

### The measurement — most of which is already someone else's job

M3 §2.3 commissions a throwaway console app against a real Drive account in week one, and its step (c)
uploads a 4 GB blob single-stream and records wall-clock MB/s over three runs. **That is the number
this slice needs**, and M6 must not commission a duplicate experiment. Read M3's `§2.3 findings`.

M6 adds two steps to that same afternoon:

1. **Line rate and non-Google baseline.** `ethtool <if>` to confirm the port negotiated what OVH sold,
   then a large HTTPS transfer to a well-connected non-Google sink. This separates "our uplink is slow"
   from "the Google path is slow", and it is the same box reading M3 §13.7.7 already asks for.
2. **The only positive proof: a second path, same moment, same account.** Rent a small VPS on a
   different network, tunnel one upload through it — `ssh -D` gives a SOCKS5 proxy and .NET speaks
   SOCKS5 natively (§5), so this needs no code at all — and run the same payload from both places
   simultaneously to the **same** Google account. Materially faster through the remote path and the
   hypothesis holds. Not faster, and §4–§7 are deleted and this spec is four sections shorter.

While M3's (c) runs, watch `ss -ti` for `retrans` and the congestion window. A cwnd collapsing without
loss on our side is the signature of shaping upstream; a cwnd that never grows is a tuning problem on
this box, which is free to fix and needs no proxies.

**Recommended ordering.** Build §10 (the byte sampler and chart) first — it is cheap, it is the
instrument that records all of the above, and the dashboard card is wanted regardless. Then §8. Then
run the experiment. Then re-read §4 with numbers in hand. §9 is independent of all of it.

**If the answer comes back "a single session caps below the link"**, the fix still is not this feature.
M3 §2.4 already names the only lever that raises a per-session ceiling: split the file across N
independent Drive files and stitch on read. A sequential writer that changes source address between
chunks does not go faster.

## 4. Egress routes: the model

A route is one way out of this process to Google or S3. Three kinds, plus the one that always exists.

| Kind | What it is | Needs OVH/OS work | Adds capacity |
|---|---|---|---|
| `Direct` | No binding, no proxy. Implicit, undeletable, always enabled. | no | — |
| `LocalSourceIp` | An address on this box's NIC, bound as the socket source | yes (§6) | no — same port |
| `HttpProxy` | `http://host:port`, CONNECT-tunnelled | no | yes, if elsewhere |
| `Socks5Proxy` | `socks5://host:port` | no | yes, if elsewhere |

`Direct` exists so that a misconfigured proxy list cannot take the product offline. If every configured
route is unhealthy, traffic falls back to `Direct` and the panel says so. A network-tuning feature that
can strand every upload is worse than no feature.

**Selection is per outbound connection to Google, from one global ledger.** Not per job, and not per
chunk-slot within a job — §3 explains why the latter is meaningless under M3's single-writer design.
The selector is consulted whenever any of these opens a connection:

- M3's sequential resumable writer, once per chunk `PUT`.
- M3's `Relay` source side, once per concurrent `Range` GET.
- M6's S3 export, on both legs (§9).
- M1's `/d/{slug}/file` download reads — in a busy product, by far the most numerous.

Because the ledger is global, the configured 60/40 is honoured across everything the box is doing at
once, which is the only level at which it can be honoured at all.

**Distribution is deterministic, not random.** 60/40 over ten connections must be six and four, not a
coin flip that lands 8/2. Weighted round robin by credit: each assignment takes the enabled, healthy
route maximising `weight × totalAssigned − assignedToRoute`, ties broken by route id so a test can
assert the exact sequence.

Weights are normalised at read time over the enabled **and healthy** set, never trusted to sum to 100
in the database. A route going unhealthy redistributes its share proportionally without an operator
touching anything — and without flipping `IsEnabled`. Operator intent and observed health are two
fields and the UI must not conflate them (§7).

`IEgressRouteSelector` lives in **Core** and is a pure function of the route list and the ledger. The
binding lives in Infrastructure. Same reasoning as M1 §4: the decision worth testing is *which route*,
and it must be testable on a machine with no Docker and no path to Google.

## 5. Binding an outbound `HttpClient` to a route in .NET 10

Both mechanisms hang off `SocketsHttpHandler`.

**Source IP** — `ConnectCallback` replaces the handler's socket setup:

```csharp
handler.ConnectCallback = async (ctx, ct) =>
{
    // Match the socket family to the bind address: the (SocketType, ProtocolType) ctor
    // yields a dual-mode IPv6 socket, and binding a raw IPv4 IPEndPoint to that fails.
    var bind = IPAddress.Parse(route.Address);
    var socket = new Socket(bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
    try
    {
        socket.Bind(new IPEndPoint(bind, 0));
        await socket.ConnectAsync(ctx.DnsEndPoint, ct).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }
    catch { socket.Dispose(); throw; }
};
```

The callback returns a **plaintext** stream; `SocketsHttpHandler` performs the TLS handshake itself for
`https://`, including ALPN. A v4-bound socket reaches Google over v4 only, which is what we want — the
hypothesis under test is about the v4 path.

*Uncertainty:* that HTTP/2 negotiation over a `ConnectCallback`-supplied stream behaves identically to
the default path is expected but not verified here. The first implementation task is an integration
test asserting the negotiated version, and asserting the connection actually left from the bound
address — easiest against a service that echoes the caller's IP, not by reading our own config back.

**Proxy** — `handler.Proxy = new WebProxy(route.ProxyUri) { Credentials = … }` with
`handler.UseProxy = true`. HTTPS destinations are tunnelled with CONNECT. .NET supports `socks4://`,
`socks4a://` and `socks5://` proxy URIs, which is what makes §3's step 2 an afternoon with `ssh -D`
rather than a project.

### The trap: one handler per route, always

`SocketsHttpHandler` pools connections by scheme, host, port and proxy. **It does not know about
`ConnectCallback`, and it does not key the pool on anything you put in `HttpRequestMessage.Options`.**
A single shared handler whose callback reads a per-request route would happily hand a request destined
for route B a pooled connection opened on route A. The failure is invisible: every upload succeeds and
the weights simply do nothing.

So: **one `SocketsHttpHandler` and one long-lived `HttpClient` per route**, held in an
`EgressClientPool` singleton keyed by route id. Not `IHttpClientFactory` named clients — routes are
database rows created at runtime and the factory's names are fixed at DI time. The pool creates a
client on first use and disposes it after a drain delay when the route is deleted or its address
changes. Because this bypasses the factory's handler rotation, set `PooledConnectionLifetime`
(10 minutes) so DNS changes are picked up.

Two interactions with M1 that must not be lost:

- M1 §9's backoff `DelegatingHandler` wraps **each** route's handler. The chain is per-route, so
  M3's `Drive:QueriesPerMinute` budget is still enforced across all of them and not once per route.
- M1 §9's single-flight token refresh stays **shared across all routes**. It is keyed by Google
  account, not by path. Eight workers on three routes still trigger one refresh.

`IDriveClient`'s methods take an `EgressRouteId?`. M1's fake client records it, which is how the
weighting is asserted with no network.

## 6. What only the OVH box can do — this part is not code

Said plainly for the owner: for `LocalSourceIp` routes, **most of the work is not in C#.**

- The address must exist on the interface (`ip addr add … dev …`) and survive reboot, via whatever the
  box's distro uses. Before that, OVH must have routed the additional IP or block to this server in the
  manager; depending on the product a virtual MAC has to be declared there or the gateway drops the
  traffic. `Bind()` on an address the kernel does not own throws, and nothing in the app works around
  it.
- With a single default route, packets sourced from a secondary address still leave via the default
  gateway — fine when the address is from the same routed block. An address from a different block or
  with its own gateway needs policy routing (a second table plus `ip rule from <addr> lookup <table>`)
  and possibly loose reverse-path filtering (`net.ipv4.conf.*.rp_filter=2`), or the kernel drops
  replies with no error the app can observe.
- **In a bridge-networked container, local source IPs are impossible.** The address does not exist in
  the container's namespace and outbound packets are SNAT'd to the host's primary address, so the
  binding is either an exception or a lie. The fixes are deployment-level — `network_mode: host`, or a
  macvlan network. §12.3.

Proxy routes need none of this: a reachable host and a URL is the whole requirement. One more reason
they are the mechanism to build first, and the only one testable from a developer machine.

## 7. Latency, health, and the design's table

The columns are «آدرس / محل / تأخیر تا گوگل / سهم ترافیک / وضعیت».

**«تأخیر تا گوگل» is a TCP connect RTT measured through the route** — open a socket to
`www.googleapis.com:443` on that route, time SYN to SYN/ACK, median of five samples every 60 seconds.
Not ICMP: for a proxy route, pinging the proxy measures the wrong hop entirely, and plenty of hosts
drop ICMP outright. For a proxy the figure is therefore the end-to-end RTT *through* the proxy to
Google, which is the only number an operator can act on. Reachability is confirmed separately with one
cheap authenticated call every five minutes (`about.get?fields=user`); three routes is under 900
calls/day against M3's `Drive:QueriesPerMinute` budget of 6,000/minute — noise.

This column is also what retires an assumption: M3 §2.2 justifies the single-writer design partly on
"a 3 ms RTT to Google's edge", citing the mock's own proxy table. After M6 that number is measured.

**Latency is a health indicator, not a routing input.** A bulk transfer with window scaling and 64 MiB
chunks is throughput-bound, not RTT-bound; the difference between 3 ms and 11 ms is irrelevant to how
fast a 96 GB file moves. Weights stay manual, exactly as the design draws them. Anything resembling
latency-based auto-weighting is a nice-sounding way to make the traffic share unpredictable, and it
would fight the deterministic ledger in §4.

**Health.** Three consecutive probe failures → `Unhealthy`; the route leaves the selector and its
weight redistributes. One success restores it. The health checker **never writes `IsEnabled`.**

The «وضعیت» cell renders three values, using colours the design already uses for exactly this purpose
elsewhere:

| State | Text | Colour |
|---|---|---|
| Enabled, healthy | فعال | `--accent-ink` |
| Enabled, unhealthy | خطای مسیر | `--warn` |
| Disabled | غیرفعال | `--muted` |

## 8. Chunk tuning

M2 §11 shipped the «کارایی انتقال» card containing only the auto-stop switch, keeping its heading and
grid position final so a later slice could fill it in. M3 §11 made the two numbers configurable as
deployment settings — `Transfer:ChunkSizeBytes` (64 MiB) and `Transfer:Concurrency` (8) — but built no
UI. M6 adds the two sliders, and promotes those keys to operator-editable columns on M2's
`PoolSettings`. The database value wins when set; the config key remains the seed and the fallback.

M6 does **not** touch the auto-stop switch or its threshold: M2 owns `AutoStopNearQuota` and
`SoftStopBytes`, and M3 reads them. The card gains two sliders and nothing else.

| Setting | Bounds | Default | Source of the bound |
|---|---|---|---|
| `ConcurrentChunks` | 1–16 | 8 | M3 `Transfer:Concurrency`; the browser leg, not Google, is the real ceiling (below) |
| `ChunkSizeMiB` | 8–256 | 64 | M3 `Transfer:ChunkSizeBytes`; the true ceiling is disk, computed live (below) |

**The default stays M3's 64 MiB, not M1's 32.** M3 changed it deliberately and gave its reasons.
Promoting a setting into the UI is not a licence to re-litigate its value in the same commit.

**Chunk size is stored in whole MiB, and that is not cosmetic.** Drive requires every chunk but the
last to be a multiple of 256 KiB. Every integer MiB is exactly 4 × 256 KiB, so whole-MiB storage makes
the rule unbreakable by construction and no validator can be forgotten. The trap this avoids is real:
the design's label reads «۶۴ MB», and 64 *decimal* MB is 64,000,000 bytes — 244.14 × 256 KiB, not a
valid chunk. Read the card's "MB" as MiB throughout.

**The real upper bound is M3's spool, and it is not a constant.** M3 §2.2 streams in-order chunks
straight through but spools out-of-order ones, bounded at `WindowChunks − 1` per session, with
`WindowChunks` equal to concurrency. M3 §11's worst case is therefore:

```
MaxConcurrentJobs × (ConcurrentChunks − 1) × ChunkSizeMiB
```

which at M3's defaults is `3 × 7 × 64 MiB = 1.31 GiB`, and M3's startup refuses to run with less than
twice that free. At the top of both M6 sliders it is `3 × 15 × 256 MiB = 11.25 GiB`, demanding 22.5 GiB
free — an order of magnitude more disk than the deployment was sized for.

So: **the settings card computes and displays the worst-case spool requirement live as the sliders
move, and a save that would exceed the free space M3's startup check validated is rejected with both
numbers in the message.** A runtime settings change would otherwise walk straight past a boot-time
guard, and the failure would arrive hours later as a disk-full mid-transfer. The static bounds in the
table are the outer envelope; free disk is the operative limit.

**Changes apply to new jobs only.** A running upload keeps the chunk size its session was opened with —
`UploadSession.ChunkSizeBytes` (M3 §4) is already per-session, and the confirmed byte ranges are laid
out against it. The card says so under the sliders.

**Where the concurrency ceiling actually sits.** M3 §2.2 establishes that the slow leg is
browser → OVH, so this slider governs *browser* connections. Over HTTP/1.1 a browser opens about six
per origin, so a setting of 8 leaves two chunks queued behind the other six. Over HTTP/2 all of them
multiplex onto **one** TCP connection, and that connection's throughput becomes the ceiling for the
whole upload regardless of the setting — which for a distant customer may be *worse* than six parallel
HTTP/1.1 connections. This is a caution, not a fact: it is exactly the sort of thing to measure on the
real path before anyone tunes a slider by intuition, and it is why the range stops at 16 rather than
going higher.

## 9. S3 export — the fifth job type

M3 shipped the type, the enum value and the storage for it: `Job.Type` includes `Export` from the first
migration, `Job.TargetDescriptor` is `jsonb` and explicitly reserved for this, and M3's worker fails an
`Export` job immediately with `NoHandlerForType`. **M6 adds no table and no migration for the job** — it
registers the handler and writes the descriptor `{ s3DestinationId, keyPrefix, effectivePartSizeMiB }`.
`Job.TenantId` is not nullable, so an export belongs to the tenant that owns the file.

**The cost, which is why this lives in the network-tuning slice.** `files.copy` is Google-to-Google;
the queue screen says so — «کپی سمت گوگل · بدون مصرف پهنای باند سرور». S3 export is not. There is no
cross-provider server-side copy, so an N-byte file costs **N bytes in and N bytes out** on the same
uplink that carries every customer download — the exact resource §4 exists to husband. On OVH's usually
unmetered ports the money cost is typically zero and the *capacity* cost is 100%. Hence: concurrent
exports default to 1, exports run at the bottom of M3's priority order, and their bytes appear in §10's
chart like everything else. The destination's own charges depend on which S3 this is — §12.4.

**Both legs are genuinely parallel, and this is the one job type where that is true.** M3 §2.1's
finding is that "nothing in Drive v3 corresponds to S3 multipart" — but S3 *is* S3, and multipart
accepts parts by number, out of order, in parallel, assembled by `CompleteMultipartUpload`. So:

1. **Read side: reuse M3's `Relay` source implementation** — N concurrent `files.get?alt=media` with
   `Range` headers against the source account. M3 already built and tested it; the export is `Relay`'s
   reader bolted to a different writer. Each read's range is one S3 part boundary.
2. **Write side: the low-level multipart API** — `InitiateMultipartUpload`, N concurrent `UploadPart`,
   `CompleteMultipartUpload`. Not `TransferUtility`, which wants a seekable stream or a known plan and
   will buffer what it is given.
3. Each part is filled into a pooled buffer, uploaded, and returned. Cost is
   `partSize × concurrentParts` — 64 MiB × 4 = 256 MiB for one export, which is the real number the
   operator is spending and the reason concurrency is bounded.

*Uncertainty:* whether the AWS .NET SDK can sign and send a non-seekable stream for `UploadPart`
without buffering it internally is not asserted here. The design above assumes it cannot and budgets a
buffer per in-flight part. If it turns out it can, the buffer is an optimisation to remove, not a
correctness problem — the honest direction to be wrong in.

**Part sizing has a hard arithmetic constraint.** S3 permits at most 10,000 parts and requires every
part but the last to be at least 5 MiB. Therefore:

```
effectivePartSizeMiB = max(ChunkSizeMiB, ceil(SizeBytes / 10_000) rounded up to the next MiB)
```

At the default 64 MiB that covers files up to 640 GiB. The design's own `dataset-full.img · 812 GB`
needs parts of at least 82 MiB, so the formula raises it to 96 MiB for that job rather than silently
violating the limit. The value is written into `TargetDescriptor` when the job is created: a resumed or
investigated export must know what it actually used.

**Failure.** Any abort calls `AbortMultipartUpload`. It will sometimes not run — process kill, network
partition, and M3's reaper returning a `Running` job to `Queued` on boot. **Orphaned multipart parts
are billed and do not appear in an object listing**, so they stay invisible until a bill arrives. The
bucket therefore gets a lifecycle rule expiring incomplete multipart uploads after 7 days, and that
rule is part of the deliverable, not an ops afterthought. On retry, M3's `Attempt` counter drives a
fresh `InitiateMultipartUpload` rather than an attempt to resume the old one; upload ids are cheap and
resumption across a process restart is not worth the state.

**Credentials.** An `S3Destination` is operator-level and its keys are encrypted with the same
`ITokenProtector` as the Google tokens, including M1 §5's "persist the Data Protection keys to the
database" rule — whose absence would orphan these secrets on the first redeploy in exactly the same
silent way. Whether a customer may export to their own bucket is a product question: §12.4.

## 10. The traffic chart, and where the seven days come from

Seven bars at 38/52/44/71/63/88/100 %, the last two in `--accent`, captioned «۷ روز اخیر» and
«اوج ۴.۱ Gb/s». M2 §11 built the dashboard grid so this card drops into its designed position with no
relayout.

Three possible sources, and the choice matters:

| Source | Verdict |
|---|---|
| Host NIC counters (`/sys/class/net/*/statistics/tx_bytes`) | The literal truth about the port, but needs host access, reads the veth rather than the NIC inside a container, and is attributable to no route, tenant or job. |
| OVH's bandwidth graphs or API | An external dependency with its own auth, granularity and outages, for one dashboard card. No. |
| **Our own accounting** | **Chosen.** In-process, works under any container network mode, attributable per route and per job, and testable with no network — the same constraint that put `IDriveClient` in Core (M1 §4). |

The number is therefore *bytes Drive Union moved*, a subset of the port's traffic that excludes TLS and
HTTP framing, retransmits, and anything else on the box. The card keeps the design's title; the caption
is written to say what is counted.

**Collection.** A `CountingStream` decorator increments an `Interlocked` counter on every read and
write, wrapped around five places: M3's resumable writer, M3's `Relay` reads, M1 §7's copy into
`Response.Body` on `/d/{slug}/file`, both legs of §9's export, and the outbound leg of a Telegram
document send (`Direction = ToTelegram`, §11) — with the caveat below, which is not a small one. A hosted
service flushes per-minute buckets to `EgressSample` once a minute.

That flush is **sessionless background work with no `HttpContext`**, so it goes through the operator
repository, exactly as M3 §5 requires of `IJobStore`. M1 §8 already decided against a global query
filter, which is what makes this safe; it is recorded here because a sampler that silently writes
nothing looks precisely like a sampler that had nothing to write.

**Telegram, where self-hosting moved the bytes out from under this counter.** The Telegram slice decided
to run its own `telegram-bot-api --local` on this box (Telegram §2.3), and the consequence for M6 is
specific rather than atmospheric: **our `CountingStream` is no longer on the socket that carries these
bytes off the box.** It is on a multipart upload to `127.0.0.1`, and the real send to Telegram happens
afterwards, inside a process M6 does not own, cannot decorate and does not instrument. The two directions
come out of that differently and must not be given the same treatment.

- **Outbound — count it, and keep this sentence beside the code.** Every byte we write to loopback is a
  byte the Bot API server then sends on to Telegram, so the figure is a faithful **1:1 proxy** for the
  uplink bytes even though it is measured on loopback. That holds only because the server is acting as a
  proxy — it is a coincidence of the arrangement rather than a property of the counter — which is exactly
  why it is written down here rather than left to be inferred. The number is *right* and it is measured
  somewhere that looks *wrong*, and the obvious repair, "this is loopback traffic, exclude it", deletes a
  real figure and leaves a zero in its place. A cached `file_id` re-send contributes nothing at all
  because no bytes leave the box (Telegram §3.2); that is the point of the cache and it will read as an
  accounting bug to whoever sees the chart first.
- **Inbound — there is no proxy, and the honest report is `unmeasured`, not `0`.** A file a customer
  sends to the bot is fetched from Telegram by the `telegram-bot-api` process and handed to us as a path
  on disk (Telegram §2.1, §3.3). Those bytes cross the box's uplink and never cross a socket this process
  owns, so there is nothing for a `CountingStream` to wrap — not an approximate counter, not a partial
  one, none. **This is not the undercount admitted above and must not be folded into it.** That caveat is
  about TLS framing and retransmits: a few per cent around a number that exists. This is an entire
  direction of real traffic with no number behind it at all, and reporting the second as though it were
  the first would turn a stated gap into a hidden one.

**So the chart renders inbound-from-Telegram as unmeasured, and never as a bar of height zero.** A zero
is a claim that nothing moved, and we do not know that. The absent figure carries its reason instead,
which is the same standard the table above applied when it rejected host NIC counters for being
attributable to nothing. Two things could later replace it and neither is invented here: the Bot API server's own
statistics port, **if it has one — Telegram §14.10 asks `--help` and the answer is not yet known**, or an
interface counter, which brings back every objection this section raised against NIC counters and adds
nothing to compare it against.

**One further thing §4 cannot do here, named so it is not discovered as a bug.** Egress-route selection
is per outbound connection *this process* opens (§4), and the connection that carries these bytes to
Telegram is opened by the Bot API server. No `LocalSourceIp` binding and no proxy route applies to it in
either direction, so a `ToTelegram` sample's `EgressRouteId` is always null and the configured traffic
share is silently not honoured over this path. That is a limit of the arrangement, not a defect to fix in
the selector.

**Rendering.** Bar height is `dayBytes / max(dayBytes over the seven days) × 100%`, so the tallest bar
is always 100% as in the mock; the two most recent take `--accent` and the rest `--soft`. The peak
figure is `max(minuteBytes) × 8 / 60` as Gb/s to one decimal — an average over the peak minute, so it
understates true instantaneous peaks, which is the honest direction to be wrong in. If the box has a
1 Gbit port the card will read about `0.9`, not the mock's `4.1`; that figure is mock data and the
port speed is M3 §13.7.7's open question, not a target.

**Retention.** `EgressSample` is kept 90 days and then deleted. Three routes in three directions at
one row a minute is roughly 400,000 rows a quarter, which Postgres will not notice.

## 11. Data model — exactly what M6 adds

Three new tables, and two columns on a table M2 owns.

```
EgressRoute   { Id, Kind (Direct|LocalSourceIp|HttpProxy|Socks5Proxy), Label, Address?,
                ProxyUri?, ProxyUsernameProtected?, ProxyPasswordProtected?, LocationLabel,
                WeightPercent, IsEnabled, Health, LastLatencyMs, LastCheckedAt,
                ConsecutiveFailures, CreatedAt }

EgressSample  { Id, MinuteUtc, EgressRouteId?, Direction (ToGoogle|ToClient|ToS3|ToTelegram), Bytes }

S3Destination { Id, Label, ServiceUrl, Region, Bucket, KeyPrefix, AccessKeyIdProtected,
                SecretAccessKeyProtected, UsePathStyle, CreatedAt }
```

- `EgressRoute` and `S3Destination` carry **no** `TenantId`. Like `GoogleAccount` (M1 §5) and
  `PoolSettings` (M2 §4), they describe the operator's infrastructure, and under M1 §1.4 a customer
  must never learn one exists. Both are covered by M5's operator-only route test.
- `EgressSample.EgressRouteId` is nullable — bytes sent to a downloading client did not leave through a
  chosen route unless one was in force, and a `ToTelegram` sample never has one, because the connection
  that carries those bytes is opened by another process entirely (§10).
- **`Direction` has four values, not three, and the fourth is only half a measurement.** `ToTelegram`
  records the outbound leg of a document send, counted on loopback as a 1:1 proxy for the real send.
  There is deliberately **no** inbound counterpart: bytes arriving from Telegram never pass through this
  process, so there is no row to write and §10 renders that direction as unmeasured rather than as zero.
  Adding an inbound value later without a real source behind it would put a fabricated zero in the
  database rather than only on the chart.
- Proxy credentials and S3 keys use the same `ITokenProtector` as the Google tokens.
- **Changed:** M2's `PoolSettings` singleton gains `ConcurrentChunks` and `ChunkSizeMiB`, seeded from
  M3's `Transfer:*` config values. M6 adds nothing else to it; `AutoStopNearQuota` and `SoftStopBytes`
  stay M2's.
- **No job table.** §9 uses M3's `Job.Type = Export` and `Job.TargetDescriptor`, both of which exist
  from M3's first migration.

One migration. `EgressRoute` is seeded with the single `Direct` row.

## 12. Before implementation starts

Four things are needed from the owner. The first two block §4–§7 entirely; the others block one section
each.

1. **Spread or burst?** Does the operator intend to move the 1.5 TB/day evenly, or in short windows?
   Evenly, and §4–§7 solve a problem the product cannot have (§3). In bursts, they *may* be justified —
   but the honest answer is more likely a faster port or a second worker machine than more addresses on
   this one.
2. **Approve step 2 of §3's measurement** — one small VPS elsewhere for a month and an evening
   alongside M3's §2.3 run. Until it has happened, §4–§7 are a guess with a settings screen. The
   recommendation in this document is to ship §10 and §8 now, measure, and re-read §4 with the numbers.
3. **Is Drive Union deployed in a container, and on which network mode?** Bridge networking removes
   `LocalSourceIp` routes from the product entirely (§6). Cheaper to decide now than after a settings
   screen implies the feature exists.
4. **Which S3, whose bucket, and for whom?** AWS, OVH Object Storage, Backblaze B2 and a self-hosted
   MinIO differ in request pricing, endpoint style, and whether they support the incomplete-multipart
   lifecycle rule §9 depends on. Also: is export an operator tool, or customer-facing with per-tenant
   destinations? The design shows it only in the operator's queue, and M5 makes it operator-only by
   default.

The OVH box's NIC speed and spool free space are needed too; M3 §13.7.7 already asks for both, and M6
uses the same answer rather than asking twice.

## 13. Deliberately not in M6

- **Bandwidth throttling per job or per tenant.** M3 §14 handed this to M6 together with "the traffic
  chart that would justify it". Taking the chart, declining the throttle: a rate limiter needs a policy
  saying whose traffic to slow and by how much, and that is a billing and SLA question. M1 §12 records
  that billing is unscoped anywhere, and M5 §14 declines per-tenant egress caps for the same reason.
  The chart makes the case measurable; it does not make the policy exist.
- **Automatic weighting.** No latency- or throughput-driven auto-tuning of traffic share. §7 says why
  latency is the wrong input, and a throughput controller needs a stable signal this product has not
  yet demonstrated it has. Weights stay manual and predictable.
- **Split-file parallel Drive sessions.** M3 §2.4 designed and deferred it, behind M3's own
  measurement and an `IResumableWriter` seam. It is the correct answer to a per-session throughput cap
  and it is not a network-tuning feature; M6 does not resurrect it.
- **Per-tenant egress routes or per-tenant chunk settings.** Operator-global, per M5 §10. Per-tenant
  network policy is a support burden with no request behind it.
- **A second machine as a full upload worker.** If §3 concludes the port is the ceiling, the answer is
  horizontal — a worker with its own uplink — which needs M3's `LeaseOwner` to mean something across
  machines and a SignalR backplane (M3 §14). That is a slice, not a proxy row.
- **IPv6 egress routes.** The hypothesis under test concerns the v4 path; a second address family
  doubles the diagnosis surface for no measured gain.
- **S3 import, and scheduled or recurring exports.** The design shows one finished export row. M3 §14
  already declines a scheduler; a backup schedule is a product feature nobody has asked for.
- **Server-side deduplication or compression before export.** Real savings, entirely separate concern.
