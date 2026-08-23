# Remaining work — the queue

**Date:** 2026-08-24 · **Standing instruction from the owner:** run everything to completion, parallel
where the files allow it and sequential where they do not, without waiting for answers. Where a
decision is genuinely theirs and unanswered, take the spec's recommendation, build it, and record the
assumption here rather than stopping.

The constraint that shapes the ordering is not effort, it is **file ownership**. Two agents editing one
file produce two incompatible versions of it, so anything sharing a file waits.

## In flight

| # | Slice | Owns |
|---|---|---|
| A | Telegram identity + linking — tests for the code that already landed | `Core/Telegram`, `Infrastructure/Telegram`, `DbContext`, `Migrations`, `Controllers/TelegramController.cs`, `Views/Telegram`, `tests/Telegram` |
| B | Telegram spec revision — self-hosted server, two bots, Drive-is-storage | the Telegram spec only |
| C | English: mechanism, culture selection, shell, Identity screens | `Web/Localization`, `Web/Resources`, `Views/Shared`, `Areas/Identity`, `app.css`, `tests/Localization` |
| D | `deploy/` scaffolding for the self-hosted Bot API server | `deploy/telegram-bot-api/**` |

## Queued, with what each waits on

| # | Slice | Waits on | Why it cannot start now |
|---|---|---|---|
| E | English across the remaining panel screens | C | needs C's mechanism and its naming rules |
| F | Plans & quotas P1 — plan model, per-file cap | A | A holds `DbContext` and the migrations |
| G | Telegram T1 transport — polling + webhook, the two bots, outbox, rate limits | A, B, D | needs the identity layer, the settled spec, and somewhere to point the client |
| H | Telegram T1 file flow — send from Drive, receive to Drive, auto-delete | G | there is nothing to send through until a transport exists |
| I | Plans & quotas P2 — traffic accounting | F | counters hang off P1's columns |
| J | UI polish pass | E, F, H | it is the last thing that should touch a view, or it polishes markup that is about to change |

## Assumptions taken because the answer has not arrived

Each of these is the spec's own recommendation, built rather than waited on. Reversing any of them is a
day, not a rewrite, and the seam is named.

1. **«دو گیگ» is the per-file cap**, not a storage cap. A storage cap of 2 GB contradicts the 96 GB
   upload path M1 built. Seam: one number on the tenant row.
2. **Both bots on the self-hosted server.** A management bot on the cloud API accepts only 20 MB
   inbound, which makes "send a file to the bot and it lands in your Drive" fail on the second file
   anybody tries. Seam: a base URL per bot.
3. **The delivery message is deleted on a timer that starts when the send completes, not when it
   begins**, and the window is configuration. A minute measured from the start interrupts a 2 GB
   download on any ordinary connection.
4. **The bot may create links; it may not delete files or revoke links.** Revocation burns a slug for
   ever (M4 §2) and a mis-tap in a chat has no undo. Seam: two handlers and two buttons.
5. **Plan numbers are placeholders** and are marked as such on the operator's screen, because inventing
   a price list is not the same as building the machinery that enforces one.

## Credentials that are still nobody else's to supply

Not blockers for building — each is stood off behind a stub the way the local-disk Drive backend stood
off Google — but they are blockers for *running*.

- Google OAuth client id and secret. Enter from the panel; no terminal needed.
- Telegram bot token, from @BotFather. Two, if the two-bot split is kept.
- `api_id` / `api_hash` from my.telegram.org, against a personal Telegram account. This one is an
  ownership decision, not a configuration one: the server is registered to whoever's account issues it.
