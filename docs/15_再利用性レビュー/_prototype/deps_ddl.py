"""DDL を部品視点で分解する。
 - テーブルがどの部品に属するか（モジュールの DbTable から）
 - 部品をまたぐ FK / トリガ / ビュー
 - 1ファイルが複数部品にまたがっているか（＝部品単位で DDL を取り出せるか）
"""
import json, os, re, collections
import os as _os, sys as _sys
# リポジトリルート = このファイルから 3 つ上（docs/15_再利用性レビュー/_prototype/）。
# 第1引数でデザインルート（Designer を含むフォルダ）を上書きできる。
_ROOT = _sys.argv[1] if len(_sys.argv) > 1 else _os.path.abspath(
    _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), '..', '..', '..'))

DESIGN = _os.path.join(_ROOT, 'Designer', 'Design')
DDL = _os.path.join(_ROOT, 'Designer', 'ddl')
MODS = os.path.join(DESIGN, 'Modules')

tbl2part = {}
for r, d, fs in os.walk(MODS):
    for f in fs:
        if f.endswith('.mod.json'):
            j = json.load(open(os.path.join(r, f), encoding='utf-8'))
            t = (j.get('DbTable') or '').strip()
            if t:
                tbl2part.setdefault(t, os.path.basename(r))

# DDL を文単位に割る
files = collections.OrderedDict()
for f in sorted(os.listdir(DDL)):
    if f.endswith('.sql'):
        files[f] = open(os.path.join(DDL, f), encoding='utf-8', errors='ignore').read()

created = {}   # obj -> (kind, file)
for f, txt in files.items():
    for m in re.finditer(r'CREATE\s+(TABLE|VIEW|TRIGGER|INDEX|UNIQUE\s+INDEX)\s+(?:IF\s+NOT\s+EXISTS\s+)?["\[]?([A-Za-z_][A-Za-z0-9_]*)', txt, re.I):
        created.setdefault(m.group(2), (re.sub(r'\s+', ' ', m.group(1).upper()), f))

known = set(tbl2part) | set(created)
alt = '|'.join(sorted(map(re.escape, known), key=len, reverse=True))


def part_of(t):
    return tbl2part.get(t, '?')


print('=== 1) 1 DDL ファイルが触る部品 ===')
multi = 0
for f, txt in files.items():
    body = re.sub(r'--.*', '', txt)
    touched = collections.Counter()
    for m in re.finditer(r'\b(?:TABLE|VIEW|TRIGGER|INTO|FROM|JOIN|UPDATE|ON)\s+(?:IF\s+NOT\s+EXISTS\s+)?["\[]?(' + alt + r')\b', body, re.I):
        touched[part_of(m.group(1))] += 1
    parts = sorted(k for k in touched if k != '?')
    if len(parts) > 1:
        multi += 1
    print('%-46s %s' % (f, ' '.join('%s(%d)' % (p, touched[p]) for p in parts) or '-'))
print('複数部品にまたがるファイル: %d / %d' % (multi, len(files)))

print()
print('=== 2) 部品をまたぐ外部キー ===')
cross = collections.Counter()
for f, txt in files.items():
    body = re.sub(r'--.*', '', txt)
    for m in re.finditer(r'CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?["\[]?([A-Za-z_][A-Za-z0-9_]*)["\]]?\s*\((.*?)\n\s*\)\s*;', body, re.I | re.S):
        tname, cols = m.group(1), m.group(2)
        sp = part_of(tname)
        for r2 in re.finditer(r'REFERENCES\s+["\[]?([A-Za-z_][A-Za-z0-9_]*)', cols, re.I):
            tp = part_of(r2.group(1))
            if tp != sp:
                cross[(sp, tname, tp, r2.group(1), f)] += 1
for (sp, tn, tp, tt, f) in sorted(cross):
    print('  %-14s %-28s -> %-14s %-28s  (%s)' % (sp, tn, tp, tt, f))

print()
print('=== 3) トリガ（部品をまたぐものを中心に） ===')
for f, txt in files.items():
    for m in re.finditer(r'CREATE\s+TRIGGER\s+(?:IF\s+NOT\s+EXISTS\s+)?["\[]?([A-Za-z_][A-Za-z0-9_]*)["\]]?(.*?)END\s*;', txt, re.I | re.S):
        name, body = m.group(1), m.group(2)
        tabs = set(re.findall(r'\b(?:ON|INTO|UPDATE|FROM|JOIN)\s+["\[]?(' + alt + r')\b', body, re.I))
        parts = sorted(set(part_of(t) for t in tabs) - {'?'})
        print('  %-40s %-46s parts=%s' % (name, f, ','.join(parts)))

print()
print('=== 4) ビュー ===')
for f, txt in files.items():
    for m in re.finditer(r'CREATE\s+VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?["\[]?([A-Za-z_][A-Za-z0-9_]*)["\]]?\s+AS(.*?);', txt, re.I | re.S):
        name, body = m.group(1), m.group(2)
        tabs = set(re.findall(r'\b(?:FROM|JOIN)\s+["\[]?(' + alt + r')\b', body, re.I))
        parts = sorted(set(part_of(t) for t in tabs) - {'?'})
        print('  %-32s %-46s parts=%s' % (name, f, ','.join(parts)))
