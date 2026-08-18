"""基盤（MasterSystem / MasterBusiness / Shell）の各モジュールが、どの部品から使われているか。
「会計だけ取り出したとき、基盤から何が不要になるか」を出すための逆引き。
JSON 参照・スクリプト型参照・クエリ SQL のテーブル参照・DDL のトリガ/FK を合算する。
"""
import json, os, re, collections
import os as _os, sys as _sys
# リポジトリルート = このファイルから 3 つ上（docs/15_再利用性レビュー/_prototype/）。
# 第1引数でデザインルート（Designer を含むフォルダ）を上書きできる。
_ROOT = _sys.argv[1] if len(_sys.argv) > 1 else _os.path.abspath(
    _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), '..', '..', '..'))

DESIGN = _os.path.join(_ROOT, 'Designer', 'Design')
DDLDIR = _os.path.join(_ROOT, 'Designer', 'ddl')
MODS = os.path.join(DESIGN, 'Modules')

mod2part, tbl2part, mod2tbl = {}, {}, {}
for r, d, fs in os.walk(MODS):
    for f in fs:
        if f.endswith('.mod.json'):
            j = json.load(open(os.path.join(r, f), encoding='utf-8'))
            n = f[:-9]
            p = os.path.basename(r)
            mod2part[n] = p
            t = (j.get('DbTable') or '').strip()
            mod2tbl[n] = t
            if t:
                tbl2part.setdefault(t, p)

BASE = {'MasterSystem', 'MasterBusiness', 'Shell'}
REF = {'ModuleName', 'Module', 'TopPageModule', 'ModuleUrlSegment'}
users = collections.defaultdict(set)   # base module -> {consumer part}


def walk(o, out):
    if isinstance(o, dict):
        for k, v in o.items():
            if k in REF and isinstance(v, str) and v in mod2part:
                out.add(v)
            else:
                walk(v, out)
    elif isinstance(o, list):
        for v in o:
            walk(v, out)


# JSON
for r, d, fs in os.walk(MODS):
    for f in fs:
        if f.endswith('.mod.json'):
            src = os.path.basename(r)
            out = set()
            walk(json.load(open(os.path.join(r, f), encoding='utf-8')), out)
            for t in out:
                if mod2part[t] in BASE and mod2part[t] != src:
                    users[t].add(src)

# PageFrames
for r, d, fs in os.walk(os.path.join(DESIGN, 'PageFrames')):
    for f in fs:
        if f.endswith('.frm.json'):
            out = set()
            walk(json.load(open(os.path.join(r, f), encoding='utf-8')), out)
            for t in out:
                if mod2part[t] in BASE:
                    users[t].add('@frame:' + f[:-9])

# スクリプト型参照
names = sorted(mod2part, key=len, reverse=True)
alt = '|'.join(map(re.escape, names))
TYPEPAT = re.compile(r'\bnew\s+(' + alt + r')\s*\(|<\s*(' + alt + r')\s*>|\bas\s+(' + alt + r')\b')
for r, d, fs in os.walk(MODS):
    for f in fs:
        if f.endswith('.mod.cs'):
            src = os.path.basename(r)
            txt = re.sub(r'//.*', '', open(os.path.join(r, f), encoding='utf-8', errors='ignore').read())
            for m in TYPEPAT.finditer(txt):
                t = m.group(1) or m.group(2) or m.group(3)
                if mod2part.get(t) in BASE and mod2part[t] != src:
                    users[t].add(src)

# クエリ SQL のテーブル参照
talt = '|'.join(sorted(map(re.escape, tbl2part), key=len, reverse=True))
TBLPAT = re.compile(r'\b(?:from|join)\s+["\[]?(' + talt + r')\b', re.I)
tbl2mod = {v: k for k, v in mod2tbl.items() if v}
for r, d, fs in os.walk(MODS):
    for f in fs:
        if f.endswith('.sql'):
            src = os.path.basename(r)
            sql = re.sub(r'--.*', '', open(os.path.join(r, f), encoding='utf-8', errors='ignore').read())
            for t in set(TBLPAT.findall(sql)):
                if tbl2part[t] in BASE and tbl2part[t] != src:
                    users[tbl2mod.get(t, t)].add(src)

print('### 基盤モジュールの利用元（部品）')
print('%-32s %-14s %s' % ('module', 'part', 'used by'))
for m in sorted(mod2part):
    if mod2part[m] not in BASE:
        continue
    u = sorted(users.get(m, []))
    parts = sorted(set(x for x in u if not x.startswith('@')))
    frames = sorted(set(x[7:] for x in u if x.startswith('@frame:')))
    print('%-32s %-14s %s' % (m, mod2part[m], ' '.join(parts) if parts else '(なし)'))
    if frames:
        print('%-47s frames: %s' % ('', ' '.join(frames)))
