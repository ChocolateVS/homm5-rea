"""Skills audit part G: apply exact (old, new) replacements to a C# file, preserving its line endings."""
import sys
def apply(path, pairs):
    t = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in t else '\n'
    t = t.replace('\r\n', '\n')
    for old, new in pairs:
        if t.count(old) != 1:
            raise SystemExit(f'pattern found {t.count(old)}x in {path}: {old[:80]!r}')
        t = t.replace(old, new, 1)
    open(path, 'w', encoding='utf-8', newline='').write(t.replace('\n', nl))
