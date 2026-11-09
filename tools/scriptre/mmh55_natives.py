"""List the script natives MMH55's system scripts call, against H5.Scripting's registrations.

Usage: python -I tools/scriptre/mmh55_natives.py <5.5 install dir> <repo root>
Reads MMH55-Index.pak / MMH55-Frame.pak (zip) Lua files (creature_N.lua skipped), the exe native list
(src/H5.Scripting/Runtime/NativeFormats.cs) and our registrations (Reg("…") / L.Register("…") in
src/H5.Scripting). A call is `Name(` not preceded by `function`, `.` or `:`; names defined in Lua
(`function Name`, `Name = function`, locals, parameters are ignored) are not natives. Each name is
counted per H55_* function body that calls it, so the ranking shows how many script features use it.
"""
import os, re, sys, zipfile

inst, repo = sys.argv[1], sys.argv[2]
files = {}
for pak in ("MMH55-Index.pak", "MMH55-Frame.pak"):
    z = zipfile.ZipFile(os.path.join(inst, "data", pak))
    for n in z.namelist():
        if n.lower().endswith(".lua") and "creature_" not in n.lower():
            files[n.split("/")[-1]] = z.read(n).decode("latin-1")

src = open(os.path.join(repo, "src/H5.Scripting/Runtime/NativeFormats.cs"), encoding="utf-8").read()
exe = {}
section = None
for line in src.splitlines():
    m = re.search(r"IReadOnlyDictionary<string, string> (\w+) =", line)
    if m: section = m.group(1)
    m = re.match(r'\s*\["(\w+)"\] = "', line)
    if m: exe.setdefault(m.group(1), set()).add(section)

ours = {"Adventure": set(), "Combat": set()}
for root, _, fs in os.walk(os.path.join(repo, "src/H5.Scripting")):
    for f in fs:
        if not f.endswith(".cs"): continue
        t = open(os.path.join(root, f), encoding="utf-8").read()
        names = set(re.findall(r'\b(?:Reg|Register)\("(\w+)"', t))
        if f == "CombatScripting.cs": ours["Combat"] |= names
        elif f == "BaseLibrary.cs": ours["Adventure"] |= names; ours["Combat"] |= names
        else: ours["Adventure"] |= names

def strip(t):
    t = re.sub(r"--\[\[.*?\]\]", "", t, flags=re.S)
    t = re.sub(r"--[^\n]*", "", t)
    t = re.sub(r'"(?:\\.|[^"\\\n])*"', '""', t)
    t = re.sub(r"'(?:\\.|[^'\\\n])*'", "''", t)
    t = re.sub(r"\[\[.*?\]\]", '""', t, flags=re.S)
    return t

LUA = {"if","while","for","return","and","or","not","function","local","elseif","until","in","then","do","end"}
defined = set()
for t in files.values():
    t = strip(t)
    defined |= set(re.findall(r"\bfunction\s+([A-Za-z_]\w*)\s*\(", t))
    defined |= set(re.findall(r"\b([A-Za-z_]\w*)\s*=\s*function\b", t))

combat_files = {"H55-Combat.lua", "combat-common.lua", "combat-startup.lua"}
for ctx in ("Adventure", "Combat"):
    users = {}
    for fn, t in files.items():
        if (fn in combat_files) != (ctx == "Combat"):
            if fn not in ("common.lua", "H55-Library.lua", "H55-Tables.lua", "H55-Settings.lua", "H55-Version.lua"): continue
        t = strip(t)
        # split into top-level function bodies (by `function NAME(` header)
        parts = re.split(r"\bfunction\s+([A-Za-z_][\w.:]*)\s*\(", t)
        chunks = [("<top>", parts[0])] + [(parts[i], parts[i + 1]) for i in range(1, len(parts), 2)]
        for owner, body in chunks:
            for m in re.finditer(r"(?<![.:\w])([A-Za-z_]\w*)\s*\(", body):
                n = m.group(1)
                if n in LUA or n in defined: continue
                users.setdefault(n, set()).add(f"{fn}:{owner}")
    print(f"== {ctx} context (MMH55 system scripts): {len(users)} called names")
    rows = []
    for n, u in users.items():
        if n in ours[ctx]: status = "ours"
        elif n in exe: status = "MISSING(exe " + "/".join(sorted(exe[n])) + ")"
        else: status = "not-a-native"
        rows.append((len(u), n, status, sorted(u)))
    for cnt, n, status, u in sorted(rows, key=lambda r: (-r[0], r[1])):
        if status != "ours":
            print(f"{n}\t{cnt}\t{status}\t{', '.join(u[:5])}")
    print(f"   implemented: {sum(1 for r in rows if r[2] == 'ours')}")
