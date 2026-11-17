// Tests for the report server: node --test tools/web/report-server/test   (or `npm test` in tools/web/report-server)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createApp, validateReport, RateLimiter, FORMAT, LIMITS } from '../server.mjs';

const JPEG = Buffer.from([0xff, 0xd8, 0xff, 0xe0, 0, 0x10, 0x4a, 0x46, 0x49, 0x46, 0, 1, 0xff, 0xd9]);
const PNG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0]);
const base = (o = {}) => ({ format: FORMAT, text: 'The victory text overlaps.', client: { platform: 'web', commit: 'abc1234' }, game: { scene: 'MainMenu' }, ...o });

async function withServer(cfg, fn) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'h5rep-'));
  const app = createApp({ dataDir: dir, rateMax: 100, rateWindowS: 600, ...cfg });
  const server = http.createServer(app.handler);
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const url = `http://127.0.0.1:${server.address().port}`;
  try { await fn({ url, dir, app }); } finally { server.close(); fs.rmSync(dir, { recursive: true, force: true }); }
}
const post = (url, body, headers = {}) => fetch(url + '/api/report', { method: 'POST', headers: { 'Content-Type': 'application/json', ...headers },
  body: typeof body === 'string' ? body : JSON.stringify(body) });

test('validation accepts a minimal report and trims the text', () => {
  const v = validateReport({ format: FORMAT, text: '  hello  ' });
  assert.equal(v.meta.text, 'hello');
  assert.equal(v.screenshot, undefined);
});

test('validation rejects bad payloads', () => {
  const bad = [
    [null, 400], [[], 400], [{ text: 'x' }, 400], [base({ format: 'other/1' }), 400],
    [base({ text: '   ' }), 400], [base({ text: 5 }), 400], [base({ text: 'x'.repeat(LIMITS.text + 1) }), 413],
    [base({ contact: 'x'.repeat(201) }), 400], [base({ client: 'web' }), 400], [base({ game: [1] }), 400],
    [base({ game: { big: 'x'.repeat(LIMITS.object) } }), 413], [base({ log: 3 }), 400],
    [base({ screenshot: 'http://example.com/x.jpg' }), 400],
    [base({ screenshot: 'data:image/jpeg;base64,' + PNG.toString('base64') }), 400],   // type does not match the bytes
    [base({ screenshot: 'data:image/jpeg;base64,@@@@' }), 400],
    [base({ screenshot: 'data:image/jpeg;base64,' + Buffer.alloc(LIMITS.screenshot + 3, 0xff).toString('base64') }), 413],
    [base({ save: 'x' }), 400], [base({ save: { name: 'a.sav' } }), 400], [base({ save: { name: 'a.sav', data: '' } }), 400],
  ];
  for (const [p, status] of bad) assert.throws(() => validateReport(p), (e) => e.status === status, JSON.stringify(p)?.slice(0, 80));
});

test('a full report is stored as a folder and listed', () => withServer({}, async ({ url, dir }) => {
  const save = Buffer.from('H5SAVE-bytes');
  const r = await post(url, base({ contact: 'Rowan', log: '[H5] web: scene MainMenu\n', screenshot: 'data:image/jpeg;base64,' + JPEG.toString('base64'),
    save: { name: 'profiles/Player/saves/autosave.sav', data: save.toString('base64') }, extra: 'ignored' }), { 'X-Real-IP': '10.0.0.5' });
  assert.equal(r.status, 201);
  const { id } = await r.json();
  assert.match(id, /^\d{8}-\d{6}-[0-9a-f]{6}$/);
  const folder = path.join(dir, id);
  assert.deepEqual(fs.readdirSync(folder).sort(), ['log.txt', 'report.json', 'save.sav', 'screenshot.jpg']);
  assert.deepEqual(fs.readFileSync(path.join(folder, 'screenshot.jpg')), JPEG);
  assert.deepEqual(fs.readFileSync(path.join(folder, 'save.sav')), save);
  const stored = JSON.parse(fs.readFileSync(path.join(folder, 'report.json'), 'utf8'));
  assert.equal(stored.text, 'The victory text overlaps.');
  assert.equal(stored.received.address, '10.0.0.5');
  assert.equal(stored.save.name, 'profiles/Player/saves/autosave.sav');
  assert.equal(stored.extra, undefined);

  const list = await (await fetch(url + '/api/reports')).json();
  assert.equal(list.count, 1);
  assert.equal(list.reports[0].id, id);
  assert.equal(list.reports[0].screen, 'MainMenu');
  assert.equal(list.reports[0].screenshot, true);
  assert.deepEqual(await (await fetch(`${url}/api/reports/${id}`)).json(), stored);
  const shot = await fetch(`${url}/api/reports/${id}/screenshot`);
  assert.equal(shot.headers.get('content-type'), 'image/jpeg');
  assert.deepEqual(Buffer.from(await shot.arrayBuffer()), JPEG);
  const dl = await fetch(`${url}/api/reports/${id}/save`);
  assert.match(dl.headers.get('content-disposition'), new RegExp(`attachment; filename="${id}-autosave.sav"`));
  assert.equal(await (await fetch(`${url}/api/reports/${id}/log`)).text(), '[H5] web: scene MainMenu\n');
  const html = await (await fetch(url + '/api/reports/view')).text();
  assert.match(html, /The victory text overlaps\./);
  assert.match(await (await fetch(`${url}/api/reports/${id}/view`)).text(), /Download save/);
}));

