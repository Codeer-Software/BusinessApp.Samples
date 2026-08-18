import json, os, collections, sys
import os as _os, sys as _sys
# リポジトリルート = このファイルから 3 つ上（docs/15_再利用性レビュー/_prototype/）。
# 第1引数でデザインルート（Designer を含むフォルダ）を上書きできる。
_ROOT = _sys.argv[1] if len(_sys.argv) > 1 else _os.path.abspath(
    _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), '..', '..', '..'))

ROOT = _os.path.join(_ROOT, 'Designer', 'Design')
os.chdir(ROOT)

mods = {}
for r, d, fs in os.walk('Modules'):
    for f in fs:
        if f.endswith('.mod.json'):
            mods[f[:-9]] = os.path.basename(r)

REF = {'ModuleName', 'Module', 'TopPageModule', 'ModuleUrlSegment'}


def walk(o, out):
    if isinstance(o, dict):
        for k, v in o.items():
            if k in REF and isinstance(v, str) and v in mods:
                out.add(v)
            else:
                walk(v, out)
    elif isinstance(o, list):
        for v in o:
            walk(v, out)


files = []
for r, d, fs in os.walk('.'):
    for f in sorted(fs):
        if f.endswith('.json') and not f.startswith('designer.settings'):
            p = os.path.join(r, f).replace(os.sep, '/')
            out = set()
            try:
                walk(json.load(open(p, encoding='utf-8')), out)
            except Exception as e:
                print('ERR', p, e)
                continue
            files.append((p, out))


def part(p):
    q = p.split('/')
    if len(q) > 2 and q[1] == 'Modules':
        return q[2]
    if 'PageFrames' in p:
        return '@PageFrames'
    return '@root'


fold = collections.defaultdict(lambda: collections.defaultdict(list))
for p, out in files:
    src = part(p)
    own = os.path.basename(p)[:-9] if p.endswith('.mod.json') else None
    for t in sorted(out):
        if t == own:
            continue
        tp = mods[t]
        if tp != src:
            fold[src][tp].append((os.path.basename(p), t))

print('### フォルダ間 JSON 参照（ModuleName/Module/TopPageModule/ModuleUrlSegment のみ）')
for s in sorted(fold):
    for t in sorted(fold[s]):
        pairs = sorted(set(fold[s][t]))
        print('%s -> %s  (%d)' % (s, t, len(pairs)))
        for f, m in pairs:
            print('    %-42s -> %s' % (f, m))
