#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""doclint.selftest — 関門そのものを検査する（`lint_docs.py --selftest`）.

**中身を空にしても緑**という状態を作らないための検査である。見るのは 3 つ。

  1. 判定の純粋部分（`updated_violation` / `body_of`）が期待どおり鳴るか
  2. **わざと壊した入力**で `check_superseded_links` が鳴り、免除の形では鳴らないか。
     件数だけでなく**指摘文の中身**まで表明する（error を warn に格下げしても件数は変わらない）
  3. 定義した検査が全部 `ALL_CHECKS` に載り、`main` から**正しい引数で**呼ばれているか
"""

from __future__ import annotations

import os
import re
from typing import Dict, List

from . import checks
from .checks import (ALL_CHECKS, Finding, check_superseded_links, successor_of,
                     updated_violation)
from .model import (ADR_LEDGER, DOCS_INDEX, Doc, INLINE_IGNORE, LINE_LIMIT, SEV_ERROR,
                    SEV_WARN, body_of, load_docs, parse_front_matter)

CLI_SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "lint_docs.py")

# main が検査を呼ぶときの**引数まで含めた**呼び出し。名前だけの包含だと
# `check_superseded_links(d, {}, findings)` のような配線の壊れ方が通ってしまう
REQUIRED_CALLS = ("check_superseded_links(d, docs_by_rel, findings)",)


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
    live = _fake("docs/09_other.md", {"status": "current"}, ["本文"])
    new = _fake("docs/decisions/0029-new.md", {"status": "current"}, ["本文"])
    far = _fake("docs/08_dead.md", {"status": "superseded", "related": "[09_other.md]"}, ["中身"])
    by_rel = {d.rel: d for d in (dead, older, gone, live, new, far)}
    to_dead, to_live = "[旧](decisions/0019-old.md)", "[今](09_other.md)"
    cur = {"status": "current"}
    to_far = "[遠](08_dead.md)"       # docs/10_x.md から見た別の superseded 文書
    to_older = "[古](decisions/0006-older.md)"  # related に current の後継が無い文書
    cases = [
        # (rel, meta, body, 鳴るべき件数)
        ("docs/10_x.md", cur, [to_dead], 1),                                  # わざと壊した入力
        ("docs/10_x.md", cur, [to_live], 0),
        ("docs/10_x.md", cur, ["0019-old.md は既に覆されている"], 0),          # 散文は咎めない
        ("docs/10_x.md", cur, ["[外](https://example.com/0019-old.md)"], 0),
        ("docs/10_x.md", cur, ["[未](decisions/0019-nowhere.md)"], 0),        # 実在しない
        ("docs/10_x.md", cur, ["[印](decisions/0018-hist.md)"], 0),           # historical は対象外
        ("docs/10_x.md", cur, ["[節](decisions/0019-old.md#2-決定)"], 1),     # アンカー付き
        ("docs/10_x.md", cur, ["[空]( decisions/0019-old.md )"], 1),          # 前後の空白を落とす
        ("docs/decisions/0030-y.md", cur, ["[上](../08_dead.md)"], 1),        # `../` 起点
        ("docs/10_x.md", cur, ["```", to_dead, "```"], 0),                    # コードフェンスの中
        ("docs/10_x.md", cur, ["```", to_dead, "```", to_dead], 1),           # フェンスを閉じたら効く
        ("docs/10_x.md", {"status": "superseded", "related": "[09_other.md]"}, [to_dead], 0),
        ("docs/10_x.md", {"status": "historical", "related": "[09_other.md]"}, [to_dead], 0),
        # `growth: append` は免除しない（2026-08-28 に外した。理由は関数の docstring）
        ("docs/10_x.md", {"status": "current", "growth": "append"}, [to_dead], 1),
        # フロントマターは対象外。**リンクの形をした値**を置いて、本文だけを見ていることを表明する
        ("docs/10_x.md", {"status": "current", "note": to_dead}, ["本文"], 0),
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
        ("docs/10_x.md", cur, [to_dead + " " + to_far + " " + INLINE_IGNORE], 0),
        ("docs/10_x.md", cur, [to_dead + " " + to_far], 2),                   # 対照（印を外す）
    ]
    for rel, meta, body, want in cases:
        got: List[Finding] = []
        check_superseded_links(_fake(rel, meta, body), by_rel, got)
        if len(got) != want:
            ng.append("check_superseded_links: 期待 {} 件 / 実際 {} 件: {} {}"
                      .format(want, len(got), rel, body))

    # 行番号は**本文の先頭以外**に置いて表明する。先頭に置くと `body_start + 1` に
    # 固定する壊れ方と区別が付かない（2026-08-28 の自己レビューで実際に空振りしていた）
    multi = _fake("docs/10_x.md", cur, ["前書き", "", to_dead, "間", to_older])
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
        ("指摘先が違反した文書", got and got[0][1] == "docs/10_x.md"),
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
    main_src = cli_src[main_at.start():]

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

    existing = {"docs/09_other.md"}
    link_cases = [
        ("実在するリンクは鳴らない", _fake("docs/a.md", ok_meta, ["[今](09_other.md)"]), 0),
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
        ("索引に載っている", ["[A](01_a.md)"], 0),
        ("索引に無い", ["（空）"], 1),
    ]
    for label, lines, want in index_cases:
        index = _fake(DOCS_INDEX, ok_meta, lines)
        got = run(checks.check_docs_index, [index, _fake("docs/01_a.md", ok_meta, ["本文"])])
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


def selftest() -> int:
    ng: List[str] = []
    for part in (_check_updated_violation, _check_superseded_links, _check_other_checks,
                 _check_real_data, _check_wiring):
        ng.extend(part())
    for msg in ng:
        print("NG  " + msg)
    print("lint_docs: すべて期待どおり" if not ng
          else "lint_docs: {} 件が期待と違う".format(len(ng)))
    return 1 if ng else 0
