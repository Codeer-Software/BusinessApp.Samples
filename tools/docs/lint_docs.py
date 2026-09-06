#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""lint_docs.py — ドキュメント規約の機械検査（入口）.

仕様書: docs/00_ドキュメント規約/

長期開発でドキュメントが腐り、肥大化するのを防ぐ。検査するのは次の 5 点である。
  1. 読まなくていい文書を判別できるか（フロントマターと status）
  2. 索引・ADR 台帳と実ファイルが食い違っていないか
  3. current でない文書をコード（コメント）が参照していないか
     （開発者の提案。2026-08-25。意図的な歴史参照は行に lint-docs:ignore を書く）
  4. current な文書が superseded な文書へリンクしていないか
     （開発者の提案。2026-08-28。読者を古い決定へ連れて行かないため）
  5. 本文を変えたのに updated: を今日にしていない文書がないか
     （開発者の指示。2026-08-27。横断レビューで 7 文書のずれが見つかったため）
  6. 条項を 80 §3 の記法で書いているか（`5 条 1 項` と書いていないか）
     （開発者の指示。2026-09-06。揃っていないと grep が効かず、実際に 4 回取りこぼした）

中身は `doclint/` パッケージが持つ（model / checks / selftest）。
本ファイルは CLI と、検査の呼び出し順だけを持つ。

使い方:
    python tools/docs/lint_docs.py            # 規約違反の検査（error / warn）
    python tools/docs/lint_docs.py --stats    # current の行数など指標
    python tools/docs/lint_docs.py --selftest # 検査そのものが空回りしていないか

終了コード: 0 = error なし / 1 = error あり / 2 = 実行失敗

Python 3.8+ / 標準ライブラリのみ（YAML パーサは使わず、必要な範囲だけ自前で読む）。
"""

from __future__ import annotations

import argparse
import os
import sys
from typing import Dict, List

# `python -P` / PYTHONSAFEPATH=1 だとスクリプトの位置が sys.path に入らず `doclint` を
# 見つけられない。**先頭ではなく末尾**に足す（先頭だと将来 tools/docs に標準モジュールと
# 同名のファイルを置いた瞬間にプロセス全体が壊れる）
sys.path.append(os.path.dirname(os.path.abspath(__file__)))

from doclint.checks import (Finding, check_adr_ledger, check_article_notation,  # noqa: E402
                            check_body, check_code_references,
                            check_docs_index, check_front_matter, check_links, check_section_references,
                            check_superseded_links, check_updated_freshness,
                            check_updated_history)
from doclint.model import REFERENCE_PREFIXES, SEV_ERROR, SEV_WARN, Doc, load_docs  # noqa: E402
from doclint.selftest import selftest  # noqa: E402


def print_stats(docs: List[Doc]) -> None:
    rows = []
    total = 0
    for d in docs:
        if d.status != "current":
            continue
        body = len(d.lines) - d.body_start
        if d.rel.startswith(REFERENCE_PREFIXES):
            continue  # 通読しない文書は指標から除く
        rows.append((body, d.rel))
        total += body
    rows.sort(reverse=True)
    print("== current の行数（decisions/ を除く） ==")
    for body, rel in rows:
        print("{:>6}  {}".format(body, rel))
    print("{:>6}  {}".format(total, "合計"))
    print("")
    by_status: Dict[str, int] = {}
    for d in docs:
        by_status[d.status or "(なし)"] = by_status.get(d.status or "(なし)", 0) + 1
    print("== status 別の文書数 ==")
    for k in sorted(by_status):
        print("{:>6}  {}".format(by_status[k], k))


def main() -> int:
    ap = argparse.ArgumentParser(description="ドキュメント規約の検査")
    ap.add_argument("--stats", action="store_true", help="指標を表示する")
    ap.add_argument("--selftest", action="store_true", help="関門そのものを検査する")
    args = ap.parse_args()

    if args.selftest:
        return selftest()

    docs, unreadable = load_docs()
    if args.stats:
        print_stats(docs)
        return 0

    existing = {d.rel for d in docs}
    docs_by_rel = {d.rel: d for d in docs}
    findings: List[Finding] = []
    for rel in unreadable:
        findings.append((SEV_ERROR, rel, "文書を読めませんでした。**検査できていない**ので黙って進まない"))
    seen_superseded_links = 0
    for d in docs:
        check_front_matter(d, findings)
        check_links(d, existing, findings)
        seen_superseded_links += check_superseded_links(d, docs_by_rel, findings)
        check_body(d, findings)
    check_adr_ledger(docs, findings)
    check_docs_index(docs, findings)
    check_code_references(docs, findings)
    check_section_references(docs, findings)
    check_updated_freshness(docs, findings)
    check_updated_history(docs, findings)
    ignored_notation = check_article_notation(docs, findings)

    errors = [f for f in findings if f[0] == SEV_ERROR]
    warns = [f for f in findings if f[0] == SEV_WARN]
    # 2 値のどちらでもない severity を黙って warn に吸い込ませない
    others = [f for f in findings if f[0] not in (SEV_ERROR, SEV_WARN)]
    for sev, rel, msg in sorted(findings, key=lambda f: (f[0] != SEV_ERROR, f[1], f[2])):
        print("{}\t{}\t{}".format(sev, rel, msg))

    print("")
    # superseded 宛リンクの数を必ず出す。0 に落ちたら「違反が無い」ではなく
    # 「配線が死んだ・免除が広がりすぎた」を疑う（黙って素通りする関門を作らないため）
    print("検査文書数: {} / error: {} / warn: {} / superseded 宛リンク: {} 件を検査 / "
          "条項の記法を外した行: {}"
          .format(len(docs), len(errors), len(warns), seen_superseded_links, ignored_notation))
    return 1 if errors or others else 0


if __name__ == "__main__":
    sys.exit(main())
