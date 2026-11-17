// H5Reborn bug-report receiver (no dependencies, Node >= 20). Players' reports from the browser build (and later
// the desktop build) arrive as one JSON POST; each is stored as a folder under the data directory:
//   <data>/<id>/report.json   the report (text, client, game state) + what the server saw (time, address, sizes)
//   <data>/<id>/screenshot.jpg|png, <data>/<id>/save.sav, <data>/<id>/log.txt   when sent
// Read-only views for the owner: GET /api/reports (JSON list), /api/reports/view (HTML list), /api/reports/<id>[/view].
// Access control is the web server's job (the same guard as the site, docs/WEB_BUILD.md "Bug reports"); an optional
// read token (H5R_REPORTS_READ_TOKEN) additionally locks the read views.
// Payload format and deployment: docs/WEB_BUILD.md, "Bug reports". Usage: node server.mjs   (configured by H5R_REPORTS_* environment variables, see configFromEnv)
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

export const FORMAT = 'h5reborn-report/1';
const MB = 1024 * 1024;
export const LIMITS = {
  body: 10 * MB,          // the whole request
  text: 20000,            // characters of the description
  contact: 200,
  object: 16 * 1024,      // client / game objects, as JSON
  log: 512 * 1024,        // characters of the console log tail
  screenshot: 4 * MB,     // decoded bytes
  save: 6 * MB,           // decoded bytes
};

export function configFromEnv(env = process.env) {
  const num = (k, d) => (env[k] !== undefined && env[k] !== '' && Number.isFinite(Number(env[k])) ? Number(env[k]) : d);
  return {
    dataDir: env.H5R_REPORTS_DIR || path.resolve('data'),
    host: env.H5R_REPORTS_HOST || '0.0.0.0',
    port: num('H5R_REPORTS_PORT', 8170),
    rateMax: num('H5R_REPORTS_RATE_MAX', 6),             // reports per address ...
    rateWindowS: num('H5R_REPORTS_RATE_WINDOW', 600),    // ... per this many seconds
    maxTotalMB: num('H5R_REPORTS_MAX_TOTAL_MB', 2048),   // stop accepting when the data directory is this big
    readToken: env.H5R_REPORTS_READ_TOKEN || '',
    trustProxy: env.H5R_REPORTS_TRUST_PROXY !== '0',     // client address from X-Real-IP / X-Forwarded-For
  };
}

class HttpError extends Error {
  constructor(status, message, headers = {}) { super(message); this.status = status; this.headers = headers; }
}

// ------------------------------------------------------------------------------------------------ validation
const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const B64 = /^[A-Za-z0-9+/]*={0,2}$/;

function decodeB64(s, max, what) {
  if (typeof s !== 'string' || s.length % 4 !== 0 || !B64.test(s)) throw new HttpError(400, `${what}: not base64`);
  if ((s.length / 4) * 3 > max + 2) throw new HttpError(413, `${what}: larger than ${max} bytes`);
  const buf = Buffer.from(s, 'base64');
  if (buf.length > max) throw new HttpError(413, `${what}: larger than ${max} bytes`);
  if (!buf.length) throw new HttpError(400, `${what}: empty`);
  return buf;
}

function smallObject(v, what) {
  if (v === undefined || v === null) return undefined;
  if (!isObj(v)) throw new HttpError(400, `${what}: expected an object`);
  if (JSON.stringify(v).length > LIMITS.object) throw new HttpError(413, `${what}: larger than ${LIMITS.object} bytes`);
  return v;
}

