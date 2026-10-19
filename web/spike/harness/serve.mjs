// Minimal static server for an AppBundle. Serves precompressed .br/.gz siblings when the client accepts them.
// Usage: node serve.mjs <dir> [port] [--coi]   (--coi adds COOP/COEP for threaded builds)
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.wasm': 'application/wasm',
  '.json': 'application/json', '.pck': 'application/octet-stream', '.svg': 'image/svg+xml', '.png': 'image/png',
  '.ico': 'image/x-icon', '.dat': 'application/octet-stream', '.css': 'text/css' };

export function serve(root, port = 0, coi = false) {
  root = path.resolve(root);
  const server = http.createServer((req, res) => {
    let p = decodeURIComponent(new URL(req.url, 'http://x').pathname);
    if (p.endsWith('/')) p += 'index.html';
    const file = path.join(root, p);
    if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404); res.end(); return; }
    const headers = { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-cache' };
    if (coi) { headers['Cross-Origin-Opener-Policy'] = 'same-origin'; headers['Cross-Origin-Embedder-Policy'] = 'require-corp'; }
    const ae = req.headers['accept-encoding'] || '';
    let send = file;
    if (/\bbr\b/.test(ae) && fs.existsSync(file + '.br')) { send = file + '.br'; headers['Content-Encoding'] = 'br'; }
    else if (/\bgzip\b/.test(ae) && fs.existsSync(file + '.gz')) { send = file + '.gz'; headers['Content-Encoding'] = 'gzip'; }
    headers['Content-Length'] = fs.statSync(send).size;
    res.writeHead(200, headers);
    fs.createReadStream(send).pipe(res);
  });
  return new Promise(r => server.listen(port, '127.0.0.1', () => r(server)));
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(new URL(import.meta.url).pathname.replace(/^\/(\w:)/, '$1'))) {
  const [dir, port] = process.argv.slice(2);
  const s = await serve(dir, Number(port || 8080), process.argv.includes('--coi'));
  console.log(`serving ${dir} on http://127.0.0.1:${s.address().port}/`);
}
