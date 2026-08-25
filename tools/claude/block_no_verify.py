#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""block_no_verify.py — Claude Code の PreToolUse フック。

コミット前フック（tools/git-hooks/pre-commit）の迂回を機械的に禁止する
（開発者の指示。2026-08-25。「以後やらない」という約束は機械にしか守れない）。

止めるもの:
  1. --no-verify（git commit / merge などのフック迂回）
  2. git commit の -n（--no-verify の短縮形）
  3. git -c core.hooksPath=...（フックの置き場のインライン差し替え。
     セットアップ用の `git config core.hooksPath ...` は止めない）

標準入力: Claude Code のフック入力 JSON（tool_input.command を見る）。
標準出力: 違反時のみ permissionDecision: deny の JSON。適合時は何も出さない。

判定はコマンド文字列全体への正規表現で、引用の中（コミットメッセージ等）も区別しない。
過剰検出側に倒す設計である——コミットメッセージなどの文言でこの語に触れたいときは
表記を変える（例:「no-verify」とダッシュを 1 つ落とす）。
"""

import json
import re
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8")  # Windows の既定は CP932 で、理由文が化ける
except Exception:
    pass

FORBIDDEN = [
    (re.compile(r"--no-verify\b"),
     "--no-verify はコミット前フック（検証）の迂回なので禁止。フックが落ちるなら原因を直す。"),
    (re.compile(r"\bgit\b[^|;&]*\bcommit\b[^|;&]*(^|\s)-n(\s|$)"),
     "git commit の -n は --no-verify の短縮形なので禁止。"),
    (re.compile(r"(^|\s)-c\s*core\.hooksPath"),
     "-c core.hooksPath はフックの置き場のインライン差し替えなので禁止。"),
]


def main() -> None:
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return  # 読めない入力は判定しない（フック自身が作業を止めない）

    command = str(payload.get("tool_input", {}).get("command", ""))
    for pattern, reason in FORBIDDEN:
        if pattern.search(command):
            print(json.dumps({
                "hookSpecificOutput": {
                    "hookEventName": "PreToolUse",
                    "permissionDecision": "deny",
                    "permissionDecisionReason": reason,
                }
            }, ensure_ascii=False))
            return


if __name__ == "__main__":
    main()
