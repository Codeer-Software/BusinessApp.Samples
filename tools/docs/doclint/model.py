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

# 検査対象外（生成物・ベンダー同梱・Git 追跡外）
EXCLUDE_PREFIXES = (
    "Designer/ClaudeCodeForDesigner/",
    "BusinessApp/",
    "LocalData/temp/",
)

# スキル（`.claude/skills/<名前>/SKILL.md`）。**検査対象だが、フロントマターだけ別仕様**。
# 欄は `name` / `description` がハーネスの仕様で決まっており、本プロジェクトの文書規約
# （title / status / scope / audience / updated）とは別物である。
#
# **免除するのは「欄の規約に依存する検査」だけで、文書としての検査は当てる。**
# 前置で丸ごと外すと、次の 8 つが落ちる——フロントマター・リンク切れ・`superseded` への
# リンク・行数・未処理マーカー・条項の記法・改正法の略称・条番号の切替。
# **節への参照と札の突合は、もともとこの経路の外で当たっている**
# （`check_section_references` などは `ls-files` を直に歩き、この前置を見ない）。
SKILL_PREFIX = ".claude/skills/"
SKILL_ENTRY = "SKILL.md"
SKILL_NAME_KEY = "name"
SKILL_REQUIRED_KEYS = [SKILL_NAME_KEY, "description"]

# YAML の折りたたみ・リテラル記法の印。**この読み手は 1 行の `key: value` しか読まない**ので、
# 値がこの印だけの行は「本体を読めていない」。空欄と同じに扱う（読めた値だと誤解しない）
FOLDED_SCALARS = frozenset((">", "|", ">-", "|-", ">+", "|+"))

# 追跡下にあるがデザイナが再生成する文書（手で直しても失われるので検査しない）
EXCLUDE_FILES = ("Designer/CLAUDE.md",)

# 「引くもの」であって通読しない文書。行数の警告と指標の対象から外す
REFERENCE_PREFIXES = ("docs/decisions/", "docs/research/",
                      "docs/qa/02_自己レビュー記録/", "docs/qa/04_実機操作テスト/")
# 行数の目安を当てない文書（docs/00 §5。開発者の決定 2026-09-10——責務は 1 つで、長さは未決の数で決まる）
LINE_LIMIT_EXEMPT = ("docs/05_開発者への問い.md",)

# コード参照検査（check_code_references）の対象拡張子と除外。
# Designer/migrations/ は適用済みがチェックサムで凍結される歴史文書なので、
# 後から文書が superseded になっても直せない（直させない）。
# **凍結の判定はここに写さず、正典から引く**（規約 §4-6。写しがずれると、
# 「直させない場所」と「直してよい場所」の境目が道具ごとに食い違う）。
# **引くのは述語であって前置リストではない**——`Designer/migrations/README.md` は
# 凍結ではない（育ててよい文書）ので、前置だけで除外すると参照が検査されなくなる。
# **先頭ではなく末尾**に足す（`lint_docs.py` が同じ理由を書いている）。
sys.path.append(os.path.join(REPO_ROOT, "tools", "clb"))
from check_frozen import is_frozen  # noqa: E402

CODE_EXTENSIONS = (".cs", ".sql", ".ps1", ".psm1", ".py", ".js", ".css")
# 凍結ではないが、生成物・追跡外なので参照を直す先が無い
CODE_EXCLUDE_PREFIXES = (
    "Designer/ClaudeCodeForDesigner/",
    "LocalData/",
)


def excluded_from_code_check(rel: str) -> bool:
    """コード参照・節参照の検査から外すか。

    凍結されたファイル（直せない）と、生成物・追跡外（直す先が無い）。
    """
    return is_frozen(rel) or rel.startswith(CODE_EXCLUDE_PREFIXES)

# 単体では文書を特定できない名前。コード参照の検査で親ディレクトリ込みにする
GENERIC_DOC_NAMES = ("README.md", "index.md")

INLINE_IGNORE = "lint-docs:ignore"

