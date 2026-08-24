# Seven phases, in the order that makes each one cheap

**Date:** 2026-08-24 · **Status:** plan agreed; P1 starts immediately

The owner's list, verbatim, ten items:

> آپلود شدن باید تو بک‌گراند هم ادامه پیدا کنه … بخش پاز کردن داشته باشه … مثل دانلود منیجر انتخاب
> کنه … هنوز صفحات بهم ریخته‌ان … چقدر از ظرفیت روز و ظرفیت کلش رفته … api یا با پروتکل s3 …
> پوشه‌بندی و دسته‌بندی و سرچ … هر یوزر تو یه فولدر جدا … داشبورد واقعاً امکانات خوبی داشته باشه …
> کاستوم کردن لینک یا صفحه دانلود

Three answers were given before this was written, and each removes a fork:

1. **S3 is wanted for real**, not a REST substitute. It is therefore its own phase, last, and it
   depends on the object model the REST phase defines.
2. **Files already in a tenant folder stay there.** Per-user folders apply to new uploads only. No
   bytes move, no link breaks, and Drive carries two layouts for a while.
3. **Screenshots come from a browser the owner opens.** Neither browser surface is reachable from
   here right now, which is why the UI phase is the one phase that cannot start on its own.

---

## Order, and why it is this order

Not the order the list was written in. Each phase is placed where its prerequisites are already
paid for:

| # | Phase | Placed here because |
|---|---|---|
| **P1** | Uploader: background, dock, pause, concurrency | The thing being used daily, and it needs nothing from the others |
| **P2** | Per-user folders + capacity in the shell | Folders in P3 need to know whose file a file is |
| **P3** | Folders, categories, search | The largest data-model change; everything visual after it reads its rows |
| **P4** | Dashboard | Its numbers are only worth drawing once P2 and P3 produce them |
| **P5** | Link and download-page customisation | Independent of all of the above; small; ships whenever |
| **P6** | REST API and tokens | Defines the object model and the credential S3 will reuse |
| **P7** | S3-compatible gateway | Buckets are tenants and keys are P3 paths; both have to exist first |

**UI repair is not a phase.** It is a standing item inside every phase: each phase ends by looking
at the screens it touched. Batching "fix the UI" into one pass is what produced the drift twice
already — the panel was built screen by screen in parallel, and a separate cleanup phase is a
promise to allow more drift until it arrives.

---

## P1 — The uploader stops being a page

**What it must do.** Keep uploading while the customer walks around the panel. Show a dock at the
bottom, the way Drive does, that names what is in flight and opens the upload screen when pressed.
Pause and resume any file. Choose how many go at once, the way a download manager does. Select
several and act on them together.

**The crux, and it decides the architecture.** A `File` handle does not survive a page load. A
Service Worker is the obvious answer and it does not work here: for a worker to read the file after
navigation the bytes must first be copied somewhere it can reach, and this product's whole claim is
96 GB files. Copying 96 GB into IndexedDB to avoid reloading a page is not a trade.

So the page must not reload. Same-origin navigations are intercepted, the response fetched, `<main>`
swapped, history pushed. The uploader lives in the shell above `<main>` and is never unmounted.
That is what Drive itself does, and it is the only option that keeps the file handle alive.

**What that costs, stated plainly.** Every island has to survive a swap: mounted on arrival,
unmounted on departure, with no listener left on a detached node. Focus and scroll have to be
restored. The back button has to work. A swap that fails has to fall back to a real navigation
rather than leave the customer on a page that did not change. This is the riskiest change in the
plan and it is first because everything else is easier once the shell is stable.

**Pause is the protocol working as designed.** A pause aborts the chunk in flight and keeps the
confirmed byte count. A resume asks `GET /api/uploads/{id}` what Drive has actually acknowledged and
continues from there. Both endpoints already exist — this is what resumable upload was for, and the
panel has simply never used it.

**Concurrency** is a stored preference, not a constant: 1, 2, 3, 5. Files run in parallel because
each is its own Drive session; chunks within one file cannot, because Drive acknowledges a single
contiguous prefix.

**Not in P1:** resuming after the browser is closed. The file handle is gone, and the honest
behaviour is to say so on return rather than to appear to resume and stall.

---

## P2 — A folder per person, and a number above the name

