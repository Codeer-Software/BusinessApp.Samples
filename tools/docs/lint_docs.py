#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""lint_docs.py — ドキュメント規約の機械検査.

仕様書: docs/00_ドキュメント規約.md

長期開発でドキュメントが腐り、肥大化するのを防ぐ。検査するのは次の 5 点である。
  1. 読まなくていい文書を判別できるか（フロントマターと status）
  2. 索引・ADR 台帳と実ファイルが食い違っていないか
  3. current でない文書をコード（コメント）が参照していないか
     （開発者の提案。2026-08-25。意図的な歴史参照は行に lint-docs:ignore を書く）
  4. current な文書が superseded な文書へリンクしていないか
     （開発者の提案。2026-08-28。読者を古い決定へ連れて行かないため）
  5. 本文を変えたのに updated: を今日にしていない文書がないか
     （開発者の指示。2026-08-27。横断レビューで 7 文書のずれが見つかったため）

使い方:
    python tools/docs/lint_docs.py          # 規約違反の検査（error / warn）
    python tools/docs/lint_docs.py --stats  # current の行数など指標

終了コード: 0 = error なし / 1 = error あり / 2 = 実行失敗

Python 3.8+ / 標準ライブラリのみ（YAML パーサは使わず、必要な範囲だけ自前で読む）。
"""

from __future__ import annotations

import argparse
import datetime
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


# 日本語のパスを git が ã のように引用して返さないようにする。
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


def successor_of(target_doc: Doc, docs_by_rel: Dict[str, Doc]) -> Optional[str]:
    """`superseded` な文書の `related` から、**`current` な**後継を 1 本選んでリポジトリ相対で返す。

    `related` の先頭をそのまま出さない。実データで **`related[0]` 自身が `superseded`** の文書が
    2 本ある（後継がさらに覆された組。2026-08-28 に実測）ので、
    先頭を機械的に案内すると**新しい違反へ誘導する**。相対パスのまま出すのも駄目である——
    後継のパスは「対象文書から見た相対」なので、指摘を受ける文書から見ると別の場所を指す。
    """
    for t in target_doc.list_field("related"):
        rel = resolve(target_doc, t)
        d = docs_by_rel.get(rel)
        if d is not None and d.status == "current":
            return rel
    return None


def check_superseded_links(doc: Doc, docs_by_rel: Dict[str, Doc],
                           findings: List[Tuple[str, str, str]]) -> int:
    """`current` な文書の本文が `superseded` な文書へ**リンク**していたら error。

    （開発者の提案。2026-08-28。コード側は `check_code_references` が既に見ていた。
    リンクに限ることと免除の範囲は設計側で決め、開発者が承認した）

    **見るのはリンクだけである。** 番号や題名を地の文で挙げるのは咎めない——
    リンクは読者をそこへ**連れて行く**が、散文の言及は経緯の記述として無害だからである。
    対象は `superseded` のみ。`historical`（見送った記録）は当時の記録として参照するのが正しい。

    咎めないもの:
      - フロントマター（`supersedes:` / `related:` は関係を記録する場所。本文だけを見る）
      - コードフェンスの中（規則の例を貼った文書が自分で落ちないように）
      - 自分が `supersedes:` に挙げている文書へのリンク（**後継は前身を語ってよい**）
      - ADR 台帳 `docs/decisions/README.md`（**全 ADR を載せるのが仕事**。`growth` とは無関係の免除）
      - `lint-docs:ignore` がある行（**その行の検査を全部**免除する。行単位である）

    `growth: append` は免除しない。実測すると、それで通っていたのは ADR 台帳の 4 件だけで、
    他の記録文書（qa/01〜04・11_CLB改善提案）には superseded へのリンクが 1 件も無かった
    （2026-08-28）。**記録文書でも、今日足す行は現在形として読まれる。**

    **連鎖は見ない**（0002 → 0024 → 0029 のように後継自身が superseded になっても、
    その中間へのリンクは前身特権で通る）。今は許容する（開発者の判断。2026-08-28）。

    戻り値は**見た superseded 宛リンクの数**（免除した分を含む）。
    `main` がこれを印字する——0 に落ちたら、配線が死んだか免除が広がりすぎたかである。
    """
    if doc.status != "current" or doc.rel == ADR_LEDGER:
        return 0
    predecessors = {resolve(doc, t) for t in doc.list_field("supersedes")}
    seen = 0
    in_code = False
    for i in range(doc.body_start, len(doc.lines)):
        line = doc.lines[i]
        if line.lstrip().startswith("```"):
            in_code = not in_code
            continue
        if in_code:
            continue
        for target in MD_LINK.findall(line):
            t = target.strip()
            if not t or t.startswith(("http", "mailto:")):
                continue
            resolved = resolve(doc, t)
            target_doc = docs_by_rel.get(resolved)
            if target_doc is None or target_doc.status != "superseded":
                continue
            seen += 1
            if resolved in predecessors or INLINE_IGNORE in line:
                continue
            hint = successor_of(target_doc, docs_by_rel)
            findings.append((SEV_ERROR, doc.rel,
                             "{}行目: superseded な文書へリンクしています: {}（{} へ張り替えるか、"
                             "経緯として要る行に {} を書く）"
                             .format(i + 1, resolved,
                                     "後継 " + hint if hint else "current な後継が related にありません",
                                     INLINE_IGNORE)))
    return seen


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
    ledger_rel = ADR_LEDGER
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


def check_code_references(docs: List[Doc], findings: List[Tuple[str, str, str]]) -> None:
    """current でない文書（superseded / historical）をコードのコメントが参照していたら error。

    参照の形は 3 つを見る: ファイル名（basename）・ADR 番号（ADR-0007）・
    番号つき文書の短縮形（docs/07）。コードのコメントは実装と一緒に読まれるので、
    腐った参照は後継文書へ張り替える。歴史として意図的に参照する行には
    lint-docs:ignore を書く。
    """
    stale_docs = [d for d in docs if d.meta and d.status and d.status != "current"]
    if not stale_docs:
        return

    patterns: List[Tuple[str, str, "re.Pattern[str]"]] = []
    for d in stale_docs:
        pats = [re.escape(os.path.basename(d.rel))]
        m = re.match(r"docs/decisions/(\d{4})-", d.rel)
        if m:
            pats.append(r"ADR-" + m.group(1) + r"\b")
        m = re.match(r"docs/(\d{2})_", d.rel)
        if m:
            pats.append(r"docs/" + m.group(1) + r"\b")
        patterns.append((d.rel, d.status, re.compile("|".join(pats))))

    for rel in run_git(["ls-files"]):
        rel_posix = rel.replace("\\", "/")
        if not rel_posix.endswith(CODE_EXTENSIONS) or rel_posix.startswith(CODE_EXCLUDE_PREFIXES):
            continue
        path = os.path.join(REPO_ROOT, rel)
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                lines = f.read().splitlines()
        except OSError:
            continue
        for i, line in enumerate(lines):
            if INLINE_IGNORE in line:
                continue
            for doc_rel, status, pattern in patterns:
                if pattern.search(line):
                    findings.append((SEV_ERROR, rel_posix,
                                     "{}行目: {} な文書を参照しています: {}（後継へ張り替えるか、"
                                     "歴史参照なら行に {} を書く）".format(i + 1, status, doc_rel, INLINE_IGNORE)))


def body_of(text: str) -> List[str]:
    """フロントマターを除いた本文の行を返す。"""
    lines = text.splitlines()
    _, start = parse_front_matter(lines)
    return lines[start:]


def updated_violation(rel: str, old_body: Optional[List[str]], new_body: List[str],
                      updated: str, today: str) -> Optional[str]:
    """本文が変わっているのに `updated:` が今日でなければ、その理由を返す（純粋関数）。

    `old_body` が None は「HEAD にそのパスが無い」＝新規追加・改名。本文が同じでも
    日付を要求する（どちらも「文書として新しくなった」ため。規約 §3-1）。
    """
    if old_body == new_body:
        return None
    if updated == today:
        return None
    return ("本文を変えたので updated: を {} にしてください（いまは {}）。"
            "フロントマターだけの変更なら動かさなくてよい。"
            "**日付をまたいだだけのときも今日に直す**（規約 §3-1）".format(today, updated or "空"))


def check_updated_freshness(docs: List[Doc], findings: List[Tuple[str, str, str]]) -> None:
    """本文を変えたのに `updated:` を今日にしていない文書を error にする。

    `updated` の意味は「**フロントマター以外の行**を最後に変えた日」である（規約 §3-1）。
    「体裁だけの変更か」は機械には判定できないので、体裁でも動かす規則にしてある。
    比較の対象は作業ツリーと HEAD——フックは作業ツリーを検査するため（ADR-0012 §7）。

    HEAD が無いリポジトリ（最初のコミットの前）では何も見ない。
    マージ・cherry-pick・rebase の途中も見ない——取り込んだ他人の変更に対して
    「今日の日付にしろ」と言っても意味がなく、衝突の解決を妨げるだけである。
    **飛ばしたことは黙らず印字する**（黙って素通りする関門を作らないため）。
    """
    if git_text(["rev-parse", "--verify", "HEAD"], allow_failure=True) is None:
        return
    git_dir = git_text(["rev-parse", "--git-dir"])
    if git_dir:
        base = os.path.join(REPO_ROOT, git_dir.strip())
        for marker in ("MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD",
                       "rebase-merge", "rebase-apply"):
            if os.path.exists(os.path.join(base, marker)):
                print("note	{}	マージ／rebase の途中なので updated: の検査を飛ばしました"
                      .format(marker))
                return

    changed = git_text(["diff", "--name-only", "HEAD", "--", "*.md"])
    changed_set = {l.strip().replace("\\", "/") for l in (changed or "").splitlines() if l.strip()}
    if not changed_set:
        return

    today = datetime.date.today().isoformat()
    for doc in docs:
        if doc.rel not in changed_set:
            continue
        old = git_text(["show", "HEAD:{}".format(doc.rel)], allow_failure=True)
        old_body = body_of(old) if old is not None else None
        msg = updated_violation(doc.rel, old_body, doc.lines[doc.body_start:],
                                doc.meta.get("updated", ""), today)
        if msg:
            findings.append((SEV_ERROR, doc.rel, msg))


def check_updated_history(docs: List[Doc], findings: List[Tuple[str, str, str]]) -> None:
    """**コミット済み**の腐りを見る。本文を最後に変えたコミットの日より `updated:` が古ければ error。

    作業ツリーの検査（`check_updated_freshness`）は、フックを迂回した分・部分ステージした分・
    フックを入れていない clone で入った分を見られない。こちらは履歴そのものを突き合わせる。

    日付は **author 日**で比べる（amend / rebase で committer 日だけが動くため）。
    日付をまたいでコミットした分を叩かないよう、**1 日の猶予**を置く。
    """
    if git_text(["rev-parse", "--verify", "HEAD"], allow_failure=True) is None:
        return
    for doc in docs:
        updated = doc.meta.get("updated", "")
        if not re.match(r"^\d{4}-\d{2}-\d{2}$", updated):
            continue  # 書式そのものの error は check_front_matter が出す
        log = git_text(["log", "--follow", "--format=%H %as", "--", doc.rel])
        body_date = None
        prev_body: Optional[List[str]] = None
        for line in (log or "").splitlines():
            sha, _, date = line.partition(" ")
            if not sha:
                continue
            text = git_text(["show", "{}:{}".format(sha, doc.rel)], allow_failure=True)
            body = body_of(text) if text is not None else None
            if prev_body is not None and body != prev_body:
                body_date = last_date
                break
            prev_body, last_date = body, date.strip()
        else:
            body_date = last_date if prev_body is not None else None
        if body_date is None:
            continue
        try:
            limit = (datetime.date.fromisoformat(body_date) - datetime.timedelta(days=1)).isoformat()
        except ValueError:
            continue
        if updated < limit:
            findings.append((SEV_ERROR, doc.rel,
                             "updated: が {} ですが、本文を最後に変えたコミットは {} です"
                             "（履歴との突合。規約 §3-1）".format(updated, body_date)))


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


ALL_CHECKS = (
    "check_front_matter", "check_links", "check_superseded_links", "check_body",
    "check_adr_ledger", "check_docs_index", "check_code_references",
    "check_updated_freshness", "check_updated_history",
)


def selftest() -> int:
    """関門そのものを検査する。**中身を空にしても緑**という状態を作らないため。

    ここで見るのは 3 つ。
      1. 判定の純粋部分（`updated_violation` / `body_of`）が期待どおり鳴るか
      2. **わざと壊した入力**で `check_superseded_links` が鳴り、免除の形では鳴らないか
      3. 定義した検査が全部 `main` から呼ばれているか（呼び出しを消しても誰も気づかない事故を防ぐ）
    """
    failed = 0
    fm = ["---", "title: x", "updated: 2026-08-27", "---"]
    cases = [
        # (old_body, new_body, updated, today, 鳴るべきか)
        (["a"], ["a"], "2026-08-01", "2026-08-27", False),  # 本文が同じ＝フロントマターだけの変更
        (["a"], ["b"], "2026-08-27", "2026-08-27", False),  # 本文を変えて今日にした
        (["a"], ["b"], "2026-08-26", "2026-08-27", True),   # 本文を変えたのに据え置き
        (["a"], ["b"], "", "2026-08-27", True),             # updated が無い
        (None, ["a"], "2026-08-26", "2026-08-27", True),    # HEAD に無いパス（新規・改名）
        (None, ["a"], "2026-08-27", "2026-08-27", False),
    ]
    for old_body, new_body, updated, today, should in cases:
        got = updated_violation("x.md", old_body, new_body, updated, today) is not None
        if got != should:
            failed += 1
            print("NG  updated_violation: 期待 {} / 実際 {}: {}".format(should, got, (old_body, new_body, updated)))

    if body_of(chr(10).join(fm + ["本文"])) != ["本文"]:
        failed += 1
        print("NG  body_of: フロントマターを落とせていない")
    if body_of("フロントマター無し") != ["フロントマター無し"]:
        failed += 1
        print("NG  body_of: フロントマターが無い文書を落としてしまった")

    # check_superseded_links: 壊した入力で鳴ること、免除の形で鳴らないことの両方を見る。
    # 「足したら一度わざと壊して鳴ることを確かめる」（qa/03 L-15）を関門の中に固定してある。
    def fake(rel: str, meta: Dict[str, str], body: List[str]) -> Doc:
        lines = ["---"] + ["{}: {}".format(k, v) for k, v in meta.items()] + ["---"] + body
        parsed, start = parse_front_matter(lines)
        return Doc(rel, lines, parsed, start)

    dead = fake("docs/decisions/0019-old.md",
                {"status": "superseded",
                 # related[0] が superseded の実データがあるので、その形で入れる
                 "related": "[0006-older.md, 0029-new.md]"}, ["中身"])
    older = fake("docs/decisions/0006-older.md",
                 {"status": "superseded", "related": "[0019-old.md]"}, ["中身"])
    gone = fake("docs/decisions/0018-hist.md",
                {"status": "historical", "related": "[0029-new.md]"}, ["中身"])
    live = fake("docs/09_other.md", {"status": "current"}, ["本文"])
    new = fake("docs/decisions/0029-new.md", {"status": "current"}, ["本文"])
    far = fake("docs/08_dead.md", {"status": "superseded", "related": "[09_other.md]"}, ["中身"])
    by_rel = {d.rel: d for d in (dead, older, gone, live, new, far)}
    to_dead, to_live = "[旧](decisions/0019-old.md)", "[今](09_other.md)"
    cur = {"status": "current"}
    link_cases = [
        # (rel, meta, body, 鳴るべき件数)
        ("docs/10_x.md", cur, [to_dead], 1),                                  # わざと壊した入力
        ("docs/10_x.md", cur, [to_dead, to_dead], 2),                         # 別の行なら別に鳴る
        ("docs/10_x.md", cur, [to_dead + " " + to_dead], 2),                  # 同じ行に 2 本でも鳴る
        # 免除は行単位である。同じ行の**無関係なリンクまで**免除されるのが現在の仕様
        ("docs/10_x.md", cur, [to_dead + " " + to_dead + " " + INLINE_IGNORE], 0),
        ("docs/10_x.md", cur, [to_live], 0),
        ("docs/10_x.md", cur, ["0019-old.md は既に覆されている"], 0),          # 散文は咎めない
        ("docs/10_x.md", cur, ["[外](https://example.com/0019-old.md)"], 0),
        ("docs/10_x.md", cur, ["[未](decisions/0019-nowhere.md)"], 0),        # 実在しない
        ("docs/10_x.md", cur, ["[印](decisions/0018-hist.md)"], 0),           # historical は対象外
        ("docs/10_x.md", cur, ["[節](decisions/0019-old.md#2-決定)"], 1),     # アンカー付き
        ("docs/10_x.md", cur, ["[空]( decisions/0019-old.md )"], 1),          # 前後の空白を落とす
        ("docs/decisions/0030-y.md", cur, ["[上](../08_dead.md)"], 1),        # `../` 起点
        ("docs/10_x.md", cur, ["```", to_dead, "```"], 0),                    # コードフェンスの中
        ("docs/10_x.md", {"status": "superseded", "related": "[09_other.md]"}, [to_dead], 0),
        ("docs/10_x.md", {"status": "historical", "related": "[09_other.md]"}, [to_dead], 0),
        # `growth: append` は免除しない（2026-08-28 に外した。理由は関数の docstring）
        ("docs/10_x.md", {"status": "current", "growth": "append"}, [to_dead], 1),
        # フロントマターは対象外。**リンクの形をした値**を置いて、本文だけを見ていることを表明する
        ("docs/10_x.md", {"status": "current", "note": to_dead}, ["本文"], 0),
        # 後継は前身を語ってよい
        ("docs/decisions/0029-new.md", {"status": "current", "supersedes": "[0019-old.md]"},
         ["[旧](0019-old.md)"], 0),
        # ADR 台帳は役割で免除する。**実データと同じく `growth: append` を付けた形**で表明する
        (ADR_LEDGER, {"status": "current", "growth": "append"}, ["[旧](0019-old.md)"], 0),
    ]
    for rel, meta, body, want in link_cases:
        got: List[Tuple[str, str, str]] = []
        check_superseded_links(fake(rel, meta, body), by_rel, got)
        if len(got) != want:
            failed += 1
            print("NG  check_superseded_links: 期待 {} 件 / 実際 {} 件: {} {}"
                  .format(want, len(got), rel, body))

    # 件数だけでなく**中身**を表明する。error を warn に格下げしても件数は変わらないため
    probe = fake("docs/10_x.md", cur, [to_dead])
    got = []
    seen = check_superseded_links(probe, by_rel, got)
    want_line = "{}行目".format(probe.body_start + 1)
    for label, ok in (
        ("severity が error", got and got[0][0] == SEV_ERROR),
        ("指摘先が違反した文書", got and got[0][1] == "docs/10_x.md"),
        ("行番号が本文の実行番号", got and want_line in got[0][2]),
        ("リンク先をリポジトリ相対で出す", got and "docs/decisions/0019-old.md" in got[0][2]),
        # related[0] は superseded（0006）。**current な 0029 を案内する**こと
        ("後継は current を選ぶ", got and "docs/decisions/0029-new.md" in got[0][2]),
        ("superseded な related を案内しない", got and "0006-older.md" not in got[0][2]),
        ("直し方を書く", got and INLINE_IGNORE in got[0][2]),
        ("見た件数を返す", seen == 1),
    ):
        if not ok:
            failed += 1
            print("NG  check_superseded_links: {}: {}".format(label, got))
    if successor_of(older, by_rel) is not None:
        failed += 1
        print("NG  successor_of: current な後継が無いのに何かを返した")

    # 実データで一度通す。`resolve()` の結果が `load_docs()` のキーと噛み合っているか、
    # main の配線が生きているかは、偽 Doc では分からない（qa/03 L-15 の型）
    real_docs, _ = load_docs()
    real_by_rel = {d.rel: d for d in real_docs}
    sink: List[Tuple[str, str, str]] = []
    real_seen = sum(check_superseded_links(d, real_by_rel, sink) for d in real_docs)
    if real_seen < 1:
        failed += 1
        print("NG  check_superseded_links: 実データで superseded 宛リンクを 1 本も見ていない"
              "（免除が広がりすぎたか、パスの解決が噛み合っていない。**0 は緑ではない**）")

    src = open(os.path.abspath(__file__), "r", encoding="utf-8").read()
    # 行頭の定義を取る。`src.index("def main()")` だと**この selftest の中の文字列リテラル**に
    # 当たり、main_src が selftest の末尾を巻き込む。そこへ検査の直呼びを書くと
    # 「main から呼ばれている」の表明が自分で自分を満たしてしまう
    main_at = re.search(r"^def main\(\)", src, re.M)
    if main_at is None:
        print("NG  def main() が見つからない")
        return 1
    main_src = src[main_at.start():]
    # 引数の配線まで見る。名前だけの包含だと `check_superseded_links(d, {}, findings)` が通る
    if "check_superseded_links(d, docs_by_rel, findings)" not in main_src:
        failed += 1
        print("NG  check_superseded_links が main で docs_by_rel を渡されていない")
    defined = set(re.findall(r"^def (check_[A-Za-z0-9_]+)\(", src, re.M))
    for name in sorted(defined - set(ALL_CHECKS)):
        failed += 1
        print("NG  {} が ALL_CHECKS に載っていない（足した検査は必ず載せる）".format(name))
    for name in ALL_CHECKS:
        if name + "(" not in main_src:
            failed += 1
            print("NG  {} が main から呼ばれていない".format(name))
        if "def {}(".format(name) not in src:
            failed += 1
            print("NG  {} が定義されていない".format(name))

    print("lint_docs: すべて期待どおり" if failed == 0 else "lint_docs: {} 件が期待と違う".format(failed))
    return 1 if failed else 0


def main() -> int:
    ap = argparse.ArgumentParser(description="ドキュメント規約の検査")
    ap.add_argument("--stats", action="store_true", help="指標を表示する")
    ap.add_argument("--selftest", action="store_true", help="関門そのものを検査する")
    args = ap.parse_args()

    if args.selftest:
        return selftest()

    docs, unreadable = load_docs()
    if args.stats:
        print_stats(docs)
        return 0

    existing = {d.rel for d in docs}
    docs_by_rel = {d.rel: d for d in docs}
    findings: List[Tuple[str, str, str]] = []
    for rel in unreadable:
        findings.append((SEV_ERROR, rel, "文書を読めませんでした。**検査できていない**ので黙って進まない"))
    seen_superseded_links = 0
    for d in docs:
        check_front_matter(d, findings)
        check_links(d, existing, findings)
        seen_superseded_links += check_superseded_links(d, docs_by_rel, findings)
        check_body(d, findings)
    check_adr_ledger(docs, findings)
    check_docs_index(docs, findings)
    check_code_references(docs, findings)
    check_updated_freshness(docs, findings)
    check_updated_history(docs, findings)

    errors = [f for f in findings if f[0] == SEV_ERROR]
    warns = [f for f in findings if f[0] == SEV_WARN]
    for sev, rel, msg in sorted(findings, key=lambda f: (f[0] != SEV_ERROR, f[1], f[2])):
        print("{}\t{}\t{}".format(sev, rel, msg))

    print("")
    # superseded 宛リンクの数を必ず出す。0 に落ちたら「違反が無い」ではなく
    # 「配線が死んだ・免除が広がりすぎた」を疑う（黙って素通りする関門を作らないため）
    print("検査文書数: {} / error: {} / warn: {} / superseded 宛リンク: {} 件を検査"
          .format(len(docs), len(errors), len(warns), seen_superseded_links))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
