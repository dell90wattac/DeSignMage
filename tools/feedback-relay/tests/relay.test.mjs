// Run with: npm test   (Node 20+; no network: Discord is replaced by a fake fetch)
import { test, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import worker, { CLIENT_HEADER, CLIENT_VALUE, LIMITS } from '../src/index.js';

const SECRET_URL = 'https://discord.com/api/webhooks/123456789012345678/SECRET_TOKEN_SHOULD_NEVER_LEAK_0123456789';
const REPORT_TXT = 'DeSignMage-Bug-20261012-101500-abc123.txt';

const realFetch = globalThis.fetch;
let discordCalls;
let discordStatus;

beforeEach(() => {
  discordCalls = [];
  discordStatus = 204;
  globalThis.fetch = async (url, init) => {
    const form = init.body;
    discordCalls.push({ url, payload: JSON.parse(form.get('payload_json')), files: [...form.entries()].filter(([k]) => k.startsWith('files[')) });
    return new Response(null, { status: discordStatus, headers: { 'retry-after': '7' } });
  };
});
afterEach(() => { globalThis.fetch = realFetch; });

function fakeKv() {
  const data = new Map();
  return { data, get: async (k) => data.get(k) ?? null, put: async (k, v) => { data.set(k, v); } };
}

const env = (extra = {}) => ({ DISCORD_WEBHOOK_URL: SECRET_URL, RATE: fakeKv(), ...extra });

const PNG = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4]);
const now = () => new Date().toISOString().replace(/\.\d+Z$/, 'Z');

// A report in the shape the program sends.
function payload(overrides = {}) {
  return {
    username: 'someone else',
    content: '@everyone buy now',
    allowed_mentions: { parse: ['everyone'] },
    embeds: [{
      title: 'Bug: it broke', description: 'text', color: 0xd9534f,
      fields: [{ name: 'From', value: 'Mom', inline: true }, { name: 'Version', value: '0.4.0', inline: true }, { name: 'Windows', value: 'Windows 11', inline: true }],
      footer: { text: 'Report 20261012-101500-abc123 · 2026-10-12 10:15 UTC' }, timestamp: now(), image: { url: 'attachment://screenshot.png' },
    }],
    attachments: [{ id: 0, filename: REPORT_TXT }],
    ...overrides,
  };
}

// Changes one thing in the embed of a good report.
function withEmbed(change) {
  const p = payload();
  change(p.embeds[0]);
  return p;
}

const goodFiles = () => [[REPORT_TXT, new Blob(['hello'], { type: 'text/plain' })], ['screenshot.png', new Blob([PNG], { type: 'image/png' })]];

const programHeaders = (extra = {}) => ({ [CLIENT_HEADER]: CLIENT_VALUE, 'user-agent': 'DeSignMage/1.0', ...extra });

function post({ json = payload(), files = goodFiles(), headers = {}, ip = '1.2.3.4' } = {}) {
  const form = new FormData();
  form.append('payload_json', typeof json === 'string' ? json : JSON.stringify(json));
  files.forEach(([name, blob], i) => form.append(`files[${i}]`, blob, name));
  return new Request('https://relay.example/', { method: 'POST', body: form, headers: { ...programHeaders(), 'cf-connecting-ip': ip, ...headers } });
}

// Request() built from FormData has no content-length until it is read; give the worker what a real client sends.
async function withLength(request) {
  const body = await request.arrayBuffer();
  const headers = new Headers(request.headers);
  headers.set('content-length', String(body.byteLength));
  return new Request(request.url, { method: 'POST', body, headers });
}

const run = async (request, e = env()) => {
  const response = await worker.fetch(await withLength(request), e);
  return { response, body: await response.json() };
};

test('a good report is forwarded, cleaned', async () => {
  const { response, body } = await run(post());
  assert.equal(response.status, 200);
  assert.equal(body.ok, true);
  assert.equal(discordCalls.length, 1);
  const sent = discordCalls[0];
  assert.equal(sent.url, SECRET_URL);
  assert.equal(sent.payload.content, undefined);                    // no free text outside the embed
  assert.deepEqual(sent.payload.allowed_mentions, { parse: [] });   // nobody can be pinged
  assert.equal(sent.payload.username, 'DeSign Mage feedback');
  assert.equal(sent.payload.embeds[0].image.url, 'attachment://screenshot.png');
  assert.deepEqual(sent.payload.attachments.map((a) => a.filename), [REPORT_TXT, 'screenshot.png']);
  assert.equal(sent.files.length, 2);
});

