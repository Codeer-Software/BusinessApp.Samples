#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""lint_secrets.py — 公開リポジトリに載せてはいけないものの機械検査.

このリポジトリは GitHub のパブリックリポジトリへ公開する。したがって
**Git 追跡下のファイルに、ローカル環境の構成（絶対パス・ユーザー名・ホスト名）と
秘密情報（接続文字列・API キー・トークン・秘密鍵）が混入していないこと**を機械的に保証する。

検査対象は `git ls-files` が返す追跡ファイルのみ（追跡外は公開されないため対象外）。

使い方:
    python tools/docs/lint_secrets.py            # 追跡ファイル全体を検査
    python tools/docs/lint_secrets.py --staged   # ステージ済みファイルだけ検査（コミット直前用）
    python tools/docs/lint_secrets.py --list-rules

終了コード: 0 = error なし / 1 = error あり / 2 = 実行失敗

抑制:
  - 行末に `lint-secrets:ignore` を書くとその行を無視する
  - `tools/docs/lint_secrets_allow.txt` に `<ルールID> <パスのglob>` を書くと
    そのファイルの当該ルールを無視する（`*` は全ルール）

Python 3.8+ / 標準ライブラリのみ。
"""

from __future__ import annotations

import argparse
import fnmatch
import os
import re
import subprocess
import sys
from typing import Dict, List, Tuple

SEV_ERROR = "error"
SEV_WARN = "warn"

# Windows の既定コンソール（cp932）だと日本語メッセージが化けるので UTF-8 に固定する
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
    except Exception:
        pass

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
ALLOW_FILE = os.path.join(REPO_ROOT, "tools", "docs", "lint_secrets_allow.txt")

INLINE_IGNORE = "lint-secrets:ignore"

# 拡張子で除外（バイナリ・生成物）
SKIP_EXT = {
    ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".webp", ".svgz",
    ".zip", ".7z", ".gz", ".tar", ".rar",
    ".db", ".sqlite", ".sqlite3",
    ".dll", ".exe", ".pdb", ".so", ".dylib",
    ".xlsx", ".xls", ".docx", ".pdf",
    ".ttf", ".otf", ".woff", ".woff2",
    ".map",
}

MAX_BYTES = 2 * 1024 * 1024


# ルールID -> (重大度, 説明, 正規表現)
RULES: Dict[str, Tuple[str, str, "re.Pattern[str]"]] = {
    "SEC-001": (
        SEV_ERROR,
        "Windows のユーザープロファイル絶対パス（ローカル構成の漏洩）",
        re.compile(r"[A-Za-z]:[\\/]{1,2}[Uu]sers[\\/]"),
    ),
    "SEC-002": (
        SEV_ERROR,
        "Unix・Git Bash・WSL のホームディレクトリ絶対パス（ローカル構成の漏洩）",
        # **Git Bash（MSYS）の `/c/Users/<名前>/` と WSL の `/mnt/c/Users/<名前>/` も拾う。**
        # この repo は Bash ツールを常用しており、貼り付く絶対パスの多くがこの形になる。
        # 素の `/Users/…` だけを見ていると、`/c/` が前に付いた瞬間に素通りしていた（2026-09-07 に発見）。
        re.compile(
            r"(?<![\w.:])/(?:home|Users)/[A-Za-z0-9._-]+/"
            r"|(?<![\w.:])(?:/mnt)?/[A-Za-z]/[Uu]sers/[A-Za-z0-9._-]+/"
        ),
    ),
    "SEC-003": (
        SEV_ERROR,
        "UNC パス（ホスト名の漏洩）",
        # 先頭が「区切りでない文字」でないこと＝ C:\\... の途中の連続バックスラッシュを拾わない
        re.compile(r"(?<![:\w\\])\\{2,4}[A-Za-z0-9._-]{2,}\\{1,2}[A-Za-z0-9._$-]"),
    ),
    "SEC-010": (
        SEV_ERROR,
        "接続文字列らしき記述（パスワード）",
        # 接続文字列は `Password=xxx;` の形（= の前後に空白を置かない）。
        # `const password = ...` のようなコード上の代入は対象外にする。
        re.compile(r"(?i)\b(?:password|pwd)=[^\s;\"'<>]+"),
    ),
    "SEC-011": (
        SEV_ERROR,
        "接続文字列らしき記述（データソース指定）",
        re.compile(r"(?i)\b(?:data\s+source|initial\s+catalog|user\s+id)\s*="),
    ),
    "SEC-020": (
        SEV_ERROR,
        "秘密鍵ブロック",
        re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
    ),
    "SEC-021": (
        SEV_ERROR,
        "既知の API キー・トークンの接頭辞",
        re.compile(
            r"(?:sk-ant-[A-Za-z0-9_\-]{8,}"
            r"|ghp_[A-Za-z0-9]{20,}"
            r"|github_pat_[A-Za-z0-9_]{20,}"
            r"|AKIA[0-9A-Z]{16}"
            r"|xox[baprs]-[A-Za-z0-9\-]{10,})"
        ),
    ),
    "SEC-022": (
        SEV_ERROR,
        "秘密情報らしき変数への長い値の代入",
        re.compile(
            r"(?i)\b(?:api[_-]?key|apikey|secret|client[_-]?secret|access[_-]?token"
            r"|auth[_-]?token|private[_-]?key)\b\s*[:=]\s*[\"']?[A-Za-z0-9/+_\-]{20,}"
        ),
    ),
    "SEC-030": (
        SEV_WARN,
        "メールアドレス（個人情報。例示ドメイン以外）",
        re.compile(r"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}"),
    ),
}

# SEC-030 の除外ドメイン・接頭辞
MAIL_ALLOW = re.compile(
    r"@(?:example\.(?:com|org|net)|test|localhost|invalid|users\.noreply\.github\.com)\b"
    r"|^noreply@",
    re.IGNORECASE,
)


def run_git(args: List[str]) -> List[str]:
    try:
        out = subprocess.run(
            ["git"] + args,
            cwd=REPO_ROOT,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=True,
        )
    except (OSError, subprocess.CalledProcessError) as e:
        sys.stderr.write("git の実行に失敗しました: {}\n".format(e))
        sys.exit(2)
    text = out.stdout.decode("utf-8", errors="replace")
    return [line for line in text.splitlines() if line.strip()]


def load_allowlist() -> List[Tuple[str, str]]:
    """(ルールID, パスglob) の一覧。ルールID が '*' なら全ルール。"""
    rules: List[Tuple[str, str]] = []
    if not os.path.exists(ALLOW_FILE):
        return rules
    with open(ALLOW_FILE, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            parts = line.split(None, 1)
            if len(parts) != 2:
                continue
            rules.append((parts[0], parts[1].replace("\\", "/")))
    return rules


def is_allowed(allow: List[Tuple[str, str]], rule_id: str, path: str) -> bool:
    for r, pattern in allow:
        if r not in ("*", rule_id):
            continue
        if fnmatch.fnmatch(path, pattern):
            return True
    return False


def read_text(abs_path: str) -> "str | None":
    try:
        if os.path.getsize(abs_path) > MAX_BYTES:
            return None
        with open(abs_path, "rb") as f:
            raw = f.read()
    except OSError:
        return None
    if b"\x00" in raw:
        return None
    return raw.decode("utf-8", errors="replace")


def scan(paths: List[str], allow: List[Tuple[str, str]]) -> List[Tuple[str, str, str, int, str]]:
    """戻り値: (重大度, ルールID, パス, 行番号, 抜粋)"""
    findings = []
    for rel in paths:
        rel_posix = rel.replace("\\", "/")
        _, ext = os.path.splitext(rel_posix)
        if ext.lower() in SKIP_EXT:
            continue
        abs_path = os.path.join(REPO_ROOT, rel)
        text = read_text(abs_path)
        if text is None:
            continue
        for lineno, line in enumerate(text.splitlines(), start=1):
            if INLINE_IGNORE in line:
                continue
            for rule_id, (sev, _desc, pattern) in RULES.items():
                if is_allowed(allow, rule_id, rel_posix):
                    continue
                m = pattern.search(line)
                if not m:
                    continue
                if rule_id == "SEC-030" and MAIL_ALLOW.search(m.group(0)):
                    continue
                excerpt = line.strip()
                if len(excerpt) > 160:
                    excerpt = excerpt[:157] + "..."
                findings.append((sev, rule_id, rel_posix, lineno, excerpt))
    return findings


def main() -> int:
    ap = argparse.ArgumentParser(description="公開リポジトリ向けの秘密情報・ローカル構成の混入検査")
    ap.add_argument("--staged", action="store_true", help="ステージ済みファイルのみ検査する")
    ap.add_argument("--list-rules", action="store_true", help="ルール一覧を表示して終了する")
    args = ap.parse_args()

    if args.list_rules:
        for rule_id, (sev, desc, _p) in RULES.items():
            print("{}  [{}] {}".format(rule_id, sev, desc))
        return 0

    if args.staged:
        paths = run_git(["diff", "--cached", "--name-only", "--diff-filter=ACMR"])
    else:
        paths = run_git(["ls-files"])

    allow = load_allowlist()
    findings = scan(paths, allow)

    errors = [f for f in findings if f[0] == SEV_ERROR]
    warns = [f for f in findings if f[0] == SEV_WARN]

    for sev, rule_id, path, lineno, excerpt in sorted(findings, key=lambda f: (f[0] != SEV_ERROR, f[2], f[3])):
        print("{}\t{}\t{}:{}\t{}".format(sev, rule_id, path, lineno, excerpt))

    print("")
    print("検査ファイル数: {} / error: {} / warn: {}".format(len(paths), len(errors), len(warns)))
    if errors:
        print("error があります。公開前に必ず解消すること（誤検知なら tools/docs/lint_secrets_allow.txt に登録）。")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
