#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ドキュメント規約リンタ.

仕様書: docs/00_ドキュメント規約.md
designcheck / lint_design.py がデザインを検査するのに対し、本スクリプトは **文書** を検査する。

使い方:
    python Designer/tools/lint_docs.py                  規約違反の検査（error / warn）
    python Designer/tools/lint_docs.py --stats          current の行数など指標
    python Designer/tools/lint_docs.py --impact HEAD~1  変更が影響する体験シナリオ
    python Designer/tools/lint_docs.py --stale          verified より後にモジュールが変わった台本

終了コード: error が 1 件でもあれば 1、それ以外は 0（warn は 0 のまま）。
Python 3.8+ 標準ライブラリのみ。外部依存なし。
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from collections import Counter, defaultdict

SEV_ERROR = "error"
SEV_WARN = "warn"

STATUS_VALUES = ("current", "superseded", "historical")
AUDIENCE_VALUES = ("開発", "運用", "営業", "テスト")
REQUIRED_FIELDS = ("title", "status", "scope", "audience", "updated")

LINE_LIMIT = 250
SCENARIO_DIR = "docs/tests/20_体験シナリオ"

# 対象外（規約 §2）
EXCLUDE_PATTERNS = (
    "ClaudeCodeForDesigner/",
    "open-iconic/",
)

DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
NOTICE_RE = re.compile(r"(さらに更新|追補|更新|改訂|最終更新)\s*[:：]")
DEFER_RE = re.compile(r"(TODO|未了|後で直す)")
# 規約そのものは禁止パターンを本文で引用するため、散文検査を免除する
PROSE_EXEMPT = ("docs/00_ドキュメント規約.md",)
INLINE_CODE_RE = re.compile(r"`[^`]*`")
HEADER_SCAN_LINES = 20


def repo_root() -> str:
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.normpath(os.path.join(here, "..", ".."))