test('the webhook address never appears in an answer', async () => {
  for (const status of [204, 404, 401, 429, 400, 500]) {
    discordStatus = status;
    const response = await worker.fetch(await withLength(post()), env());
    const text = JSON.stringify([...response.headers]) + (await response.text());
    assert.ok(!text.includes('SECRET_TOKEN'), `leaked for ${status}`);
  }
  const health = await worker.fetch(new Request('https://relay.example/health'), env());
  assert.ok(!JSON.stringify(await health.json()).includes('SECRET'));
});

test('a link image is dropped; only attachments of the same report count', async () => {
  const json = payload();
  json.embeds[0].image = { url: 'https://evil.example/x.png' };
  await run(post({ json }));
  assert.equal(discordCalls[0].payload.embeds[0].image, undefined);
});

test('only the program gets in: app header, program name, no browser headers', async () => {
  const wrongHeader = await run(post({ headers: { [CLIENT_HEADER]: 'nope' } }));
  assert.equal(wrongHeader.response.status, 403);
  assert.equal(wrongHeader.body.error, 'bad_client');
  assert.equal((await run(post({ headers: { 'user-agent': 'curl/8.0' } }))).response.status, 403);
  assert.equal((await run(post({ headers: { origin: 'https://evil.example' } }))).response.status, 403);
  assert.equal((await run(post({ headers: { 'sec-fetch-mode': 'cors' } }))).response.status, 403);
  assert.equal(discordCalls.length, 0);
});

test('junk is refused before any counter is touched', async () => {
  const e = env();
  await run(post({ headers: { 'user-agent': 'curl/8.0' } }), e);                       // not the program
  await run(post({ json: withEmbed((x) => { x.color = 1; }) }), e);                    // not the report shape
  await run(post({ files: [['evil.exe', new Blob(['MZ'])]] }), e);                     // not an allowed attachment
  assert.equal(e.RATE.data.size, 0);
  assert.equal(discordCalls.length, 0);
});

test('the kill switch turns everything off (503, reports stay in the outbox)', async () => {
  const { response, body } = await run(post(), env({ RELAY_ENABLED: 'false' }));
  assert.equal(response.status, 503);
  assert.equal(body.error, 'relay_disabled');
  assert.equal(discordCalls.length, 0);
});

test('a missing secret is a clear 503, not a crash', async () => {
  const { response, body } = await run(post(), { RATE: fakeKv() });
  assert.equal(response.status, 503);
  assert.equal(body.error, 'not_configured');
});

test('the eleventh report in an hour from one address is limited, another address is fine', async () => {
  const e = env();
  for (let i = 0; i < 10; i++) assert.equal((await run(post(), e)).response.status, 200);
  const eleventh = await run(post(), e);
  assert.equal(eleventh.response.status, 429);
  assert.equal(eleventh.body.error, 'rate_limited');
  assert.ok(eleventh.body.retry_after > 0 && eleventh.body.retry_after <= 3600);
  assert.ok(Number(eleventh.response.headers.get('retry-after')) > 0);
  assert.equal(discordCalls.length, 10);
  assert.equal((await run(post({ ip: '9.9.9.9' }), e)).response.status, 200);
  for (const key of e.RATE.data.keys()) assert.ok(!key.includes('1.2.3.4'), 'addresses are stored hashed');
});

test('the hourly limit can be changed with RATE_LIMIT_PER_HOUR', async () => {
  const e = env({ RATE_LIMIT_PER_HOUR: '2' });
  assert.equal((await run(post(), e)).response.status, 200);
  assert.equal((await run(post(), e)).response.status, 200);
  assert.equal((await run(post(), e)).response.status, 429);
});

test('a limited request writes nothing', async () => {
  const e = env({ RATE_LIMIT_PER_HOUR: '1' });
  await run(post(), e);
  const before = JSON.stringify([...e.RATE.data]);
  assert.equal((await run(post(), e)).response.status, 429);
  assert.equal(JSON.stringify([...e.RATE.data]), before);
});

