"""Base/LevelDivisor/PerDividedLevel structs: ((level-1)/div)*per + base in integers (exe 00a5c300 Blooddrinker,
004bc470 Wyvern Tamer, warcries)."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8').read().replace('\r\n', '\n')
old = '''        double b = st.Get(path + "/Base", defBase), div = Math.Max(1, st.Get(path + "/LevelDivisor", defDiv)), per = st.Get(path + "/PerDividedLevel", defPer);
        return b + Math.Floor(level / div) * per;'''
new = '''        // exe (e.g. Blooddrinker 00a5c300): integer ((level - 1) / LevelDivisor) * PerDividedLevel + Base.
        int b = (int)st.Get(path + "/Base", defBase), div = Math.Max(1, (int)st.Get(path + "/LevelDivisor", defDiv)), per = (int)st.Get(path + "/PerDividedLevel", defPer);
        return (level - 1) / div * per + b;'''
if new in t: sys.exit(0)
if t.count(old) != 1: sys.exit('anchor not found')
open(p, 'w', encoding='utf-8', newline='').write(t.replace(old, new))