/** Checks a parsed payload; returns { meta (goes to report.json), screenshot?: {ext, data}, save?: {name, data}, log? }. */
export function validateReport(p) {
  if (!isObj(p)) throw new HttpError(400, 'expected a JSON object');
  if (p.format !== FORMAT) throw new HttpError(400, `format: expected "${FORMAT}"`);
  if (typeof p.text !== 'string' || !p.text.trim()) throw new HttpError(400, 'text: required');
  if (p.text.length > LIMITS.text) throw new HttpError(413, `text: longer than ${LIMITS.text} characters`);
  if (p.contact !== undefined && p.contact !== null && (typeof p.contact !== 'string' || p.contact.length > LIMITS.contact))
    throw new HttpError(400, `contact: expected a string of at most ${LIMITS.contact} characters`);
  const out = {
    meta: { format: FORMAT, text: p.text.trim(), contact: p.contact ? String(p.contact).trim() : undefined,
      client: smallObject(p.client, 'client'), game: smallObject(p.game, 'game') },
  };
  if (p.log !== undefined && p.log !== null) {
    if (typeof p.log !== 'string') throw new HttpError(400, 'log: expected a string');
    if (p.log.length > LIMITS.log) throw new HttpError(413, `log: longer than ${LIMITS.log} characters`);
    if (p.log.length) out.log = p.log;
  }
  if (p.screenshot !== undefined && p.screenshot !== null) {
    const m = typeof p.screenshot === 'string' && /^data:image\/(jpeg|png);base64,/.exec(p.screenshot);
    if (!m) throw new HttpError(400, 'screenshot: expected a data:image/jpeg or data:image/png base64 URL');
    const data = decodeB64(p.screenshot.slice(m[0].length), LIMITS.screenshot, 'screenshot');
    const jpeg = data[0] === 0xff && data[1] === 0xd8 && data[2] === 0xff;
    const png = data.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]));
    if (!(m[1] === 'jpeg' ? jpeg : png)) throw new HttpError(400, `screenshot: not a ${m[1]} image`);
    out.screenshot = { ext: m[1] === 'jpeg' ? 'jpg' : 'png', data };
  }
  if (p.save !== undefined && p.save !== null) {
    if (!isObj(p.save) || typeof p.save.name !== 'string' || p.save.name.length > 300) throw new HttpError(400, 'save: expected { name, data }');
    out.save = { name: p.save.name, data: decodeB64(p.save.data, LIMITS.save, 'save') };
  }
  return out;
}

// ------------------------------------------------------------------------------------------------ storage
const ID = /^\d{8}-\d{6}-[0-9a-f]{6}$/;
export const isId = (s) => typeof s === 'string' && ID.test(s);

function newId(now) {
  const t = now.toISOString(); // 2026-11-17T15:30:12.345Z
  return `${t.slice(0, 4)}${t.slice(5, 7)}${t.slice(8, 10)}-${t.slice(11, 13)}${t.slice(14, 16)}${t.slice(17, 19)}-${crypto.randomBytes(3).toString('hex')}`;
}

function dirSize(dir) {
  let n = 0;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    n += e.isDirectory() ? dirSize(p) : fs.statSync(p).size;
  }
  return n;
}

export class ReportStore {
  constructor(dir) {
    this.dir = path.resolve(dir);
    fs.mkdirSync(this.dir, { recursive: true });
    // Leftovers of an interrupted write.
    for (const e of fs.readdirSync(this.dir)) if (e.startsWith('.tmp-')) fs.rmSync(path.join(this.dir, e), { recursive: true, force: true });
    this.bytes = dirSize(this.dir);
  }

  /** Writes a validated report (into a temporary folder, then renamed); returns its id. */
  save(v, received) {
    let id;
    do id = newId(received.at); while (fs.existsSync(path.join(this.dir, id)));
    const tmp = path.join(this.dir, `.tmp-${id}`);
    fs.mkdirSync(tmp);
    const files = {};
    const write = (name, data) => { fs.writeFileSync(path.join(tmp, name), data); files[name] = data.length; };
    if (v.screenshot) write(`screenshot.${v.screenshot.ext}`, v.screenshot.data);
    if (v.save) write('save.sav', v.save.data);
    if (v.log) write('log.txt', Buffer.from(v.log, 'utf8'));
    const report = { id, ...v.meta, save: v.save ? { name: v.save.name, file: 'save.sav' } : undefined,
      received: { at: received.at.toISOString(), address: received.address, userAgent: received.userAgent, bytes: received.bytes }, files };
    write('report.json', Buffer.from(JSON.stringify(report, null, 2) + '\n', 'utf8'));
    fs.renameSync(tmp, path.join(this.dir, id));
    this.bytes += Object.values(files).reduce((a, b) => a + b, 0);
    return id;
  }

  ids() { return fs.readdirSync(this.dir).filter(isId).sort().reverse(); }