test('the whole relay has a daily cap, whatever the address', async () => {
  const e = env({ DAILY_LIMIT: '3' });
  for (let i = 0; i < 3; i++) assert.equal((await run(post({ ip: `10.0.0.${i}` }), e)).response.status, 200);
  const fourth = await run(post({ ip: '10.0.0.99' }), e);
  assert.equal(fourth.response.status, 503);          // the program keeps the report and tries later
  assert.equal(fourth.body.error, 'busy');
  assert.ok(fourth.body.retry_after > 0);
  assert.equal(discordCalls.length, 3);
});

test('a full counter store fails closed', async () => {
  const e = env({ RATE: { get: async () => null, put: async () => { throw new Error('KV put() limit exceeded'); } } });
  const { response, body } = await run(post(), e);
  assert.equal(response.status, 503);
  assert.equal(body.error, 'busy');
  assert.equal(discordCalls.length, 0);
});

test('a missing counter store is a 503, never "no limit"', async () => {
  const { response, body } = await run(post(), { DISCORD_WEBHOOK_URL: SECRET_URL });
  assert.equal(response.status, 503);
  assert.equal(body.error, 'not_configured');
  assert.equal(discordCalls.length, 0);
});

test('size caps', async () => {
  const big = new Blob([new Uint8Array(LIMITS.maxFileBytes + 1)]);
  assert.equal((await run(post({ files: [[REPORT_TXT, new Blob(['x'])], ['design.dsm', big]] }))).response.status, 413);

  // The request as a whole: only the header matters, the body is not read.
  const request = new Request('https://relay.example/', {
    method: 'POST', body: 'x', headers: { ...programHeaders(), 'content-type': 'multipart/form-data; boundary=a', 'content-length': String(LIMITS.maxRequestBytes + 1) },
  });
  const response = await worker.fetch(request, env());
  assert.equal(response.status, 413);
  assert.equal(discordCalls.length, 0);
});

test('only the attachment types the program sends', async () => {
  const bad = [
    ['evil.exe', new Blob(['MZ'])],
    ['page.html', new Blob(['<html>'])],
    ['../up.txt', new Blob(['x'])],
    ['fake.png', new Blob(['not a png'])],
    ['noext', new Blob(['x'])],
    ['other.txt', new Blob(['x'])],          // a text file that is not the report text (the program sends exactly one)
  ];
  for (const file of bad) assert.equal((await run(post({ files: [[REPORT_TXT, new Blob(['x'])], file] }))).response.status, 415, file[0]);
  assert.equal(discordCalls.length, 0);
  const ok = await run(post({ files: [[REPORT_TXT, new Blob(['x'])], ['design.dsm', new Blob(['PK'])], ['screenshot.jpg', new Blob([new Uint8Array([0xff, 0xd8, 0xff, 0xe0])])]] }));
  assert.equal(ok.response.status, 200);
});

test('too many files, duplicates, unknown form parts', async () => {
  const many = Array.from({ length: LIMITS.maxFiles + 1 }, (_, i) => [`f${i}.dsm`, new Blob(['x'])]);
  assert.equal((await run(post({ files: many }))).response.status, 400);
  assert.equal((await run(post({ files: [['a.dsm', new Blob(['x'])], ['A.dsm', new Blob(['y'])]] }))).response.status, 400);

  const form = new FormData();
  form.append('payload_json', JSON.stringify(payload()));
  form.append('content', 'extra');
  const request = await withLength(new Request('https://relay.example/', { method: 'POST', body: form, headers: programHeaders() }));
  assert.equal((await worker.fetch(request, env())).status, 400);
});

test('bad JSON and embeds are refused with plain messages', async () => {
  assert.equal((await run(post({ json: '{not json' }))).response.status, 400);
  assert.equal((await run(post({ json: { embeds: [] } }))).response.status, 400);
  const two = payload();
  two.embeds.push(two.embeds[0]);
  assert.equal((await run(post({ json: two }))).response.status, 400);
  const { body } = await run(post({ json: '{not json' }));
  assert.match(body.message, /report/i);
});

