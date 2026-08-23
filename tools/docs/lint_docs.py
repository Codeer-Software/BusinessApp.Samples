#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""lint_docs.py — ドキュメント規約の機械検査.

仕様書: docs/00_ドキュメント規約.md

長期開発でドキュメントが腐り、肥大化するのを防ぐ。検査するのは次の 2 点である。
  1. 読まなくていい文書を判別できるか（フロントマターと status）
  2. 索引・ADR 台帳と実ファイルが食い違っていないか

使い方:
    python tools/docs/lint_docs.py          # 規約違反の検査（error / warn）
    python tools/docs/lint_docs.py --stats  # current の行数など指標

終了コード: 0 = error なし / 1 = error あり / 2 = 実行失敗

Python 3.8+ / 標準ライブラリのみ（YAML パーサは使わず、必要な範囲だけ自前で読む）。
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from typing import Dict, List, Optional, Tuple

for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
    except Exception:
        pass

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

SEV_ERROR = "error"
SEV_WARN = "warn"

VALID_STATUS = {"current", "superseded", "historical"}
VALID_AUDIENCE = {"開発", "運用", "営業", "テスト"}
REQUIRED_KEYS = ["title", "status", "scope", "audience", "updated"]

LINE_LIMIT = 250

# 検査対象外（生成物・ベンダー同梱・Git 追跡外）
EXCLUDE_PREFIXES = (
    "Designer/ClaudeCodeForDesigner/",
    "BusinessApp/",
    "LocalData/temp/",
)

# 追跡下にあるがデザイナが再生成する文書（手で直しても失われるので検査しない）
EXCLUDE_FILES = ("Designer/CLAUDE.md",)

# 「引くもの」であって通読しない文書。行数の警告と指標の対象から外す
REFERENCE_PREFIXES = ("docs/decisions/", "docs/research/")

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


