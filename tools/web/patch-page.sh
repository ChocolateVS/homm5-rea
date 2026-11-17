#!/usr/bin/env bash
# Fast path for page-only changes (docs/WEB_BUILD.md, "Page-only changes"): updates the page files of an existing web
# build in place, without the 5-15 minute rebuild.
#   tools/web/patch-page.sh [--dist DIR] [--dry-run] [--force]
# Copies web/game/H5Reborn.web/wwwroot/* (index.html, h5*.js, h5shaders.json ...) into DIR (default web/dist) when they
# differ, rewrites their precompressed .br / .gz siblings (a server with `precompressed br gzip` would otherwise keep
# serving the old page), updates twodog.sizes.json if it lists a patched file, and records the patch in build-info.json
# ("page": commit, time, files). The engine, the .NET assemblies and godot.pck are never touched.
# Refuses (unless --force) when anything the page talks to changed since DIR was built: the web host and its
# [JSImport] bindings (web/game/src, web/game/H5Reborn.web/*.cs, emscripten/, the project files) or tools/web/build.sh,
# because the page and the compiled code must agree; game code changes (game/, src/) are reported as not deployed.
# Needs git and node (for brotli / gzip: node's zlib, no other tools).
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DIST="$REPO/web/dist"
DRY=false
FORCE=false
while [ $# -gt 0 ]; do
  case "$1" in
    --dist) DIST="$(cd "$2" && pwd)"; shift 2 ;;
    --dry-run) DRY=true; shift ;;
    --force) FORCE=true; shift ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
SRC="$REPO/web/game/H5Reborn.web/wwwroot"
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s\n' "$1"; fi; }

for f in index.html godot.wasm godot.js build-info.json; do
  [ -f "$DIST/$f" ] || { echo "error: $DIST is not a web build (no $f); run tools/web/build.sh first" >&2; exit 1; }
done
command -v node >/dev/null || { echo "error: node is required" >&2; exit 1; }

# --- is the build compatible with the current page? --------------------------------------------------------------
BUILT="$(node -e 'const j=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));process.stdout.write(String(j.commit||""))' "$(native "$DIST/build-info.json")")"
BUILT_DIRTY="$(node -e 'const j=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));process.stdout.write(String(!!j.dirty))' "$(native "$DIST/build-info.json")")"
[ "$BUILT_DIRTY" = true ] && echo "warning: $DIST was built from uncommitted changes on top of $BUILT; the check below compares with $BUILT only" >&2
if [ -z "$BUILT" ] || ! git -C "$REPO" cat-file -e "$BUILT^{commit}" 2>/dev/null; then
  echo "warning: the build's commit ($BUILT) is unknown to this repository: cannot check the page against it" >&2
  $FORCE || { echo "error: rerun with --force to patch anyway" >&2; exit 1; }
else
  # Compared with the working tree: uncommitted edits count too.
  HOST_CHANGES="$(git -C "$REPO" -c core.safecrlf=false diff --name-only "$BUILT" -- web/game ':(exclude)web/game/H5Reborn.web/wwwroot' tools/web/build.sh)"
  if [ -n "$HOST_CHANGES" ]; then
    echo "The web host changed since $DIST was built ($BUILT); the page may not match the compiled code:" >&2
    printf '  %s\n' $HOST_CHANGES >&2
    $FORCE || { echo "error: rebuild with tools/web/build.sh (or --force if you know the page does not depend on these)" >&2; exit 1; }
  fi
  GAME_CHANGES="$(git -C "$REPO" -c core.safecrlf=false diff --name-only "$BUILT" -- game src | wc -l | tr -d ' ')"
  [ "$GAME_CHANGES" = 0 ] || echo "note: $GAME_CHANGES game file(s) changed since $BUILT; they are NOT deployed by this script (that needs tools/web/build.sh)"
fi

# --- patch -------------------------------------------------------------------------------------------------------
# One node process does the work: compare, write <file>.patch-tmp (+ siblings), then rename everything into place.
COMMIT="$(git -C "$REPO" rev-parse --short HEAD 2>/dev/null || echo unknown)"
DIRTY="$(git -C "$REPO" status --porcelain -- web/game/H5Reborn.web/wwwroot 2>/dev/null | head -1)"
node - "$(native "$SRC")" "$(native "$DIST")" "$DRY" "$COMMIT" "$([ -n "$DIRTY" ] && echo true || echo false)" <<'NODE'
const fs = require('fs'), path = require('path'), zlib = require('zlib');
const [src, dist, dry, commit, dirty] = process.argv.slice(2);
const changed = [];
for (const name of fs.readdirSync(src).sort()) {
  const from = path.join(src, name);
  if (!fs.statSync(from).isFile()) continue;
  const data = fs.readFileSync(from), to = path.join(dist, name);
  if (fs.existsSync(to) && Buffer.compare(fs.readFileSync(to), data) === 0) continue;
  changed.push({ name, data, to, isNew: !fs.existsSync(to) });
}
if (!changed.length) { console.log(`page: ${dist} already has the current page files`); process.exit(0); }
for (const c of changed) console.log(`page: ${c.isNew ? 'add' : 'update'} ${c.name} (${c.data.length} bytes)`);
if (dry === 'true') { console.log('page: dry run, nothing written'); process.exit(0); }

// Siblings: rewrite the ones the build made (a stale .br would be served instead of the new file); a new file gets
// them when it is big enough to be worth it (the publish leaves small files uncompressed too).
const writes = [];
for (const c of changed) {
  writes.push([c.to, c.data]);
  const want = (ext) => fs.existsSync(c.to + ext) || (c.isNew && c.data.length >= 4096);
  if (want('.br')) writes.push([c.to + '.br', zlib.brotliCompressSync(c.data, { params: {
    [zlib.constants.BROTLI_PARAM_QUALITY]: 11, [zlib.constants.BROTLI_PARAM_SIZE_HINT]: c.data.length } })]);
  if (want('.gz')) writes.push([c.to + '.gz', zlib.gzipSync(c.data, { level: 9 })]);
}
// Size manifest (the page's progress bar): keep any listed size exact.
const sizesFile = path.join(dist, 'twodog.sizes.json');
if (fs.existsSync(sizesFile)) {
  const sizes = JSON.parse(fs.readFileSync(sizesFile, 'utf8'));
  let touched = false;
  for (const c of changed) if (c.name in sizes) { sizes[c.name] = c.data.length; touched = true; }
  if (touched) writes.push([sizesFile, Buffer.from(JSON.stringify(sizes))]);
}
const infoFile = path.join(dist, 'build-info.json');
const info = JSON.parse(fs.readFileSync(infoFile, 'utf8'));
info.page = { commit, dirty: dirty === 'true', patched: new Date().toISOString().replace(/\.\d+Z$/, 'Z'),
  files: [...new Set([...(info.page?.files || []), ...changed.map((c) => c.name)])].sort() };
writes.push([infoFile, Buffer.from(JSON.stringify(info, null, 1) + '\n')]);
// All temporary files first, then the renames (each atomic): a reader sees the old or the new file, never half of one.
for (const [file, data] of writes) fs.writeFileSync(file + '.patch-tmp', data);
for (const [file] of writes) fs.renameSync(file + '.patch-tmp', file);
console.log(`page: patched ${changed.length} file(s) in ${dist} (page ${commit}${dirty === 'true' ? ', uncommitted changes' : ''}; engine build ${info.commit})`);
NODE
