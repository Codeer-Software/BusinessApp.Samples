"""スクリプト(.mod.cs)の部品間「型参照」を抽出する。

CLB のスクリプトは他モジュールを生成プロキシ型として使う:
    new X()  /  ModuleSearcher<X>  /  (X)obj  /  List<X>  /  X.Create()  /  as X
フィールドアクセス(.Project など)は JSON 側の依存として既に数えているので除く。
"""
import os, re, collections
import os as _os, sys as _sys
# リポジトリルート = このファイルから 3 つ上（docs/15_再利用性レビュー/_prototype/）。
# 第1引数でデザインルート（Designer を含むフォルダ）を上書きできる。
_ROOT = _sys.argv[1] if len(_sys.argv) > 1 else _os.path.abspath(
    _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), '..', '..', '..'))

ROOT = _os.path.join(_ROOT, 'Designer', 'Design', 'Modules')
os.chdir(ROOT)

mods = {}
for r, d, fs in os.walk('.'):
    for f in fs:
        if f.endswith('.mod.json'):
            mods[f[:-9]] = os.path.basename(r)

names = sorted(mods, key=len, reverse=True)
alt = '|'.join(map(re.escape, names))
PATS = [
    ('new', re.compile(r'\bnew\s+(' + alt + r')\s*\(')),
    ('generic', re.compile(r'<\s*(' + alt + r')\s*>')),
    ('cast', re.compile(r'\(\s*(' + alt + r')\s*\)\s*[A-Za-z_(]')),
    ('as', re.compile(r'\bas\s+(' + alt + r')\b')),
    ('decl', re.compile(r'(?:^|[\s(,])(' + alt + r')\s+[a-z_][A-Za-z0-9_]*\s*[=;,)]')),
]

out = collections.defaultdict(lambda: collections.defaultdict(list))
for r, d, fs in os.walk('.'):
    for f in sorted(fs):
        if not f.endswith('.mod.cs'):
            continue
        src = os.path.basename(r)
        own = f[:-7]
        text = open(os.path.join(r, f), encoding='utf-8', errors='ignore').read()
        # コメント行は除く
        text = re.sub(r'//.*', '', text)
        hits = collections.Counter()
        for kind, pat in PATS:
            for m in pat.findall(text):
                if m == own:
                    continue
                hits[(m, kind)] += 1
        for (m, kind), n in sorted(hits.items()):
            tgt = mods[m]
            if tgt != src:
                out[src][tgt].append((f, m, kind, n))

print('### スクリプト(.mod.cs)の部品間 型参照')
for s in sorted(out):
    for t in sorted(out[s]):
        rows = out[s][t]
        print('%s -> %s  (%d)' % (s, t, len(rows)))
        for f, m, kind, n in rows:
            print('    %-38s %-24s %-8s x%d' % (f, m, kind, n))