def run_git(args: List[str]) -> List[str]:
    try:
        out = subprocess.run(["git"] + args, cwd=REPO_ROOT,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
    except (OSError, subprocess.CalledProcessError) as e:
        sys.stderr.write("git の実行に失敗しました: {}\n".format(e))
        sys.exit(2)
    return [l for l in out.stdout.decode("utf-8", errors="replace").splitlines() if l.strip()]


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


def load_docs() -> List[Doc]:
    docs = []
    for rel in run_git(["ls-files", "*.md"]):
        rel_posix = rel.replace("\\", "/")
        if rel_posix.startswith(EXCLUDE_PREFIXES) or rel_posix in EXCLUDE_FILES:
            continue
        path = os.path.join(REPO_ROOT, rel)
        try:
            with open(path, "r", encoding="utf-8") as f:
                lines = f.read().splitlines()
        except OSError:
            continue
        meta, body_start = parse_front_matter(lines)
        docs.append(Doc(rel_posix, lines, meta, body_start))
    return docs


def check_front_matter(doc: Doc, findings: List[Tuple[str, str, str]]) -> None:
    add = lambda sev, msg: findings.append((sev, doc.rel, msg))

    if not doc.meta:
        add(SEV_ERROR, "フロントマターがありません（規約 §3）")
        return
    for key in REQUIRED_KEYS:
        if key not in doc.meta or not doc.meta[key].strip():
            add(SEV_ERROR, "フロントマターに {} がありません".format(key))
    status = doc.status
    if status and status not in VALID_STATUS:
        add(SEV_ERROR, "status が 3 値以外です: {}".format(status))
    updated = doc.meta.get("updated", "")
    if updated and not DATE_RE.match(updated):
        add(SEV_ERROR, "updated が YYYY-MM-DD 形式ではありません: {}".format(updated))
    for a in doc.list_field("audience"):
        if a not in VALID_AUDIENCE:
            add(SEV_ERROR, "audience に未知の値があります: {}".format(a))
    if status == "superseded" and not doc.list_field("related"):
        add(SEV_ERROR, "status が superseded なのに related が空です（後継を書く）")


def resolve(doc: Doc, target: str) -> str:
    base = os.path.dirname(doc.rel)
    return os.path.normpath(os.path.join(base, target)).replace("\\", "/")


def check_links(doc: Doc, existing: set, findings: List[Tuple[str, str, str]]) -> None:
    add = lambda sev, msg: findings.append((sev, doc.rel, msg))

    for key in ("supersedes", "related"):
        for target in doc.list_field(key):
            if target.startswith("http"):
                continue
            resolved = resolve(doc, target)
            if resolved in existing:
                continue
            # 検査対象外の文書（デザイナ生成物など）でも、実在するならリンクとしては妥当
            if os.path.exists(os.path.join(REPO_ROOT, resolved)):
                continue
            add(SEV_ERROR, "{} のリンク先が実在しません: {}".format(key, target))

    for i in range(doc.body_start, len(doc.lines)):
        for target in MD_LINK.findall(doc.lines[i]):
            t = target.strip()
            if not t or t.startswith(("http", "mailto:")) or t.startswith("#"):
                continue
            resolved = resolve(doc, t)
            if resolved in existing:
                continue
            if os.path.exists(os.path.join(REPO_ROOT, resolved)):
                continue
            add(SEV_ERROR, "{}行目: リンク先が実在しません: {}".format(i + 1, t))


def check_body(doc: Doc, findings: List[Tuple[str, str, str]]) -> None:
    add = lambda sev, msg: findings.append((sev, doc.rel, msg))
    if doc.status != "current":
        return

    is_reference = doc.rel.startswith(REFERENCE_PREFIXES)
    body_len = len(doc.lines) - doc.body_start
    if body_len > LINE_LIMIT and not doc.is_append and not is_reference:
        add(SEV_WARN, "{} 行あります（目安 {} 行）。分割を検討する".format(body_len, LINE_LIMIT))

    head_end = min(doc.body_start + 20, len(doc.lines))
    for i in range(doc.body_start, head_end):
        if APPEND_ANTIPATTERN.match(doc.lines[i]):
            add(SEV_WARN, "{}行目: ヘッダに更新履歴を積んでいます（規約 §4-2）".format(i + 1))

    if is_reference:
        # ADR とリサーチは「引くもの」。自身の未確認事項を本文で列挙するのが正しい姿なので、
        # 未処理マーカーの検査対象から外す
        return

    in_hold_list = False
    in_code = False
    for i in range(doc.body_start, len(doc.lines)):
        line = doc.lines[i]
        if line.lstrip().startswith("```"):
            in_code = not in_code
            continue
        if in_code:
            continue  # コード例の中の語は散文ではない
        if re.match(r"^#{1,6}\s", line):
            in_hold_list = "保留リスト" in line
            continue
        if in_hold_list or INLINE_IGNORE in line:
            continue
        if STALE_MARKER.search(line):
            add(SEV_WARN, "{}行目: 未処理マーカーがあります。保留リストへ移す（規約 §4-4）".format(i + 1))


def check_adr_ledger(docs: List[Doc], findings: List[Tuple[str, str, str]]) -> None:
    ledger_rel = "docs/decisions/README.md"
    ledger = next((d for d in docs if d.rel == ledger_rel), None)
    if ledger is None:
        return
    listed = set()
    for line in ledger.lines:
        for target in MD_LINK.findall(line):
            if target.endswith(".md"):
                listed.add(resolve(ledger, target))
    actual = {d.rel for d in docs
              if d.rel.startswith("docs/decisions/") and not d.rel.endswith("README.md")}
    for missing in sorted(actual - listed):
        findings.append((SEV_ERROR, ledger_rel, "台帳に載っていない ADR があります: {}".format(missing)))
    for ghost in sorted(listed - actual):
        findings.append((SEV_ERROR, ledger_rel, "台帳の行に対応する ADR がありません: {}".format(ghost)))


def check_docs_index(docs: List[Doc], findings: List[Tuple[str, str, str]]) -> None:
    index_rel = "docs/README.md"
    index = next((d for d in docs if d.rel == index_rel), None)
    if index is None:
        return
    listed = set()
    for line in index.lines:
        for target in MD_LINK.findall(line):
            listed.add(resolve(index, target))
    for d in docs:
        if not d.rel.startswith("docs/"):
            continue
        if d.rel == index_rel or d.rel.count("/") > 1:
            continue  # サブディレクトリはディレクトリ単位で案内する
        if d.rel not in listed:
            findings.append((SEV_WARN, index_rel, "索引に載っていない文書があります: {}".format(d.rel)))


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
    print("== current の行数（decisions/ を除く） ==")
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
    args = ap.parse_args()

    docs = load_docs()
    if args.stats:
        print_stats(docs)
        return 0

    existing = {d.rel for d in docs}
    findings: List[Tuple[str, str, str]] = []
    for d in docs:
        check_front_matter(d, findings)
        check_links(d, existing, findings)
        check_body(d, findings)
    check_adr_ledger(docs, findings)
    check_docs_index(docs, findings)

    errors = [f for f in findings if f[0] == SEV_ERROR]
    warns = [f for f in findings if f[0] == SEV_WARN]
    for sev, rel, msg in sorted(findings, key=lambda f: (f[0] != SEV_ERROR, f[1], f[2])):
        print("{}\t{}\t{}".format(sev, rel, msg))

    print("")
    print("検査文書数: {} / error: {} / warn: {}".format(len(docs), len(errors), len(warns)))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