  get(id) {
    if (!isId(id)) return null;
    try { return JSON.parse(fs.readFileSync(path.join(this.dir, id, 'report.json'), 'utf8')); } catch { return null; }
  }

  /** A file of a report ('screenshot', 'save', 'log'): { path, name } or null. */
  file(id, kind) {
    const r = this.get(id);
    if (!r) return null;
    const name = kind === 'screenshot' ? Object.keys(r.files || {}).find((f) => f.startsWith('screenshot.'))
      : kind === 'save' ? (r.files?.['save.sav'] ? 'save.sav' : null)
        : kind === 'log' ? (r.files?.['log.txt'] ? 'log.txt' : null) : null;
    return name ? { path: path.join(this.dir, id, name), name } : null;
  }

  summary(r) {
    return { id: r.id, at: r.received?.at, text: r.text.length > 200 ? r.text.slice(0, 200) + '…' : r.text, contact: r.contact,
      commit: r.client?.commit, platform: r.client?.platform, screen: r.game?.scene, map: r.game?.map,
      screenshot: !!this.file(r.id, 'screenshot'), save: r.save?.name, log: !!r.files?.['log.txt'] };
  }

  list(limit = 200) {
    const out = [];
    for (const id of this.ids()) {
      if (out.length >= limit) break;
      const r = this.get(id);
      if (r) out.push(this.summary(r));
    }
    return out;
  }
}

// ------------------------------------------------------------------------------------------------ rate limit
export class RateLimiter {
  constructor(max, windowMs, now = () => Date.now()) { this.max = max; this.windowMs = windowMs; this.now = now; this.hits = new Map(); }
  /** Records a hit; returns 0 when allowed, else the seconds until the next one is. */
  take(key) {
    const t = this.now(), since = t - this.windowMs;
    const list = (this.hits.get(key) || []).filter((x) => x > since);
    if (this.hits.size > 10000) for (const [k, v] of this.hits) if (!v.some((x) => x > since)) this.hits.delete(k);
    if (list.length >= this.max) { this.hits.set(key, list); return Math.max(1, Math.ceil((list[0] + this.windowMs - t) / 1000)); }
    list.push(t);
    this.hits.set(key, list);
    return 0;
  }
}

// ------------------------------------------------------------------------------------------------ HTML views
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const PAGE_CSS = 'body{margin:0;padding:16px;background:#120c07;color:#2b1d0e;font:15px/1.45 Georgia,serif}' +
  'main{max-width:1100px;margin:0 auto;background:#e8d6a8;border:3px solid #8a6a2c;border-radius:6px;padding:1rem 1.4rem}' +
  'h1{margin:.2rem 0 .8rem;color:#5a1d0c;font-size:1.6rem}table{border-collapse:collapse;width:100%}' +
  'td,th{border-bottom:1px solid #b8913f;padding:.35rem .4rem;vertical-align:top;text-align:left}th{color:#5a1d0c}' +
  'pre{white-space:pre-wrap;word-break:break-word;background:rgba(90,60,20,.12);padding:.5rem;border-radius:4px;font:13px Consolas,monospace}' +
  'a{color:#7a2a14}img{max-width:100%;border:2px solid #8a6a2c}.thumb{max-width:160px}.dim{color:#6b4a1e;font-size:.9em}' +
  '@media (max-width:700px){td,th{display:block;border:0}tr{display:block;border-bottom:1px solid #b8913f;padding:.4rem 0}}';

function page(title, body) {
  return `<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">` +
    `<title>${esc(title)}</title><style>${PAGE_CSS}</style></head><body><main>${body}</main></body></html>`;
}

function listPage(store, q) {
  const rows = store.list(500).map((s) => `<tr><td><a href="${esc(s.id)}/view${q}">${esc(s.at?.replace('T', ' ').slice(0, 19))}</a>` +
    `<div class="dim">${esc(s.platform || '')} ${esc(s.commit || '')}</div></td>` +
    `<td>${esc(s.text)}${s.contact ? `<div class="dim">— ${esc(s.contact)}</div>` : ''}</td>` +
    `<td>${esc(s.screen || '')}${s.map ? `<div class="dim">${esc(s.map)}</div>` : ''}</td>` +
    `<td>${s.screenshot ? `<a href="${esc(s.id)}/view${q}"><img class="thumb" src="${esc(s.id)}/screenshot${q}" alt="screenshot" loading="lazy"></a>` : ''}` +
    `${s.save ? '<div class="dim">save</div>' : ''}${s.log ? '<div class="dim">log</div>' : ''}</td></tr>`).join('');
  return page('H5Reborn bug reports', `<h1>Bug reports (${store.ids().length})</h1>` +
    (rows ? `<table><tr><th>When</th><th>Report</th><th>Screen / map</th><th></th></tr>${rows}</table>` : '<p>No reports yet.</p>'));
}

