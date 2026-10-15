// DeSign Mage feedback relay (Cloudflare Worker).
//
// The program posts a feedback report here instead of straight to Discord, so the Discord webhook address never ships inside a
// build. This Worker checks the request, rebuilds a clean Discord message from only the fields the program sends, and forwards it
// to the webhook stored in the Worker secret DISCORD_WEBHOOK_URL. The address is never logged, echoed or put in a response.
//
// Request (what the program sends): POST, multipart/form-data
//   payload_json   Discord-style JSON: one embed (title, description, color, fields, footer, timestamp, image) + attachments
//   files[0..n]    the report .txt, an optional screenshot (.png/.jpg) and designs (.dsm)
//   header         X-DeSignMage-Client: feedback-v1   (public; only filters stray traffic)
//
// Answers: JSON { ok, error?, message } with a plain-words message the program can show as it is.

export const CLIENT_HEADER = 'X-DeSignMage-Client';
export const CLIENT_VALUE = 'feedback-v1';

export const LIMITS = {
  maxRequestBytes: 8_000_000,   // whole request (Discord refuses more than ~8-10 MB)
  maxFileBytes: 7_700_000,      // one attachment
  maxFiles: 8,
  maxPayloadChars: 20_000,      // payload_json
  title: 256,
  description: 4096,
  fieldName: 256,
  fieldValue: 1024,
  maxFields: 25,
  footer: 2048,
  threadName: 100,
};

// What the program attaches: the extension decides the type (the sender's own Content-Type is not trusted).
const FILE_TYPES = {
  txt: { contentType: 'text/plain; charset=utf-8' },
  png: { contentType: 'image/png', magic: [0x89, 0x50, 0x4e, 0x47] },
  jpg: { contentType: 'image/jpeg', magic: [0xff, 0xd8, 0xff] },
  jpeg: { contentType: 'image/jpeg', magic: [0xff, 0xd8, 0xff] },
  dsm: { contentType: 'application/octet-stream' },
};
const FILE_NAME = /^[A-Za-z0-9][A-Za-z0-9._ -]{0,79}$/;

class Refusal extends Error {
  constructor(status, error, message, extra = {}) {
    super(message);
    this.status = status;
    this.error = error;
    this.extra = extra;
  }
}

function reply(status, body, headers = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', ...headers },
  });
}

const trunc = (s, n) => {
  s = typeof s === 'string' ? s : '';
  return s.length <= n ? s : s.slice(0, n - 1) + '\u2026';
};

export default {
  async fetch(request, env) {
    try {
      return await handle(request, env);
    } catch (e) {
      if (e instanceof Refusal) {
        const headers = e.extra.retryAfter ? { 'retry-after': String(e.extra.retryAfter) } : {};
        const body = { ok: false, error: e.error, message: e.message };
        if (e.extra.retryAfter) body.retry_after = e.extra.retryAfter;   // the program's sender reads this field
        return reply(e.status, body, headers);
      }
      // Never include e.message: be sure nothing from a failed fetch (which could name the webhook) reaches the caller.
      console.error('relay: unexpected error', e && e.name);
      return reply(500, { ok: false, error: 'relay_error', message: 'The feedback service had a problem. Your report is kept and will be tried again later.' });
    }
  },
};

async function handle(request, env) {
  const url = new URL(request.url);

  if (request.method === 'GET' && url.pathname === '/health') {
    return reply(200, {
      ok: true,
      enabled: isEnabled(env),
      configured: Boolean(env.DISCORD_WEBHOOK_URL) && Boolean(env.RATE),
      limits: { perComputerPerHour: rateLimit(env), perDay: dailyLimit(env) },   // what is in force (not a secret): lets the owner check a setting without posting
    });
  }
  if (request.method !== 'POST' || url.pathname !== '/') {
    throw new Refusal(404, 'not_found', 'Nothing here.');
  }
  // Cheapest checks first. None of them touches storage, so junk traffic costs nothing of the daily counter quota.
  if (request.headers.get(CLIENT_HEADER) !== CLIENT_VALUE || !(request.headers.get('user-agent') || '').startsWith('DeSignMage/')) {
    throw new Refusal(403, 'bad_client', 'This address only accepts reports from DeSign Mage.');
  }
  // The program is not a web page: anything that carries browser headers is somebody's script on a web page.
  if (request.headers.has('origin') || request.headers.has('sec-fetch-mode') || request.headers.has('sec-fetch-site')) {
    throw new Refusal(403, 'bad_client', 'This address only accepts reports from DeSign Mage.');
  }
  if (!isEnabled(env)) {
    throw new Refusal(503, 'relay_disabled', 'Receiving reports is switched off for now. Your report is kept and will be sent later.');
  }
  if (!env.DISCORD_WEBHOOK_URL) {
    throw new Refusal(503, 'not_configured', 'The feedback service is not set up yet. Your report is kept and will be sent later.');
  }

  // Cheap checks first, before reading the body.
  const length = Number(request.headers.get('content-length'));
  if (!Number.isFinite(length) || length <= 0) throw new Refusal(411, 'length_required', 'The report could not be sent (no size given).');
  if (length > LIMITS.maxRequestBytes) throw new Refusal(413, 'too_large', 'The report is too big to send. Try again without the design or screenshot.');
  if (!(request.headers.get('content-type') || '').toLowerCase().startsWith('multipart/form-data')) {
    throw new Refusal(415, 'bad_type', 'The report was not in the expected form.');
  }

  let form;
  try { form = await request.formData(); } catch { throw new Refusal(400, 'bad_form', 'The report could not be read.'); }

  // Everything that can be checked without storage is checked before the counters are touched.
  const { message, files } = await validate(form);
  await checkRate(request, env);
  await forward(message, files, env);
  return reply(200, { ok: true, message: 'Thanks, sent.' });
}

