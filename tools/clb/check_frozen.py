#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""凍結されたファイルの変更・削除・改名を、コミット前に拒む.

**適用済みのマイグレーションは 1 バイトも変えられない。** `migrate.ps1` が SHA-256 で
照合し、変わっていれば「適用後に編集されている」として移行を拒む（ADR-0020）。
つまり**壊れたことに気づくのは、次に誰かが `migrate.ps1` を流したとき**であり、
そのときにはもう `main` に入っている。**コミットの時点で止めるのがこの関門の仕事。**

**なぜ要るか。** 2026-09-06 の改番で、実際にここを 10 本書き換えた。
書き換えの道具は「追跡下のテキスト全部」を見ており、**凍結という概念を知らなかった**。
`lint_docs.py` は同じ理由でここを検査から外していたのに、その除外を写さなかった。
**既にある機械の除外リストは、次に作る道具の仕様である**（qa/02 のラウンド 40）。

**追加（A）は通す。** 新しいマイグレーションを足すのは正常な操作である。
拒むのは**既にあるものの変更・削除・改名**だけ。

**逃げ道は環境変数 1 つ**（`--no-verify` は `tools/claude/block_no_verify.py` が塞いでいる）。
`baseline/` を畳む掃除（ADR-0020 §4・`Designer/migrations/README.md`）でだけ使う:

    ALLOW_FROZEN_EDIT=1 git commit ...

