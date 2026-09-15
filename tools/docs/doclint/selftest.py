#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""doclint.selftest — 関門そのものを検査する（`lint_docs.py --selftest`）.

**中身を空にしても緑**という状態を作らないための検査である。見るもの:

  1. 判定の純粋部分（`updated_violation` / `body_of`）が期待どおり鳴るか
  2. **わざと壊した入力**で `check_superseded_links` が鳴り、免除の形では鳴らないか。
     件数だけでなく**指摘文の中身**まで表明する（error を warn に格下げしても件数は変わらない）
  3. `section_refs` が節への参照の 5 形を拾い、**拾ってはいけない形を拾わない**か
  4. 定義した検査が全部 `ALL_CHECKS` に載り、`main` から**正しい引数で**呼ばれているか
  5. `article_notation_violations` が `5 条 1 項` を拾い、**公布番号と「12 項目」を拾わない**か  # lint-docs:article-ok
  6. `check_dated_switches` が発効日前は数えるだけで、発効日以後は error にし、印で外れるか
  7. `banned_law_abbreviations` が `28年改正法`・`改正令附則` を拾い、**法令番号つきの略称と普通名詞を拾わない**か  # lint-docs:abbrev-ok 検体
  8. スキルが**文書として検査に掛かり**、フロントマターだけが別仕様として扱われるか

