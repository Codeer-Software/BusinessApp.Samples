#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""guard_delete.py — Claude Code の PreToolUse フック。削除の当たり先を絞る。

開発者の指示（2026-08-26）:
  「Git 追跡外を deny に含め、スクラッチパッド下は allow する」

なぜ許可リストではなくフックか
------------------------------
Claude Code の Bash 権限は**コマンド文字列の前方一致**で判定する。
`Bash(rm -rf <スクラッチパッド>/*)` のような許可を書いても、`cd` を挟む・変数展開する・
`find -exec` を使う、といった書き方は同じ規則で捕まらない。
**パスで許可を絞るのは事故防止にはなっても、境界の保証にはならない。**
フックならコマンド文字列全体を見られるので、少なくとも「保護対象の名前に触れる削除」を
書き方によらず止められる。

守りたいもの（**Git で戻せないもの**）
--------------------------------------
リポジトリの追跡ファイルは git が完全な復元手段なので、消えても戻せる。
本当に失われるのは追跡外のものだけである。

  LocalData/                       稼働 DB・デプロイ zip（.gitignore）
  Designer/LocalEnvironment.md     デザイナ exe のパス等（Git 追跡外）
  .claude/settings.local.json      マシン固有の許可とフック（Git 追跡外）
  .git/                            履歴そのもの

限界（承知のうえで残す）
------------------------
判定はコマンド文字列への正規表現であり、シェルを解釈しない。
変数に入れてから消す・パスを分割して組み立てる、といった書き方はすり抜ける。
**これは「うっかり」を止める装置であって、悪意を止める装置ではない。**
すり抜けた先の被害を小さくするのは、追跡外のものを増やさない設計のほうである。

標準入力: Claude Code のフック入力 JSON（tool_input.command を見る）。
標準出力: 拒否するとき deny、スクラッチパッド限定の削除のとき allow。それ以外は何も出さない
          （＝通常の確認に落ちる）。
"""

import json
import re
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8")  # Windows の既定は CP932 で、理由文が化ける
except Exception:
    pass

# 削除にあたる操作。Bash と PowerShell の両方を見る。
DELETION = re.compile(
    r"(^|[\s;&|(])"
    r"(rm|rmdir|del|erase|unlink"
    r"|Remove-Item|ri|rd|Clear-Content|Clear-Item"
    r"|git\s+clean)"
    r"([\s;&|)]|$)",
    re.IGNORECASE,
)

# `find ... -delete` と `find ... -exec rm` は上の語形に当たらないので別に見る。
FIND_DELETE = re.compile(r"\bfind\b.*(-delete\b|-exec\s+rm\b)", re.IGNORECASE)

# Git で戻せないもの。`/` と `\` のどちらの区切りでも当たるようにする。
PROTECTED = [
    (r"LocalData", "LocalData/（稼働 DB・デプロイ zip）は Git 追跡外で、消すと戻せない"),
    (r"LocalEnvironment\.md", "Designer/LocalEnvironment.md は Git 追跡外で、消すと戻せない"),
    (r"settings\.local\.json", ".claude/settings.local.json は Git 追跡外で、消すと戻せない"),
    (r"\.git([\\/]|\s|$)", ".git/ は履歴そのもの"),
]

# スクラッチパッドの目印。セッションごとにパスが変わるので、深い階層の名前で見る。
SCRATCHPAD = re.compile(r"[\\/]scratchpad[\\/]|[\\/]scratchpad(\s|$)", re.IGNORECASE)

# 消してよいのはスクラッチパッドの中だけ、と言い切れるか。
# `&&` や `;` で別のコマンドが続くものは、この判定の対象にしない（読み切れないため）。
CHAINED = re.compile(r"[;&|]")


def decide(command: str):
    """(decision, reason) を返す。判定しないときは (None, None)。"""
    if not (DELETION.search(command) or FIND_DELETE.search(command)):
        return None, None

    for pattern, reason in PROTECTED:
        if re.search(pattern, command, re.IGNORECASE):
            return "deny", (
                f"{reason}。この削除は許可しない（tools/claude/guard_delete.py）。"
                "作業用の複製が要るなら、サブエージェントの worktree かスクラッチパッドを使う。"
            )

    if SCRATCHPAD.search(command) and not CHAINED.search(command):
        return "allow", "スクラッチパッド配下だけの削除（tools/claude/guard_delete.py）。"

    return None, None


# 期待する判定。**この表がこのフックの仕様である。**
# 守る道具にテストが無いと、書き換えたときに「止めているつもりで素通り」になる。
SELFTEST = [
    ("rm -rf LocalData/db", "deny"),
    ("rm LocalData/designs/App.zip", "deny"),
    ("Remove-Item -Recurse LocalData", "deny"),
    ("rm Designer/LocalEnvironment.md", "deny"),
    ("rm .claude/settings.local.json", "deny"),
    ("rm -rf .git", "deny"),
    ("find . -name '*.tmp' -path './LocalData/*' -delete", "deny"),
    # 連結して保護対象に触れるものも止める（スクラッチパッドが混ざっていても）
    ("rm -rf /tmp/x/scratchpad/clone && rm -rf LocalData", "deny"),
    # スクラッチパッドだけに閉じた削除は通す（区切りは / でも \\ でも）。
    # **絶対パスは書かない**（追跡ファイルに実在しうるパスを残さない。CLAUDE.md §5）。
    ("rm -rf /c/Users/x/AppData/Local/Temp/claude/p/s/scratchpad/clone", "allow"),
    ("rm -rf work\\scratchpad\\rev8", "allow"),
    # 追跡ファイルの削除は判定しない（git で戻せるので、通常の確認に落とす）
    ("rm BusinessApp/foo.cs", None),
    ("git clean -xdf", None),
    # 削除でないものには触れない
    ("cat LocalData/README.md", None),
    ("dotnet build", None),
]


def selftest() -> int:
    failed = 0
    for command, expected in SELFTEST:
        actual, _ = decide(command)
        if actual != expected:
            failed += 1
            print(f"NG  期待 {expected} / 実際 {actual}: {command}")
    print("guard_delete: すべて期待どおり" if failed == 0 else f"guard_delete: {failed} 件が期待と違う")
    return 1 if failed else 0


def main() -> None:
    if "--selftest" in sys.argv:
        sys.exit(selftest())

    try:
        payload = json.load(sys.stdin)
    except Exception:
        return  # 読めない入力は判定しない（フック自身が作業を止めない）

    command = str(payload.get("tool_input", {}).get("command", ""))
    decision, reason = decide(command)
    if decision is None:
        return

    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": decision,
            "permissionDecisionReason": reason,
        }
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
