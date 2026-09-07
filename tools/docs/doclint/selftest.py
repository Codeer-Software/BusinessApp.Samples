#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""doclint.selftest — 関門そのものを検査する（`lint_docs.py --selftest`）.

**中身を空にしても緑**という状態を作らないための検査である。見るのは 4 つ。

  1. 判定の純粋部分（`updated_violation` / `body_of`）が期待どおり鳴るか
  2. **わざと壊した入力**で `check_superseded_links` が鳴り、免除の形では鳴らないか。
     件数だけでなく**指摘文の中身**まで表明する（error を warn に格下げしても件数は変わらない）
  3. `section_refs` が節への参照の 5 形を拾い、**拾ってはいけない形を拾わない**か
  4. 定義した検査が全部 `ALL_CHECKS` に載り、`main` から**正しい引数で**呼ばれているか
  5. `article_notation_violations` が `5 条 1 項` を拾い、**公布番号と「12 項目」を拾わない**か  # lint-docs:article-ok
  6. `check_dated_switches` が発効日前は数えるだけで、発効日以後は error にし、印で外れるか
"""

from __future__ import annotations

import datetime
import os
import re
from typing import Dict, List

from . import checks
from .checks import (ALL_CHECKS, ARTICLE_IGNORE, DATED_SWITCHES, SWITCH_IGNORE, Finding,
                     article_notation_violations, check_superseded_links, dated_switch_hits,
                     successor_of, updated_violation)
from .model import (ADR_LEDGER, DOCS_INDEX, Doc, INLINE_IGNORE, LINE_LIMIT, SEV_ERROR,
                    SEV_WARN, body_of, load_docs, parse_front_matter)

CLI_SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "lint_docs.py")

# main が検査を呼ぶときの**引数まで含めた**呼び出し。名前だけの包含だと
# `check_superseded_links(d, {}, findings)` のような配線の壊れ方が通ってしまう
REQUIRED_CALLS = ("check_superseded_links(d, docs_by_rel, findings)",
                  "scanned_notation, ignored_notation = check_article_notation(docs, findings)",
                  "remaining_switch, ignored_switch = check_dated_switches(docs, findings, today=today)",
                  # 要約行の印字。消すと「0 に落ちたら疑う」の設計が黙って死ぬ
                  "条番号の切替: 旧の字面が {} 行（印で外した {} 行）",
                  "remaining_switch, ignored_switch))")


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


def selftest() -> int:
    ng: List[str] = []
    for part in (_check_updated_violation, _check_superseded_links, _check_other_checks,
                 _check_section_ref_forms, _check_article_notation_forms, _check_dated_switch_forms,
                 _check_real_data, _check_wiring):
        ng.extend(part())
    for msg in ng:
        print("NG  " + msg)
    print("lint_docs: すべて期待どおり" if not ng
          else "lint_docs: {} 件が期待と違う".format(len(ng)))
    return 1 if ng else 0