**本数は数えない**（足すたびに直し忘れる。正典は `selftest()` が回す関数の並び）。
"""

from __future__ import annotations

import datetime
import os
import re
from typing import Dict, List

from . import checks
from .checks import (ABBREV_IGNORE, ALL_CHECKS, ARTICLE_IGNORE, DATED_SWITCHES, SWITCH_IGNORE, Finding,
                     article_notation_violations, banned_law_abbreviations, check_body,
                     check_front_matter, check_links, check_superseded_links, dated_switch_hits,
                     has_dated_updated, misplaced_skill_entry, skill_front_matter_violations,
                     successor_of, treated_as_current, updated_violation)
from .model import (ADR_LEDGER, DOCS_INDEX, Doc, INLINE_IGNORE, LINE_LIMIT, REPO_ROOT, SEV_ERROR,
                    SEV_WARN, SKILL_ENTRY, SKILL_PREFIX, body_of, front_matter_unclosed,
                    load_docs, parse_front_matter, scalar_value)

CLI_SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "lint_docs.py")

# main が検査を呼ぶときの**引数まで含めた**呼び出し。名前だけの包含だと
# `check_superseded_links(d, {}, findings)` のような配線の壊れ方が通ってしまう
REQUIRED_CALLS = ("check_superseded_links(d, docs_by_rel, findings)",
                  "scanned_notation, ignored_notation = check_article_notation(docs, findings)",
                  "remaining_switch, ignored_switch = check_dated_switches(docs, findings, today=today)",
                  "scanned_abbrev, ignored_abbrev = check_law_abbreviations(docs, findings)",
                  "改正法の略称: {} 行を走査し {} 行を印で外した",
                  # `format` の引数列。末尾だけ照合すると、切替の 2 値を 0 に差し替えても通る（R82）
                  "scanned_notation, ignored_notation, remaining_switch, ignored_switch,",
                  # 要約行の印字。消すと「0 に落ちたら疑う」の設計が黙って死ぬ
                  "条番号の切替: 旧の字面が {} 行（印で外した {} 行）",
                  "scanned_abbrev, ignored_abbrev))")


def _fake(rel: str, meta: Dict[str, str], body: List[str]) -> Doc:
    lines = ["---"] + ["{}: {}".format(k, v) for k, v in meta.items()] + ["---"] + body
    parsed, start = parse_front_matter(lines)
    return Doc(rel, lines, parsed, start)


def _check_updated_violation() -> List[str]:
    ng = []
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
            ng.append("updated_violation: 期待 {} / 実際 {}: {}"
                      .format(should, got, (old_body, new_body, updated)))

    fm = ["---", "title: x", "updated: 2026-08-27", "---"]
    if body_of(chr(10).join(fm + ["本文"])) != ["本文"]:
        ng.append("body_of: フロントマターを落とせていない")
    if body_of("フロントマター無し") != ["フロントマター無し"]:
        ng.append("body_of: フロントマターが無い文書を落としてしまった")
    return ng


def _check_superseded_links() -> List[str]:
    """「足したら一度わざと壊して鳴ることを確かめる」（ADR-0021 §4）を関門の中に固定してある。"""
    ng = []
    dead = _fake("docs/decisions/0019-old.md",
                 {"status": "superseded",
                  # related[0] が superseded の実データがあるので、その形で入れる
                  "related": "[0006-older.md, 0029-new.md]"}, ["中身"])
    older = _fake("docs/decisions/0006-older.md",
                  {"status": "superseded", "related": "[0019-old.md]"}, ["中身"])
    gone = _fake("docs/decisions/0018-hist.md",
                 {"status": "historical", "related": "[0029-new.md]"}, ["中身"])
    live = _fake("docs/79_other.md", {"status": "current"}, ["本文"])
    new = _fake("docs/decisions/0029-new.md", {"status": "current"}, ["本文"])
    far = _fake("docs/78_dead.md", {"status": "superseded", "related": "[79_other.md]"}, ["中身"])
    by_rel = {d.rel: d for d in (dead, older, gone, live, new, far)}
    to_dead, to_live = "[旧](decisions/0019-old.md)", "[今](79_other.md)"
    cur = {"status": "current"}
    to_far = "[遠](78_dead.md)"       # docs/77_x.md から見た別の superseded 文書
    to_older = "[古](decisions/0006-older.md)"  # related に current の後継が無い文書
    cases = [
        # (rel, meta, body, 鳴るべき件数)
        ("docs/77_x.md", cur, [to_dead], 1),                                  # わざと壊した入力
        ("docs/77_x.md", cur, [to_live], 0),
        ("docs/77_x.md", cur, ["0019-old.md は既に覆されている"], 0),          # 散文は咎めない
        ("docs/77_x.md", cur, ["[外](https://example.com/0019-old.md)"], 0),
        ("docs/77_x.md", cur, ["[未](decisions/0019-nowhere.md)"], 0),        # 実在しない
        ("docs/77_x.md", cur, ["[印](decisions/0018-hist.md)"], 0),           # historical は対象外
        ("docs/77_x.md", cur, ["[節](decisions/0019-old.md#2-決定)"], 1),     # アンカー付き
        ("docs/77_x.md", cur, ["[空]( decisions/0019-old.md )"], 1),          # 前後の空白を落とす
        ("docs/decisions/0030-y.md", cur, ["[上](../78_dead.md)"], 1),        # `../` 起点
        ("docs/77_x.md", cur, ["```", to_dead, "```"], 0),                    # コードフェンスの中
        ("docs/77_x.md", cur, ["```", to_dead, "```", to_dead], 1),           # フェンスを閉じたら効く
        ("docs/77_x.md", {"status": "superseded", "related": "[79_other.md]"}, [to_dead], 0),
        ("docs/77_x.md", {"status": "historical", "related": "[79_other.md]"}, [to_dead], 0),
        # `growth: append` は免除しない（2026-08-28 に外した。理由は関数の docstring）
        ("docs/77_x.md", {"status": "current", "growth": "append"}, [to_dead], 1),
        # フロントマターは対象外。**リンクの形をした値**を置いて、本文だけを見ていることを表明する
        ("docs/77_x.md", {"status": "current", "note": to_dead}, ["本文"], 0),
        # --- 免除には必ず対照を置く。「免除された」と「そもそも検出できていない」は別物である ---
        # 後継は前身を語ってよい（対照: supersedes を外すと鳴る）
        ("docs/decisions/0029-new.md", {"status": "current", "supersedes": "[0019-old.md]"},
         ["[旧](0019-old.md)"], 0),
        ("docs/decisions/0029-new.md", cur, ["[旧](0019-old.md)"], 1),
        # ADR 台帳は役割で免除する（対照: 台帳でない同じ場所の文書は鳴る）。
        # meta は**実データと同じく `growth: append` を付けた形**にする
        (ADR_LEDGER, {"status": "current", "growth": "append"}, ["[旧](0019-old.md)"], 0),
        ("docs/decisions/README2.md", {"status": "current", "growth": "append"},
         ["[旧](0019-old.md)"], 1),
        # `lint-docs:ignore` は**行単位**。同じ行の無関係なリンクまで免除されるのが現在の仕様
        ("docs/77_x.md", cur, [to_dead + " " + to_far + " " + INLINE_IGNORE], 0),
        ("docs/77_x.md", cur, [to_dead + " " + to_far], 2),                   # 対照（印を外す）
    ]
    for rel, meta, body, want in cases:
        got: List[Finding] = []
        check_superseded_links(_fake(rel, meta, body), by_rel, got)
        if len(got) != want:
            ng.append("check_superseded_links: 期待 {} 件 / 実際 {} 件: {} {}"
                      .format(want, len(got), rel, body))

    # 行番号は**本文の先頭以外**に置いて表明する。先頭に置くと `body_start + 1` に
    # 固定する壊れ方と区別が付かない（2026-08-28 の自己レビューで実際に空振りしていた）
    multi = _fake("docs/77_x.md", cur, ["前書き", "", to_dead, "間", to_older])
    got: List[Finding] = []
    seen = check_superseded_links(multi, by_rel, got)
    want_lines = [multi.body_start + 3, multi.body_start + 5]
    got_lines = [int(re.match(r"(\d+)行目", f[2]).group(1)) for f in got
                 if re.match(r"(\d+)行目", f[2])]
    if got_lines != want_lines:
        ng.append("check_superseded_links: 行番号が違う。期待 {} / 実際 {}"
                  .format(want_lines, got_lines))

    # 件数だけでなく**中身**を表明する。error を warn に格下げしても件数は変わらないため
    for label, ok in (
        ("severity が error", got and all(f[0] == SEV_ERROR for f in got)),
        ("指摘先が違反した文書", got and got[0][1] == "docs/77_x.md"),
        ("リンク先をリポジトリ相対で出す", got and "docs/decisions/0019-old.md" in got[0][2]),
        # related[0] は superseded（0006）。**current な 0029 を案内する**こと
        ("後継は current を選ぶ", got and "docs/decisions/0029-new.md" in got[0][2]),
        ("superseded な related を案内しない", got and "0006-older.md" not in got[0][2]),
        ("直し方を書く", got and INLINE_IGNORE in got[0][2]),
        ("current な後継が無いときはそう書く",
         len(got) > 1 and "related に current な後継がありません" in got[1][2]),
        ("見た件数を返す", seen == 2),
    ):
        if not ok:
            ng.append("check_superseded_links: {}: {}".format(label, got))
    if successor_of(older, by_rel) is not None:
        ng.append("successor_of: current な後継が無いのに何かを返した")
    return ng


def _check_section_ref_forms() -> List[str]:
    """`section_refs` が節への参照の 5 形を拾い、拾ってはいけない形を拾わないか（純粋部分）。

    **番号だけの短縮形（`NN §3`）は、リンクが張れない場所で最も多く使われる形**であり、
    改番で最も静かに壊れる。`docs_entries` を注入して実ファイルなしで表明する。

    **検体に実在の文書番号・題名を使わない。** 使うと改番のたびにこの検査が道連れになり、
    しかも**検体まで一緒に書き換わって通ってしまう**——番号が動いたことを表明できない。
    """
    ng = []
    entries = ["77_架空の分冊", "78_架空の設計.md", "79_架空の原則.md"]
    cases = [
        ("番号の短縮形", "詳細は docs/78 §1 の表", [("docs/78_架空の設計.md", ["1"])]),  # lint-docs:ignore 架空の番号を使う検体
        ("分冊を持つディレクトリ", "（docs/77 §4-9）", [("docs/77_架空の分冊", ["4-9"])]),  # lint-docs:ignore 架空の番号を使う検体
        ("全角空白", "docs/78　§2 を見る", [("docs/78_架空の設計.md", ["2"])]),  # lint-docs:ignore 架空の番号を使う検体
        ("相対の上り", "（../docs/79 §2）", [("docs/79_架空の原則.md", ["2"])]),  # lint-docs:ignore 架空の番号を使う検体
        ("ファイル名の形", "-- docs/79_架空の原則.md §2 に反する",  # lint-docs:ignore 架空の番号を使う検体
         [("docs/79_架空の原則.md", ["2"])]),  # lint-docs:ignore 架空の番号を使う検体
        ("番号だけの形", "-- 79 §3 は「既定で絞らない」", [("docs/79_架空の原則.md", ["3"])]),  # lint-docs:ignore 架空の番号を使う検体
        ("消えた番号は指し先なし", "詳細は docs/76 §1", [("docs/76", ["1"])]),  # lint-docs:ignore 架空の番号を使う検体
        # 拾ってはいけないもの
        ("別リポジトリのパス", "他/docs/78 §1 は対象外", []),  # lint-docs:ignore 架空の番号を使う検体
        ("ADR 番号", "[ADR-0021 §4](x.md) は ADR", [("docs/x.md", ["4"])]),
        ("qa の番号", "qa/78 §2 の台本", []),  # lint-docs:ignore 架空の番号を使う検体
        ("研究記録の日付", "docs/research/2026-08-23_x.md §1", []),
    ]
    for label, line, want in cases:
        got = checks.section_refs("docs/z.md", line, entries)
        if sorted(got) != sorted(want):
            ng.append("section_refs: {}: 期待 {} / 実際 {}".format(label, want, got))
    if checks.docs_num_target("76", entries) is not None:
        ng.append("docs_num_target: 無い番号に指し先を返した")
    if checks.docs_num_target("78", entries) != "docs/78_架空の設計.md":
        ng.append("docs_num_target: 78 を引けない")
    return ng


SKILL_MD = SKILL_PREFIX + "self-review/" + SKILL_ENTRY
OK_META = {"name": "self-review", "description": "観点を分けて読ませる"}


def _check_skill_front_matter_forms() -> List[str]:
    """`skill_front_matter_violations` と `misplaced_skill_entry` を**完全一致**で表明する。

    **真偽や部分一致で見ない**（隣の条項・略称の検査と同じ流儀）——語が含まれるかだけを見ると、
    指摘が増えても・順序が変わっても・`name=` と `ディレクトリ=` が入れ替わっても緑になる。
    """
    ng = []
    cases = [
        # (ディレクトリ名, meta, 閉じていないか, 期待する違反の全部)
        ("self-review", OK_META, False, []),
        # 引用符つきは YAML として正しい。**落とさないと偽の違反になる**
        ("self-review", {"name": '"self-review"', "description": "'説明'"}, False, []),
        ("self-review", {}, False,
         ["フロントマターがありません（name と description が要る）"]),
        ("self-review", OK_META, True,
         ["フロントマターが `---` で閉じていません（読み手はこれをフロントマターとして読みません）"]),
        ("self-review", {"name": "self-review", "description": "   "}, False,
         ["スキルのフロントマターに description がありません"]),
        ("self-review", {"description": "説明だけある"}, False,
         ["スキルのフロントマターに name がありません"]),
        # 折りたたみは「空でない」ように見えて中身を読めていない
        ("self-review", {"name": "self-review", "description": ">-"}, False,
         ["description が折りたたみ記法です。1 行で書いてください"]),
        ("live-test", OK_META, False,
         ["name がディレクトリ名と違うので、その名前では起動できません: "
          "name=self-review / ディレクトリ=live-test"]),
    ]
    for dir_name, meta, unclosed, want in cases:
        got = skill_front_matter_violations(dir_name, meta, unclosed)
        if got != want:
            ng.append("skill_front_matter_violations({}, {}, unclosed={}): {} のはずが {}"
                      .format(dir_name, meta, unclosed, want, got))

    place_cases = [
        (SKILL_MD, None),
        (SKILL_PREFIX + "self-review/references/観点.md", None),  # 補助ファイルは位置を問わない
        ("docs/README.md", None),
        # 名前のディレクトリが無い直下も読み込まれないので断る
        (SKILL_PREFIX + SKILL_ENTRY,
         ".claude/skills/SKILL.md が読み込まれる位置にありません"
         "（.claude/skills/<名前>/SKILL.md に置く）"),
        (SKILL_PREFIX + "a/b/" + SKILL_ENTRY,
         ".claude/skills/a/b/SKILL.md が読み込まれる位置にありません"
         "（.claude/skills/<名前>/SKILL.md に置く）"),
    ]
    for rel, want in place_cases:
        got = misplaced_skill_entry(rel)
        if got != want:
            ng.append("misplaced_skill_entry({}): {} のはずが {}".format(rel, want, got))

    # 読み手の前提（`model` 側の純粋関数）
    for raw, want in [("foo", "foo"), ('"foo"', "foo"), ("'foo'", "foo"),
                      (">-", None), ("|", None), ("", ""), ("  foo  ", "foo")]:
        if scalar_value(raw) != want:
            ng.append("scalar_value({!r}): {!r} のはずが {!r}".format(raw, want, scalar_value(raw)))
    for lines, want in [(["---", "a: 1", "---", "本文"], False),
                        (["---", "a: 1", "本文"], True),
                        (["本文"], False), ([], False)]:
        if front_matter_unclosed(lines) != want:
            ng.append("front_matter_unclosed({!r}): {} のはず".format(lines, want))
    # ハイフンを含む欄（`allowed-tools:` など）。読めないと**書いたのに読まれていない**が見えない
    meta, _ = parse_front_matter(["---", "name: foo", "allowed-tools: Read", "---"])
    if meta.get("allowed-tools") != "Read":
        ng.append("parse_front_matter: ハイフンを含む欄を読み落としている: {}".format(meta))
    return ng


def _check_skill_is_checked() -> List[str]:
    """スキルが**文書として検査に掛かっている**ことを、検査ごとに鳴らして表明する。

    属性の読み返し（`doc.status` を読んで同じ値と比べる）では**何も守れない**——
    `check_body` や `check_superseded_links` の status ガードを通ってスキルで**実際に鳴る**
    ことを見る。qa/02 の R19-12・R20-16 が宿題にしていたのは、まさにこの経路である。
    """
    ng = []

    # ① 欄の規約は当てない（当てると、正しいスキルが毎回 error になる）
    sink: List[Finding] = []
    check_front_matter(_fake(SKILL_MD, OK_META, ["本文"]), sink)
    if sink:
        ng.append("check_front_matter: 正しいスキルが鳴っている: {}".format(sink))

    # ② **壊したスキルでは鳴る。** 分岐を「黙って免除」に書き換えたら赤くなるのはここだけ。
    #    severity・指摘先・文面を組で当てる（件数だけ見ると格下げに気づけない。qa/03 L-17）
    sink = []
    check_front_matter(_fake(SKILL_PREFIX + "live-test/" + SKILL_ENTRY, OK_META, ["本文"]), sink)
    want = [(SEV_ERROR, SKILL_PREFIX + "live-test/" + SKILL_ENTRY,
             "name がディレクトリ名と違うので、その名前では起動できません: "
             "name=self-review / ディレクトリ=live-test")]
    if sink != want:
        ng.append("check_front_matter: 壊したスキルの所見が {} のはずが {}".format(want, sink))

    # ③ 補助ファイル（`references/`）に欄を要求しない。**要求すると置いた日に必ず赤くなる**
    sink = []
    check_front_matter(_fake(SKILL_PREFIX + "self-review/references/観点.md", {}, ["本文"]), sink)
    if sink:
        ng.append("check_front_matter: スキルの補助ファイルに欄を要求している: {}".format(sink))

    # ④ 読み込まれない位置の SKILL.md は断る
    sink = []
    check_front_matter(_fake(SKILL_PREFIX + "a/b/" + SKILL_ENTRY, OK_META, ["本文"]), sink)
    if len(sink) != 1 or sink[0][0] != SEV_ERROR or "読み込まれる位置" not in sink[0][2]:
        ng.append("check_front_matter: 位置の違う SKILL.md を断れていない: {}".format(sink))

    # ⑤ 行数と未処理マーカー（`check_body`）が当たる＝`treated_as_current` が効いている
    sink = []
    check_body(_fake(SKILL_MD, OK_META, ["x"] * (LINE_LIMIT + 1)), sink)
    if not [f for f in sink if f[0] == SEV_WARN and "目安" in f[2]]:
        ng.append("check_body: スキルに行数の目安が当たっていない: {}".format(sink))
    sink = []
    check_body(_fake(SKILL_MD, OK_META, ["ここは TODO のまま"]), sink)
    if not [f for f in sink if "未処理マーカー" in f[2]]:
        ng.append("check_body: スキルに未処理マーカーの検査が当たっていない: {}".format(sink))
    if treated_as_current(_fake("docs/x.md", {"status": "superseded"}, [])):
        ng.append("treated_as_current: superseded を current 扱いしている")

    # ⑥ **superseded へのリンクが鳴る**（R19-12 の本丸）。`../../../` の解決も同時に守る
    old = _fake("docs/98_旧.md", {"status": "superseded", "related": "[99_新.md]"}, ["旧"])
    new = _fake("docs/99_新.md", {"status": "current"}, ["新"])
    by_rel = {old.rel: old, new.rel: new}
    sink = []
    seen = check_superseded_links(
        _fake(SKILL_MD, OK_META, ["[旧](../../../docs/98_旧.md)"]), by_rel, sink)
    if seen != 1 or len(sink) != 1 or "docs/99_新.md" not in sink[0][2]:
        ng.append("check_superseded_links: スキルで鳴らない／後継を案内していない"
                  "（seen={} sink={}）".format(seen, sink))

    # ⑦ リンク切れ。**実在するほうで鳴らない対照を置く**（空集合だと何でも鳴るので表明にならない）
    sink = []
    check_links(_fake(SKILL_MD, OK_META, ["[新](../../../docs/99_新.md)"]), {new.rel}, sink)
    if sink:
        ng.append("check_links: 実在するリンクで鳴っている（相対パスの解決が違う）: {}".format(sink))
    sink = []
    check_links(_fake(SKILL_MD, OK_META, ["[無い](../../../docs/97_ない.md)"]), {new.rel}, sink)
    if len(sink) != 1:
        ng.append("check_links: スキルの中のリンク切れを見ていない: {}".format(sink))

    # ⑧ 鮮度の 2 本は同じ述語で外れる（片方だけ外すと、欄を足した日に非対称ができる）
    if has_dated_updated(_fake(SKILL_MD, OK_META, [])):
        ng.append("has_dated_updated: 欄の無いスキルを当たりにしている")
    if not has_dated_updated(_fake(SKILL_MD, dict(OK_META, updated="2026-09-15"), [])):
        ng.append("has_dated_updated: 欄を足しても当たらない（保留を実行した日に効かない）")
    return ng


def _check_skill_real_data() -> List[str]:
    """実データで往復させる。**本数のラチェットと、実ファイルが通ることの両方**を見る。

    「1 本以上あるか」だけだと、いまのリポジトリでは「そのファイルが消えていない」としか
    言っていない。**ディスク上の実数と突き合わせる**ことで、
    **追跡されていないスキル**（`git ls-files` に載らず、黙って検査 0 本になる）を拾う。
    """
    ng = []
    root = os.path.join(REPO_ROOT, *SKILL_PREFIX.rstrip("/").split("/"))
    on_disk = sorted(
        (SKILL_PREFIX + name + "/" + SKILL_ENTRY)
        for name in (os.listdir(root) if os.path.isdir(root) else [])
        if os.path.isfile(os.path.join(root, name, SKILL_ENTRY)))
    docs, _ = load_docs()
    loaded = sorted(d.rel for d in docs if d.is_skill)
    if not on_disk:
        return ["ディスクにスキルが 1 本も無い（**0 本は緑ではない**）"]
    if loaded != on_disk:
        ng.append("load_docs が拾ったスキルがディスクと違う: 検査 {} / ディスク {}"
                  "（追跡されていないスキルは黙って検査 0 本になる。EXCLUDE_PREFIXES へ戻っていないか）"
                  .format(loaded, on_disk))
    # **実ファイル → parse_front_matter → 検査の往復。** 偽 `Doc` だけだと、本物の
    # description（長い 1 行・読点・`§` を含む）が通ることを機械が一度も言っていない
    for doc in (d for d in docs if d.is_skill):
        sink: List[Finding] = []
        check_front_matter(doc, sink)
        if sink:
            ng.append("実在のスキル {} が新しい関門で鳴っている: {}".format(doc.rel, sink))
    return ng


def _check_real_data() -> List[str]:
    """実データで一度通す。

    `resolve()` の結果が `load_docs()` のキーと噛み合っているかは、偽 `Doc` では分からない
    （qa/03 L-15 の型）。**0 件は「違反が無い」ではなく「配線が死んだ」を疑う数字**である。
    """
    docs, _ = load_docs()
    by_rel = {d.rel: d for d in docs}
    sink: List[Finding] = []
    seen = sum(check_superseded_links(d, by_rel, sink) for d in docs)
    # 実測は 10 件（2026-08-28）。免除が広がって半分以下に落ちたら気づけるようにする。
    # 下げるときは理由をここに書く
    if seen < 5:
        return ["check_superseded_links: 実データで見た superseded 宛リンクが {} 件しかない"
                "（免除が広がりすぎたか、パスの解決が噛み合っていない。**少ないのは緑ではない**）"
                .format(seen)]
    return []


def _check_wiring() -> List[str]:
    """定義・登録・呼び出しの 3 つが揃っているかを見る。

    定義の列挙は**ソースの正規表現ではなくモジュールの中身**から取る。正規表現だと
    ファイルを動かした・別のファイルを読ませた瞬間に `defined` が空集合になり、
    `defined - ALL_CHECKS` も空なので**何も言わずに緑**になる（2026-08-28 の自己レビュー）。
    `__module__` で絞るのは、`from .model import ...` で入ってきた名前を数えないため。
    """
    ng = []
    defined = {n for n in dir(checks)
               if n.startswith("check_")
               and getattr(getattr(checks, n), "__module__", "") == checks.__name__}
    if not defined:
        return ["checks.py から検査を 1 本も見つけられない（**0 本は緑ではない**）"]

    for name in sorted(defined - set(ALL_CHECKS)):
        ng.append("{} が ALL_CHECKS に載っていない（足した検査は必ず載せる）".format(name))
    for name in sorted(set(ALL_CHECKS) - defined):
        ng.append("{} が ALL_CHECKS にあるのに checks.py に無い".format(name))

    try:
        cli_src = open(CLI_SRC, "r", encoding="utf-8").read()
    except OSError as e:
        return ng + ["lint_docs.py を読めない（{}）。配線を検査できていない".format(e)]
    # `main` の中だけを見る。行頭の定義から後ろを取る
    main_at = re.search(r"^def main\(\)", cli_src, re.M)
    if main_at is None:
        return ng + ["lint_docs.py に def main() が見つからない"]
    # コメントを落としてから照合する。呼び出しをコメントアウトした壊れ方が部分文字列の照合を通るため（R44-03）
    main_src = re.sub(r"#[^\n]*", "", cli_src[main_at.start():])

    # 引数まで含めて照合する。名前だけの包含だと `check_superseded_links(d, {}, findings)` が通る。
    # 照合する文字列は**別ファイル**（lint_docs.py）を探すので、この表明が自分で自分を満たすことはない
    for call in REQUIRED_CALLS:
        if call not in main_src:
            ng.append("main の配線が違う。次の形で呼ぶこと: {}".format(call))
    for name in ALL_CHECKS:
        if name + "(" not in main_src:
            ng.append("{} が main から呼ばれていない".format(name))
    return ng


def _check_other_checks() -> List[str]:
    """git を使わない残りの検査を、**壊した入力と対照の組**で表明する。

    ここが空だと「所見を組み立てる行」が一度も実行されないまま緑になり、
    最初に本物の違反が出た日に**報告ではなくクラッシュ**する（2026-08-28 の自己レビュー）。
    """
    ng = []

    def run(fn, *args):
        got: List[Finding] = []
        fn(*(args + (got,)))
        return got

    ok_meta = {"title": "x", "status": "current", "scope": "全体",
               "audience": "[開発]", "updated": "2026-08-28"}

    def without(key):
        m = dict(ok_meta)
        del m[key]
        return m

    fm_cases = [
        ("整った文書は鳴らない", _fake("a.md", ok_meta, ["本文"]), 0, None),
        ("フロントマターが無い", Doc("a.md", ["本文"], {}, 0), 1, "フロントマター"),
        ("必須欠落", _fake("a.md", without("scope"), ["本文"]), 1, "scope"),
        ("status が 3 値以外", _fake("a.md", dict(ok_meta, status="draft"), ["本文"]), 1, "3 値"),
        ("updated の書式違い", _fake("a.md", dict(ok_meta, updated="2026/08/28"), ["本文"]), 1, "YYYY"),
        # `\d` は全角も通す。通すと書式 error をすり抜け、履歴突合まで黙って無効になる
        ("updated が全角", _fake("a.md", dict(ok_meta, updated="２０２６-０８-２８"), ["本文"]), 1, "YYYY"),
        ("audience が未知", _fake("a.md", dict(ok_meta, audience="[法務]"), ["本文"]), 1, "audience"),
        ("superseded なのに related が空",
         _fake("a.md", dict(ok_meta, status="superseded"), ["本文"]), 1, "related"),
    ]
    for label, doc, want, needle in fm_cases:
        got = run(checks.check_front_matter, doc)
        if len(got) != want or (needle and not any(needle in f[2] for f in got)):
            ng.append("check_front_matter: {}: {}".format(label, got))
        if got and got[0][0] != SEV_ERROR:
            ng.append("check_front_matter: {}: error であるべき: {}".format(label, got))

    existing = {"docs/79_other.md"}
    link_cases = [
        ("実在するリンクは鳴らない", _fake("docs/a.md", ok_meta, ["[今](79_other.md)"]), 0),
        ("本文のリンク切れ", _fake("docs/a.md", ok_meta, ["[無](09_none.md)"]), 1),
        ("related のリンク切れ",
         _fake("docs/a.md", dict(ok_meta, related="[09_none.md]"), ["本文"]), 1),
        ("http は見ない", _fake("docs/a.md", ok_meta, ["[外](https://example.com/x.md)"]), 0),
    ]
    for label, doc, want in link_cases:
        got = run(checks.check_links, doc, existing)
        if len(got) != want:
            ng.append("check_links: {}: 期待 {} 件 / 実際 {}".format(label, want, got))

    long_body = ["x"] * (LINE_LIMIT + 1)
    body_cases = [
        ("短い文書は鳴らない", _fake("docs/a.md", ok_meta, ["本文"]), 0),
        ("250 行超で warn", _fake("docs/a.md", ok_meta, long_body), 1),
        ("growth: append は免除", _fake("docs/a.md", dict(ok_meta, growth="append"), long_body), 0),
        ("引くものは免除", _fake("docs/decisions/a.md", ok_meta, long_body), 0),
        ("current でなければ見ない", _fake("docs/a.md", dict(ok_meta, status="historical"), long_body), 0),
        ("ヘッダの更新履歴", _fake("docs/a.md", ok_meta, ["> 更新: 2026-08-28 …"]), 1),
        ("未処理マーカー", _fake("docs/a.md", ok_meta, ["あとで TODO にする"]), 1),
        ("保留リストの節は免除", _fake("docs/a.md", ok_meta, ["## 保留リスト", "TODO がある"]), 0),
        ("印のある行は免除", _fake("docs/a.md", ok_meta, ["TODO " + INLINE_IGNORE]), 0),
        ("コードフェンスの中は免除", _fake("docs/a.md", ok_meta, ["```", "TODO", "```"]), 0),
    ]
    for label, doc, want in body_cases:
        got = run(checks.check_body, doc)
        if len(got) != want:
            ng.append("check_body: {}: 期待 {} 件 / 実際 {}".format(label, want, got))
        if got and got[0][0] != SEV_WARN:
            ng.append("check_body: {}: warn であるべき: {}".format(label, got))

    adr = _fake("docs/decisions/0001-a.md", ok_meta, ["本文"])
    ledger_cases = [
        ("台帳と実ファイルが一致", ["[A](0001-a.md)"], 0, None),
        ("台帳に無い ADR", ["（空）"], 1, "台帳に載っていない"),
        ("実体の無い行", ["[A](0001-a.md)", "[B](0002-b.md)"], 1, "対応する ADR がありません"),
    ]
    for label, lines, want, needle in ledger_cases:
        ledger = _fake(ADR_LEDGER, ok_meta, lines)
        got = run(checks.check_adr_ledger, [ledger, adr])
        if len(got) != want or (needle and not any(needle in f[2] for f in got)):
            ng.append("check_adr_ledger: {}: {}".format(label, got))

    index_cases = [
        ("索引に載っている", ["[A](76_a.md)"], 0),
        ("索引に無い", ["（空）"], 1),
    ]
    for label, lines, want in index_cases:
        index = _fake(DOCS_INDEX, ok_meta, lines)
        got = run(checks.check_docs_index, [index, _fake("docs/76_a.md", ok_meta, ["本文"])])
        if len(got) != want:
            ng.append("check_docs_index: {}: 期待 {} 件 / 実際 {}".format(label, want, got))
    # サブディレクトリは 1 本ずつではなく**ディレクトリ単位**で見る（規約 §7-1）
    sub_a = _fake("docs/sub/a.md", ok_meta, ["本文"])
    sub_b = _fake("docs/sub/b.md", ok_meta, ["本文"])
    sub_cases = [
        ("ディレクトリの中の 1 本が載っていれば足りる", ["[S](sub/a.md)"], [sub_a, sub_b], 0),
        ("ディレクトリそのものでも足りる", ["[S](sub/)"], [sub_a, sub_b], 0),
        ("どれも載っていなければ鳴る", ["（空）"], [sub_a], 1),
        ("鳴るのはディレクトリにつき 1 件", ["（空）"], [sub_a, sub_b], 1),
    ]
    for label, lines, subs, want in sub_cases:
        got = run(checks.check_docs_index, [_fake(DOCS_INDEX, ok_meta, lines)] + subs)
        if len(got) != want:
            ng.append("check_docs_index: {}: 期待 {} 件 / 実際 {}".format(label, want, got))
    return ng


def _check_article_notation_forms() -> List[str]:
    """条項の記法の判定と、検査そのものが空回りしていないか（80 §3）。

    **返り値を完全一致で表明する。** 真偽だけを見ると、連なりを落としても
    公布番号の除外を壊しても緑のままになる（2026-09-06 の自己レビューで実際にそうなっていた）。
    """
    ng = []
    # (入力, 期待する返り値)
    cases = [
        # --- 拾うべき形 -------------------------------------------------------
        ("電帳規則5条5項1号", ["5条", "5項", "1号"]),                       # lint-docs:article-ok lint-docs:switch-ok 検体
        ("電帳規則第5条第5項", ["第5条", "第5項"]),                          # lint-docs:article-ok lint-docs:switch-ok 検体
        ("法人税法施行規則 54 条を「別表二十一」と示した", ["54 条"]),        # lint-docs:article-ok
        ("| 20 条 | 収集し、又は保管してはならない |", ["20 条"]),            # 表のセル（法令名は別の行） lint-docs:article-ok
        ("- **入力期間（6項一号）**", ["6項"]),                              # 箇条書き（条は見出しにある） lint-docs:article-ok
        ("**30 条 2 項の目的外利用**になり", ["30 条", "2 項"]),              # 強調の中 lint-docs:article-ok
        ("（附則 2 条 2 項。施行前にした", ["2 条", "2 項"]),                 # 附則 lint-docs:article-ok
        ("title: 電帳規則 5 条 5 項の要件", ["5 条", "5 項"]),                # フロントマターも見る lint-docs:article-ok lint-docs:switch-ok 検体
        # --- 拾ってはいけない形（**こちらが本体**） ---------------------------
        ("| 政令 | 同施行令 | 令和3年政令第128号 |", []),                     # 公布番号
        ("平成10年大蔵省令第43号", []),                                       # 同上
        ("同日前開始事業年度は9年（平27財務省令23号 附則2①）", []),           # 「第」なしの公布番号
        ("令和 8 年法律第 12 号（令和 8 年 3 月 31 日成立・公布）", []),       # 同上
        ("令和5年国税庁告示第26号", []),                                       # 同上
        # **公布番号の除外を広げると鳴る**——法令名に続く「号」は条項の引用である
        ("電帳規則 3 号の要件", ["3 号"]),  # lint-docs:article-ok
        # **全角数字を落とすと鳴る**——貼り付けた条文は全角で来る
        ("電帳規則５条１項", ["５条", "１項"]),  # lint-docs:article-ok
        ("サイドバーが 12 項目の並列で見にくい", []),                          # 項目
        ("全部の 4 条件を一度に決める", []),                                   # 条件
        ("この方法 3 条件をすべて満たす", []),                                 # 方法＋条件
        ("命名規則 5 項目に反する", []),                                       # 規則＋項目
        ("この記法 3 条件", []),                                               # **この規則の主題そのもの**
        ("IDE0045 / IDE0046（三項演算子への寄せ）", []),                       # 漢数字の演算子
        ("### 3.1 条文（現行・電帳規則 5 ⑤一）", []),                          # 節番号＋条文 lint-docs:switch-ok 検体
        ("**消税法 30 ⑦・38 ②・38 の 2 ②・58**に規定する帳簿", []),          # 正しい記法
        ("電帳規則 5 ⑤一イ", []),                                              # 同上 lint-docs:switch-ok 検体
        ("1 桁の検査用数字 ＋ 12 桁の基礎番号", []),                           # 法令の話ではない
    ]
    for line, want in cases:
        got = article_notation_violations(line)
        if got != want:
            ng.append("article_notation_violations: 期待 {} / 実際 {}: {}".format(want, got, line))

    # --- 検査そのもの（走査・除外・severity・行番号・指摘文） -------------------
    findings: List[Finding] = []
    doc = _fake("docs/x.md", {"title": "x", "status": "current"},
                ["ふつうの行", "電帳規則 5 条 1 項", "電帳規則 5 条 1 項  <!-- {} -->".format(ARTICLE_IGNORE),  # lint-docs:article-ok
                 # **フェンスの中は見ない**（中に HTML コメントの印を書くと画面に出てしまう）
                 "```", "電帳規則 5 条 1 項", "```"])  # lint-docs:article-ok
    scanned, ignored = checks.check_article_notation([doc], findings)
    mine = [f for f in findings if f[1] == "docs/x.md"]
    if len(mine) != 1:
        ng.append("check_article_notation: 所見が 1 件でない: {}".format(mine))
    else:
        sev, _, msg = mine[0]
        if sev != SEV_ERROR:
            ng.append("check_article_notation: severity が {} になっている".format(sev))
        if "6行目" not in msg:      # フロントマター 3 行 ＋ 本文 2 行目
            ng.append("check_article_notation: 行番号が指摘文に出ていない: " + msg)
        if "5 条・1 項" not in msg:  # lint-docs:article-ok
            ng.append("check_article_notation: 違反した字面が指摘文に出ていない: " + msg)
        if ARTICLE_IGNORE not in msg:
            ng.append("check_article_notation: 外し方が指摘文に出ていない: " + msg)
    # **偽の文書 1 本でも、コードは実物を歩く**（`iter_scan_targets` の設計）。
    # だから「ちょうど 1」ではなく「印を読めているか」を見る
    if ignored < 1:
        ng.append("check_article_notation: 印で外した行が 0（印の判定が死んだ）")
    if scanned < 6:
        ng.append("check_article_notation: 走査した行が少なすぎる（コードを歩いていない）: {}".format(scanned))

    # **コードを歩いているか**を型で表明する（`.md` だけ数えると行数の下限をすり抜ける）
    kinds = {os.path.splitext(rel)[1] for rel, _ in checks.iter_scan_targets([], include_md=False)}
    for want in (".cs", ".sql", ".py"):
        if want not in kinds:
            ng.append("iter_scan_targets: {} を歩いていない: {}".format(want, sorted(kinds)))

    # **実データで件数のラチェットを持つ**（配線が死んだら 0 に落ちる。qa/03 L-15 と同じ作法）
    real, _ = load_docs()
    real_findings: List[Finding] = []
    real_scanned, real_ignored = checks.check_article_notation(real, real_findings)
    if real_scanned < 5000:
        ng.append("check_article_notation: 実データの走査が {} 行しかない（対象が痩せた）".format(real_scanned))
    if real_ignored < 10:
        ng.append("check_article_notation: 実データで印を読めた行が {} しかない（印の判定が死んだ）".format(real_ignored))
    if real_findings:
        ng.append("check_article_notation: 実データに違反が {} 件ある".format(len(real_findings)))
    return ng


def _check_law_abbreviation_forms() -> List[str]:
    """改正法の略称の判定と、検査そのものが空回りしていないか（80 §2。開発者の指示。2026-09-11）。

    **返り値を完全一致で表明する**（`_check_article_notation_forms` と同じ理由）。
    **拾ってはいけない形が本体**——法令番号を添えた略称と、普通名詞の「改正法」「改正省令」を鳴らすと、
    正しい書き方まで直させることになる。
    """
    ng = []
    cases = [
        # --- 拾うべき形 -------------------------------------------------------
        ("経過措置（28年改正法附則 52・53）", ["28年改正法"]),                        # lint-docs:abbrev-ok 検体
        ("経過措置（28 年改正法附則 52・53）", ["28 年改正法"]),                      # 空白あり lint-docs:abbrev-ok 検体
        ("**令和8年改正法**（所得税法等の一部を改正する法律）", ["令和8年改正法"]),   # 元号つき lint-docs:abbrev-ok 検体
        ("平成15年改正省令附則 2 の旧経過措置", ["平成15年改正省令"]),               # 省令 lint-docs:abbrev-ok 検体
        ("令和元年改正法附則", ["令和元年改正法"]),                                  # 元年 lint-docs:abbrev-ok 検体
        ("令和8年改正政令", ["令和8年改正政令"]),                                    # 政令 lint-docs:abbrev-ok 検体
        ("平成28年改正規則", ["平成28年改正規則"]),                                  # 規則 lint-docs:abbrev-ok 検体
        ("平成28年改正財務省令", ["平成28年改正財務省令"]),                          # 財務省令 lint-docs:abbrev-ok 検体
        ("帳簿のみで仕入税額控除可（改正令附則24の2①）", ["改正令附則"]),           # 裸の略称＋附則 lint-docs:abbrev-ok 検体
        ("旧 2 は改正法附則 34 ①一", ["改正法附則"]),                                # 同上 lint-docs:abbrev-ok 検体
        ("改正政令附則 2", ["改正政令附則"]),                                        # 同上（政令） lint-docs:abbrev-ok 検体
        ("**改正法**附則 34", ["改正法**附則"]),                                     # 強調記号で分断 lint-docs:abbrev-ok 検体
        ("金商法改正法の施行の日の属する年", ["金商法改正法"]),                       # 法令名の直後 lint-docs:abbrev-ok 検体
        ("| 法律 | **２８年改正法** |", ["２８年改正法"]),                            # 全角数字 lint-docs:abbrev-ok 検体
        ("所税法等一部改正法附則 52・53", ["一部改正法"]),                            # **番号の無い一部改正法（規則の核心）** lint-docs:abbrev-ok 検体
        ("消税規則一部改正省令附則 2", ["一部改正省令"]),                            # 同上（省令） lint-docs:abbrev-ok 検体
        ("所税法等一部改正法の 2 本", ["一部改正法"]),                                # 同上（附則なし） lint-docs:abbrev-ok 検体
        # --- 拾ってはいけない形（**こちらが本体**） ---------------------------
        ("所税法等一部改正法（平成28年法律第15号）附則 52・53", []),                   # 法令番号つきの略称
        ("消税令等一部改正令（平成28年政令第148号）附則 24 の 2 ①", []),              # 同上（政令）
        ("消税規則一部改正省令（平成15年財務省令第92号）附則 2", []),                 # 同上（省令）
        ("所税法等一部改正法（令和8年法律第12号）", []),                              # 同上（附則なし）
        ("所得税法等の一部を改正する法律（令和8年法律第12号）", []),                   # 正式名称
        ("改正法の附則は被改正法の側に載る", []),                                     # 普通名詞
        ("被改正法附則 52", []),                                                      # 被改正法
        ("電帳規則の改正省令は存在しない", []),                                       # 普通名詞
        ("令和8年改正法人税法 22", []),                                               # 改正法人税法
        ("令和8年度税制改正の大綱（令和7年12月26日閣議決定）", []),                    # 年度＋改正
        ("令和 7 年度改正（本体はデジタルシームレス保存の新設）", []),                 # 同上
        ("金融商品取引法を改正する法律（法令番号は未確認）", []),                       # 言い換えた形
        ("`<法令ID>_<施行日>_<改正法令ID>` の形", []),                                # e-Gov の ID
    ]
    for line, want in cases:
        got = banned_law_abbreviations(line)
        if got != want:
            ng.append("banned_law_abbreviations: 期待 {} / 実際 {}: {}".format(want, got, line))

    # --- 検査そのもの（走査・印の相互作用・複数ヒット・フロントマター・フェンス） ------
    # 行番号を完全一致で表明する（`_check_dated_switch_forms` と同じ作法。壊れ方と区別が付く形にする）
    findings: List[Finding] = []
    doc = _fake("docs/x.md", {"title": "28年改正法の経過措置", "status": "current"},               # 2 行目 lint-docs:abbrev-ok 検体
                ["ふつうの行",                                                                    # 5
                 "28年改正法附則 52",                                                             # 6 lint-docs:abbrev-ok 検体
                 "28年改正法附則 52  <!-- {} 検体 -->".format(ABBREV_IGNORE),                     # 7 印（理由あり）lint-docs:abbrev-ok 検体
                 "28年改正法附則 52  <!-- {} -->".format(ABBREV_IGNORE),                          # 8 **理由の無い印は効かない** lint-docs:abbrev-ok 検体
                 "28年改正法附則 52  <!-- {} 検体 -->".format(INLINE_IGNORE),                     # 9 汎用の印でも黙る lint-docs:abbrev-ok 検体
                 "改正法附則 34  <!-- {} 検体 -->".format(ARTICLE_IGNORE),                        # 10 **条項の印では黙らない** lint-docs:abbrev-ok 検体
                 "28年改正法附則 52・改正令附則 24",                                              # 11 同じ行に 2 つ lint-docs:abbrev-ok 検体
                 "```", "28年改正法附則 52", "```"])                                            # フェンスの中は見ない lint-docs:abbrev-ok 検体
    scanned, ignored = checks.check_law_abbreviations([doc], findings, targets=[("docs/x.md", doc.lines)])
    mine = [f for f in findings if f[1] == "docs/x.md"]
    got_lines = sorted(int(re.match(r"(\d+)行目", m).group(1)) for _, _, m in mine if re.match(r"(\d+)行目", m))
    if got_lines != [2, 6, 8, 10, 11]:
        ng.append("check_law_abbreviations: 鳴った行が違う: 期待 [2, 6, 8, 10, 11] / 実際 {}".format(got_lines))
    for sev, _, msg in mine:
        if sev != SEV_ERROR:
            ng.append("check_law_abbreviations: severity が {} になっている".format(sev))
        if ABBREV_IGNORE not in msg or "80 §2" not in msg:
            ng.append("check_law_abbreviations: 外し方か規則の置き場が指摘文に出ていない: " + msg)
    two = [m for _, _, m in mine if m.startswith("11行目")]
    if not two or "28年改正法・改正令附則" not in two[0]:  # lint-docs:abbrev-ok 検体
        ng.append("check_law_abbreviations: 同じ行の 2 つの字面が指摘文に並んでいない: {}".format(two))
    if ignored != 2:
        ng.append("check_law_abbreviations: 印で外した行が 2 でない（理由あり＋汎用）: {}".format(ignored))
    if scanned != 11:  # フロントマター 4 ＋ 本文 7（フェンスの 3 行は数えない）
        ng.append("check_law_abbreviations: 走査した行が 11 でない: {}".format(scanned))

    # **実データで件数のラチェットを持つ**（配線が死んだら 0 に落ちる）。この検査自身の検体（tools/）は除く
    real, _ = load_docs()
    real_targets = [t for t in checks.iter_scan_targets(real, include_md=True) if not t[0].startswith("tools/")]
    real_findings: List[Finding] = []
    real_scanned, real_ignored = checks.check_law_abbreviations(real, real_findings, targets=real_targets)
    if real_scanned < 5000:
        ng.append("check_law_abbreviations: 実データの走査が {} 行しかない（対象が痩せた）".format(real_scanned))
    # 実測 11 行（2026-09-11。tools/ を除く——qa/02 の当時の記録 7・05 の 2・80 と 00 README の規則の説明 2）。
    # 半分以下に落ちたら印の判定が死んだ、倍以上なら印をばら撒いた。動かすときは理由をここに書く
    if not 5 <= real_ignored <= 25:
        ng.append("check_law_abbreviations: 実データ（tools/ を除く）で印を読めた行が {}（5〜25 の外。判定が死んだか、ばら撒いた）".format(real_ignored))
    if real_findings:
        ng.append("check_law_abbreviations: 実データに違反が {} 件ある".format(len(real_findings)))
    return ng


# 切替ごとの検体（入力, 拾うべき字面）。**`DATED_SWITCHES` に行を足したらここにも足す**（無いと赤）。
# 拾ってはいけない形がこちらの本体——電帳規則の別の項、別の法令の同じ項、新の表記
SWITCH_CASES = {
    "電帳規則 5 ⑤": [  # lint-docs:switch-ok 検体
        ("電帳規則 5 ⑤一イ(1)", ["電帳規則 5 ⑤"]),                          # lint-docs:switch-ok 検体
        ("電帳規則5⑤二", ["電帳規則5⑤"]),                                    # 空白なし lint-docs:switch-ok 検体
        ("**現行が電帳規則 5 **⑤**、", ["電帳規則 5 **⑤"]),                   # 強調記号 lint-docs:switch-ok 検体
        ("電帳規則５⑤", ["電帳規則５⑤"]),                                    # 全角数字（貼り付けた条文） lint-docs:switch-ok 検体
        ("電帳規則5条5項・電帳規則第5条第5項", ["電帳規則5条5項", "電帳規則第5条第5項"]),  # 記法違反の形も拾う lint-docs:article-ok lint-docs:switch-ok 検体
        ("電帳規則 5 ④一", []),                                               # 新の表記
        ("電帳規則 2 ⑤・電帳規則 5 ①三", []),                                # 別の条・項
        ("電帳法 8 ⑤・消税法 5 ⑤", []),                                       # 別の法令
        ("電帳規則 5 ⑤一（2027-01-01 以後は 5 ④一）", ["電帳規則 5 ⑤"]),     # 併記も旧の字面として数える lint-docs:switch-ok 検体
    ],
}


def _check_dated_switch_forms() -> List[str]:
    """日付で発効する条番号の切替（電帳法リサーチ §0-1・ADR-0043）。

    **発効日を注入して 4 つの時点を通す**——発効日の 1 年前（数えるだけ）・31 日前（まだ warn しない）・
    30 日前（warn 1 件）・発効日（行ごとに error）。走査対象も注入し、**コードを歩くこと**と
    **理由の無い印・汎用の印では外れないこと**を対照つきで表明する。
    データ 1 行を空にしても、印の判定を落としても、発効日の比較を逆にしても赤になることを、ここで確かめる。
    """
    ng = []
    if not DATED_SWITCHES:
        return ["DATED_SWITCHES が空（**0 件は緑ではない**。2027-01-01 の切替が消えている）"]
    if DATED_SWITCHES[0].effective != datetime.date(2027, 1, 1) or DATED_SWITCHES[0].new != "電帳規則 5 ④":
        ng.append("DATED_SWITCHES の 1 行目が 2027-01-01 の電帳規則 5 ④ への切替でない: {}".format(DATED_SWITCHES[0]))

    for switch in DATED_SWITCHES:
        cases = SWITCH_CASES.get(switch.label)
        if cases is None:
            ng.append("SWITCH_CASES に {} の検体が無い（切替を足したら検体も足す）".format(switch.label))
            continue
        for line, want in cases:
            got = dated_switch_hits(line, switch)
            if got != want:
                ng.append("dated_switch_hits: 期待 {} / 実際 {}: {}".format(want, got, line))

        # 走査対象を注入する（文書 1 本＋コード 2 本）。**コードを歩かない壊れ方**をここで捕まえる
        md = _fake("docs/x.md", {"title": "x", "status": "current"}, [
            "ふつうの行",                                                              # 5 行目
            "優良の要件は電帳規則 5 ⑤一イ",                                            # 6 行目: 旧の字面 lint-docs:switch-ok 検体
            "改正後の電帳規則 5 ⑤二 <!-- {} 改正後の番号 -->".format(SWITCH_IGNORE),  # 7 行目: 理由つきの印で外れる lint-docs:switch-ok 検体
            "```", "電帳規則 5 ⑤（フェンスの中）", "```",                             # 8〜10 行目: フェンスは見ない lint-docs:switch-ok 検体
            "電帳規則5⑤ <!-- {} -->".format(SWITCH_IGNORE),                          # 11 行目: **理由の無い印は効かない** lint-docs:switch-ok 検体
            "電帳規則 5 ⑤ <!-- {} -->".format(INLINE_IGNORE),                        # 12 行目: **汎用の印では外れない** lint-docs:switch-ok 検体
            "電帳規則 5 ⑤一ロ と 電帳規則 5 ⑤一ハ",                                    # 13 行目: 同じ行に 2 つ ＝ 1 所見 lint-docs:switch-ok 検体
        ])
        targets = [(md.rel, md.lines),
                   ("BusinessApp/X/Y.cs", ["// 入力年月日（電帳規則 5 ⑤一イ(2)）", "// ふつうの行"]),  # lint-docs:switch-ok 検体
                   ("Designer/ddl/z.sql", ["-- 電帳規則5⑤一ロ  lint-docs:switch-ok 一連番号の説明"])]     # 印つき lint-docs:switch-ok 検体
        want_remaining, want_ignored = 5, 2   # md の 6・11・12・13 行目 ＋ .cs の 1 行／md の 7 行目 ＋ .sql

        # 1 年前: 数えるだけ
        fs: List[Finding] = []
        remaining, ignored = checks.check_dated_switches([], fs, today=switch.effective - datetime.timedelta(days=365),
                                                          targets=targets)
        if fs:
            ng.append("check_dated_switches: 発効日前に所見を積んだ: {}".format(fs))
        if (remaining, ignored) != (want_remaining, want_ignored):
            ng.append("check_dated_switches: 件数が違う。期待 {} / 実際 {}".format((want_remaining, want_ignored), (remaining, ignored)))

        # 31 日前: まだ warn しない／30 日前: warn 1 件（全体）で、ファイル別の件数と --today の案内を出す
        fs = []
        checks.check_dated_switches([], fs, today=switch.effective - datetime.timedelta(days=checks.SWITCH_NOTICE_DAYS + 1),
                                    targets=targets)
        if fs:
            ng.append("check_dated_switches: 31 日前に所見を出した: {}".format(fs))
        fs = []
        checks.check_dated_switches([], fs, today=switch.effective - datetime.timedelta(days=checks.SWITCH_NOTICE_DAYS),
                                    targets=targets)
        notices = [f for f in fs if f[1] == "（全体）"]
        others = [f for f in fs if f[1] != "（全体）"]
        if len(notices) != 1 or notices[0][0] != SEV_WARN:
            ng.append("check_dated_switches: 30 日前の warn が 1 件でない: {}".format(fs))
        else:
            for want in (switch.effective.isoformat(), "docs/x.md 4 行", "BusinessApp/X/Y.cs 1 行", "--today"):
                if want not in notices[0][2]:
                    ng.append("check_dated_switches: 30 日前の warn に {} が無い: {}".format(want, notices[0][2]))
        if others:
            ng.append("check_dated_switches: 30 日前に行ごとの所見を出した: {}".format(others))

        # 発効日: 印の無い行ごとに error。コードも鳴る。行番号・旧新・印の書き方が指摘文にある
        fs = []
        checks.check_dated_switches([], fs, today=switch.effective, targets=targets)
        errs = [f for f in fs if f[0] == SEV_ERROR]
        if len(errs) != want_remaining or len(fs) != len(errs):
            ng.append("check_dated_switches: 発効日の所見が error {} 件でない: {}".format(want_remaining, fs))
        else:
            got_lines = sorted(int(re.match(r"(\d+)行目", m).group(1)) for _, r, m in errs if r == "docs/x.md")
            if got_lines != [6, 11, 12, 13]:
                ng.append("check_dated_switches: 文書の行番号が違う。期待 [6, 11, 12, 13] / 実際 {}".format(got_lines))
            if not any(r.endswith(".cs") for _, r, _ in errs):
                ng.append("check_dated_switches: コード（.cs）の旧の字面が error になっていない")
            if any(r.endswith(".sql") for _, r, _ in errs):
                ng.append("check_dated_switches: 印つきの .sql が error になっている")
            for want in (switch.effective.isoformat(), switch.label, switch.new, SWITCH_IGNORE, INLINE_IGNORE):
                if not all(want in m for _, _, m in errs):
                    ng.append("check_dated_switches: 指摘文に {} が無い行がある".format(want))
            if switch.old.pattern in errs[0][2]:
                ng.append("check_dated_switches: 指摘文に正規表現の生文字列が出ている")

    # 実データ。**発効日前は「まだ残っている」のが正しい**——0 に落ちたら配線が死んだか、早まって置換した
    # （2027-01-01 より前に 5 ④ と書くと現行の条文を指せない）。**印は上限も持つ**（ばら撒いて残を減らす壊れ方）。
    # **発効日を過ぎたら逆**——残っていれば本体の検査が error にするので、ここは印の側だけを見る
    real, _ = load_docs()
    real_targets = checks.iter_scan_targets(real, include_md=True)
    outside_tools = [t for t in real_targets if not t[0].startswith("tools/")]   # この検査自身の検体を除く
    real_fs: List[Finding] = []
    real_remaining, real_ignored = checks.check_dated_switches(real, real_fs, targets=outside_tools)
    first = DATED_SWITCHES[0]
    if datetime.date.today() < first.effective:
        # 実測 41 行（2026-09-07）。半分以下に落ちたら気づけるようにする。下げるときは理由をここに書く
        if real_remaining < 20:
            ng.append("check_dated_switches: 実データで旧の字面が {} 行しかない（配線が死んだか、発効日前に置換した）"
                      .format(real_remaining))
        if [f for f in real_fs if f[0] == SEV_ERROR]:
            ng.append("check_dated_switches: 発効日前に error を出した: {}".format(real_fs[:3]))
        # 発効日を先取りして、**コードの旧の字面も error になる**ことを実物で表明する（.cs・.sql が 13 行ある）
        ahead: List[Finding] = []
        checks.check_dated_switches(real, ahead, today=first.effective, targets=outside_tools)
        kinds = {os.path.splitext(r)[1] for sev, r, _ in ahead if sev == SEV_ERROR}
        for want in (".md", ".cs", ".sql"):
            if want not in kinds:
                ng.append("check_dated_switches: 発効日の先取りで {} が error に出ない（コードを歩いていない）: {}".format(want, sorted(kinds)))
    # 実測 19 行（2026-09-07。tools/ を除く）。半分以下に落ちたら印の判定が死んだ、倍以上なら印をばら撒いた
    if not 10 <= real_ignored <= 40:
        ng.append("check_dated_switches: 実データ（tools/ を除く）で印を読めた行が {}（10〜40 の外。判定が死んだか、ばら撒いた）"
                  .format(real_ignored))
    return ng


def _check_link_label_forms() -> List[str]:
    """札と行き先の文書番号の突き合わせ（`check_link_label_targets`）を、当たる形と当たらない形で表明する。

    **この検査は 2026-09-13 に足した**——節を別の文書へ出した回に、札だけ新番号へ直して
    行き先を残した形が 3 本出た（qa/02 のラウンド 86）。指し先の文書にその節が実在してしまうので、
    `check_section_references` は緑のまま通る。**規約を壊す最短の書き方を検体に持つ。**
    """
    ng = []
    cases = [
        # (元の文書, 札, 行き先, 鳴るか)
        ("docs/a.md", "15 §1-2", "10_会計ドメイン設計.md", True),
        ("docs/a.md", "docs/14 §4", "13_取引先設計.md", True),
        ("Designer/seed/README.md", "docs/15 §4-1", "../../docs/10_会計ドメイン設計.md", True),
        ("docs/a.md", "15_記帳の枠組み", "10_会計ドメイン設計.md", True),
        ("docs/a.md", "`13 §4`", "10_会計ドメイン設計.md", True),
        # 当たってはいけない形
        ("docs/a.md", "15 §1-2", "15_記帳の枠組み.md", False),
        ("docs/a.md", "ADR-0013", "decisions/0013-x.md", False),
        ("docs/a.md", "取引先の設計", "13_取引先設計.md", False),
        ("docs/a.md", "13 §4", "https://example.com/13", False),
        ("docs/a.md", "80 §3", "../tools/README.md", False),
    ]
    for from_rel, label, target, want in cases:
        got = checks.link_label_mismatch(from_rel, label, target) is not None
        if got != want:
            ng.append("check_link_label_targets: [{}]({}) は {} はず".format(
                label, target, "鳴る" if want else "鳴らない"))
    return ng


def _check_question_forms() -> List[str]:
    """閉じた問いへの参照の検体。

    **この検査は 2026-09-13 に足した**——開発者の答えを反映して 3 つの問いを消した回に、
    それを指す参照が 6 か所残った（qa/02 のラウンド 87）。
    [30 §11](../../docs/30_作業のルール.md) が番号の使い回しを禁じているので、
    **無い番号は必ず腐った参照**である。**規約を壊す最短の書き方を検体に持つ。**
    """
    ng = []
    defined = {"11", "21"}
    cases = [
        # (行, 鳴る番号)
        ("上限は [05 の Q-20](05_開発者への問い.md) で諮っている", ["20"]),
        ("Q-23 の ① と Q-09 の答え", ["23", "09"]),
        ("| 何を決めたか | Q-24 |", ["24"]),
        ("Q-21 が決まった回に Q-99 も見る", ["99"]),
        ("列の作り替えは Q-9 で諮った", ["9"]),
        # 当たってはいけない形
        ("いまも生きている [05 の Q-21](05_開発者への問い.md)", []),
        ("**問い（旧 Q-20）は 05 から消した**", []),
        ("旧Q-23 の ③ は 12 が引き取った", []),
        ("番号 `Q-nn` は使い回さない", []),
        ("消税軽減Q&A（制度）問 3", []),
        ("Q-11 と Q-21 の 2 つが残る", []),
    ]
    for line, want in cases:
        got = checks.dangling_questions(line, defined)
        if got != want:
            ng.append("check_question_numbers: {!r} は {} のはずが {}".format(line, want, got))
    return ng


def selftest() -> int:
    ng: List[str] = []
    for part in (_check_updated_violation, _check_superseded_links, _check_other_checks,
                 _check_section_ref_forms, _check_article_notation_forms, _check_dated_switch_forms,
                 _check_law_abbreviation_forms, _check_link_label_forms, _check_question_forms,
                 _check_skill_front_matter_forms, _check_skill_is_checked, _check_skill_real_data,
                 _check_real_data, _check_wiring):
        ng.extend(part())
    for msg in ng:
        print("NG  " + msg)
    print("lint_docs: すべて期待どおり" if not ng
          else "lint_docs: {} 件が期待と違う".format(len(ng)))
    return 1 if ng else 0