test('a report that is not in the shape the program sends is refused', async () => {
  const cases = {
    'title without a kind': (e) => { e.title = 'hello there'; },
    'color of another kind': (e) => { e.color = 0x2e86de; },          // Feature color on a Bug title
    'no description': (e) => { e.description = ''; },
    'footer not a report footer': (e) => { e.footer = { text: 'buy my stuff' }; },
    'no From field': (e) => { e.fields = e.fields.filter((f) => f.name !== 'From'); },
    'no Version field': (e) => { e.fields = e.fields.filter((f) => f.name !== 'Version'); },
    'a bug without Windows': (e) => { e.fields = e.fields.filter((f) => f.name !== 'Windows'); },
    'no timestamp': (e) => { delete e.timestamp; },
    'a report from the far past': (e) => { e.timestamp = '2020-01-01T00:00:00Z'; },
    'a report from the future': (e) => { e.timestamp = new Date(Date.now() + 5 * 86_400_000).toISOString().replace(/\.\d+Z$/, 'Z'); },
    'too many links': (e) => { e.description = 'see https://a.example https://b.example https://c.example https://d.example'; },
  };
  for (const [name, change] of Object.entries(cases)) {
    const { response, body } = await run(post({ json: withEmbed(change) }));
    assert.equal(response.status, 400, name);
    assert.equal(body.error, 'bad_payload', name);
  }
  // a bug or crash report has its text attachment; the name must say the same kind as the title
  assert.equal((await run(post({ files: [['screenshot.png', new Blob([PNG])]] }))).response.status, 400);
  assert.equal((await run(post({ files: [['DeSignMage-Crash-20261012-101500-abc123.txt', new Blob(['x'])]] }))).response.status, 400);
  assert.equal(discordCalls.length, 0);
});

test('a feature request needs neither a text file nor a Windows field', async () => {
  const json = withEmbed((e) => {
    e.title = 'Feature: add a polygon tool';
    e.color = 0x2e86de;
    e.fields = e.fields.filter((f) => f.name !== 'Windows');
    delete e.image;
  });
  assert.equal((await run(post({ json, files: [] }))).response.status, 200);
});

test('long text is trimmed to Discord limits', async () => {
  const json = payload();
  json.embeds[0].title = 'Bug: ' + 'T'.repeat(1000);
  json.embeds[0].description = 'D'.repeat(9000);
  json.embeds[0].fields.push(...Array.from({ length: 40 }, () => ({ name: 'n', value: 'v'.repeat(5000) })));
  await run(post({ json }));
  const e = discordCalls[0].payload.embeds[0];
  assert.ok(e.title.length <= 256 && e.description.length <= 4096 && e.fields.length === 25 && e.fields.every((f) => f.value.length <= 1024));
});

test('forum threads come from the relay setting, not the sender', async () => {
  const json = payload({ thread_name: 'chosen by the sender' });
  await run(post({ json }), env());
  assert.equal(discordCalls[0].payload.thread_name, undefined);
  await run(post({ json }), env({ FORUM_THREADS: 'true' }));
  assert.equal(discordCalls[1].payload.thread_name, 'Bug: it broke');
});

test('Discord problems become plain, retryable answers', async () => {
  discordStatus = 429;
  let r = await run(post());
  assert.equal(r.response.status, 429);
  assert.equal(r.body.retry_after, 7);
  discordStatus = 404;   // webhook deleted
  r = await run(post());
  assert.equal(r.response.status, 502);
  discordStatus = 413;
  r = await run(post());
  assert.equal(r.response.status, 413);
  globalThis.fetch = async () => { throw new TypeError('connect to ' + SECRET_URL + ' failed'); };
  r = await run(post());
  assert.equal(r.response.status, 502);
  assert.ok(!JSON.stringify(r.body).includes('SECRET'));
});

test('GET /health reports the switch and the limits, other routes do not exist', async () => {
  const on = await (await worker.fetch(new Request('https://relay.example/health'), env())).json();
  assert.deepEqual(on, { ok: true, enabled: true, configured: true, limits: { perComputerPerHour: 10, perDay: 200 } });
  const custom = await (await worker.fetch(new Request('https://relay.example/health'), env({ RATE_LIMIT_PER_HOUR: '3', DAILY_LIMIT: '50' }))).json();
  assert.deepEqual(custom.limits, { perComputerPerHour: 3, perDay: 50 });
  const off = await (await worker.fetch(new Request('https://relay.example/health'), env({ RELAY_ENABLED: 'false' }))).json();
  assert.equal(off.enabled, false);
  assert.equal((await worker.fetch(new Request('https://relay.example/admin'), env())).status, 404);
  assert.equal((await worker.fetch(new Request('https://relay.example/', { method: 'GET' }), env())).status, 404);
});
