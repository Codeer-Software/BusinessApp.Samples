#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""凍結されたファイルの変更・削除・改名を、コミット前に拒む.

**適用済みのマイグレーションは 1 バイトも変えられない。** `migrate.ps1 -Apply` が SHA-256 で
照合し、変わっていれば「適用後に編集されている」として移行を拒む（ADR-0020）。
つまり**壊れたことに気づくのは、次に誰かが移行を流したとき**であり、そのときはもう `main` に入っている。

**なぜ要るか。** 2026-09-06 の改番で、道具が凍結された 10 本を書き換えた。
その道具は「追跡下のテキスト全部」を見ており、**凍結という概念を知らなかった**。
`lint_docs.py` は同じ理由でここを検査から外していたのに、その除外を写さなかった。
**既にある機械の除外リストは、次に作る道具の仕様である**（qa/02 のラウンド 40）。

**この関門が拾えないもの**（過大に表明しない）:

- **`git commit` を経ない経路。** rebase の衝突解決・cherry-pick・revert・am は
  git が `pre-commit` を呼ばない（マージだけは `pre-merge-commit` から委譲してある）
- **索引に載せていない変更。** 見るのは `git diff --cached` である
- **`core.hooksPath` を設定していない clone**（`tools/README` の有効化を 1 回だけ行う）
- `migrate.ps1` は改行を LF に正規化してからハッシュを取るので、**改行だけの差**は
  あちらでは咎められない。ここは `git` の状態文字を見るので咎める（安全側に外している）

**追加は通す。** 新しいマイグレーションを足すのは正常な操作である。
拒むのは**既にあるものの変更・削除・改名**だけ。
なお**追加そのものの規約**（ファイル名・版番号の重複・適用済みより小さい番号）は
`migrate.ps1` が見る。ここでは見ない。

**逃げ道は「使い捨ての印」1 つ**（`--no-verify` は `tools/claude/block_no_verify.py` が塞いでいる）。
`baseline/` を畳む掃除（ADR-0020 §4・`Designer/migrations/README.md`）でだけ使う:

    <git-dir>/allow-frozen-edit   … 理由を 1 行書いたファイルを置く