**Per-user folders.** `DriveUnion/{tenant}/{user}/` for new uploads. `StoredFile` gains an owner.
Existing rows keep their folder and their `DriveFileId`, so nothing moves and nothing breaks; the
code reads the folder from the row rather than deriving it, which is what makes both layouts
coexist without a special case.

Folder resolution needs a cache. Today every upload asks Drive to find-or-create the tenant folder,
which is two API calls against a 12,000-per-minute budget for a fact that never changes.

**Capacity in the shell — and one correction to the request.** The ask was "like the operator". The
operator's card shows the **daily 750 GB per Google account**, which is a fact about the operator's
pool and is exactly what §1.4 says a customer must never see. What a tenant should see above their
name is their own two numbers: storage used against their plan's cap, and traffic this month against
their plan's. Both already exist on the tenant row. The shape of the card is the operator's; the
figures in it are the customer's.

---

## P3 — Folders, categories, search

The largest change, and the one M2 already designed: folders are logical rows, materialised per
account only when a file needs one, with the path denormalised onto `StoredFile` so a subtree query
is cheap. The price is that renaming a folder is expensive, and M2 put rename out of scope for
exactly that reason.

Categories are not folders. A file lives in one folder and carries any number of labels, which is
what makes "invoices" and "2026" both work without duplicating the file.

Search is over name, path and label, scoped to the tenant. Postgres does this well with a trigram
index; the thing to avoid is a `LIKE '%…%'` that reads every row of a table that grows for ever.

---

## P4 — A dashboard worth opening

Today it redirects. It should answer, for a customer: what is stored, what was uploaded recently,
which links are live, what has been downloaded and when. For an operator: the pool, each account's
health and remaining daily allowance, tenants near their ceilings, and the transfers in flight.

Deliberately after P2 and P3, because a dashboard drawn before its numbers exist is a mock that has
to be rebuilt.

---

## P5 — The link, and the page it opens

A custom address instead of a generated slug, with the uniqueness and reserved-word rules that
implies. A title, a description and an optional cover for the public page. The password, expiry,
download cap and alias filename that M4 specified and that were never built.

Independent of every other phase, so it can be pulled forward if it turns out to be what a customer
is actually asking for.

---

## P6 — An API, and the credential S3 will reuse

Tokens per tenant: hashed at rest, scoped, revocable, and shown once. Endpoints for listing,
uploading through the existing chunked protocol, creating links, and reading usage.

This phase defines the object model — what a path is, what a listing looks like, what an object's
metadata is. P7 is a second protocol over the same model, so getting it right here is what stops S3
from becoming a parallel implementation of the same product.

---

## P7 — S3, honestly scoped

Enough of S3 that `rclone`, `s3cmd` and the AWS SDKs work: SigV4 request signing, `ListObjectsV2`,
`HeadObject`, `GetObject` with `Range`, `PutObject`, and the multipart trio mapped onto Drive's
resumable session — `CreateMultipartUpload` opens one, `UploadPart` writes a chunk,
`CompleteMultipartUpload` finishes it. A bucket is a tenant; a key is a P3 path.

**What it will not be.** ACLs, versioning, object tagging, bucket policies, lifecycle rules,
presigned-POST forms, or the parts of the API that assume S3's own consistency model. Those are not
a long tail — they are a second product. The line is drawn where a sync client stops caring, and
the spec for this phase will say so in a list rather than leaving it to be discovered.

One thing to settle before it starts: S3 clients expect `PutObject` to be one request. Drive's
resumable session is what makes a 96 GB file possible, and a single `PutObject` of that size is the
body this product exists to avoid. The likely answer is a size threshold above which non-multipart
`PutObject` is refused with the S3 error code that tells the client to use multipart — but it needs
checking against what rclone actually does before it is designed around.

---

## What is already known to be missing, and stays missing until its phase

- Roles inside a tenant, and invitations (M5 §2 and §6). Not in this plan; they belong with P6's
  credential work if they are wanted.
- The seat-cap race: two operators taking the last seat in the same instant both pass the check.
  Closing it needs a counter column, which is a migration nobody has needed badly enough yet.
- Resuming an upload after the tab is closed — see P1.

---

## How each phase ends

Green build, green suite, the app booted, the screens it touched looked at in a browser, and a
commit that says what was decided rather than what was typed. A phase that cannot be looked at is
not finished — which is why the browser being unreachable is a blocker worth naming rather than
working around.