使うと大きく印字する。**黙って通る経路を作らない。**
"""

from __future__ import annotations

import os
import subprocess
import sys
from typing import Iterable, List, Sequence, Tuple

REPO_ROOT = os.path.abspath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", ".."))

ESCAPE_ENV = "ALLOW_FROZEN_EDIT"

# **凍結の正典。** ここが「1 バイトも変えてはいけない場所」の唯一の一覧である。
# 他の道具はここを引く（`tools/docs/doclint/model.py` が実例）。**写さない。**
#
# `Designer/migrations/README.md` は凍結ではない——手順を書いた文書であり、育ててよい。
# だから接尾辞で絞る。
FROZEN_PREFIXES: Tuple[str, ...] = ("Designer/migrations/",)
FROZEN_SUFFIXES: Tuple[str, ...] = (".sql",)
# 拡張子では拾えないが凍結されているもの（`baseline/` がどこまで畳んだかの記録）
FROZEN_EXACT: Tuple[str, ...] = ("Designer/migrations/baseline/VERSION",)

WHY = ("適用済みのマイグレーションと baseline は、変えると migrate.ps1 の"
       "チェックサム照合に落ちて移行できなくなる（ADR-0020）")


def is_frozen(rel: str) -> bool:
    """`rel`（リポジトリ相対・`/` 区切り）が凍結されているか（純粋関数）。"""
    if rel in FROZEN_EXACT:
        return True
    return rel.startswith(FROZEN_PREFIXES) and rel.endswith(FROZEN_SUFFIXES)


def violations(changes: Iterable[Tuple[str, str]]) -> List[Tuple[str, str]]:
    """`(状態, パス)` の列から、拒むべきものだけを返す（純粋関数）。

    状態は `git diff --name-status` の 1 文字。**`A`（追加）は通す**——
    新しいマイグレーションを足すのは正常な操作である。
    改名は「元のパスが凍結されているか」で見る（改名も禁止。README §「適用済み」）。
    """
    out = []
    for status, rel in changes:
        if status.startswith("A"):
            continue
        if is_frozen(rel):
            out.append((status[0], rel))
    return out


def staged_changes() -> List[Tuple[str, str]]:
    """インデックスに載っている変更を `(状態, パス)` で返す。

    改名は**元のパスと先のパスの両方**を返す（元が凍結なら拒む・先が凍結でも拒む）。
    `-z` で読むのは、日本語のファイル名が quotepath で化けるため。
    """
    raw = subprocess.run(
        ["git", "diff", "--cached", "--name-status", "-z"],
        cwd=REPO_ROOT, capture_output=True, check=True).stdout.decode("utf-8")
    parts = [p for p in raw.split("\0") if p != ""]
    out: List[Tuple[str, str]] = []
    i = 0
    while i < len(parts):
        status = parts[i]
        if status.startswith(("R", "C")):        # 改名・複製は「元」「先」の 2 つが続く
            out.append((status, parts[i + 1]))
            out.append(("A" if status.startswith("C") else status, parts[i + 2]))
            i += 3
        else:
            out.append((status, parts[i + 1]))
            i += 2
    return out


def selftest() -> int:
    """**中身を空にしても緑**にならないことを確かめる（1/8・2/8・3/8 と同じ作法）。"""
    ng: List[str] = []
    cases: Sequence[Tuple[str, Sequence[Tuple[str, str]], int]] = (
        ("適用済みの変更は拒む", [("M", "Designer/migrations/0002_partner_identity.sql")], 1),
        ("適用済みの削除は拒む", [("D", "Designer/migrations/0002_partner_identity.sql")], 1),
        ("baseline の変更は拒む", [("M", "Designer/migrations/baseline/004_masters.sql")], 1),
        ("baseline の VERSION も拒む", [("M", "Designer/migrations/baseline/VERSION")], 1),
        ("改名も拒む", [("R100", "Designer/migrations/0002_partner_identity.sql")], 1),
        # --- 通すもの。対照が無いと「全部拒む」実装でも緑になる ---
        ("新しいマイグレーションの追加は通す",
         [("A", "Designer/migrations/0010_new.sql")], 0),
        ("migrations の README は凍結ではない",
         [("M", "Designer/migrations/README.md")], 0),
        ("ddl の正典は凍結ではない", [("M", "Designer/ddl/004_masters.sql")], 0),
        ("無関係のファイル", [("M", "docs/README.md")], 0),
        ("混在しても凍結だけを拾う",
         [("M", "docs/README.md"), ("M", "Designer/migrations/baseline/VERSION")], 1),
    )
    for label, changes, want in cases:
        got = len(violations(changes))
        if got != want:
            ng.append("violations: {}: 期待 {} 件 / 実際 {} 件".format(label, want, got))

    # `is_frozen` を直に表明する（`violations` 経由だけだと A の分岐で隠れる）
    for rel, want in (("Designer/migrations/0002_partner_identity.sql", True),
                      ("Designer/migrations/baseline/VERSION", True),
                      ("Designer/migrations/README.md", False),
                      ("Designer/ddl/004_masters.sql", False)):
        if is_frozen(rel) != want:
            ng.append("is_frozen({}) が {} を返した".format(rel, not want))

    # 逃げ道の名前が docstring と実体で食い違わないこと（README がこの名前を書く）
    if ESCAPE_ENV not in __doc__:
        ng.append("逃げ道の環境変数名が docstring に書かれていない")

    for msg in ng:
        print("NG  " + msg)
    print("check_frozen: すべて期待どおり" if not ng
          else "check_frozen: {} 件が期待と違う".format(len(ng)))
    return 1 if ng else 0


def main() -> int:
    if "--selftest" in sys.argv:
        return selftest()

    bad = violations(staged_changes())
    if not bad:
        return 0

    if os.environ.get(ESCAPE_ENV) == "1":
        print("!! {}=1 で凍結ファイルの変更を通しました（{} 件）".format(ESCAPE_ENV, len(bad)))
        for status, rel in bad:
            print("   {} {}".format(status, rel))
        print("!! baseline を畳む掃除（ADR-0020 §4）以外でこれを使ったなら、戻すこと。")
        return 0

    print("凍結されたファイルを変更・削除・改名しています（{} 件）。".format(len(bad)))
    for status, rel in bad:
        print("   {} {}".format(status, rel))
    print("")
    print(WHY + "。")
    print("**旧い番号や古い記述がここに残るのは正しい。** 直さない。")
    print("baseline を畳む掃除（Designer/migrations/README.md）でだけ、")
    print("  {}=1 git commit ...".format(ESCAPE_ENV))
    print("で通す。--no-verify は使わない（tools/claude/block_no_verify.py が塞いでいる）。")
    return 1


if __name__ == "__main__":
    sys.exit(main())