function detailPage(store, r, q) {
  const shot = store.file(r.id, 'screenshot');
  const log = store.file(r.id, 'log');
  const json = { ...r, text: undefined };
  return page(`Report ${r.id}`, `<p><a href="../view${q}">← all reports</a></p><h1>Report ${esc(r.id)}</h1>` +
    `<p class="dim">${esc(r.received?.at)} · ${esc(r.received?.address)}${r.contact ? ` · ${esc(r.contact)}` : ''}</p>` +
    `<pre>${esc(r.text)}</pre>` +
    (shot ? `<p><a href="screenshot${q}"><img src="screenshot${q}" alt="screenshot"></a></p>` : '') +
    `<p>${r.save ? `<a href="save${q}">Download save</a> <span class="dim">(${esc(r.save.name)})</span> · ` : ''}` +
    `${log ? `<a href="log${q}">Console log</a> · ` : ''}<a href="../${esc(r.id)}${q}">report.json</a></p>` +
    `<pre>${esc(JSON.stringify(json, null, 2))}</pre>`);
}

// ------------------------------------------------------------------------------------------------ HTTP
function readBody(req, max) {
  return new Promise((resolve, reject) => {
    const len = Number(req.headers['content-length']);
    if (Number.isFinite(len) && len > max) { reject(new HttpError(413, `request larger than ${max} bytes`)); req.resume(); return; }
    const chunks = [];
    let n = 0, failed = false;
    req.on('data', (c) => {
      if (failed) return;
      n += c.length;
      if (n > max) { failed = true; reject(new HttpError(413, `request larger than ${max} bytes`)); return; }
      chunks.push(c);
    });
    req.on('end', () => { if (!failed) resolve(Buffer.concat(chunks)); });
    req.on('error', (e) => { if (!failed) { failed = true; reject(e); } });
  });
}

function clientAddress(req, trustProxy) {
  if (trustProxy) {
    const real = req.headers['x-real-ip'];
    if (typeof real === 'string' && real) return real.trim();
    const fwd = req.headers['x-forwarded-for'];
    if (typeof fwd === 'string' && fwd) return fwd.split(',')[0].trim();
  }
  return req.socket.remoteAddress || 'unknown';
}

const SECURITY = { 'X-Content-Type-Options': 'nosniff', 'Cache-Control': 'no-store', 'Referrer-Policy': 'no-referrer' };

function send(res, status, body, type, extra = {}) {
  const buf = Buffer.isBuffer(body) ? body : Buffer.from(body, 'utf8');
  res.writeHead(status, { ...SECURITY, 'Content-Type': type, 'Content-Length': buf.length, ...extra });
  res.end(buf);
}
const sendJson = (res, status, obj, extra) => send(res, status, JSON.stringify(obj), 'application/json; charset=utf-8', extra);
const HTML_CSP = { 'Content-Security-Policy': "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'" };

function tokenOk(req, url, token) {
  if (!token) return true;
  const given = url.searchParams.get('token') || (req.headers.authorization || '').replace(/^Bearer\s+/i, '');
  const a = Buffer.from(String(given)), b = Buffer.from(token);
  return a.length === b.length && crypto.timingSafeEqual(a, b);
}