test('HTML views escape the report text', () => withServer({}, async ({ url }) => {
  const { id } = await (await post(url, base({ text: '<script>alert(1)</script>', game: { scene: '<b>x</b>' } }))).json();
  for (const p of ['/api/reports/view', `/api/reports/${id}/view`]) {
    const r = await fetch(url + p);
    const html = await r.text();
    assert.ok(!html.includes('<script>alert'), p);
    assert.ok(html.includes('&lt;script&gt;'), p);
    assert.match(r.headers.get('content-security-policy'), /default-src 'none'/);
  }
}));

test('bad requests get the right status and store nothing', () => withServer({}, async ({ url, dir }) => {
  assert.equal((await post(url, '{not json')).status, 400);
  assert.equal((await post(url, base(), { 'Content-Type': 'text/plain' })).status, 415);
  assert.equal((await fetch(url + '/api/report')).status, 405);
  assert.equal((await post(url, { format: FORMAT })).status, 400);
  const big = await post(url, JSON.stringify(base({ log: 'x'.repeat(LIMITS.body) })));
  assert.equal(big.status, 413);
  assert.equal((await fetch(url + '/api/reports/../../etc/passwd')).status, 404);
  assert.equal((await fetch(url + '/api/reports/20260101-000000-zzzzzz')).status, 404);
  assert.equal((await fetch(url + '/api/reports', { method: 'DELETE' })).status, 405);
  assert.deepEqual(fs.readdirSync(dir), []);
}));

test('rate limit per address', () => withServer({ rateMax: 2 }, async ({ url }) => {
  assert.equal((await post(url, base(), { 'X-Real-IP': '1.1.1.1' })).status, 201);
  assert.equal((await post(url, base(), { 'X-Real-IP': '1.1.1.1' })).status, 201);
  const r = await post(url, base(), { 'X-Real-IP': '1.1.1.1' });
  assert.equal(r.status, 429);
  assert.ok(Number(r.headers.get('retry-after')) > 0);
  assert.equal((await post(url, base(), { 'X-Real-IP': '2.2.2.2' })).status, 201);
}));

test('rate limiter window expires', () => {
  let t = 0;
  const l = new RateLimiter(1, 1000, () => t);
  assert.equal(l.take('a'), 0);
  assert.equal(l.take('a'), 1);
  t = 1001;
  assert.equal(l.take('a'), 0);
});

test('storage cap and read token', () => withServer({ maxTotalMB: 0, readToken: 's3cret' }, async ({ url, app }) => {
  assert.equal((await post(url, base())).status, 201);         // empty directory: still under the cap
  assert.ok(app.store.bytes > 0);
  assert.equal((await post(url, base())).status, 507);
  assert.equal((await fetch(url + '/api/reports')).status, 401);
  assert.equal((await fetch(url + '/api/reports?token=wrong')).status, 401);
  const ok = await fetch(url + '/api/reports?token=s3cret');
  assert.equal(ok.status, 200);
  const { reports } = await ok.json();
  assert.equal((await fetch(`${url}/api/reports/${reports[0].id}`, { headers: { Authorization: 'Bearer s3cret' } })).status, 200);
  assert.match(await (await fetch(url + '/api/reports/view?token=s3cret')).text(), /view\?token=s3cret/);
}));

test('a restarted server sees earlier reports and drops half-written ones', () => withServer({}, async ({ url, dir }) => {
  await post(url, base());
  fs.mkdirSync(path.join(dir, '.tmp-20260101-000000-abcdef'));
  const again = createApp({ dataDir: dir });
  assert.equal(again.store.ids().length, 1);
  assert.ok(!fs.existsSync(path.join(dir, '.tmp-20260101-000000-abcdef')));
  assert.ok(again.store.bytes > 0);
}));
