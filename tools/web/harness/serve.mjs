// Minimal static server for the web build (no dependencies). Serves precompressed .br/.gz siblings when the client
// accepts them, with the MIME types the build needs. COOP/COEP are NOT required (the build is single-threaded);
// --coi adds them for experiments with a threaded build.
// Usage: node serve.mjs <dir> [port] [--host 0.0.0.0] [--coi]
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript',
  '.wasm': 'application/wasm', '.json': 'application/json', '.pck': 'application/octet-stream',
  '.dat': 'application/octet-stream', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon', '.css': 'text/css' };

export function serve(root, port = 0, { coi = false, host = '127.0.0.1' } = {}) {
  root = path.resolve(root);
  const server = http.createServer((req, res) => {
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
  });
  return new Promise((r) => server.listen(port, host, () => r(server)));
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const a = process.argv.slice(2);
  const hi = a.indexOf('--host');
  const host = hi >= 0 ? a[hi + 1] : '127.0.0.1';
  const pos = a.filter((x, i) => !x.startsWith('--') && (hi < 0 || i !== hi + 1));
  const s = await serve(pos[0] || '.', Number(pos[1] || 8080), { coi: a.includes('--coi'), host });
  console.log(`serving ${path.resolve(pos[0] || '.')} on http://${host}:${s.address().port}/`);
}
