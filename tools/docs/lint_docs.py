#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""lint_docs.py — ドキュメント規約の機械検査（入口）.

仕様書: docs/00_ドキュメント規約/

長期開発でドキュメントが腐り、肥大化するのを防ぐ。守っているのは 2 つだけである。

  1. **読まなくていい文書を、開いて数行で判別できること**
  2. **`current` の本文が、常に「今どうなっているか」だけを語っていること**

**検査項目の正典は docs/00 §6 の表**（docs/00_ドキュメント規約/README.md）で、ここには写さない
（**本数も数えない**——足すたびに直し忘れ、入口が一番古い状態になる）。
正典のほうが「何を・なぜ見るか」と、**それぞれの限界**まで持っている。

中身は `doclint/` パッケージが持つ（model / checks / selftest）。
本ファイルは CLI と、検査の呼び出し順だけを持つ。

使い方:
    python tools/docs/lint_docs.py            # 規約違反の検査（error / warn）
    python tools/docs/lint_docs.py --stats    # current の行数など指標
    python tools/docs/lint_docs.py --selftest # 検査そのものが空回りしていないか
    python tools/docs/lint_docs.py --today 2027-01-01  # 日付で発効する切替を先取りして洗う

終了コード: 0 = error なし / 1 = error あり / 2 = 実行失敗

Python 3.8+ / 標準ライブラリのみ（YAML パーサは使わず、必要な範囲だけ自前で読む）。
"""

from __future__ import annotations

import argparse
import datetime
import os
import sys
from typing import Dict, List

# `python -P` / PYTHONSAFEPATH=1 だとスクリプトの位置が sys.path に入らず `doclint` を
# 見つけられない。**先頭ではなく末尾**に足す（先頭だと将来 tools/docs に標準モジュールと
# 同名のファイルを置いた瞬間にプロセス全体が壊れる）
sys.path.append(os.path.dirname(os.path.abspath(__file__)))

from doclint.checks import (Finding, check_adr_ledger, check_article_notation,  # noqa: E402
                            check_body, check_code_references, check_dated_switches,
                            check_docs_index, check_front_matter, check_law_abbreviations, check_link_label_targets, check_links, check_question_numbers,
                            check_section_references, check_superseded_links, check_updated_freshness,
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
    print("== current の行数（{} を除く） ==".format("・".join(REFERENCE_PREFIXES)))
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
    ap.add_argument("--today", metavar="YYYY-MM-DD",
                    help="日付で発効する切替を、この日を今日として検査する（発効日の先取り）")
    args = ap.parse_args()
    today = datetime.date.fromisoformat(args.today) if args.today else None

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
    check_link_label_targets(docs, findings)
    check_question_numbers(docs, findings)
    check_updated_freshness(docs, findings)
    check_updated_history(docs, findings)
    scanned_notation, ignored_notation = check_article_notation(docs, findings)
    remaining_switch, ignored_switch = check_dated_switches(docs, findings, today=today)
    scanned_abbrev, ignored_abbrev = check_law_abbreviations(docs, findings)

    errors = [f for f in findings if f[0] == SEV_ERROR]
    warns = [f for f in findings if f[0] == SEV_WARN]
    # 2 値のどちらでもない severity を黙って warn に吸い込ませない
    others = [f for f in findings if f[0] not in (SEV_ERROR, SEV_WARN)]
    for sev, rel, msg in sorted(findings, key=lambda f: (f[0] != SEV_ERROR, f[1], f[2])):
        print("{}\t{}\t{}".format(sev, rel, msg))

    print("")
    # superseded 宛リンクの数を必ず出す。0 に落ちたら「違反が無い」ではなく
    # 「配線が死んだ・免除が広がりすぎた」を疑う（黙って素通りする関門を作らないため）
    # 条番号の切替も件数を必ず出す。発効日前に 0 に落ちたら「切り替え済み」ではなく
    # 「配線が死んだ・印が広がった」を疑う（印で外した行も並べて出す理由）
    print("検査文書数: {} / error: {} / warn: {} / superseded 宛リンク: {} 件を検査 / "
          "条項の記法: {} 行を走査し {} 行を印で外した / "
          "条番号の切替: 旧の字面が {} 行（印で外した {} 行） / "
          "改正法の略称: {} 行を走査し {} 行を印で外した"
          .format(len(docs), len(errors), len(warns), seen_superseded_links,
                  scanned_notation, ignored_notation, remaining_switch, ignored_switch,
                  scanned_abbrev, ignored_abbrev))
    return 1 if errors or others else 0


if __name__ == "__main__":
    sys.exit(main())
