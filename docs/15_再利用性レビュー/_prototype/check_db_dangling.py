#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""DB 内のビュー・トリガが、存在しないテーブルを参照していないかを検査する（試作）。

部品を外したあとに残る「静かな地雷」を機械検査する目的:
  designcheck も check_navigation も DB の中は見ないため、
  「基盤マスタのトリガが、外した部品のテーブルを UPDATE している」型の破壊を検出できない。
  （実測: 構成A で取引先の名称変更が 'no such table: main.vendor_invoices' で失敗した）

使い方: python check_db_dangling.py <sqlite.db>
終了コード: 0=問題なし / 1=参照切れあり
"""
import re, sqlite3, sys

db = sys.argv[1]
con = sqlite3.connect(db)
cur = con.cursor()

rows = cur.execute(
    "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE sql IS NOT NULL").fetchall()
existing = set(r[1] for r in rows if r[0] in ('table', 'view'))
existing |= {'sqlite_master', 'sqlite_sequence'}

# 「参照」とみなすキーワードの直後の識別子。UPDATE OF col / AFTER ... ON tbl のうち
# ON はトリガ対象表なので別途拾い、OF は列名なので拾わない。
REF = re.compile(r'\b(?:FROM|JOIN|INTO|UPDATE|DELETE\s+FROM)\s+(?!OF\b|SET\b)["\[]?([A-Za-z_][A-Za-z0-9_]*)["\]]?', re.I)
SQLKW = {'select', 'values', 'set', 'where', 'when', 'then', 'begin', 'end',
         'new', 'old', 'main', 'of', 'on', 'as', 'not', 'exists', 'case'}

bad = []
checked = 0
for typ, name, tbl, sql in rows:
    if typ not in ('view', 'trigger'):
        continue
    checked += 1
    body = re.sub(r'--.*', '', sql)
    refs = set()
    for m in REF.finditer(body):
        t = m.group(1)
        if t.lower() in SQLKW:
            continue
        refs.add(t)
    if typ == 'trigger':
        m = re.search(r'\bON\s+["\[]?([A-Za-z_][A-Za-z0-9_]*)', body, re.I)
        if m:
            refs.add(m.group(1))
    # CTE 名（WITH x AS ( ... ), y AS ( ... )）とサブクエリ別名は自己解決なので除く
    ctes = set(re.findall(r'([A-Za-z_][A-Za-z0-9_]*)\s*(?:\([^()]*\))?\s+AS\s*\(', body, re.I))
    for t in sorted(refs - existing - ctes):
        bad.append((typ, name, t))

for typ, name, t in bad:
    print('ERROR %-8s %-40s -> 存在しないテーブル/ビュー %s' % (typ, name, t))
print('検査対象 view/trigger: %d 件 / 参照切れ: %d 件' % (checked, len(bad)))
sys.exit(1 if bad else 0)
