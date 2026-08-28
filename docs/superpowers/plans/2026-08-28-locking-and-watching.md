# Locking a file that is here, and watching one without downloading it

Written 2026-08-28, after the work. The ask was two things that turned out to share almost nothing:

1. A file already uploaded in the clear should be able to be encrypted in place — "like the link
   upload" — and **the readable copy must be deleted from Google Drive only after the encrypted one
   is completely uploaded.**
2. Films and the like should be watchable, encrypted or not, without having to download them first.

## What was already true

- **Server-side sealing existed.** `RemoteFetcher` (P13b) already reads a stream, seals it segment by
  segment and writes it into Drive. E1 is that loop inside `AccountMigrator`'s shape, which already
  did read-from-Drive, write-to-Drive, verify, repoint, delete-the-old.
- **`du1` was built for random access.** Segment *i* sits at `i * (segmentSize + 16)`, so a plaintext
  range maps to a contiguous ciphertext range by arithmetic. P7b turned that into a service worker.
- **The panel served no bytes at all.** Three byte routes existed — the public link, the API key, the
  S3 gateway — and none was cookie-authenticated. A customer reached their own file by making a
  share link.

## Decisions taken, and by whom

- **Existing share links are revoked when a file is locked** (asked, and chosen). A link handed out
  as "click and it downloads" that silently becomes "type a passphrase nobody gave you" is worse
  than one that has stopped working: the second is a thing the sender can be told about, the first
  is one the recipient blames themselves for.
- **Both surfaces get a player** (asked, and chosen): the owner's panel and the public link page.

## E1 — locking a file that is already here — **done, `bc0ca51` + `a075f68` + `15b578a`**

The order is the feature:

1. read the plaintext, seal it, write a **new** Drive file;
2. ask Drive what it stored and compare **length and checksum** — a missing checksum is refused, because
   "I could not check" must never read as "I checked and it was fine" on a path that ends in a delete;
3. write the header and repoint the catalogue;
4. **then** delete the readable copy.

`SourceDriveFileId` outlives the swap, so a process that stops between 3 and 4 — the dangerous
gap — is picked up again and finishes the delete rather than leaving a readable copy nobody points
at. Moving the delete before the sealing fails three tests.

The file keeps its row: id, name, folder and tags all survive. Locking needs room for both copies
and says so before it starts.

**The passphrase does not reach the server**, and this is where E1 deliberately differs from the
link-upload path it resembles. That path sends what the customer typed and derives on the server,
which it can defend — it is fetching the file, so it holds the plaintext anyway. The defence does
not extend to the passphrase: people use one secret for everything, so a server that has seen it
once could open every file that customer ever locked in their own browser. E1 derives in the browser
and posts the header plus the content key for this one file.

> **Owed, and since paid — `f854344`.** P13b was brought up to this protocol: a link fetch now posts
> custody derived in the browser instead of the passphrase, and the customer can still supply the key
> themselves. `FetchCustodyTests` holds the line by asserting the server-side path has no
> `DeriveWrappingKey` and no `WrapKey` left in it.

## E2 — a player in the panel — **done, `e4b1c89`**

Adds `/files/{id}/content`, the first cookie-authenticated byte route. It **meters and caps**, because
the API and S3 gates were closed precisely to stop "your own files are free if you fetch them a
particular way", and a panel route without the same gate is that hole with a nicer front door.

Unlocked files play directly. Locked ones go through P7b: passphrase in the panel, key unwrapped in
the browser, service worker decrypting a segment at a time. Nothing is fetched until play is pressed.

## E3 — big films on a public link — **done, `8222f71`**

The 25 MB preview ceiling stays, and this agrees with it rather than working around it. A preview
spends no download — it cannot, or a link capped at five would be empty after five page loads — so
the ceiling is the only thing bounding what a capped link leaks per request.

A big film gets a play button that reads from the **download** address, so pressing play spends a
download exactly as pressing Download does; seeking afterwards spends none. Measured against a real
link: play took the count 3 → 4, a mid-file seek left it at 4.

## Not done

- **The browser halves of E1 and E2 have not been driven in a real browser.** They are behind
  sign-in. The engines are covered by tests — 8 for the lock runner including a mutation check on the
  delete ordering, 3 for the new byte route including one on the cap — and the crypto contract
  (`exportKey` on the sealed content key) was verified in a browser. The user said they would test
  the UI themselves.
- No bulk locking. One file at a time; the queue and the worker would take a list without changes,
  but the card is per-file.
- Locking is exempt from the egress meter, argued in `EveryEgressPathIsMeteredTests`: the bytes go
  from a pool account to this process and back to the same pool account, and no reader receives one.
