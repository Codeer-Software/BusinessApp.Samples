#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""doclint.model — 設定値・`Doc`・git 呼び出し・文書の読み込み・パス解決.

**判定はここに書かない。** 検査は `checks.py` が持つ。
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
from typing import Dict, List, Optional, Tuple

REPO_ROOT = os.path.abspath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))

SEV_ERROR = "error"
SEV_WARN = "warn"

VALID_STATUS = {"current", "superseded", "historical"}
VALID_AUDIENCE = {"開発", "運用", "営業", "テスト"}
REQUIRED_KEYS = ["title", "status", "scope", "audience", "updated"]

LINE_LIMIT = 250

DOCS_INDEX = "docs/README.md"

# ADR 台帳。**全 ADR を載せるのが仕事**なので、superseded へのリンク検査から外す。
# `growth: append` だからではない——役割による免除なので、フロントマターが変わっても効く
ADR_LEDGER = "docs/decisions/README.md"

# 検査対象外（生成物・ベンダー同梱・Git 追跡外・別の規約に従うもの）
EXCLUDE_PREFIXES = (
    "Designer/ClaudeCodeForDesigner/",
    "BusinessApp/",
    "LocalData/temp/",
    # スキルのフロントマターは name / description が仕様で決まっており、
    # 本プロジェクトの文書規約（title / status / scope / audience / updated）とは別物である。
    ".claude/skills/",
)

# 追跡下にあるがデザイナが再生成する文書（手で直しても失われるので検査しない）
EXCLUDE_FILES = ("Designer/CLAUDE.md",)

# 「引くもの」であって通読しない文書。行数の警告と指標の対象から外す
REFERENCE_PREFIXES = ("docs/decisions/", "docs/research/")

# コード参照検査（check_code_references）の対象拡張子と除外。
# Designer/migrations/ は適用済みがチェックサムで凍結される歴史文書なので、
# 後から文書が superseded になっても直せない（直させない）。
CODE_EXTENSIONS = (".cs", ".sql", ".ps1", ".psm1", ".py", ".js", ".css")
CODE_EXCLUDE_PREFIXES = (
    "Designer/ClaudeCodeForDesigner/",
    "Designer/migrations/",
    "LocalData/",
)

INLINE_IGNORE = "lint-docs:ignore"

APPEND_ANTIPATTERN = re.compile(r"^\s*>?\s*(更新|さらに更新|追補)\s*[:：]")
STALE_MARKER = re.compile(r"\b(TODO|FIXME)\b|未了|後述")
DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
MD_LINK = re.compile(r"\[[^\]]*\]\(([^)#]+?)(?:#[^)]*)?\)")


class Doc:
    def __init__(self, rel: str, lines: List[str], meta: Dict[str, str], body_start: int):
        self.rel = rel
        self.lines = lines
        self.meta = meta
        self.body_start = body_start

    @property
    def status(self) -> str:
        return self.meta.get("status", "")

    @property
    def is_append(self) -> bool:
        return self.meta.get("growth", "") == "append"

    def list_field(self, key: str) -> List[str]:
        raw = self.meta.get(key, "").strip()
        if not raw:
            return []
        raw = raw.strip("[]")
        return [x.strip().strip("'\"") for x in raw.split(",") if x.strip()]


# 日本語のパスを git が ã のように引用して返さないようにする。
# 既定（core.quotepath=true）だと、パスの突合を行う検査が黙って素通りする。
GIT = ["git", "-c", "core.quotepath=false"]


def run_git(args: List[str]) -> List[str]:
    try:
        out = subprocess.run(GIT + args, cwd=REPO_ROOT,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
    except (OSError, subprocess.CalledProcessError) as e:
        sys.stderr.write("git の実行に失敗しました: {}\n".format(e))
        sys.exit(2)
    return [l for l in out.stdout.decode("utf-8", errors="replace").splitlines() if l.strip()]


def git_text(args: List[str], allow_failure: bool = False) -> Optional[str]:
    """git の標準出力を丸ごと返す。

    `allow_failure=True` のときだけ、失敗を None として受ける（HEAD に無い blob など、
    失敗が答えになる問い合わせ）。それ以外の失敗は `run_git` と同じく exit 2 で落とす——
    `.git/index.lock` を握られている等の異常を「変更なし」と読むと、検査が無言で素通りする。
    """
    try:
        out = subprocess.run(GIT + args, cwd=REPO_ROOT,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
    except (OSError, subprocess.CalledProcessError) as e:
        if allow_failure:
            return None
        sys.stderr.write("git の実行に失敗しました: {}\n".format(e))
        sys.exit(2)
    return out.stdout.decode("utf-8", errors="replace")


def parse_front_matter(lines: List[str]) -> Tuple[Dict[str, str], int]:
    """先頭の --- で囲まれたブロックを key: value として読む。戻り値は (meta, 本文の開始行index)。"""
    if not lines or lines[0].strip() != "---":
        return {}, 0
    meta: Dict[str, str] = {}
    for i in range(1, len(lines)):
        line = lines[i]
        if line.strip() == "---":
            return meta, i + 1
        m = re.match(r"^([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(.*)$", line)
        if m:
            meta[m.group(1)] = m.group(2).strip()
    return meta, 0


def body_of(text: str) -> List[str]:
    """フロントマターを除いた本文の行を返す。"""
    lines = text.splitlines()
    _, start = parse_front_matter(lines)
    return lines[start:]


def load_docs() -> Tuple[List[Doc], List[str]]:
    """検査対象の文書と、**読めなかったファイル**を返す。

    読めなかったものを黙って捨てない。捨てると「検査文書数」だけが減り、
    error 0 のまま**何も検査していない**状態になる（実測: git の既定
    `core.quotepath=true` だと日本語のパスが引用されて 58 → 9 に落ちた。2026-08-27）。
    """
    docs: List[Doc] = []
    unreadable: List[str] = []
    for rel in run_git(["ls-files", "*.md"]):
        rel_posix = rel.replace("\\", "/")
        if rel_posix.startswith(EXCLUDE_PREFIXES) or rel_posix in EXCLUDE_FILES:
            continue
        path = os.path.join(REPO_ROOT, rel)
        try:
            # utf-8-sig: BOM 付きでもフロントマターを読み落とさない
            with open(path, "r", encoding="utf-8-sig") as f:
                lines = f.read().splitlines()
        except (OSError, UnicodeDecodeError) as e:
            unreadable.append("{}（{}）".format(rel_posix, type(e).__name__))
            continue
        meta, body_start = parse_front_matter(lines)
        docs.append(Doc(rel_posix, lines, meta, body_start))
    return docs, unreadable


def resolve(doc: Doc, target: str) -> str:
    """`doc` から見た相対パスを、**リポジトリ相対**に直す。"""
    base = os.path.dirname(doc.rel)
    return os.path.normpath(os.path.join(base, target)).replace("\\", "/")