function isEnabled(env) {
  return String(env.RELAY_ENABLED ?? 'true').trim().toLowerCase() !== 'false';
}

// ---------------------------------------------------------------------------------------------------------------- rate limit

// Two counters in a KV namespace (binding RATE), fixed windows:
//   per computer per hour  (a hash of the caller's IP address)   RATE_LIMIT_PER_HOUR, default 10, 0 = off
//   whole relay per day    (UTC day)                              DAILY_LIMIT, default 200, 0 = off
// A report that is refused (limit reached) writes nothing. Each accepted report is two KV writes, and the free plan allows 1,000
// writes a day, so the default daily cap of 200 leaves plenty of room; if KV itself runs out the relay answers "busy" (fails closed).
const rateLimit = (env) => (env.RATE_LIMIT_PER_HOUR === undefined ? 10 : Number(env.RATE_LIMIT_PER_HOUR));
const dailyLimit = (env) => (env.DAILY_LIMIT === undefined ? 200 : Number(env.DAILY_LIMIT));

async function checkRate(request, env) {
  const limit = rateLimit(env);
  const daily = dailyLimit(env);
  if (limit === 0 && daily === 0) return;                            // both turned off on purpose
  if (!Number.isFinite(limit) || limit < 0 || !Number.isFinite(daily) || daily < 0 || !env.RATE) {
    // Forgetting the counter binding must never silently remove the limits.
    throw new Refusal(503, 'not_configured', 'The feedback service is not set up yet. Your report is kept and will be sent later.');
  }
  const hour = 3_600_000;
  const day = 86_400_000;
  const now = Date.now();
  const hourBucket = Math.floor(now / hour);
  const dayBucket = Math.floor(now / day);
  const ipKey = `rl:${await shortHash(request.headers.get('cf-connecting-ip') || 'unknown')}:${hourBucket}`;
  const dayKey = `day:${dayBucket}`;
  const waitHour = Math.max(1, Math.ceil(((hourBucket + 1) * hour - now) / 1000));
  const waitDay = Math.max(1, Math.ceil(((dayBucket + 1) * day - now) / 1000));

  let ipCount = 0;
  let dayCount = 0;
  try {
    [ipCount, dayCount] = (await Promise.all([env.RATE.get(ipKey), env.RATE.get(dayKey)])).map((v) => Number(v) || 0);
  } catch {
    throw new Refusal(503, 'busy', 'The feedback service is busy right now. Your report is kept and will be tried again later.', { retryAfter: 600 });
  }
  if (limit > 0 && ipCount >= limit) {
    throw new Refusal(429, 'rate_limited', `Too many reports from this computer. Please try again in ${Math.ceil(waitHour / 60)} minutes.`, { retryAfter: waitHour });
  }
  if (daily > 0 && dayCount >= daily) {
    throw new Refusal(503, 'busy', 'The feedback service has received a lot of reports today. Your report is kept and will be tried again later.', { retryAfter: Math.min(waitDay, 3600) });
  }
  try {
    await Promise.all([
      limit > 0 ? env.RATE.put(ipKey, String(ipCount + 1), { expirationTtl: 2 * 3600 }) : null,
      daily > 0 ? env.RATE.put(dayKey, String(dayCount + 1), { expirationTtl: 2 * 86400 }) : null,
    ]);
  } catch {
    throw new Refusal(503, 'busy', 'The feedback service is busy right now. Your report is kept and will be tried again later.', { retryAfter: 600 });
  }
}
async function shortHash(text) {
  const bytes = new TextEncoder().encode('dsm-relay|' + text);
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
  return [...digest.slice(0, 12)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

// ---------------------------------------------------------------------------------------------------------------- validation

async function validate(form) {
  const raw = form.get('payload_json');
  if (typeof raw !== 'string' || raw.length === 0 || raw.length > LIMITS.maxPayloadChars) {
    throw new Refusal(400, 'bad_payload', 'The report text was missing or too long.');
  }
  let payload;
  try { payload = JSON.parse(raw); } catch { throw new Refusal(400, 'bad_payload', 'The report text could not be read.'); }
  if (!payload || typeof payload !== 'object') throw new Refusal(400, 'bad_payload', 'The report text could not be read.');

  // Only files[0..n] are accepted; anything else in the form is an error rather than silently forwarded.
  const files = [];
  for (const [name, value] of form.entries()) {
    if (name === 'payload_json') continue;
    if (!/^files\[\d{1,2}\]$/.test(name) || typeof value === 'string') throw new Refusal(400, 'bad_field', 'The report had parts that are not allowed.');
    files.push({ index: Number(name.slice(6, -1)), file: value });
  }
  if (files.length > LIMITS.maxFiles) throw new Refusal(400, 'too_many_files', 'The report had too many attachments.');
  files.sort((a, b) => a.index - b.index);

  const seen = new Set();
  const checked = [];
  for (const { file } of files) {
    const fileName = file.name || '';
    const ext = fileName.includes('.') ? fileName.split('.').pop().toLowerCase() : '';
    const type = FILE_TYPES[ext];
    if (!FILE_NAME.test(fileName) || !type) throw new Refusal(415, 'bad_file_type', 'An attachment is not a kind the feedback service accepts.');
    if (seen.has(fileName.toLowerCase())) throw new Refusal(400, 'duplicate_file', 'Two attachments have the same name.');
    seen.add(fileName.toLowerCase());
    if (file.size === 0) continue;   // an empty attachment is just left out
    if (file.size > LIMITS.maxFileBytes) throw new Refusal(413, 'too_large', 'An attachment is too big to send. Try again without the design or screenshot.');
    if (type.magic) {
      const head = new Uint8Array(await file.slice(0, type.magic.length).arrayBuffer());
      if (!type.magic.every((b, i) => head[i] === b)) throw new Refusal(415, 'bad_file_type', 'An attachment is not the kind of file it says it is.');
    }
    checked.push({ fileName, contentType: type.contentType, file });
  }

  const message = cleanMessage(payload, new Set(checked.map((f) => f.fileName)));
  checkShape(message.embeds[0], checked.map((f) => f.fileName));
  return { message, files: checked };
}

// The program's reports all look alike: this is the shape it sends (ReportBuilder, RelaySender). A request that is not in that
// shape is somebody else's, or an old or modified program, and is turned away. It is not a secret check (the source is public, so
// anybody can copy the shape); it keeps stray traffic and careless scripts out, and it keeps the channel's posts uniform.
// If the program's report format ever changes, change this and the client header value (eedback-v1) together.
const KIND_COLORS = { Bug: 0xd9534f, Feature: 0x2e86de, Crash: 0x8b0000 };
const REPORT_TXT = /^DeSignMage-(Bug|Feature|Crash)-[A-Za-z0-9._-]{1,70}\.txt$/;
const FOOTER = /^Report [A-Za-z0-9-]{8,60} \u00b7 .{8,40} UTC$/;   // the date part follows the sender's culture in older programs

function checkShape(embed, fileNames) {
  const bad = (message = 'The report is not in the form DeSign Mage sends.') => new Refusal(400, 'bad_payload', message);

  const kind = /^(Bug|Feature|Crash): \S/.exec(embed.title)?.[1];
  if (!kind || embed.color !== KIND_COLORS[kind]) throw bad();
  if (!embed.description || !FOOTER.test(embed.footer?.text ?? '')) throw bad();

  const fields = new Map((embed.fields ?? []).map((f) => [f.name, f.value]));
  const needed = kind === 'Feature' ? ['From', 'Version'] : ['From', 'Version', 'Windows'];
  if (!needed.every((name) => fields.has(name) && fields.get(name) !== '-')) throw bad();

  // Made by the program when the report was written (it can wait in the outbox up to 30 days).
  const made = Date.parse(embed.timestamp ?? '');
  if (!Number.isFinite(made) || made > Date.now() + 86_400_000 || made < Date.now() - 35 * 86_400_000) throw bad('The report has a wrong date. Check the clock on this computer.');

  // Attachments: the one report text (named for its kind), one screenshot, designs. A bug or crash report always has its text.
  let texts = 0;
  let shots = 0;
  for (const name of fileNames) {
    const m = REPORT_TXT.exec(name);
    if (m) { if (m[1] !== kind || ++texts > 1) throw bad(); }
    else if (/^screenshot\.(png|jpe?g)$/.test(name)) { if (++shots > 1) throw bad(); }
    else if (!/\.dsm$/.test(name)) throw new Refusal(415, 'bad_file_type', 'An attachment is not a kind the feedback service accepts.');
  }
  if (kind !== 'Feature' && texts !== 1) throw bad();

  // Links in the text the sender wrote: a bug report hardly needs more than a few.
  const written = [embed.title, embed.description, ...fields.values()].join(' ');
  if ((written.match(/https?:\/\//gi) ?? []).length > 3) throw bad('The report has too many web links in it.');
}

// Rebuilds the Discord message from known fields only (no content, avatar, components, extra embeds, link images, mentions).
function cleanMessage(payload, fileNames) {
  const embeds = Array.isArray(payload.embeds) ? payload.embeds : [];
  if (embeds.length !== 1 || !embeds[0] || typeof embeds[0] !== 'object') throw new Refusal(400, 'bad_payload', 'The report text could not be read.');
  const e = embeds[0];

  const embed = {
    title: trunc(e.title, LIMITS.title),
    description: trunc(e.description, LIMITS.description),
    color: Number.isInteger(e.color) && e.color >= 0 && e.color <= 0xffffff ? e.color : 0x808080,
  };
  if (Array.isArray(e.fields)) {
    embed.fields = e.fields.slice(0, LIMITS.maxFields)
      .filter((f) => f && typeof f === 'object')
      .map((f) => ({
        name: trunc(f.name, LIMITS.fieldName) || '-',
        value: trunc(f.value, LIMITS.fieldValue) || '-',
        inline: f.inline === true,
      }));
  }
  if (e.footer && typeof e.footer.text === 'string') embed.footer = { text: trunc(e.footer.text, LIMITS.footer) };
  if (typeof e.timestamp === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z$/.test(e.timestamp)) embed.timestamp = e.timestamp;
  if (e.image && typeof e.image.url === 'string') {
    const m = /^attachment:\/\/(.+)$/.exec(e.image.url);
    if (m && fileNames.has(m[1])) embed.image = { url: e.image.url };   // only a picture attached to this report, never a link
  }

  return {
    username: 'DeSign Mage feedback',
    allowed_mentions: { parse: [] },
    embeds: [embed],
  };
}

// ---------------------------------------------------------------------------------------------------------------- forwarding

async function forward(message, files, env) {
  const out = new FormData();
  const message2 = { ...message };
  if (String(env.FORUM_THREADS ?? '').trim().toLowerCase() === 'true') {
    message2.thread_name = trunc(message.embeds[0].title, LIMITS.threadName) || 'Report';
  }
  if (files.length > 0) message2.attachments = files.map((f, i) => ({ id: i, filename: f.fileName }));
  out.append('payload_json', JSON.stringify(message2));
  files.forEach((f, i) => out.append(`files[${i}]`, new Blob([f.file], { type: f.contentType }), f.fileName));

  let response;
  try {
    response = await fetch(env.DISCORD_WEBHOOK_URL, { method: 'POST', body: out });
  } catch {
    throw new Refusal(502, 'discord_unreachable', 'The feedback service could not reach its destination. Your report is kept and will be tried again later.');
  }
  if (response.ok) return;

  console.error('relay: discord answered', response.status);
  if (response.status === 429) {
    const wait = Math.min(3600, Math.max(1, Math.ceil(Number(response.headers.get('retry-after')) || 5)));
    throw new Refusal(429, 'rate_limited', 'The feedback service is busy for a moment. Your report is kept and will be tried again later.', { retryAfter: wait });
  }
  if (response.status === 413 || response.status === 400) {
    // The program retries a 413 without attachments.
    throw new Refusal(413, 'too_large', 'The report was too big for the destination. Try again without the design or screenshot.');
  }
  // 401/403/404 mean the webhook was deleted or replaced wrongly: the owner has to fix it, so the program just keeps the report.
  throw new Refusal(502, 'destination_refused', 'The feedback service could not deliver the report. It is kept and will be tried again later.');
}
