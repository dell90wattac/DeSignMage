# Feedback relay

A tiny Cloudflare Worker that receives feedback and crash reports from DeSign Mage and passes them to a Discord channel. The
Discord webhook address is a **Worker secret**, so no build of the program (and no file in this repository) ever contains it.

```
DeSign Mage  --POST multipart-->  this Worker  --checks, rebuilds a clean message-->  Discord webhook (Worker secret)
```

Runs entirely on Cloudflare's **free plan** (Workers free, KV free). Nothing here costs money.

## What it checks

| Guard | Detail |
|---|---|
| App identifier | the header `X-DeSignMage-Client: feedback-v1` and a user agent starting with `DeSignMage/` must be present, and the request must carry no browser headers (`Origin`, `Sec-Fetch-*`). All public (they are in the source), none a secret; they only turn away stray traffic and web-page scripts. |
| Report shape | the report must look like the ones the program builds: title `Bug: ...` / `Feature: ...` / `Crash: ...` with that kind's colour, a `Report <id> · <date> UTC` footer, the fields From and Version (and Windows for bugs and crashes), a creation time within the last 35 days and not in the future, exactly one report text file named for the same kind (bugs and crashes), at most one screenshot (`screenshot.png/jpg`) and some `.dsm` designs, and no more than 3 web links in the text. Anything else is refused with HTTP 400 `bad_payload`. These checks run **before** any counter is touched, so junk costs none of the free-plan storage quota. |
| Size | the request must declare its size (at most 8,000,000 bytes), one attachment at most 7,700,000 bytes, at most 8 attachments. |
| Only what the program sends | form parts `payload_json` and `files[0..n]` and nothing else; attachments only `.txt`, `.png`, `.jpg`/`.jpeg` (checked by their first bytes) and `.dsm`, with plain file names. The content type is set from the extension, not taken from the sender. |
| A clean message | the Discord message is **rebuilt** from known fields (one embed: title, description, colour, fields, footer, timestamp, an image only if it is an attachment of the same report). No `content`, no mentions (`allowed_mentions` is empty), no links as images, text trimmed to Discord's limits. |
| Rate limit | 10 reports per hour per computer (`RATE_LIMIT_PER_HOUR`): "computer" means the caller's IP address (hashed; no address is stored), counted in a KV namespace. More than that gets HTTP 429 with a `retry_after`; the program keeps the report and tries later. A refused request writes nothing. |
| Daily cap | 200 reports a day for the whole relay (`DAILY_LIMIT`), however many addresses they come from. Past that the answer is HTTP 503 `busy`; the programs keep their reports and try again later. This protects the Discord channel and the free storage quota from a flood from many addresses. |
| Kill switch | the Worker variable `RELAY_ENABLED` = `false` makes every report answer HTTP 503 ("switched off"); the program keeps reports in its outbox and sends them when it is switched on again. |
| Never echoes the webhook | the address is only used for the one outgoing request; errors never include it, and `GET /health` only says `enabled` / `configured`. |

Answers are JSON `{ "ok": false, "error": "<code>", "message": "<plain words>" }`. Error codes: `bad_client` (403), `relay_disabled`
and `not_configured` and `busy` (503; `busy` also when the daily cap is reached), `rate_limited` (429), `too_large` (413), `bad_file_type` (415), `bad_form`, `bad_payload` (not the report shape, a wrong date, too many links),
`bad_field`, `too_many_files`, `duplicate_file` (400), `length_required` (411), `discord_unreachable` and `destination_refused` (502).

**What this cannot do:** nothing here proves a request really comes from the program. The source is public, so anybody can copy the
header, the user agent and the report shape; there is no secret that could be hidden inside a public program. The checks keep out stray
traffic, web pages and careless scripts, and the limits cap what a determined sender can do: 10 reports an hour from one address and 200
a day in total, each at most 8 MB, every one posted as the same plain message (no mentions, no free text outside the report). If someone
does spam it anyway, set `RELAY_ENABLED` to `false`; real reports wait in the programs' outboxes.

**Free-plan limits to know:** KV allows 1,000 writes a day, and each accepted report uses two (the hourly and the daily counter), so
about 500 reports a day is the most the free plan can count; the default daily cap of 200 stays well under that, and if KV itself runs
out the relay answers `busy` (503). Workers allow 100,000 requests a day; if junk ever used them up, Cloudflare refuses further requests
(it never charges on the free plan) and the programs keep their reports.

## Setup (one time, no tools needed)

1. Cloudflare dashboard > **Workers & Pages** > **Create** > **Create Worker**, name it (the program expects the address in
   `RelayConfig.DefaultUrl`), **Deploy**. Then **Edit code**, replace everything with [`src/index.js`](src/index.js), **Deploy**.
2. **Storage & Databases > KV > Create a namespace** (any name). In the Worker: **Settings > Bindings > Add > KV namespace**, variable
   name exactly `RATE`.
3. Worker **Settings > Variables and Secrets**:
   * **Secret** `DISCORD_WEBHOOK_URL` = the Discord webhook address (type it yourself in the dashboard).
   * optional **Text** variables: `RELAY_ENABLED` (`false` = off), `RATE_LIMIT_PER_HOUR` (default 10; `0` = no per-computer limit),
     `DAILY_LIMIT` (default 200; `0` = no daily cap),
     `FORUM_THREADS` (`true` when the Discord channel is a Forum channel: each report becomes its own post).
4. Check: open `https://<worker>.<account>.workers.dev/health`. It should say `{"ok":true,"enabled":true,"configured":true,"limits":{"perComputerPerHour":10,"perDay":200}}` (the limits in force: a quick
   way to check a variable without posting anything).

### With the command line instead (optional)

`wrangler.jsonc` describes the same Worker. With Node.js installed: `npm install`, `npx wrangler login` (your browser asks you to
approve), `npx wrangler secret put DISCORD_WEBHOOK_URL` (type the address in your own terminal), `npx wrangler deploy` (it creates the
KV namespace itself). `npm test` runs the relay's tests (a fake Discord and a fake counter store, no network).

## Operating it

* **Switch off / on:** set `RELAY_ENABLED` to `false` / delete it or set `true`. Takes effect within seconds.
* **Rotate the Discord webhook** (if it ever leaks, or the channel changes): in Discord delete the webhook and create a new one, then in
  the Worker edit the secret `DISCORD_WEBHOOK_URL` and Deploy. No program build has to change.
* **Change the limits:** the `RATE_LIMIT_PER_HOUR` and `DAILY_LIMIT` variables (check `/health`).
* **If the program's report format changes** (new field names, file names, colours): change `checkShape` in `src/index.js` and the
  `X-DeSignMage-Client` value together, then redeploy. `RelayShapeTests` in the Feedback tests pins the program's side.
* **Redeploy after editing `src/index.js`:** paste it into the dashboard editor again (or `npx wrangler deploy`).
