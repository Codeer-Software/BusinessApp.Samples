#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""doclint — ドキュメント規約の機械検査（`tools/docs/lint_docs.py` の中身）.

仕様書: docs/00_ドキュメント規約/

  model.py     設定値・`Doc`・git 呼び出し・文書の読み込み・パス解決
  checks.py    検査 9 本と、その純粋な判定部分
  selftest.py  関門そのものの検査（`--selftest`）

入口は `tools/docs/lint_docs.py`（CLI と `main`）。**呼び出し方は変えていない。**
分けたのは 1 ファイルが 770 行を超えたためで、普通のプログラムと同じ理由である
（開発者の指摘。2026-08-28）。
"""

import sys

# 日本語を印字するのは `doclint` 側なので、手当てもここに置く。入口に置くと
# `lint_docs.py` を経由しない使い方で cp932 コンソールが化ける（2026-08-28 に実測）
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
    except Exception:
        pass
