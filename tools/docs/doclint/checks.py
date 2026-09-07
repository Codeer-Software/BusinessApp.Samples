#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""doclint.checks — 検査 11 本と、その純粋な判定部分.

**足した検査は必ず `ALL_CHECKS` に載せる**（`selftest.py` が突合し、`main` から
呼ばれているかまで見る）。所見は `(severity, ファイル, メッセージ)` の組で積む。
"""

from __future__ import annotations

import datetime
import os
import re
from typing import Dict, List, NamedTuple, Optional, Tuple

from .model import (ADR_LEDGER, APPEND_ANTIPATTERN, CODE_EXTENSIONS, excluded_from_code_check,
                    DATE_RE, DOCS_INDEX, Doc, GENERIC_DOC_NAMES, INLINE_IGNORE, LINE_LIMIT, MD_LINK,
                    REFERENCE_PREFIXES, REPO_ROOT, REQUIRED_KEYS, SEV_ERROR, SEV_WARN,
                    STALE_MARKER, VALID_AUDIENCE, VALID_STATUS, body_of, git_text,
                    resolve, run_git)

Finding = Tuple[str, str, str]

ALL_CHECKS = (
    "check_front_matter", "check_links", "check_superseded_links", "check_body",
    "check_adr_ledger", "check_docs_index", "check_code_references",
    "check_section_references", "check_updated_freshness", "check_updated_history",
    "check_article_notation", "check_dated_switches",
)


def check_front_matter(doc: Doc, findings: List[Finding]) -> None:
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


def check_links(doc: Doc, existing: set, findings: List[Finding]) -> None:
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
                           findings: List[Finding]) -> int:
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
      - ADR 台帳（**全 ADR を載せるのが仕事**。`growth` とは無関係の免除）
      - `lint-docs:ignore` がある行（**その行の検査を全部**免除する。行単位である）

    `growth: append` は免除しない。実測すると、それで通っていたのは ADR 台帳の 4 件だけで、
    他の記録文書（qa/01〜04・CLB改善提案）には superseded へのリンクが 1 件も無かった
    （2026-08-28）。**記録文書でも、今日足す行は現在形として読まれる。**

    **連鎖は見ない**（後継自身がさらに superseded になっても、その中間へのリンクは
    前身特権で通る）。今は許容する（開発者の判断。2026-08-28）。

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
            how = ("後継 {} へ張り替えるか、経緯として要る行に {} を書く".format(hint, INLINE_IGNORE)
                   if hint else
                   "related に current な後継がありません。経緯として要る行に {} を書く"
                   .format(INLINE_IGNORE))
            findings.append((SEV_ERROR, doc.rel,
                             "{}行目: superseded な文書へリンクしています: {}（{}）"
                             .format(i + 1, resolved, how)))
    return seen


def check_body(doc: Doc, findings: List[Finding]) -> None:
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


def check_adr_ledger(docs: List[Doc], findings: List[Finding]) -> None:
    ledger = next((d for d in docs if d.rel == ADR_LEDGER), None)
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
        findings.append((SEV_ERROR, ADR_LEDGER, "台帳に載っていない ADR があります: {}".format(missing)))
    for ghost in sorted(listed - actual):
        findings.append((SEV_ERROR, ADR_LEDGER, "台帳の行に対応する ADR がありません: {}".format(ghost)))


def check_code_references(docs: List[Doc], findings: List[Finding]) -> None:
    """current でない文書（superseded / historical）をコードのコメントが参照していたら error。

    参照の形は 3 つを見る: ファイル名（basename）・ADR 番号（ADR-0007）・
    番号つき文書の短縮形（docs/13）。コードのコメントは実装と一緒に読まれるので、
    腐った参照は後継文書へ張り替える。歴史として意図的に参照する行には
    lint-docs:ignore を書く。
    """
    stale_docs = [d for d in docs if d.meta and d.status and d.status != "current"]
    if not stale_docs:
        return

    patterns: List[Tuple[str, str, "re.Pattern[str]"]] = []
    for d in stale_docs:
        name = os.path.basename(d.rel)
        # `README.md` のような汎用名を素で使うと、その語を含むコード行が丸ごと error になる。
        # 親ディレクトリ込みで照合する（2026-08-28。実測で 4 ファイル 6 行が巻き添えだった）
        pats = [re.escape("/".join(d.rel.split("/")[-2:]) if name in GENERIC_DOC_NAMES else name)]
        m = re.match(r"docs/decisions/(\d{4})-", d.rel)
        if m:
            pats.append(r"ADR-" + m.group(1) + r"\b")
        m = re.match(r"docs/(\d{2})_", d.rel)
        if m:
            pats.append(r"docs/" + m.group(1) + r"\b")
        patterns.append((d.rel, d.status, re.compile("|".join(pats))))

    for rel in run_git(["ls-files"]):
        rel_posix = rel.replace("\\", "/")
        if not rel_posix.endswith(CODE_EXTENSIONS) or excluded_from_code_check(rel_posix):
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


SECTION_NO = r"[0-9]+(?:-[0-9]+)*"
HEADING_RE = re.compile(r"^#{2,6} +(" + SECTION_NO + r")\.", re.M)
SECTION_REF_RE = re.compile(r"§ ?(" + SECTION_NO + r")")
# `[ラベル](先.md)` と、その直後に続く `§4-9`。間に読点や「の」が挟まる書き方も拾う
LINK_THEN_SECTION_RE = re.compile(r"\[([^\]]*)\]\(([^)\s]+\.md)\)([^\n]{0,8})")
# リンクを張れないコードのコメントのための `CLAUDE.md §5`
BARE_CLAUDE_REF_RE = re.compile(r"(?<![\w/.])CLAUDE\.md.{0,3}?§ ?(" + SECTION_NO + r")")
# 同じくリンクを張れない場所で使う `docs/10 §1` の短縮形。
# `../docs/10 §1` は拾い、`他のリポジトリ/docs/04` は拾わない（上りだけを接頭辞に許す）。
# `docs/10_会計ドメイン設計.md §1` は上の LINK 系が拾うので `_` の手前で切る
BARE_DOCS_REF_RE = re.compile(
    r"(?<![\w/])(?:\.\.?/)*docs/([0-9]{2})(?![0-9_\w])[ 　]?§ ?(" + SECTION_NO + r")")
# リンクを張らずにファイル名で書く `docs/21_画面の原則.md §2`（コードのコメントに多い）
BARE_DOCS_FILE_RE = re.compile(
    r"(?<![\w/(])(?:\.\.?/)*docs/([0-9]{2}_[^\s\)\]\"'`、。]+\.md)[ 　]?§ ?(" + SECTION_NO + r")")
# 番号だけで指す公式の短縮形 `21 §3`（規約が「節だけで指してよい」と定めている）。
# `ADR-0021 §4`・`qa/04 §2`・`0026 §1` を拾わないよう、直前の 1 字で切る
BARE_NUM_REF_RE = re.compile(
    r"(?<![\w/\-§])([0-9]{2}) ?§ ?(" + SECTION_NO + r")")


def docs_num_target(no, entries):
    """`docs/NN` の NN から、docs 直下の実体のパスを引く（純粋関数）。

    `entries` は `docs/` 直下の名前の一覧。**番号から題名を推測しない**——
    改番で意味が変わる番号を静的な表で持つと、表の側が腐る。
    見つからなければ None（＝そんな番号の文書は無い）。
    """
    for name in sorted(entries):
        base = name[:-3] if name.endswith(".md") else name
        if re.fullmatch(no + r"_.+", base):
            return "docs/" + name
    return None


def _headings_of(rel, cache):
    """`rel` の番号つき見出しの集合。番号を振っていない文書は None（検査の対象外）。

    ディレクトリなら配下の `.md` の見出しの**和**を返す——`00_ドキュメント規約` は
    3 冊で節番号を共有しており、`00 §4-9` はそのうち 1 冊にしかない。
    """
    if rel in cache:
        return cache[rel]
    full = os.path.join(REPO_ROOT, rel)
    if os.path.isdir(full):
        found = set()
        for name in sorted(os.listdir(full)):
            if name.endswith(".md"):
                sub = _headings_of(rel + "/" + name, cache)
                found |= sub or set()
        cache[rel] = found or None
        return cache[rel]
    try:
        with open(full, "r", encoding="utf-8", errors="replace") as f:
            text = f.read()
    except OSError:
        cache[rel] = None
        return None
    found = set(HEADING_RE.findall(text))
    cache[rel] = found or None
    return cache[rel]


def resolve_rel(src_rel, target):
    """`src_rel` から見た相対パスを、リポジトリ相対に直す（純粋関数）。"""
    joined = os.path.normpath(os.path.join(os.path.dirname(src_rel), target))
    return joined.replace(os.sep, "/")


def section_refs(rel, line, docs_entries=()):
    """1 行から `(指し先の文書, 節番号の一覧)` の組を取り出す（純粋関数）。

    `docs_entries` は `docs/` 直下の名前の一覧。`docs/10 §1` の解決に要る。
    渡さなければその形は見ない（純粋なままにするための注入口）。
    指し先が引けない番号は `docs/NN`（実体なし）のまま返し、呼び手が error にする。
    """
    out = []
    for m in LINK_THEN_SECTION_RE.finditer(line):
        label, target, after = m.group(1), m.group(2), m.group(3)
        if "://" in target:
            continue
        secs = SECTION_REF_RE.findall(label)
        tail = SECTION_REF_RE.match(after.lstrip(" の、"))
        if tail:
            secs.append(tail.group(1))
        if secs:
            out.append((resolve_rel(rel, target), secs))
    for m in BARE_CLAUDE_REF_RE.finditer(line):
        out.append(("CLAUDE.md", [m.group(1)]))
    for m in BARE_DOCS_REF_RE.finditer(line):
        target = docs_num_target(m.group(1), docs_entries)
        out.append((target or "docs/" + m.group(1), [m.group(2)]))
    for m in BARE_DOCS_FILE_RE.finditer(line):
        name = m.group(1)
        out.append(("docs/" + name if name in docs_entries else "docs/" + name[:2],
                    [m.group(2)]))
    for m in BARE_NUM_REF_RE.finditer(line):
        target = docs_num_target(m.group(1), docs_entries)
        out.append((target or "docs/" + m.group(1), [m.group(2)]))
    return out


def check_section_references(docs: List[Doc], findings: List[Finding]) -> None:
    """`§4-12` のような節への参照が、指し先の文書に実在するかを見る。

    **`check_links` はファイルの実在しか見ない。** 節が消えた・番号が動いたときは
    リンクが生きたまま**別の内容を指す**ので、機械が鳴かないと誰も気づかない。
    実際に 2 度起きた（qa/03 の L-18。2026-08-26 と 2026-09-05）。

    拾うのは 5 形。**リンクの札に節を書く形**（`[ラベル §4-9](先.md)` と
    `[ラベル](先.md) §4-9` の 2 通りの書き方を `LINK_THEN_SECTION_RE` 1 本で見る）と、
    **リンクを張れない場所のための 4 つ**——`CLAUDE.md §5`・`docs/10 §1`・
    `docs/21_画面の原則.md §2`・`21 §3`。
    **番号つき見出しを持たない文書への参照は見ない**（その文書は番号で引く作りではない）。
    歴史として古い番号を書く行には lint-docs:ignore を書く。

    `docs/NN` の解決は**作業ツリーの実体**を見る（番号と題名の対応を表に持たない）。
    その番号の文書が無ければ「指し先が無い」として鳴る——**改番で番号が消えたときに、
    リンクを張れない場所の取り残しを捕まえるのはこの経路だけである。**
    """
    cache = {}
    # **追跡下から引く。** 作業ツリーを見ると、置き忘れた未追跡の `docs/05_メモ.md` で
    # 死んだ番号が解決してしまい、取り残しが鳴らなくなる
    docs_entries = sorted({r.split("/")[1] for r in run_git(["ls-files", "docs"])
                           if r.count("/") >= 1})
    for rel in run_git(["ls-files"]):
        rel_posix = rel.replace(os.sep, "/")
        if not rel_posix.endswith(CODE_EXTENSIONS + (".md",)):
            continue
        if excluded_from_code_check(rel_posix):
            continue
        try:
            with open(os.path.join(REPO_ROOT, rel), "r", encoding="utf-8", errors="replace") as f:
                lines = f.read().splitlines()
        except OSError:
            continue
        for i, line in enumerate(lines):
            if INLINE_IGNORE in line:
                continue
            for target, secs in section_refs(rel_posix, line, docs_entries):
                if re.fullmatch(r"docs/[0-9]{2}", target):
                    findings.append((SEV_ERROR, rel_posix,
                                     "{}行目: {} という文書はありません"
                                     "（番号が動いた・文書が消えた。指し先を直すか、"
                                     "歴史として要る行に {} を書く）"
                                     .format(i + 1, target, INLINE_IGNORE)))
                    continue
                heads = _headings_of(target, cache)
                if heads is None:
                    continue
                for sec in secs:
                    if sec not in heads:
                        findings.append((SEV_ERROR, rel_posix,
                                         "{}行目: {} に §{} はありません"
                                         "（番号が動いた・節が消えた。指し先を直すか、"
                                         "歴史として要る行に {} を書く）"
                                         .format(i + 1, target, sec, INLINE_IGNORE)))


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


def check_updated_freshness(docs: List[Doc], findings: List[Finding]) -> None:
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


def check_updated_history(docs: List[Doc], findings: List[Finding]) -> None:
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
        if not DATE_RE.match(updated):
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


def check_docs_index(docs: List[Doc], findings: List[Finding]) -> None:
    """`docs/` 直下は 1 本ずつ、**サブディレクトリはディレクトリ単位**で索引と突き合わせる。

    サブディレクトリを丸ごと飛ばしていたので、文書をディレクトリに分けるたびに
    この検査が薄くなり、しかも警告が 1 件も出ないので誰も気づかなかった
    （2026-08-28 の自己レビュー。規約を 3 分冊にした直後に発覚）。
    """
    index = next((d for d in docs if d.rel == DOCS_INDEX), None)
    if index is None:
        return
    listed = set()
    for line in index.lines:
        for target in MD_LINK.findall(line):
            listed.add(resolve(index, target))
    seen_dirs = set()
    for d in docs:
        if not d.rel.startswith("docs/") or d.rel == DOCS_INDEX:
            continue
        parts = d.rel.split("/")
        if len(parts) == 2:
            if d.rel not in listed:
                findings.append((SEV_WARN, DOCS_INDEX,
                                 "索引に載っていない文書があります: {}".format(d.rel)))
            continue
        sub = "/".join(parts[:2])
        if sub in seen_dirs:
            continue
        seen_dirs.add(sub)
        # ディレクトリそのもの・その中のどれか 1 本が載っていれば案内できている
        if sub not in listed and not any(l.startswith(sub + "/") for l in listed):
            findings.append((SEV_WARN, DOCS_INDEX,
                             "索引に載っていないディレクトリがあります: {}/".format(sub)))


# --- 条項の記法（80 §3。開発者の指示。2026-09-06） ---------------------------
# **数字に続く「条・項・号」そのものを違反とする。** 記法を定めた以上それに統一する、
# というのが決定であり、揃っていないと grep が効かない
# （2027-01-01 の条番号切り替えは、**表記ゆれで 2 回落ちた**。qa/02 の R22-02・R41-01）。
#
# **この関門の限界**（過大に表明しない。qa/02 の R22-05「限界は検査のそばに置く」）:
# - **「置換してはいけない箇所まで置換する」型は防げない**（R22-01・R41-02）。記法の話ではないため
# - **空白と強調記号の揺れは見ない**（80 §3 が「どちらでもよい」と決めている）
# - **汎用の `lint-docs:ignore` でも黙る。** 80 §3-1 は専用の印を使えと書いているが、**機械は区別しない**
#
# **「法令名の直後」を錨にしてはいけない**（2026-09-06 に実測して捨てた）。
# 錨は緩すぎて厳しすぎる——「この方法 3 条件」「命名規則 5 項目」を error にする一方、
# 法令名が別の行にある表のセル（`| 20 条 |`）と箇条書き（`（6項一号）`）を取りこぼし、  lint-docs:article-ok
# **実在した違反 71 行のうち 27 行を拾えなかった**。**外すものを名指しするほうが正しい。**
ARTICLE_UNIT_RE = re.compile(r"第?\s*[0-9０-９]+\s*[条項号](?![目件文機室棟型線番])")
# **法令番号（公布番号）は条項ではない**（80 §3-1 ①）。「〈法令の種類〉第〈N〉号」で見分ける
GAZETTE_RE = re.compile(r"(?:法律|政令|省令|告示|府令)[\s*`]*第?\s*[0-9０-９]+\s*号")
# **この検査だけを外す印**。汎用の `lint-docs:ignore` を使うと
# `check_section_references` まで一緒に黙るので、節への参照を持つ行では**こちらを使う**
ARTICLE_IGNORE = "lint-docs:article-ok"
FENCE_RE = re.compile(r"^\s*(?:```|~~~)")


def article_notation_violations(line: str) -> List[str]:
    """80 §3 の記法に反する条項号の引用を返す（純粋関数）。

    返すのは違反した字面。**「12 項目」「4 条件」「3.1 条文」のような
    法令でない複合語**と、**公布番号**（`政令第128号`）は返さない。
    """
    skip = [m.span() for m in GAZETTE_RE.finditer(line)]
    hits: List[str] = []
    for m in ARTICLE_UNIT_RE.finditer(line):
        if any(a <= m.start() and m.end() <= b for a, b in skip):
            continue
        hits.append(m.group(0).strip())
    return hits


def iter_scan_targets(docs: List[Doc], include_md: bool) -> List[Tuple[str, List[str]]]:
    """検査が歩く `(パス, 行)` を返す。コードは常に、`.md` は `include_md` のときだけ。

    **凍結・生成物の除外述語を `.md` にも当てる**（`.md` を凍結対象に入れた日に、
    1 バイトも直せないファイルを error で叩かないため）。
    """
    targets: List[Tuple[str, List[str]]] = []
    seen = set()
    if include_md:
        for d in docs:
            if not excluded_from_code_check(d.rel):
                targets.append((d.rel, d.lines))
            seen.add(d.rel)
    for rel in run_git(["ls-files"]):
        rel_posix = rel.replace("\\", "/")
        if rel_posix in seen or not rel_posix.endswith(CODE_EXTENSIONS):
            continue
        if excluded_from_code_check(rel_posix):
            continue
        try:
            with open(os.path.join(REPO_ROOT, rel), "r", encoding="utf-8", errors="replace") as f:
                targets.append((rel_posix, f.read().splitlines()))
        except OSError:
            continue
    return targets


def check_article_notation(docs: List[Doc], findings: List[Finding]) -> Tuple[int, int]:
    """`5 条 1 項` の形を error にし、`(走査した行数, 外した行数)` を返す。 lint-docs:article-ok

    走査は `.md` とコードの両方。現行の条番号はコードのコメントにもある。
    **`.md` はフロントマターも見る**（隣の検査は本文だけを見るが、`title:` は索引へ写されて
    人が読む字なので、`title: 電帳規則 5 条 5 項の要件` を通すと規則が骨抜きになる）。  # lint-docs:article-ok lint-docs:switch-ok 検体
    **コードフェンスの中は見ない**（`check_body` と同じ作法。フェンスの中に HTML コメントの
    印を書くと画面にそのまま出てしまい、逐語引用を貼れなくなる）。

    **2 つ返すのは、下がったときに「違反が無い」と読み違えないため。**
    外した行だけだと、走査が死んだのか除外が広がったのかを見分けられない。
    """
    scanned = 0
    ignored = 0
    for rel, lines in iter_scan_targets(docs, include_md=True):
        in_fence = False
        for i, line in enumerate(lines):
            if rel.endswith(".md") and FENCE_RE.match(line):
                in_fence = not in_fence
                continue
            if in_fence:
                continue
            scanned += 1
            hits = article_notation_violations(line)
            if not hits:
                continue
            if INLINE_IGNORE in line or ARTICLE_IGNORE in line:
                ignored += 1
                continue
            findings.append((SEV_ERROR, rel,
                             "{}行目: 条項は 80 §3 の記法で書きます（条＝アラビア数字／項＝丸数字／"
                             "号＝漢数字）。直すか、80 §3-1 の 4 つに当たるなら行に {} を書く: {}"
                             .format(i + 1, ARTICLE_IGNORE, "・".join(hits))))
    return scanned, ignored


# --- 日付で発効する条番号の切替（電帳法リサーチ §0-1。開発者の指示。2026-09-06） ----------
# 2027-01-01 に優良な電子帳簿の要件が電帳規則 5 ⑤ から 5 ④ へ移る。**手順を文章で持ったまま 4 回落ちた**  lint-docs:switch-ok 改番の事実
# （qa/02 の R22-01・R22-02・R41-01・R41-02）ので、**発効日・旧の字面・新の表記をデータ 1 行で持ち**、
# 発効日前は件数を印字、発効日以後は error にする（30 §9 の「機械の関門（成果物）」。ADR-0043）。
#
# **除外は行の印で表す**（`lint-docs:switch-ok 理由`。**理由が無い印は効かない**）。除外の型は 3 つ（電帳法リサーチ §0-1）——
# ①改正後の 5 ⑤（デジタルシームレス保存）を指す記述 ②改番の**事実**だけを述べていて発効日以後も真である記述
# ③税特措規則 9 の 6 ③ が引いている番号。**同じ字面なので型を機械では判別できない**。
# だから型ごとの一覧を持たず、行の印に理由を書かせる。
# **「現行は 5 ⑤」「いま書くもの」のように発効日に偽になる行には印を付けない**——
# 印は発効日以後にしか効かないので、そこに付けると当日に直すべき行が関門から消える（qa/02 の R44-01）。
#
# **この関門の限界**（過大に表明しない。qa/02 の R22-05）:
# - 印を付けた行は発効日以後も見ない。**印の理由が正しいかは人が見る**（機械は理由の有無しか見ない）
# - 発効日以後に「新しい 5 ⑤」の意味で旧の字面が正しく増えるときも印が要る（印を書く手間は置換の事故より軽い）
# - 印は切替ごとではなく共通なので、1 件目のために付けた印は 2 件目からも外れる
# - 発効日前は数えるだけで、**件数が減ったこと**を鳴らさない（早まった置換は selftest のラチェットが見る）
# **この 4 つは ADR-0043 の帰結と同じ列挙である**（片方だけ増やさない）
class DatedSwitch(NamedTuple):
    """日付で発効する字面の切替 1 件。`old` に当たる行は `effective` 以後 error になる。"""
    effective: datetime.date
    old: "re.Pattern[str]"
    label: str          # 人に見せる旧の字面の札（指摘文用。正規表現を見せない）
    new: str
    why: str
    exempt_hint: str    # 印を付けてよい行の型（指摘文用）


DATED_SWITCHES: Tuple[DatedSwitch, ...] = (
    DatedSwitch(
        effective=datetime.date(2027, 1, 1),
        # 半角空白の有無・強調記号・「第」の有無・全角数字・記法違反の形（5条5項）をすべて拾う  lint-docs:article-ok 検索する字面の説明
        # （R41-01 で「第」なしを、R22-02 で空白ゆれを、それぞれ取りこぼした）
        old=re.compile(r"電帳規則\s*(?:第\s*)?[5５]\s*\**(?:⑤|条\s*(?:第\s*)?[5５]\s*項)"),
        label="電帳規則 5 ⑤",  # lint-docs:switch-ok 旧の字面の札
        new="電帳規則 5 ④",
        why="令和 7 年財務省令第 28 号（2027-01-01 施行）で優良な電子帳簿の要件が電帳規則 5 ④ に移る"
            "（電帳法リサーチ §0-1）",
        exempt_hint="改正後の番号・改番の事実（発効日以後も真である行）・税特措規則が引く番号",
    ),
)
SWITCH_IGNORE = "lint-docs:switch-ok"
# **理由を要求する**（印の後ろに空白と 1 字以上）。発効日に人が印を見直す前提だから。
# HTML コメントの閉じ `-->` を理由と読まない（selftest がこれで 1 度落ちた。qa/02 の R44-07）
SWITCH_IGNORE_RE = re.compile(re.escape(SWITCH_IGNORE) + r"[ \t]+(?!--)\S")
# 発効日のこの日数前から、残っている件数を warn で知らせる（発効日に一斉に赤くなるのを避ける）
SWITCH_NOTICE_DAYS = 30

ScanTargets = List[Tuple[str, List[str]]]


def dated_switch_hits(line: str, switch: DatedSwitch) -> List[str]:
    """`switch.old` に当たる字面を返す（純粋関数）。"""
    return [m.group(0).strip() for m in switch.old.finditer(line)]


def check_dated_switches(docs: List[Doc], findings: List[Finding],
                         today: Optional[datetime.date] = None,
                         targets: Optional[ScanTargets] = None) -> Tuple[int, int]:
    """発効日を過ぎた旧の字面を error にし、`(旧の字面が残る行数, 印で外した行数)` を返す。

    走査は `check_article_notation` と同じ——`.md` とコードの両方、`.md` のコードフェンスの中は見ない。
    **1 行 1 所見**（同じ行に旧の字面が 2 つあっても所見は 1 つ、印も 1 つで全部外れる）。
    **発効日前は数えるだけ**で所見を積まない（発効日の 30 日前からは warn でファイル別の件数を知らせる）。
    **2 つ返すのは、0 に落ちたとき「切り替え済み」と読み違えないため**——配線が死んだか、
    印が広がったかを見分けられるように、印で外した行も数える。
    `today` と `targets` はテストと `--today` のための注入口。
    """
    today = today or datetime.date.today()
    if targets is None:
        targets = iter_scan_targets(docs, include_md=True)
    remaining = 0
    ignored = 0
    for switch in DATED_SWITCHES:
        effective = today >= switch.effective
        notice = not effective and (switch.effective - today).days <= SWITCH_NOTICE_DAYS
        per_file: Dict[str, int] = {}
        for rel, lines in targets:
            in_fence = False
            for i, line in enumerate(lines):
                if rel.endswith(".md") and FENCE_RE.match(line):
                    in_fence = not in_fence
                    continue
                if in_fence:
                    continue
                hits = dated_switch_hits(line, switch)
                if not hits:
                    continue
                if SWITCH_IGNORE_RE.search(line):
                    ignored += 1
                    continue
                per_file[rel] = per_file.get(rel, 0) + 1
                if effective:
                    findings.append((SEV_ERROR, rel,
                                     "{}行目: {} に発効した条番号の切替が残っています（{} → {}）。"
                                     "書き換えるか、{} なら行末に「{} 理由」を書く"
                                     "（理由の無い印は効かない。{} では外れない）: {}"
                                     .format(i + 1, switch.effective.isoformat(), switch.label, switch.new,
                                             switch.exempt_hint, SWITCH_IGNORE, INLINE_IGNORE,
                                             "・".join(hits))))
        count_here = sum(per_file.values())
        remaining += count_here
        if notice and count_here:
            findings.append((SEV_WARN, "（全体）",
                             "{} に条番号の切替が発効します（{}）。旧の字面が {} 行残っています——{}。"
                             "当日に一斉に error になるので、`lint_docs.py --today {}` で先に洗い、"
                             "印を付けるか当日の差分を用意しておく（発効日前に新の番号は書けない）"
                             .format(switch.effective.isoformat(), switch.why, count_here,
                                     "・".join("{} {} 行".format(r, n) for r, n in sorted(per_file.items())),
                                     switch.effective.isoformat())))
    return remaining, ignored