/** The request handler; exported with its store for tests. */
export function createApp(config) {
  const cfg = { ...configFromEnv({}), ...config };
  const store = new ReportStore(cfg.dataDir);
  const limiter = new RateLimiter(cfg.rateMax, cfg.rateWindowS * 1000, cfg.now);
  const MIME = { jpg: 'image/jpeg', png: 'image/png' };

  async function handle(req, res) {
    const url = new URL(req.url, 'http://x');
    const p = url.pathname.replace(/\/+$/, '') || '/';
    if (p === '/api/health') return sendJson(res, 200, { ok: true });

    if (p === '/api/report') {
      if (req.method !== 'POST') throw new HttpError(405, 'use POST', { Allow: 'POST' });
      if (!/^application\/json\b/i.test(req.headers['content-type'] || '')) throw new HttpError(415, 'expected application/json');
      const address = clientAddress(req, cfg.trustProxy);
      const wait = limiter.take(address);
      if (wait) throw new HttpError(429, `too many reports; try again in ${wait} s`, { 'Retry-After': String(wait) });
      if (store.bytes > cfg.maxTotalMB * MB) throw new HttpError(507, 'report storage is full');
      const body = await readBody(req, LIMITS.body);
      let parsed;
      try { parsed = JSON.parse(body.toString('utf8')); } catch { throw new HttpError(400, 'invalid JSON'); }
      const v = validateReport(parsed);
      const id = store.save(v, { at: cfg.now ? new Date(cfg.now()) : new Date(), address, userAgent: String(req.headers['user-agent'] || '').slice(0, 400), bytes: body.length });
      console.log(`report ${id} from ${address}: ${v.meta.text.slice(0, 80).replace(/\s+/g, ' ')}`);
      return sendJson(res, 201, { ok: true, id });
    }

    if (p === '/api/reports' || p.startsWith('/api/reports/')) {
      if (req.method !== 'GET' && req.method !== 'HEAD') throw new HttpError(405, 'read-only', { Allow: 'GET' });
      if (!tokenOk(req, url, cfg.readToken)) throw new HttpError(401, 'read token required');
      const q = url.searchParams.get('token') ? `?token=${encodeURIComponent(url.searchParams.get('token'))}` : '';
      const parts = p.split('/').slice(3); // after /api/reports
      if (!parts.length) return sendJson(res, 200, { count: store.ids().length, reports: store.list(Math.min(1000, Number(url.searchParams.get('limit')) || 200)) });
      if (parts.length === 1 && parts[0] === 'view') return send(res, 200, listPage(store, q), 'text/html; charset=utf-8', HTML_CSP);
      const r = store.get(parts[0]);
      if (!r || parts.length > 2) throw new HttpError(404, 'no such report');
      if (parts.length === 1) return sendJson(res, 200, r);
      if (parts[1] === 'view') return send(res, 200, detailPage(store, r, q), 'text/html; charset=utf-8', HTML_CSP);
      const f = store.file(r.id, parts[1]);
      if (!f) throw new HttpError(404, 'no such file');
      const data = fs.readFileSync(f.path);
      if (parts[1] === 'screenshot') return send(res, 200, data, MIME[f.name.split('.').pop()]);
      if (parts[1] === 'log') return send(res, 200, data, 'text/plain; charset=utf-8');
      const name = path.basename(r.save.name.replace(/\\/g, '/')).replace(/[^A-Za-z0-9._-]/g, '_') || 'save.sav';
      return send(res, 200, data, 'application/octet-stream', { 'Content-Disposition': `attachment; filename="${r.id}-${name}"` });
    }
    throw new HttpError(404, 'not found');
  }

  const handler = (req, res) => {
    handle(req, res).catch((e) => {
      const status = e instanceof HttpError ? e.status : 500;
      if (status === 500) console.error(e);
      if (!res.headersSent) sendJson(res, status, { ok: false, error: status === 500 ? 'internal error' : e.message }, e.headers || {});
      else res.destroy();
    });
  };
  return { handler, store, config: cfg };
}

export function start(config = configFromEnv()) {
  const app = createApp(config);
  const server = http.createServer(app.handler);
  server.requestTimeout = 60000;
  return new Promise((resolve) => server.listen(app.config.port, app.config.host, () => resolve({ server, ...app })));
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { server, store, config } = await start();
  const a = server.address();
  console.log(`h5reborn report server on ${a.address}:${a.port}, data ${store.dir} (${store.ids().length} reports, ${(store.bytes / MB).toFixed(1)} MB)` +
    `${config.readToken ? ', read token set' : ''}`);
  const stop = () => server.close(() => process.exit(0));
  process.on('SIGTERM', stop);
  process.on('SIGINT', stop);
}
