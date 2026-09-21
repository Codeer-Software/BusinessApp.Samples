#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""normalize_eol.py — 追跡ファイルの作業コピーの改行を LF に保つ（PostToolUse フック ＋ 検査）.

**リポジトリに入る中身は既に LF である**——`.gitattributes` の `* text=auto eol=lf` が
コミットのときに正規化するため。**ずれるのは作業コピーだけ**で、そこを読む道具が環境ごとに違う結果を出す。
Windows では `Write` ツールと python の `write_text`（既定の `newline=None`）が **CRLF** を書く。

**何を LF にすべきかは `.gitattributes` が決める。この道具は拡張子の一覧を持たない**
——一覧は必ず腐るし、`.gitattributes` と二重管理になる。判定は git に任せる
（`git ls-files --eol` の `attr/` と、1 ファイルなら `git check-attr eol`）。

  python tools/claude/normalize_eol.py --check     # 追跡ファイルの作業コピーに CRLF/CRLF 混在が無いか
  python tools/claude/normalize_eol.py --fix       # 見つかったものを直す
  python tools/claude/normalize_eol.py --selftest  # 判定そのものを検査する
  （標準入力に JSON を流すと PostToolUse フックとして動く）

**フックは「直す」側で、「止める」側ではない。** 判断に迷えば何もしないで終わる。
**止めるのは `--check` の段**で、そちらが最後の砦になる。

**限界**（過大に表明しない）。

- **`--check` が見るのは追跡ファイルだけ**である（`git ls-files` の一覧）。
  **追跡外は PostToolUse フックの側でしか直らない**し、そちらは `Bash` / `PowerShell` が
  書いたものを見ない（何を書いたかがコマンド文字列からは決まらないため）
- **`protected_paths.json` は読まない。** 保護対象を上書きから守るのは `guard_delete.py` の仕事で、
  この道具は**改行だけを置き換える**（中身の意味を変えない）ので、同じ守りを二重に持たない
