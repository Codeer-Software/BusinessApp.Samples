"""クエリ SQL(*.sql) が、どの部品のテーブルを読むかを出す。"""
import json, os, re, collections
import os as _os, sys as _sys
# リポジトリルート = このファイルから 3 つ上（docs/15_再利用性レビュー/_prototype/）。
# 第1引数でデザインルート（Designer を含むフォルダ）を上書きできる。
_ROOT = _sys.argv[1] if len(_sys.argv) > 1 else _os.path.abspath(
    _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), '..', '..', '..'))

DESIGN = _os.path.join(_ROOT, 'Designer', 'Design')
DDL = _os.path.join(_ROOT, 'Designer', 'ddl')
MODS = os.path.join(DESIGN, 'Modules')

# 1) テーブル -> 部品
tbl2part = {}
for r, d, fs in os.walk(MODS):
    for f in fs:
        if not f.endswith('.mod.json'):
            continue
        j = json.load(open(os.path.join(r, f), encoding='utf-8'))
        t = (j.get('DbTable') or '').strip()
        if t:
            tbl2part.setdefault(t, os.path.basename(r))

# 2) DDL の VIEW/TABLE も
ddl_objs = {}
for f in sorted(os.listdir(DDL)):
    if not f.endswith('.sql'):
        continue
    txt = open(os.path.join(DDL, f), encoding='utf-8', errors='ignore').read()
    for m in re.finditer(r'CREATE\s+(TABLE|VIEW)\s+(?:IF\s+NOT\s+EXISTS\s+)?["\[]?([A-Za-z_][A-Za-z0-9_]*)', txt, re.I):
        ddl_objs.setdefault(m.group(2), []).append(f)

known = set(tbl2part) | set(ddl_objs)
alt = '|'.join(sorted(map(re.escape, known), key=len, reverse=True))
TBLPAT = re.compile(r'\b(?:from|join)\s+["\[]?(' + alt + r')\b', re.I)

out = collections.defaultdict(lambda: collections.defaultdict(set))
for r, d, fs in os.walk(MODS):
    for f in sorted(fs):
        if not f.endswith('.sql'):
            continue
        part = os.path.basename(r)
        sql = open(os.path.join(r, f), encoding='utf-8', errors='ignore').read()
        sql = re.sub(r'--.*', '', sql)
        for t in sorted(set(TBLPAT.findall(sql))):
            tp = tbl2part.get(t, '@view:' + t)
            if tp != part:
                out[part][tp].add((f, t))

print('### クエリ SQL の 部品間テーブル参照')
for s in sorted(out):
    for t in sorted(out[s]):
        rows = sorted(out[s][t])
        print('%s -> %s  (%d)' % (s, t, len(rows)))
        for f, tb in rows:
            print('    %-42s %s' % (f, tb))
