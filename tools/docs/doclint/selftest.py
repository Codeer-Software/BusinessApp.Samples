#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""doclint.selftest — 関門そのものを検査する（`lint_docs.py --selftest`）.

**中身を空にしても緑**という状態を作らないための検査である。見るのは 3 つ。

  1. 判定の純粋部分（`updated_violation` / `body_of`）が期待どおり鳴るか
  2. **わざと壊した入力**で `check_superseded_links` が鳴り、免除の形では鳴らないか。
     件数だけでなく**指摘文の中身**まで表明する（error を warn に格下げしても件数は変わらない）
  3. 定義した検査が全部 `ALL_CHECKS` に載り、`main` から**正しい引数で**呼ばれているか
"""

from __future__ import annotations

import os
import re
from typing import Dict, List, Tuple

from . import checks
from .checks import (ALL_CHECKS, Finding, check_superseded_links, successor_of,
                     updated_violation)
from .model import ADR_LEDGER, Doc, INLINE_IGNORE, SEV_ERROR, body_of, load_docs, parse_front_matter

CHECKS_SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "checks.py")
CLI_SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "lint_docs.py")

# main が検査を呼ぶときの**引数まで含めた**呼び出し。名前だけの包含だと
# `check_superseded_links(d, {}, findings)` のような配線の壊れ方が通ってしまう
REQUIRED_CALLS = ("check_superseded_links(d, docs_by_rel, findings)",)


def _fake(rel: str, meta: Dict[str, str], body: List[str]) -> Doc:
    lines = ["---"] + ["{}: {}".format(k, v) for k, v in meta.items()] + ["---"] + body
    parsed, start = parse_front_matter(lines)
    return Doc(rel, lines, parsed, start)


def _check_updated_violation() -> List[str]:
    ng = []
    cases = [
        # (old_body, new_body, updated, today, 鳴るべきか)
        (["a"], ["a"], "2026-08-01", "2026-08-27", False),  # 本文が同じ＝フロントマターだけの変更
        (["a"], ["b"], "2026-08-27", "2026-08-27", False),  # 本文を変えて今日にした
        (["a"], ["b"], "2026-08-26", "2026-08-27", True),   # 本文を変えたのに据え置き
        (["a"], ["b"], "", "2026-08-27", True),             # updated が無い
        (None, ["a"], "2026-08-26", "2026-08-27", True),    # HEAD に無いパス（新規・改名）
        (None, ["a"], "2026-08-27", "2026-08-27", False),
    ]
    for old_body, new_body, updated, today, should in cases:
        got = updated_violation("x.md", old_body, new_body, updated, today) is not None
        if got != should:
            ng.append("updated_violation: 期待 {} / 実際 {}: {}"
                      .format(should, got, (old_body, new_body, updated)))

    fm = ["---", "title: x", "updated: 2026-08-27", "---"]
    if body_of(chr(10).join(fm + ["本文"])) != ["本文"]:
        ng.append("body_of: フロントマターを落とせていない")
    if body_of("フロントマター無し") != ["フロントマター無し"]:
        ng.append("body_of: フロントマターが無い文書を落としてしまった")
    return ng


def _check_superseded_links() -> List[str]:
    """「足したら一度わざと壊して鳴ることを確かめる」（ADR-0021 §4）を関門の中に固定してある。"""
    ng = []
    dead = _fake("docs/decisions/0019-old.md",
                 {"status": "superseded",
                  # related[0] が superseded の実データがあるので、その形で入れる
                  "related": "[0006-older.md, 0029-new.md]"}, ["中身"])
    older = _fake("docs/decisions/0006-older.md",
                  {"status": "superseded", "related": "[0019-old.md]"}, ["中身"])
    gone = _fake("docs/decisions/0018-hist.md",
                 {"status": "historical", "related": "[0029-new.md]"}, ["中身"])
    live = _fake("docs/09_other.md", {"status": "current"}, ["本文"])
    new = _fake("docs/decisions/0029-new.md", {"status": "current"}, ["本文"])
    far = _fake("docs/08_dead.md", {"status": "superseded", "related": "[09_other.md]"}, ["中身"])
    by_rel = {d.rel: d for d in (dead, older, gone, live, new, far)}
    to_dead, to_live = "[旧](decisions/0019-old.md)", "[今](09_other.md)"
    cur = {"status": "current"}
    cases = [
        # (rel, meta, body, 鳴るべき件数)
        ("docs/10_x.md", cur, [to_dead], 1),                                  # わざと壊した入力
        ("docs/10_x.md", cur, [to_dead, to_dead], 2),                         # 別の行なら別に鳴る
        ("docs/10_x.md", cur, [to_dead + " " + to_dead], 2),                  # 同じ行に 2 本でも鳴る
        # 免除は行単位である。同じ行の**無関係なリンクまで**免除されるのが現在の仕様
        ("docs/10_x.md", cur, [to_dead + " " + to_dead + " " + INLINE_IGNORE], 0),
        ("docs/10_x.md", cur, [to_live], 0),
        ("docs/10_x.md", cur, ["0019-old.md は既に覆されている"], 0),          # 散文は咎めない
        ("docs/10_x.md", cur, ["[外](https://example.com/0019-old.md)"], 0),
        ("docs/10_x.md", cur, ["[未](decisions/0019-nowhere.md)"], 0),        # 実在しない
        ("docs/10_x.md", cur, ["[印](decisions/0018-hist.md)"], 0),           # historical は対象外
        ("docs/10_x.md", cur, ["[節](decisions/0019-old.md#2-決定)"], 1),     # アンカー付き
        ("docs/10_x.md", cur, ["[空]( decisions/0019-old.md )"], 1),          # 前後の空白を落とす
        ("docs/decisions/0030-y.md", cur, ["[上](../08_dead.md)"], 1),        # `../` 起点
        ("docs/10_x.md", cur, ["```", to_dead, "```"], 0),                    # コードフェンスの中
        ("docs/10_x.md", {"status": "superseded", "related": "[09_other.md]"}, [to_dead], 0),
        ("docs/10_x.md", {"status": "historical", "related": "[09_other.md]"}, [to_dead], 0),
        # `growth: append` は免除しない（2026-08-28 に外した。理由は関数の docstring）
        ("docs/10_x.md", {"status": "current", "growth": "append"}, [to_dead], 1),
        # フロントマターは対象外。**リンクの形をした値**を置いて、本文だけを見ていることを表明する
        ("docs/10_x.md", {"status": "current", "note": to_dead}, ["本文"], 0),
        # 後継は前身を語ってよい
        ("docs/decisions/0029-new.md", {"status": "current", "supersedes": "[0019-old.md]"},
         ["[旧](0019-old.md)"], 0),
        # ADR 台帳は役割で免除する。**実データと同じく `growth: append` を付けた形**で表明する
        (ADR_LEDGER, {"status": "current", "growth": "append"}, ["[旧](0019-old.md)"], 0),
    ]
    for rel, meta, body, want in cases:
        got: List[Finding] = []
        check_superseded_links(_fake(rel, meta, body), by_rel, got)
        if len(got) != want:
            ng.append("check_superseded_links: 期待 {} 件 / 実際 {} 件: {} {}"
                      .format(want, len(got), rel, body))

    # 件数だけでなく**中身**を表明する。error を warn に格下げしても件数は変わらないため
    probe = _fake("docs/10_x.md", cur, [to_dead])
    got = []
    seen = check_superseded_links(probe, by_rel, got)
    want_line = "{}行目".format(probe.body_start + 1)
    for label, ok in (
        ("severity が error", got and got[0][0] == SEV_ERROR),
        ("指摘先が違反した文書", got and got[0][1] == "docs/10_x.md"),
        ("行番号が本文の実行番号", got and want_line in got[0][2]),
        ("リンク先をリポジトリ相対で出す", got and "docs/decisions/0019-old.md" in got[0][2]),
        # related[0] は superseded（0006）。**current な 0029 を案内する**こと
        ("後継は current を選ぶ", got and "docs/decisions/0029-new.md" in got[0][2]),
        ("superseded な related を案内しない", got and "0006-older.md" not in got[0][2]),
        ("直し方を書く", got and INLINE_IGNORE in got[0][2]),
        ("見た件数を返す", seen == 1),
    ):
        if not ok:
            ng.append("check_superseded_links: {}: {}".format(label, got))
    if successor_of(older, by_rel) is not None:
        ng.append("successor_of: current な後継が無いのに何かを返した")
    return ng


def _check_real_data() -> List[str]:
    """実データで一度通す。

    `resolve()` の結果が `load_docs()` のキーと噛み合っているかは、偽 `Doc` では分からない
    （qa/03 L-15 の型）。**0 件は「違反が無い」ではなく「配線が死んだ」を疑う数字**である。
    """
    docs, _ = load_docs()
    by_rel = {d.rel: d for d in docs}
    sink: List[Finding] = []
    seen = sum(check_superseded_links(d, by_rel, sink) for d in docs)
    if seen < 1:
        return ["check_superseded_links: 実データで superseded 宛リンクを 1 本も見ていない"
                "（免除が広がりすぎたか、パスの解決が噛み合っていない。**0 は緑ではない**）"]
    return []


def _check_wiring() -> List[str]:
    """定義・登録・呼び出しの 3 つが揃っているかをソースで突き合わせる。"""
    ng = []
    checks_src = open(CHECKS_SRC, "r", encoding="utf-8").read()
    cli_src = open(CLI_SRC, "r", encoding="utf-8").read()
    # 行頭の定義から後ろだけを見る。`cli_src.index("def main()")` だと
    # この表明自身の文字列リテラルに当たり、表明が自分で自分を満たしてしまう
    main_at = re.search(r"^def main\(\)", cli_src, re.M)
    if main_at is None:
        return ["lint_docs.py に def main() が見つからない"]
    main_src = cli_src[main_at.start():]

    for call in REQUIRED_CALLS:
        if call not in main_src:
            ng.append("main の配線が違う。次の形で呼ぶこと: {}".format(call))
    defined = set(re.findall(r"^def (check_[A-Za-z0-9_]+)\(", checks_src, re.M))
    for name in sorted(defined - set(ALL_CHECKS)):
        ng.append("{} が ALL_CHECKS に載っていない（足した検査は必ず載せる）".format(name))
    for name in ALL_CHECKS:
        if not hasattr(checks, name):
            ng.append("{} が checks.py に定義されていない".format(name))
        if name + "(" not in main_src:
            ng.append("{} が main から呼ばれていない".format(name))
    return ng


def selftest() -> int:
    ng: List[str] = []
    for part in (_check_updated_violation, _check_superseded_links,
                 _check_real_data, _check_wiring):
        ng.extend(part())
    for msg in ng:
        print("NG  " + msg)
    print("lint_docs: すべて期待どおり" if not ng
          else "lint_docs: {} 件が期待と違う".format(len(ng)))
    return 1 if ng else 0
