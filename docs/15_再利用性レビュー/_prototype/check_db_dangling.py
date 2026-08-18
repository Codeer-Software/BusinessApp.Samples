#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""DB のビュー・トリガが、存在しないテーブル／ビューを参照していないかを検査する。

**なぜ要るか**: designcheck はモジュールの `DbTable` は照合するが、DB の中に作った
ビュー・トリガの中身は見ない。そのため次の型の破壊を誰も検出できない。

  - 部品を外してテーブルを DROP したのに、基盤マスタのトリガがそれを書きに行く
    （実測: 会計のみ構成で `UPDATE partners` が `no such table: main.vendor_invoices` で失敗した。
     エンドユーザーから見ると「取引先の会社名を直せない」。→ docs/15_再利用性レビュー/02）
  - テーブルを改名したのに、古いビューが旧名を参照したまま残る

いずれも**実行時まで露見しない**うえ、エラー文が原因と結びつかない。

**状態**: 試作。正式なツールではない（`Designer/tools/` への昇格は 03_改善提案 P7 の提案）。

使い方:
    python docs/15_再利用性レビュー/_prototype/check_db_dangling.py              # 既定の DB
    python docs/15_再利用性レビュー/_prototype/check_db_dangling.py <path/to.db> # DB を指定

終了コード: 0 = 参照切れなし / 1 = 参照切れあり / 2 = 実行失敗
"""
from __future__ import annotations

import os
import re
import sqlite3
import sys

# 既定の DB（LocalData/db/business-app_v1.db）。
# このファイルは docs/15_再利用性レビュー/_prototype/ にあるのでリポジトリルートは 3 つ上
_HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_DB = os.path.abspath(os.path.join(
    _HERE, os.pardir, os.pardir, os.pardir, 'LocalData', 'db', 'business-app_v1.db'))

# 参照とみなすキーワードの直後の識別子。
#   UPDATE OF col ... の OF は列名なので拾わない（SET も同様）
#   AFTER ... ON tbl の ON はトリガの対象表なので別途拾う
REF = re.compile(
    r'\b(?:FROM|JOIN|INTO|UPDATE|DELETE\s+FROM)\s+(?!OF\b|SET\b)["\[]?([A-Za-z_][A-Za-z0-9_]*)["\]]?',
    re.I)
TRIGGER_TARGET = re.compile(r'\bON\s+["\[]?([A-Za-z_][A-Za-z0-9_]*)', re.I)
# WITH x AS ( / WITH RECURSIVE x(col, ...) AS ( / サブクエリ別名
CTE = re.compile(r'([A-Za-z_][A-Za-z0-9_]*)\s*(?:\([^()]*\))?\s+AS\s*\(', re.I)

SQL_KEYWORDS = {
    'select', 'values', 'set', 'where', 'when', 'then', 'begin', 'end',
    'new', 'old', 'main', 'of', 'on', 'as', 'not', 'exists', 'case',
}


def check(db_path: str) -> int:
    if not os.path.isfile(db_path):
        print('DB が見つからない: %s' % db_path, file=sys.stderr)
        return 2

    con = sqlite3.connect(db_path)
    try:
        rows = con.execute(
            'SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL').fetchall()
    finally:
        con.close()

    existing = {name for typ, name, _ in rows if typ in ('table', 'view')}
    existing |= {'sqlite_master', 'sqlite_sequence'}

    bad = []
    checked = 0
    for typ, name, sql in rows:
        if typ not in ('view', 'trigger'):
            continue
        checked += 1
        body = re.sub(r'--.*', '', sql)
        refs = {m for m in REF.findall(body) if m.lower() not in SQL_KEYWORDS}
        if typ == 'trigger':
            m = TRIGGER_TARGET.search(body)
            if m:
                refs.add(m.group(1))
        refs -= set(CTE.findall(body))
        for t in sorted(refs - existing):
            bad.append((typ, name, t))

    for typ, name, t in bad:
        print('ERROR %-8s %-40s -> 存在しないテーブル/ビュー %s' % (typ, name, t))
    print('DB: %s' % db_path)
    print('検査対象 view/trigger: %d 件 / 参照切れ: %d 件' % (checked, len(bad)))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(check(sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DB))