def git(root: str, *args: str) -> str:
    out = subprocess.run(
        ["git", "-C", root, *args],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    return out.stdout or ""


def tracked_docs(root: str):
    """検査対象の .md を返す（リポジトリ相対・スラッシュ区切り）。

    追跡済みに加え **未追跡だが .gitignore で除外されていないもの**（`--others
    --exclude-standard`）も含める。含めないと、新規作成してまだ add していない文書が
    リンタから見えず、索引の突合が誤検出になる。
    """
    listed = git(root, "ls-files", "--cached", "--others", "--exclude-standard", "*.md")
    paths = sorted({p.strip() for p in listed.splitlines() if p.strip()})
    return [p for p in paths if not any(x in p for x in EXCLUDE_PATTERNS)]


def read_text(root: str, rel: str):
    full = os.path.join(root, rel.replace("/", os.sep))
    try:
        with open(full, encoding="utf-8") as f:
            return f.read()
    except OSError:
        return None


# --------------------------------------------------------------------------- #
# フロントマター（最小限の YAML サブセット）
# --------------------------------------------------------------------------- #

def strip_scalar(value: str):
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
        value = value[1:-1]
    return value.strip()


def split_inline_list(value: str):
    """[a, b, "c d"] -> ['a', 'b', 'c d']。カンマ区切り・引用符に対応。"""
    inner = value.strip()[1:-1].strip()
    if not inner:
        return []
    items, buf, quote = [], "", None
    for ch in inner:
        if quote:
            if ch == quote:
                quote = None
            else:
                buf += ch
        elif ch in "\"'":
            quote = ch
        elif ch == ",":
            items.append(buf)
            buf = ""
        else:
            buf += ch
    items.append(buf)
    return [s.strip() for s in items if s.strip()]


def parse_frontmatter(text: str):
    """(dict または None, 本文開始行番号) を返す。値は str か list[str]。"""
    lines = text.splitlines()
    if not lines or lines[0].strip() != "---":
        return None, 0
    end = None
    for i in range(1, len(lines)):
        if lines[i].strip() == "---":
            end = i
            break
    if end is None:
        return None, 0

    data, key = {}, None
    for raw in lines[1:end]:
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue
        if raw.lstrip().startswith("- ") and key is not None:
            data.setdefault(key, [])
            if isinstance(data[key], str):
                data[key] = [data[key]] if data[key] else []
            data[key].append(strip_scalar(raw.lstrip()[2:]))
            continue
        if ":" not in raw:
            continue
        key, value = raw.split(":", 1)
        key, value = key.strip(), value.strip()
        if value.startswith("[") and value.endswith("]"):
            data[key] = split_inline_list(value)
        elif value == "":
            data[key] = []
        else:
            data[key] = strip_scalar(value)
    return data, end + 1


def as_list(value):
    if value is None:
        return []
    if isinstance(value, list):
        return value
    return [value] if value else []


# --------------------------------------------------------------------------- #
# モジュール
# --------------------------------------------------------------------------- #

def known_modules(root: str):
    base = os.path.join(root, "Designer", "Design", "Modules")
    found = set()
    for dirpath, _dirnames, filenames in os.walk(base):
        for name in filenames:
            if name.endswith(".mod.json"):
                folder = os.path.basename(dirpath)
                found.add(folder + "/" + name[: -len(".mod.json")])
    return found


def module_of_path(rel: str):
    """Designer/Design/Modules/<Folder>/<Name>.* -> '<Folder>/<Name>'。それ以外は None。"""
    prefix = "Designer/Design/Modules/"
    if not rel.startswith(prefix):
        return None
    rest = rel[len(prefix):].split("/")
    if len(rest) < 2:
        return None
    return rest[0] + "/" + rest[-1].split(".")[0]


# --------------------------------------------------------------------------- #
# 文書の読み込み
# --------------------------------------------------------------------------- #

class Doc:
    def __init__(self, root: str, rel: str):
        self.rel = rel
        self.text = read_text(root, rel) or ""
        self.lines = self.text.splitlines()
        self.fm, self.body_start = parse_frontmatter(self.text)

    @property
    def status(self):
        return self.fm.get("status") if self.fm else None

    def get(self, key):
        return self.fm.get(key) if self.fm else None

    def body_lines(self):
        return self.lines[self.body_start:]

    def prose_lines(self):
        """本文からコードフェンス内を除いた (1 始まりの行番号, 行) を返す。

        規約そのものや手順書は違反例をコードブロックで示すため、フェンス内は検査しない。
        """
        fence = None
        for offset, line in enumerate(self.body_lines()):
            marker = re.match(r"\s*(`{3,}|~{3,})", line)
            if marker:
                token = marker.group(1)[0]
                if fence is None:
                    fence = token
                elif fence == token:
                    fence = None
                continue
            if fence is None:
                # インラインコード（`TODO` のような言及）は検査しない
                yield self.body_start + offset + 1, INLINE_CODE_RE.sub("", line)


def resolve_link(root: str, doc_rel: str, target: str):
    """related / supersedes のパスを解決する。文書のディレクトリ → docs/ → リポジトリ直下の順。"""
    target = target.strip()
    if not target or target.startswith(("http://", "https://")):
        return True
    target = target.split("#", 1)[0]
    if not target:
        return True
    candidates = [
        os.path.normpath(os.path.join(os.path.dirname(doc_rel), target)),
        os.path.normpath(os.path.join("docs", target)),
        os.path.normpath(target),
    ]
    return any(os.path.exists(os.path.join(root, c)) for c in candidates)


# --------------------------------------------------------------------------- #
# 検査
# --------------------------------------------------------------------------- #

def lint(root: str):
    findings = []

    def add(sev, rel, msg, line=None):
        findings.append((sev, rel, msg, line))

    paths = tracked_docs(root)
    docs = {p: Doc(root, p) for p in paths}
    modules = known_modules(root)

    for rel, doc in docs.items():
        if doc.fm is None:
            add(SEV_ERROR, rel, "フロントマターが無い（規約 §3）")
            continue

        for field in REQUIRED_FIELDS:
            if not doc.get(field):
                add(SEV_ERROR, rel, "必須フィールドが無い: %s" % field)

        status = doc.status
        if status and status not in STATUS_VALUES:
            add(SEV_ERROR, rel, "status が 3 値以外: %r（%s）" % (status, " / ".join(STATUS_VALUES)))

        for field in ("updated", "verified"):
            value = doc.get(field)
            if value and not DATE_RE.match(str(value)):
                add(SEV_ERROR, rel, "%s の日付形式が不正: %r（YYYY-MM-DD）" % (field, value))

        for aud in as_list(doc.get("audience")):
            if aud not in AUDIENCE_VALUES:
                add(SEV_ERROR, rel, "audience が既定値以外: %r（%s）" % (aud, " / ".join(AUDIENCE_VALUES)))

        for field in ("supersedes", "related"):
            for target in as_list(doc.get(field)):
                if not resolve_link(root, rel, target):
                    add(SEV_ERROR, rel, "%s のリンク先が実在しない: %s" % (field, target))

        if status == "superseded" and not as_list(doc.get("related")):
            add(SEV_ERROR, rel, "superseded なのに related（後継）が空（規約 §3-2）")

        if rel.startswith(SCENARIO_DIR) and os.path.basename(rel) != "README.md":
            if not as_list(doc.get("modules")) and not as_list(doc.get("verifies")):
                add(SEV_ERROR, rel, "体験シナリオに modules:／verifies: が無い（規約 §6-1）")
            for field in ("modules", "verifies"):
                for name in as_list(doc.get(field)):
                    if name not in modules:
                        add(SEV_ERROR, rel, "%s に実在しないモジュール: %s" % (field, name))

        if status == "current":
            if doc.get("growth") != "append" and len(doc.lines) > LINE_LIMIT:
                add(SEV_WARN, rel, "current で %d 行（目安 %d 行・分割を検討／台帳型なら growth: append）"
                    % (len(doc.lines), LINE_LIMIT))
            prose = [] if rel in PROSE_EXEMPT else list(doc.prose_lines())
            for lineno, line in prose[:HEADER_SCAN_LINES]:
                if NOTICE_RE.search(line):
                    add(SEV_WARN, rel, "冒頭に更新履歴の積み上げ（規約 §4-2）: %s"
                        % line.strip()[:60], lineno)
                    break
            for lineno, line in prose:
                if "保留リスト" in line:
                    continue  # 保留リストへの誘導そのものは違反ではない
                if DEFER_RE.search(line):
                    add(SEV_WARN, rel, "current に先送りの記述（規約 §4-3・保留リストへ）: %s"
                        % line.strip()[:60], lineno)
                    break

    findings.extend(check_adr_index(root, docs))
    findings.extend(check_readme_index(root, docs))
    return findings


def check_adr_index(root: str, docs):
    """ADR の実ファイルと decisions/README.md の行を突合する（規約 §8-4）。"""
    out = []
    index_rel = "docs/decisions/README.md"
    text = read_text(root, index_rel)
    if text is None:
        return out
    listed = set(re.findall(r"\]\((0\d{3}-[^)]+\.md)\)", text))
    actual = {os.path.basename(p) for p in docs if p.startswith("docs/decisions/0")}
    for name in sorted(actual - listed):
        out.append((SEV_ERROR, index_rel, "ADR が索引に載っていない: %s" % name, None))
    for name in sorted(listed - actual):
        out.append((SEV_ERROR, index_rel, "索引の行に対応する ADR が無い: %s" % name, None))
    return out


def check_readme_index(root: str, docs):
    """docs 直下の文書が README の索引に載っているか（規約 §8-3）。"""
    out = []
    index_rel = "docs/README.md"
    text = read_text(root, index_rel)
    if text is None:
        return out
    for rel in sorted(docs):
        parts = rel.split("/")
        if len(parts) != 2 or parts[0] != "docs":
            continue
        name = parts[1]
        if name == "README.md":
            continue
        if name not in text:
            out.append((SEV_WARN, index_rel, "索引に載っていない文書: %s" % name, None))
    return out


# --------------------------------------------------------------------------- #
# --stats
# --------------------------------------------------------------------------- #

def stats(root: str):
    docs = {p: Doc(root, p) for p in tracked_docs(root)}
    by_status = Counter()
    total_lines = Counter()
    read_often = []

    for rel, doc in docs.items():
        status = doc.status or "(無し)"
        by_status[status] += 1
        total_lines[status] += len(doc.lines)
        if status == "current" and not rel.startswith("docs/decisions/"):
            read_often.append((len(doc.lines), rel))

    print("== 文書数と行数 ==")
    for status, count in by_status.most_common():
        print("  %-12s %3d 本 / %6d 行" % (status, count, total_lines[status]))
    print("  %-12s %3d 本 / %6d 行" % ("合計", sum(by_status.values()), sum(total_lines.values())))

    often_lines = sum(n for n, _ in read_often)
    print()
    print("== 管理指標: 頻繁に読む current（decisions/ を除く）==")
    print("  %d 本 / %d 行" % (len(read_often), often_lines))
    print()
    print("== 長い current（上位 10 本）==")
    for count, rel in sorted(read_often, reverse=True)[:10]:
        mark = " [append]" if docs[rel].get("growth") == "append" else ""
        print("  %5d 行  %s%s" % (count, rel, mark))
    return 0


# --------------------------------------------------------------------------- #
# --impact
# --------------------------------------------------------------------------- #

def impact(root: str, ref: str):
    diff = git(root, "diff", "--name-only", "%s..HEAD" % ref)
    changed = [p.strip() for p in diff.splitlines() if p.strip()]
    if not changed:
        diff = git(root, "diff", "--name-only", ref)
        changed = [p.strip() for p in diff.splitlines() if p.strip()]

    touched = sorted({m for m in (module_of_path(p) for p in changed) if m})
    print("== 変更されたモジュール（%s..HEAD）==" % ref)
    if not touched:
        print("  なし（Designer/Design/Modules 配下の変更が無い）")
        return 0
    for name in touched:
        print("  " + name)

    hits_modules = defaultdict(list)
    hits_verifies = defaultdict(list)
    for rel in tracked_docs(root):
        if not rel.startswith(SCENARIO_DIR):
            continue
        doc = Doc(root, rel)
        if doc.fm is None:
            continue
        for name in as_list(doc.get("modules")):
            if name in touched:
                hits_modules[rel].append(name)
        for name in as_list(doc.get("verifies")):
            if name in touched:
                hits_verifies[rel].append(name)

    print()
    print("== 影響する体験シナリオ ==")
    if not hits_modules and not hits_verifies:
        print("  なし")
    for rel in sorted(set(hits_modules) | set(hits_verifies)):
        tags = []
        if rel in hits_modules:
            tags.append("modules: " + ", ".join(sorted(set(hits_modules[rel]))))
        if rel in hits_verifies:
            tags.append("verifies: " + ", ".join(sorted(set(hits_verifies[rel]))))
        print("  %s" % rel[len(SCENARIO_DIR) + 1:])
        print("      " + " / ".join(tags))
    print()
    print("→ ①導線・画面名・項目名 の層をこのコミットで直したか確認する（規約 §6-2）")
    return 0


# --------------------------------------------------------------------------- #
# --stale
# --------------------------------------------------------------------------- #

def module_last_change(root: str):
    """モジュール名 -> 最終変更日（YYYY-MM-DD）。"""
    out = {}
    log = git(root, "log", "--format=@%ad", "--date=short", "--name-only",
              "--", "Designer/Design/Modules")
    date = None
    for line in log.splitlines():
        line = line.strip()
        if line.startswith("@"):
            date = line[1:]
        elif line and date:
            name = module_of_path(line)
            if name and name not in out:
                out[name] = date
    return out


def stale(root: str):
    changes = module_last_change(root)
    rows = []
    for rel in tracked_docs(root):
        if not rel.startswith("docs/tests/"):
            continue
        doc = Doc(root, rel)
        if doc.fm is None:
            continue
        verified = doc.get("verified")
        declared = as_list(doc.get("modules")) + as_list(doc.get("verifies"))
        if not verified or not declared:
            continue
        newer = sorted({(changes[m], m) for m in declared
                        if m in changes and changes[m] > str(verified)}, reverse=True)
        if newer:
            rows.append((newer[0][0], rel, verified, newer))

    print("== verified より後にモジュールが変わった文書 ==")
    if not rows:
        print("  なし")
        return 0
    for _newest, rel, verified, newer in sorted(rows, reverse=True):
        print("  %s" % rel)
        print("      verified=%s / 以後に変わったモジュール: %s"
              % (verified, ", ".join("%s(%s)" % (m, d) for d, m in newer[:6])))
    print()
    print("→ 通し直して verified を更新するか、腐りを保留リストへ積む（規約 §4-3）")
    return 0


# --------------------------------------------------------------------------- #

def main():
    parser = argparse.ArgumentParser(description="ドキュメント規約リンタ")
    parser.add_argument("--stats", action="store_true", help="current の行数など指標を出す")
    parser.add_argument("--impact", metavar="REF", help="REF..HEAD の変更が影響する体験シナリオ")
    parser.add_argument("--stale", action="store_true", help="verified より後に変わった文書")
    parser.add_argument("--warn-only", action="store_true", help="warn のみ表示")
    args = parser.parse_args()

    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass

    root = repo_root()

    if args.stats:
        return stats(root)
    if args.impact:
        return impact(root, args.impact)
    if args.stale:
        return stale(root)

    findings = lint(root)
    errors = [f for f in findings if f[0] == SEV_ERROR]
    warns = [f for f in findings if f[0] == SEV_WARN]

    for sev, group in ((SEV_ERROR, errors), (SEV_WARN, warns)):
        if args.warn_only and sev == SEV_ERROR:
            continue
        if not group:
            continue
        print("== %s: %d 件 ==" % (sev, len(group)))
        for _sev, rel, msg, line in sorted(group, key=lambda f: (f[1], f[3] or 0)):
            where = "%s:%d" % (rel, line) if line else rel
            print("  %s  %s" % (where, msg))
        print()

    print("error %d / warn %d" % (len(errors), len(warns)))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