APPEND_ANTIPATTERN = re.compile(r"^\s*>?\s*(更新|さらに更新|追補)\s*[:：]")
STALE_MARKER = re.compile(r"\b(TODO|FIXME)\b|未了|後述")
# `\d` は Unicode 十進数字なので全角も通る。通すと書式 error をすり抜けたうえ、
# `updated < limit` の文字列比較が常に False になり履歴突合が黙って無効になる（2026-08-28）
DATE_RE = re.compile(r"^[0-9]{4}-[0-9]{2}-[0-9]{2}$")
MD_LINK = re.compile(r"\[[^\]]*\]\(([^)#]+?)(?:#[^)]*)?\)")


class Doc:
    def __init__(self, rel: str, lines: List[str], meta: Dict[str, str], body_start: int):
        self.rel = rel
        self.lines = lines
        self.meta = meta
        self.body_start = body_start

    @property
    def skill_name(self) -> Optional[str]:
        """`.claude/skills/<名前>/SKILL.md` のときだけ `<名前>` を返す。

        **前置一致で判定しない。** `references/` `scripts/` はハーネスのスキルの標準構成なので、
        配下を丸ごと「スキル本体」と見なすと、**補助ファイルを 1 枚置いた日に必ず赤くなる**——
        しかも直し方が「`.claude/skills/` を除外へ戻す」しか無くなり、塞いだ穴へ戻る力が働く。
        """
        if not self.rel.startswith(SKILL_PREFIX):
            return None
        rest = self.rel[len(SKILL_PREFIX):].split("/")
        return rest[0] if len(rest) == 2 and rest[1] == SKILL_ENTRY else None

    @property
    def is_skill(self) -> bool:
        """スキル本体（`.claude/skills/<名前>/SKILL.md`）か。"""
        return self.skill_name is not None

    @property
    def under_skill(self) -> bool:
        """スキルのディレクトリ配下か（本体と、`references/` などの補助ファイル）。"""
        return self.rel.startswith(SKILL_PREFIX)

    @property
    def status(self) -> str:
        """フロントマターの `status` を**素で**返す。

        スキルを `current` に読み替えるのはここではない（[checks.treated_as_current]）——
        **`--stats` の指標まで黙って動く**からである。ここは「書いてあること」だけを返す。
        """
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
        # ハイフンを許す。許さないと `allowed-tools:` のような欄が**黙って meta に入らず**、
        # 「書いたのに読まれていない」が誰にも見えない
        m = re.match(r"^([A-Za-z_][A-Za-z0-9_-]*)\s*:\s*(.*)$", line)
        if m:
            meta[m.group(1)] = m.group(2).strip()
    return meta, 0


def front_matter_unclosed(lines: List[str]) -> bool:
    """`---` で開いたまま閉じていないフロントマターか。

    **`parse_front_matter` は閉じフェンスが無くても `meta` を埋めて返す**ので、
    欄の実在だけを見る検査は通ってしまう。ところが**読み手（ハーネスや静的サイト）は
    フロントマターとして認識しない**ので、「検査は正しいと言うが、実際には効いていない」
    という**緑で失敗する**形になる。だから閉じているかを別に見る。
    """
    if not lines or lines[0].strip() != "---":
        return False
    return not any(lines[i].strip() == "---" for i in range(1, len(lines)))


def scalar_value(raw: str) -> Optional[str]:
    """1 行の `key: value` から値を取り出す。**読めない形は None** を返す。

    引用符は落とす（`Doc.list_field` と同じ流儀。`name: "foo"` を `"foo"` のまま比べると、
    **YAML として正しい書き方が偽の違反になる**）。
    折りたたみ（`>` `|`）は本体が次の行以降にあるが、この読み手は 1 行しか見ない——
    **印だけの値は「読めていない」**として None を返す（空でないと誤解しない）。
    """
    v = raw.strip()
    if v in FOLDED_SCALARS:
        return None
    return v.strip("'\"").strip()


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