"""

from __future__ import annotations

import io
import json
import os
import subprocess
import sys
import tempfile
from typing import List, NamedTuple, Optional

sys.stdout.reconfigure(encoding="utf-8")  # Windows の既定は CP932 で、理由文が化ける

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

# ファイルのパスを持つ道具。**`NotebookEdit` は入れない**——入力のキーが `notebook_path` なので
# 当たらない（死んだ分岐を置かない。docs/20 §5）
FILE_TOOLS = ("Write", "Edit", "MultiEdit")

CRLF = b"\r\n"
GIT = ["git", "-c", "core.quotepath=false"]
# `git ls-files --eol` が作業コピー側に返す値のうち、**LF でないもの**。
# `-text`（binary）と `none`（改行を含まない）と `lf` は正しい。**`mixed` を落とさない**——
# CRLF と LF が混ざった状態こそ「道具が環境ごとに違う結果を出す」当のものである
NOT_LF = ("crlf", "mixed")
# 追跡ファイルの下限。**0 件を「すべて LF」と報告しない**ための歯止めで、
# 実測は 589 件（2026-09-15）。**減ったら検査ではなく読み取りを疑う**
TRACKED_FLOOR = 100


class Problem(NamedTuple):
    rel: str
    worktree: str


def _git(args: List[str]) -> Optional[str]:
    try:
        out = subprocess.run(GIT + args, cwd=REPO_ROOT, stdout=subprocess.PIPE,
                             stderr=subprocess.PIPE, check=True)
    except (OSError, subprocess.CalledProcessError):
        return None
    return out.stdout.decode("utf-8", errors="replace")


def parse_eol(raw: str) -> List[Problem]:
    """`git ls-files --eol` の出力から、**LF を求められているのに LF でない**ものを返す（純粋関数）。

    1 行の形は `i/<index> w/<作業コピー> attr/<属性>\\t<パス>`。
    **属性で絞る**——`.gitattributes` が別の改行を要求する種類（`eol=crlf`）を足した日に、
    **直せない赤が出続けてコミットが通らなくなる**のを防ぐ。
    """
    out: List[Problem] = []
    for line in raw.splitlines():
        at = line.find("attr/")
        if at < 0 or "\t" not in line[at:]:
            continue
        attr, _, path = line[at:].partition("\t")
        head = line[:at]
        worktree = ""
        for token in head.replace("\t", " ").split():
            if token.startswith("w/"):
                worktree = token[2:]
        if worktree not in NOT_LF:
            continue
        if "eol=lf" not in attr:
            continue  # 別の改行を求められているファイル。ここでは咎めない
        out.append(Problem(path, worktree))
    return sorted(out)


def eol_problems() -> Optional[List[Problem]]:
    """実データで `parse_eol` を回す。git が読めなければ None（**0 件ではない**）。"""
    raw = _git(["ls-files", "--eol"])
    return None if raw is None else parse_eol(raw)


def wants_lf(path: str) -> bool:
    """`.gitattributes` がこのパスに LF を要求しているか（追跡外でも効く）。

    **限界: いまの `.gitattributes` は `* text=auto eol=lf` なので、これは何に対しても真を返す。**
    つまりこの関数が外せるのは「**別の改行を明示的に要求されたファイル**」だけで、
    **binary を外しているのは下の NUL の検査 1 本である**（`text=auto` の binary 判定は
    `check-attr eol` には出ない）。git が答えられないときは False（触らない側に倒す）。
    """
    rel = os.path.relpath(path, REPO_ROOT).replace(os.sep, "/")
    out = _git(["check-attr", "eol", "--", rel])
    return bool(out) and out.strip().endswith(": lf")


def normalize(path: str) -> bool:
    """CRLF があれば LF に直す。直したら True。

    **読めない・書けない・中身が binary のときは何もしない**（例外を投げない）——
    直せなかったことより、道具を止めることのほうが害が大きい。
    """
    try:
        with io.open(path, "rb") as f:
            data = f.read()
    except OSError:
        return False
    # **NUL を含むものは触らない。** git 自身の binary の見分け方に合わせる
    if CRLF not in data or b"\x00" in data:
        return False
    if not wants_lf(path):
        return False
    try:
        with io.open(path, "wb") as f:
            f.write(data.replace(CRLF, b"\n"))
    except OSError:
        return False
    return True


def target_path(payload: dict) -> Optional[str]:
    """フック入力から、見てよいファイルの絶対パスを返す。判断が付かなければ None。"""
    if payload.get("tool_name") not in FILE_TOOLS:
        return None
    raw = (payload.get("tool_input") or {}).get("file_path")
    if not raw or not isinstance(raw, str):
        return None
    path = os.path.abspath(os.path.join(payload.get("cwd") or REPO_ROOT, raw))
    try:
        # **リポジトリの外は触らない**（スクラッチパッド・一時ディレクトリ）。
        # 別ドライブだと `commonpath` は例外を投げるので、そこも「触らない」に倒す
        if os.path.commonpath([path, REPO_ROOT]) != REPO_ROOT:
            return None
    except ValueError:
        return None
    rel = os.path.relpath(path, REPO_ROOT).replace(os.sep, "/")
    if rel == ".git" or rel.startswith(".git/"):
        return None  # git の内部は触らない
    return path if os.path.isfile(path) else None


def run_check(fix: bool) -> int:
    tracked = _git(["ls-files"])
    if tracked is not None and len(tracked.splitlines()) < TRACKED_FLOOR:
        print("normalize_eol: 追跡ファイルが {} 件しかありません（下限 {}）。"
              "**0 件は緑ではありません**——読み取りが死んでいないか確かめてください。"
              .format(len(tracked.splitlines()), TRACKED_FLOOR))
        return 2
    problems = eol_problems()
    if problems is None:
        print("normalize_eol: git を読めませんでした。**0 件ではありません**——判定していません。")
        return 2
    if not problems:
        print("normalize_eol: 追跡ファイルの作業コピーの改行はすべて LF")
        return 0
    if not fix:
        for p in problems:
            print("error\t{}\t作業コピーの改行が {} です"
                  "（`python tools/claude/normalize_eol.py --fix` で直せます）".format(p.rel, p.worktree))
        print("normalize_eol: LF でないものが {} 件残っています".format(len(problems)))
        return 1
    rest = []
    for p in problems:
        if normalize(os.path.join(REPO_ROOT, p.rel)):
            print("normalize_eol: 直しました: {}".format(p.rel))
        else:
            rest.append(p.rel)
    for rel in rest:
        print("normalize_eol: 直せませんでした: {}".format(rel))
    return 1 if rest else 0


def run_hook() -> int:
    try:
        payload = json.load(sys.stdin)
    except (ValueError, OSError):
        return 0
    if not isinstance(payload, dict):
        return 0
    path = target_path(payload)
    if path and normalize(path):
        print("normalize_eol: 改行を LF に直しました: {}"
              .format(os.path.relpath(path, REPO_ROOT).replace(os.sep, "/")))
    return 0


def main() -> int:
    if "--selftest" in sys.argv:
        return _selftest()
    if "--check" in sys.argv:
        return run_check(fix=False)
    if "--fix" in sys.argv:
        return run_check(fix=True)
    if sys.stdin.isatty():
        # 素で打った人を待たせない（フックとしては標準入力から JSON が来る）
        print(__doc__.strip().splitlines()[0])
        print("使い方: --check / --fix / --selftest（フックとしては標準入力に JSON）")
        return 0
    return run_hook()


# --- 自己検査 ---------------------------------------------------------------

_SAMPLE = (
    "i/lf\tw/lf\tattr/text=auto eol=lf \tdocs/README.md\n"
    "i/lf\tw/crlf\tattr/text=auto eol=lf \tDesigner/.claude/settings.json\n"
    "i/lf\tw/mixed\tattr/text=auto eol=lf \tDesigner/ddl/085.sql\n"
    "i/-text\tw/-text\tattr/text=auto eol=lf \tassets/logo.png\n"
    "i/none\tw/none\tattr/text=auto eol=lf \tVERSION\n"
    "i/crlf\tw/crlf\tattr/text eol=crlf \ttools/win/run.bat\n"
    "i/lf\tw/crlf\tattr/ \tvendor/x.txt\n"
)


def _selftest() -> int:
    """**壊したら赤くなること**まで見る。緑は「いま鳴らない」しか言っていない。"""
    ng = []

    # ① 出力の解析。**`mixed` を落とさない**／**別の改行を求められた行を咎めない**
    got = parse_eol(_SAMPLE)
    want = [Problem("Designer/.claude/settings.json", "crlf"),
            Problem("Designer/ddl/085.sql", "mixed")]
    if got != want:
        ng.append("parse_eol: {} のはずが {}".format(want, got))
    if parse_eol("") != []:
        ng.append("parse_eol: 空の入力で空を返していない")
    if parse_eol("こわれた行\n") != []:
        ng.append("parse_eol: 形の違う行で落ちるか拾うかしている")

    # ② **検査の赤を実際に見る**（`run_check` が通る経路。件数ではなく戻り値で）
    original = globals()["eol_problems"]
    try:
        globals()["eol_problems"] = lambda: []
        if run_check(fix=False) != 0:
            ng.append("run_check: 0 件なのに 0 以外で終わる")
        globals()["eol_problems"] = lambda: [Problem("x.md", "crlf")]
        if run_check(fix=False) != 1:
            ng.append("run_check: CRLF 1 件で赤にならない")
        globals()["eol_problems"] = lambda: [Problem("x.md", "mixed")]
        if run_check(fix=False) != 1:
            ng.append("run_check: mixed 1 件で赤にならない")
        globals()["eol_problems"] = lambda: None
        if run_check(fix=False) != 2:
            ng.append("run_check: git を読めないときに 2 で終わらない（0 件と区別が付かない）")
    finally:
        globals()["eol_problems"] = original

    # ③ 書き換えの判定。検体はリポジトリ内に 1 つだけ作って消す
    probe = os.path.join(REPO_ROOT, "tools", "claude", "_eol_probe.cs")
    try:
        io.open(probe, "wb").write(b"a\r\nb\r\n")
        if not normalize(probe) or io.open(probe, "rb").read() != b"a\nb\n":
            ng.append("normalize: LF を求めるファイルを直せていない")
        if normalize(probe):
            ng.append("normalize: LF のファイルを書き換えている（毎回差分が出る）")
        io.open(probe, "wb").write(b"a\r\n\x00b")
        if normalize(probe) or io.open(probe, "rb").read() != b"a\r\n\x00b":
            ng.append("normalize: NUL を含むファイルを書き換えている")
    finally:
        if os.path.exists(probe):
            os.remove(probe)

    # ④ 対象の切り分け。**リポジトリの外は一時ディレクトリで試す**（他所にゴミを置かない）
    with tempfile.TemporaryDirectory(prefix="eol_") as tmp:
        io.open(os.path.join(tmp, "x.cs"), "wb").write(b"a\r\n")
        cases = [
            ({"tool_name": "Write", "cwd": REPO_ROOT,
              "tool_input": {"file_path": "tools/claude/normalize_eol.py"}}, True),
            ({"tool_name": "Edit", "cwd": REPO_ROOT,
              "tool_input": {"file_path": "tools/claude/normalize_eol.py"}}, True),
            ({"tool_name": "Write", "cwd": tmp, "tool_input": {"file_path": "x.cs"}}, False),
            ({"tool_name": "Bash", "cwd": REPO_ROOT,
              "tool_input": {"command": "echo hi"}}, False),
            ({"tool_name": "NotebookEdit", "cwd": REPO_ROOT,
              "tool_input": {"notebook_path": "x.ipynb"}}, False),
            ({"tool_name": "Write", "cwd": REPO_ROOT,
              "tool_input": {"file_path": "tools/claude/_no_such_file.cs"}}, False),
            ({"tool_name": "Write", "cwd": REPO_ROOT, "tool_input": {}}, False),
            ({"tool_name": "Write"}, False),
        ]
        for payload, want_hit in cases:
            got_hit = target_path(payload) is not None
            if got_hit != want_hit:
                ng.append("target_path: {} は {} のはずが {}"
                          .format(payload.get("tool_input"), want_hit, got_hit))

    # ⑤ **実データで 1 度通す。** 読み取りが死んでいないか（0 件は緑ではない）
    if eol_problems() is None:
        ng.append("eol_problems: git を読めない（**None は 0 件ではない**）")
    tracked = _git(["ls-files"])
    if not tracked or len(tracked.splitlines()) < TRACKED_FLOOR:
        ng.append("追跡ファイルが下限未満（読み取りが死んでいないか）")

    for m in ng:
        print("NG  " + m)
    print("normalize_eol: すべて期待どおり" if not ng
          else "normalize_eol: {} 件が期待と違う".format(len(ng)))
    return 1 if ng else 0


if __name__ == "__main__":
    sys.exit(main())