**環境変数にしない。** 主シェルの PowerShell にはインライン env 前置が無く、
`$env:X='1'` はセッションに残って**次のコミット以降も無言で関門を消す**。
印はこの道具が**読んだら消す**ので、1 コミットしか効かない。
"""

from __future__ import annotations

import os
import subprocess
import sys
from typing import List, Optional, Sequence, Tuple

REPO_ROOT = os.path.abspath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", ".."))

MARKER_NAME = "allow-frozen-edit"

# **凍結の正典。** ここが「1 バイトも変えてはいけない場所」の唯一の判定である。
# 他の道具は `is_frozen()` を引く（`tools/docs/doclint/model.py` が実例）。**写さない。**
#
# `Designer/migrations/README.md` は凍結ではない——手順を書いた文書であり、育ててよい。
# だから接尾辞で絞る。
FROZEN_PREFIXES: Tuple[str, ...] = ("Designer/migrations/",)
FROZEN_SUFFIXES: Tuple[str, ...] = (".sql",)
# 拡張子では拾えないが凍結されているもの（`baseline/` がどこまで畳んだかの記録）
FROZEN_EXACT: Tuple[str, ...] = ("Designer/migrations/baseline/VERSION",)

BASELINE_PREFIX = "Designer/migrations/baseline/"


def is_frozen(rel: str) -> bool:
    """`rel`（リポジトリ相対・`/` 区切り）が凍結されているか（純粋関数）。"""
    if rel in FROZEN_EXACT:
        return True
    return rel.startswith(FROZEN_PREFIXES) and rel.endswith(FROZEN_SUFFIXES)


def why(rel: str) -> str:
    """そのパスを凍結している**実際の**仕組みを返す（純粋関数）。

    `migrate.ps1` の `Get-MigrationFiles` は `-Recurse` を付けていないので
    **`baseline/` を読まない**。守っているのは同値テストのほうである。
    理由を一括で「チェックサム」と書くと、当たった人が `migrate.ps1 -Status` を見て迷う。
    """
    if rel.startswith(BASELINE_PREFIX):
        return "baseline は同値テスト（BusinessApp.Schema.Tests）の起点。掃除でだけ前進する"
    return "適用済みは migrate.ps1 -Apply が SHA-256 で照合し、変わっていると移行を拒む"


def parse_name_status(raw: str) -> List[Tuple[str, str]]:
    """`git diff --name-status -z` の生出力を `(状態, パス)` に開く（純粋関数）。

    **改名は「元の削除」＋「先の追加」に開く。** 元が凍結なら拒み、先は追加として通す
    （`docs/x.sql` を `Designer/migrations/0011_x.sql` へ改名して足すのは正常な操作）。
    **複製は元を変えない**ので、元は捨てて先だけを追加として返す。
    `-z` で読むのは、日本語のファイル名が `core.quotepath` で化けるため。
    """
    parts = [p for p in raw.split("\0") if p != ""]
    out: List[Tuple[str, str]] = []
    i = 0
    while i < len(parts):
        status = parts[i]
        if status.startswith("R"):
            out.append(("D", parts[i + 1]))     # 元は消える
            out.append(("A", parts[i + 2]))     # 先は生える
            i += 3
        elif status.startswith("C"):
            out.append(("A", parts[i + 2]))     # 元は無傷。先だけ
            i += 3
        else:
            out.append((status[0], parts[i + 1]))
            i += 2
    return out


def violations(changes: Sequence[Tuple[str, str]]) -> List[Tuple[str, str]]:
    """`(状態, パス)` の列から、拒むべきものだけを返す（純粋関数）。

    **`A`（追加）は通す。** それ以外で凍結されたパスに触れているものを拒む。
    """
    return [(s, rel) for s, rel in changes if s != "A" and is_frozen(rel)]


def decide(bad: Sequence[Tuple[str, str]], reason: Optional[str]) -> Tuple[int, List[str]]:
    """違反と印から、終了コードと印字する行を決める（純粋関数）。

    `reason` は印の中身。`None` は印が無いこと。**印は呼び手が消す**。
    """
    if not bad:
        return 0, []
    if reason is not None:
        lines = ["!! 使い捨ての印で凍結ファイルの変更を通しました（{} 件）".format(len(bad)),
                 "   理由: " + (reason.strip() or "（書かれていない）")]
        lines += ["   {} {}".format(s, rel) for s, rel in bad]
        lines.append("!! 印は消しました。次のコミットではまた止まります。")
        return 0, lines
    lines = ["凍結されたファイルを変更・削除・改名しています（{} 件）。".format(len(bad))]
    for s, rel in bad:
        lines.append("   {} {}   — {}".format(s, rel, why(rel)))
    lines += [
        "",
        "**旧い番号や古い記述がここに残るのは正しい。** 直さない。",
        "baseline を畳む掃除（Designer/migrations/README.md）でだけ、",
        "  <git-dir>/{} に理由を 1 行書いて置く".format(MARKER_NAME),
        "で通す（読んだら消える。1 コミットしか効かない）。",
        "--no-verify は使わない（tools/claude/block_no_verify.py が塞いでいる）。",
    ]
    return 1, lines


def _git(args: Sequence[str]) -> subprocess.CompletedProcess:
    """フックの作業ディレクトリのまま git を呼ぶ。

    **`cwd` を固定しない。** `core.hooksPath` は絶対パスなので、リンクした worktree では
    このファイル自身は本体クローンにある。`cwd` を `__file__` から導くと
    **別のリポジトリの索引を見て無言で緑になる**（`pre-commit` は既に
    `git rev-parse --show-toplevel` へ `cd` している）。
    """
    return subprocess.run(["git"] + list(args), capture_output=True)


def staged_changes() -> List[Tuple[str, str]]:
    r = _git(["diff", "--cached", "--name-status", "-z"])
    if r.returncode != 0:
        sys.stderr.write(r.stderr.decode("utf-8", "replace"))
        raise SystemExit(2)
    return parse_name_status(r.stdout.decode("utf-8"))


def take_marker() -> Optional[str]:
    """印があれば中身を返し、**その場で消す**（使い捨てにするため）。"""
    r = _git(["rev-parse", "--git-dir"])
    if r.returncode != 0:
        return None
    path = os.path.join(r.stdout.decode("utf-8").strip(), MARKER_NAME)
    if not os.path.isfile(path):
        return None
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        reason = f.read()
    os.remove(path)
    return reason


def selftest() -> int:
    """**中身を空にしても緑**にならないことを確かめる（コミット前フックで自己検査を持つ段はどれも同じ作法）。

    表明するのは 4 つの純粋関数すべてである。以前は `violations` と `is_frozen` しか
    見ておらず、**`-z` の解析を壊しても緑のまま通った**（自己レビューで見つけた）。
    """
    ng: List[str] = []

    # --- parse_name_status: ここを壊すと状態とパスがずれて、関門が丸ごと無効になる ---
    parse_cases = (
        ("変更 1 件", "M\0a.sql\0", [("M", "a.sql")]),
        ("2 件つづき", "M\0a.sql\0D\0b.sql\0", [("M", "a.sql"), ("D", "b.sql")]),
        ("改名は 3 要素で、元の削除と先の追加に開く",
         "R100\0src.sql\0dst.sql\0", [("D", "src.sql"), ("A", "dst.sql")]),
        ("複製は元を変えないので先だけ", "C100\0src.sql\0dst.sql\0", [("A", "dst.sql")]),
        ("改名と通常が混ざっても位置がずれない",
         "R090\0s.sql\0d.sql\0M\0x.sql\0",
         [("D", "s.sql"), ("A", "d.sql"), ("M", "x.sql")]),
        ("日本語のファイル名", "M\0docs/13_取引先設計.md\0", [("M", "docs/13_取引先設計.md")]),
        ("空", "", []),
    )
    for label, raw, want in parse_cases:
        got = parse_name_status(raw)
        if got != want:
            ng.append("parse_name_status: {}: 期待 {} / 実際 {}".format(label, want, got))

    # --- is_frozen ---
    for rel, want in (("Designer/migrations/0002_partner_identity.sql", True),
                      ("Designer/migrations/baseline/004_masters.sql", True),
                      ("Designer/migrations/baseline/VERSION", True),
                      ("Designer/migrations/README.md", False),
                      ("Designer/ddl/004_masters.sql", False),
                      ("docs/README.md", False)):
        if is_frozen(rel) != want:
            ng.append("is_frozen({}) が {} を返した".format(rel, not want))

    # --- violations: 免除には必ず対照を置く ---
    v_cases = (
        ("適用済みの変更", [("M", "Designer/migrations/0002_partner_identity.sql")], 1),
        ("適用済みの削除", [("D", "Designer/migrations/0002_partner_identity.sql")], 1),
        ("baseline の VERSION", [("M", "Designer/migrations/baseline/VERSION")], 1),
        ("新しいマイグレーションの追加は通す", [("A", "Designer/migrations/0010_new.sql")], 0),
        ("改名の先（＝追加）は通す", [("A", "Designer/migrations/0011_moved.sql")], 0),
        ("migrations の README は凍結ではない", [("M", "Designer/migrations/README.md")], 0),
        ("ddl の正典は凍結ではない", [("M", "Designer/ddl/004_masters.sql")], 0),
        ("混在しても凍結だけを拾う",
         [("M", "docs/README.md"), ("M", "Designer/migrations/baseline/VERSION")], 1),
    )
    for label, changes, want in v_cases:
        got = len(violations(changes))
        if got != want:
            ng.append("violations: {}: 期待 {} 件 / 実際 {} 件".format(label, want, got))

    # --- decide: 違反の有無 × 印の有無の 4 通り。ここを壊すと素通りする ---
    one = [("M", "Designer/migrations/baseline/VERSION")]
    for label, bad, reason, want_code in (
            ("違反なし・印なし", [], None, 0),
            ("違反なし・印あり", [], "掃除", 0),
            ("違反あり・印なし", one, None, 1),
            ("違反あり・印あり", one, "掃除", 0)):
        code, lines = decide(bad, reason)
        if code != want_code:
            ng.append("decide: {}: 期待 {} / 実際 {}".format(label, want_code, code))
        if want_code == 1 and not any(MARKER_NAME in l for l in lines):
            ng.append("decide: 拒むときに印の使い方を案内していない")
    # 理由が空でも「書かれていない」と分かること（黙って通す形にしない）
    if "書かれていない" not in "".join(decide(one, "  ")[1]):
        ng.append("decide: 理由が空の印を、空だと言わずに通している")

    # --- why: baseline と適用済みで守り手が違う ---
    if why("Designer/migrations/baseline/VERSION") == why("Designer/migrations/0002_x.sql"):
        ng.append("why: baseline と適用済みで理由を出し分けていない")

    # --- 逃げ道の使い方が、それを使う唯一の作業の文書に書かれているか ---
    # **この道具の docstring に書いてあることを自分で確かめても意味が無い**（同語反復）。
    # 掃除をする人が読むのは migrations の README である。
    readme = os.path.join(REPO_ROOT, "Designer", "migrations", "README.md")
    try:
        with open(readme, "r", encoding="utf-8") as f:
            text = f.read()
    except OSError as e:
        ng.append("migrations の README を読めない: {}".format(e))
    else:
        if MARKER_NAME not in text:
            ng.append("掃除の手順（Designer/migrations/README.md）に印の使い方が無い"
                      "——当たった人が「拒まれた。手順書に書いていない」で詰まる")

    for msg in ng:
        print("NG  " + msg)
    print("check_frozen: すべて期待どおり" if not ng
          else "check_frozen: {} 件が期待と違う".format(len(ng)))
    return 1 if ng else 0


def main() -> int:
    if "--selftest" in sys.argv:
        return selftest()
    bad = violations(staged_changes())
    code, lines = decide(bad, take_marker() if bad else None)
    for line in lines:
        print(line)
    return code


if __name__ == "__main__":
    sys.exit(main())
