// Minimal static server for the web build (no dependencies). Serves precompressed .br/.gz siblings when the client
// accepts them, with the MIME types the build needs. COOP/COEP are NOT required (the build is single-threaded);
// --coi adds them for experiments with a threaded build.
// --tls serves https with a throw-away self-signed certificate (made with the openssl command line tool), and
// --proxy /prefix/=host:port forwards WebSocket upgrades under that path (prefix stripped) to host:port: the same as the
// LAN server's Caddy / nginx in front of an h5server (wss://, docs/WEB_BUILD.md, Multiplayer).
// Usage: node serve.mjs <dir> [port] [--host 0.0.0.0] [--coi] [--tls] [--proxy /h5host/=192.168.1.20:27615]
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

/** A self-signed certificate for 127.0.0.1 / localhost (openssl), valid for two days. */
export function selfSignedCert() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'h5tls-'));
  try {
    const key = path.join(dir, 'key.pem'), cert = path.join(dir, 'cert.pem'), conf = path.join(dir, 'req.cnf');
    // Own minimal config: a machine-wide OPENSSL_CONF may point at a missing file.
    fs.writeFileSync(conf, '[req]\ndistinguished_name = dn\n[dn]\n');
    execFileSync('openssl', ['req', '-config', conf, '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', cert, '-days', '2',
      '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1'], { stdio: 'ignore', env: { ...process.env, MSYS_NO_PATHCONV: '1' } });
    return { key: fs.readFileSync(key), cert: fs.readFileSync(cert) };
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
}

/** Forwards a WebSocket upgrade request to host:port with the path prefix removed (a reverse proxy's job). */
function proxyUpgrade(req, socket, head, prefix, target) {
  const [host, port] = [target.slice(0, target.lastIndexOf(':')), Number(target.slice(target.lastIndexOf(':') + 1))];
  const upstream = net.connect(port, host, () => {
    const p = '/' + req.url.slice(prefix.length);
    const lines = [`${req.method} ${p} HTTP/1.1`];
    for (let i = 0; i < req.rawHeaders.length; i += 2) lines.push(`${req.rawHeaders[i]}: ${req.rawHeaders[i + 1]}`);
    upstream.write(lines.join('\r\n') + '\r\n\r\n');
    if (head?.length) upstream.write(head);
    upstream.pipe(socket); socket.pipe(upstream);
  });
  const close = () => { upstream.destroy(); socket.destroy(); };
  upstream.on('error', close); socket.on('error', close); upstream.on('close', close); socket.on('close', close);
}

const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript',
  '.wasm': 'application/wasm', '.json': 'application/json', '.pck': 'application/octet-stream',
  '.dat': 'application/octet-stream', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon', '.css': 'text/css' };

export function serve(root, port = 0, { coi = false, host = '127.0.0.1', tls = null, proxy = {} } = {}) {
  root = path.resolve(root);
  const handler = (req, res) => {
    let p = decodeURIComponent(new URL(req.url, 'http://x').pathname);
    if (p.endsWith('/')) p += 'index.html';
    const file = path.join(root, p);
    if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404); res.end(); return; }
    const headers = { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-cache',
      'Vary': 'Accept-Encoding' };
    if (coi) { headers['Cross-Origin-Opener-Policy'] = 'same-origin'; headers['Cross-Origin-Embedder-Policy'] = 'require-corp'; }
    const ae = req.headers['accept-encoding'] || '';
    let send = file;
    if (/\bbr\b/.test(ae) && fs.existsSync(file + '.br')) { send = file + '.br'; headers['Content-Encoding'] = 'br'; }
    else if (/\bgzip\b/.test(ae) && fs.existsSync(file + '.gz')) { send = file + '.gz'; headers['Content-Encoding'] = 'gzip'; }
    headers['Content-Length'] = fs.statSync(send).size;
    res.writeHead(200, headers);
    if (req.method === 'HEAD') { res.end(); return; }
    fs.createReadStream(send).pipe(res);
  };
  const server = tls ? https.createServer(tls, handler) : http.createServer(handler);
  server.on('upgrade', (req, socket, head) => {
    const prefix = Object.keys(proxy).find((k) => req.url.startsWith(k));
    if (prefix) proxyUpgrade(req, socket, head, prefix, proxy[prefix]);
    else socket.destroy();
  });
  return new Promise((r) => server.listen(port, host, () => r(server)));
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const a = process.argv.slice(2);
  const valued = new Set(['--host', '--proxy']);
  const hi = a.indexOf('--host');
  const host = hi >= 0 ? a[hi + 1] : '127.0.0.1';
  const proxy = {};
  a.forEach((x, i) => { if (x === '--proxy') { const [k, v] = a[i + 1].split('='); proxy[k] = v; } });
  const pos = a.filter((x, i) => !x.startsWith('--') && !valued.has(a[i - 1]));
  const tls = a.includes('--tls') ? selfSignedCert() : null;
  const s = await serve(pos[0] || '.', Number(pos[1] || 8080), { coi: a.includes('--coi'), host, tls, proxy });
  console.log(`serving ${path.resolve(pos[0] || '.')} on ${tls ? 'https' : 'http'}://${host}:${s.address().port}/`);
  for (const [k, v] of Object.entries(proxy)) console.log(`  WebSocket ${k} -> ${v}`);
}
