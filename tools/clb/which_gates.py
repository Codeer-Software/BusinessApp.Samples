#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""which_gates.py — この回に流すべき計器を、差分から決めて印字する.

[31 §6](../../docs/31_検証のルール.md) は「コミット前フックに載っていない計器は、流す回を自分で決める」と定め、
その判定を文章で持っている。**入力が差分なら、そのうち機械に当てられる分は機械の仕事である。**

**流すのはこの道具の仕事ではない。決めて印字するだけ**——掃引は分かかるので、
フックにも載せないし、ここからも起こさない
（[ADR-0058](../../docs/decisions/0058-行セットの差分で殺す掃引は入力コーパスを持たず行動テストが流した入力をその場で当てる.md) の決定 9）。

**「流さない」も必ず声に出す。** 黙ると「言われなかったから流さなくてよい」に倒れ、
**この道具自身が「最良の報告」を返す関門**になる（`self-review` スキル §9 の 1）。
**差分が 0 件のときも、git が読めないときも黙らない**——どちらも「触っていない」とは別である。

**この道具が当てられない判定が 2 つある**（31 §6 は 4 つ持っている）。どちらも印字で名指しする。

- **フェーズの区切り**にノックアウトを流す（[ADR-0053](../../docs/decisions/0053-制約ノックアウトはDDLを1つずつ外し振る舞いのテストだけで赤になるかを見る.md) の決定 7 の②）——**差分からは決まらない**
- **「拒まれること」のテストを書いた回**に `knockout.ps1 -Only <点>`、
  **クエリの SQL を書いた回**に `sql_sweep.ps1 -Mode Tests`——**中身を読まないと決まらない**

使い方:
    python tools/clb/which_gates.py                 # main との差分
    python tools/clb/which_gates.py --base HEAD~1
    python tools/clb/which_gates.py --selftest      # 判定そのものを検査する

終了コード: 0 = 判定できた（流すものがあっても 0）／1 = `--selftest` が赤／
            2 = 判定できなかった（差分 0 件・`--base` が無い・git が動かない）
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from typing import Callable, Dict, List, NamedTuple, Optional, Sequence, Set, Tuple

sys.stdout.reconfigure(encoding="utf-8")  # Windows の既定は CP932 で、理由文が化ける

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

# クエリモジュールの正典は `Designer/Design/Modules/**/<名前>.Query.sql`、
# その行動テストは `BusinessApp.Schema.Tests/<名前>QueryTests.cs`。
# **`sql_sweep.ps1 -Only` が受け取る名前と同じ**（どちらもファイル名から取る）
QUERY_SQL_DIR = "Designer/Design/Modules/"
QUERY_SQL_SUFFIX = ".Query.sql"
QUERY_TESTS_DIR = "BusinessApp/BusinessApp.Schema.Tests/"
QUERY_TESTS_SUFFIX = "QueryTests.cs"

# 制約ノックアウトの契機。**`Designer/ddl/` は ADR-0053 決定 7 が名指ししているもの**で、
# `Designer/migrations/` は **Claude が足した**（トリガは実測で migrations 側にも書かれており、
# 落とすと「配った差分でトリガを足した回」が素通りする。安全側へ倒した）
DDL_DIRS = ("Designer/ddl/", "Designer/migrations/")

# **正典はファイル名で指す。** ディレクトリで指すと、同じ場所にある別の計器の正典まで
# 巻き込んで**正反対の判定**を返す。**両方の計器について対称に持つ**
KNOCKOUT_CANON = ("BusinessApp/BusinessApp.TestSupport/SchemaKnockout.cs",
                  "BusinessApp/BusinessApp.KnockoutCli/")
SWEEP_CANON = ("BusinessApp/BusinessApp.TestSupport/SqlMutationProbe.cs",
               "tools/clb/sql_mutate.py")

# 実在する `*.Query.sql` の本数。**下限ではなく実数で持ち、増減したら赤くする**
# （`sql_sweep.ps1` のラチェット表と同じ作法。下限だけだと、走査が痩せても気づけない）
QUERY_SQL_COUNT = 4

PHASE_NOTE = "フェーズの区切りなら流す（差分からは決まらない。[04 §1]・ADR-0053 決定 7）"
THROWS_NOTE = "「拒まれること」のテストを書いたなら `-Only <点>` で流す（中身を読まないと決まらない）"
TESTS_NOTE = "クエリの SQL を書いたなら `-Mode Tests` も流す（弱い表明を見つけるのは A 案だけ。qa/03 L-46）"
SLOW_NOTE = "**分かかる。バックグラウンドで回し、その間ビルドしない**（31 §6）"


class Finding(NamedTuple):
    run: bool
    command: str
    why: str
    note: str


def module_of(rel: str) -> Optional[str]:
    """クエリモジュールの名前を返す。クエリに関わらないファイルなら None。"""
    name = rel.rsplit("/", 1)[-1]
    if rel.startswith(QUERY_SQL_DIR) and name.endswith(QUERY_SQL_SUFFIX):
        return name[: -len(QUERY_SQL_SUFFIX)]
    if rel.startswith(QUERY_TESTS_DIR) and name.endswith(QUERY_TESTS_SUFFIX):
        return name[: -len(QUERY_TESTS_SUFFIX)]
    return None


def _listed(items: Sequence[str], limit: int = 3) -> str:
    """先頭だけを見せるときは、**省いたことを言う**（黙って切らない）。"""
    items = list(items)
    if len(items) <= limit:
        return "・".join(items)
    return "・".join(items[:limit]) + "・ほか {} 件".format(len(items) - limit)


def query_modules(root: str = REPO_ROOT) -> List[str]:
    """実在する `*.Query.sql` のモジュール名（`sql_sweep.ps1 -Only` が受け取る名前）。"""
    found = []
    base = os.path.join(root, *QUERY_SQL_DIR.rstrip("/").split("/"))
    for dirpath, _dirs, files in os.walk(base):
        for name in files:
            if name.endswith(QUERY_SQL_SUFFIX):
                found.append(name[: -len(QUERY_SQL_SUFFIX)])
    return sorted(found)


def decide(changed: Sequence[str], known: Optional[Set[str]] = None) -> List[Finding]:
    """変わったファイルの一覧から、流す計器を決める（純粋関数）。**必ず 2 行返す。**

    `known` は実在する `*.Query.sql` のモジュール名。**知らない名前を `-Only` に載せない**
    ——`sql_sweep.ps1` はラチェット表と実在の両側突合で throw するので、
    **印字したコマンドが必ず失敗する**（消したモジュール・SQL の無い行動テストがこれに当たる）。
    """
    touched = sorted({m for m in (module_of(r) for r in changed) if m})
    modules = sorted(m for m in touched if known is None or m in known)
    unknown = sorted(m for m in touched if known is not None and m not in known)
    ddl = sorted(r for r in changed if r.startswith(DDL_DIRS) and r.endswith(".sql"))
    k_canon = sorted(r for r in changed if r.startswith(KNOCKOUT_CANON))
    s_canon = sorted(r for r in changed if r.startswith(SWEEP_CANON))

    out: List[Finding] = []
    if ddl or k_canon:
        why = "・".join(filter(None, [
            "DDL を触った（{}）".format(_listed(ddl)) if ddl else "",
            "外す点の正典を触った（{}）".format(_listed(k_canon)) if k_canon else ""]))
        out.append(Finding(True, "pwsh -NoProfile -File tools/clb/knockout.ps1", why,
                           SLOW_NOTE + "／" + THROWS_NOTE))
    else:
        out.append(Finding(False, "tools/clb/knockout.ps1",
                           "DDL も外す点の正典も触っていない",
                           PHASE_NOTE + "／" + THROWS_NOTE))

    sweep_why = "・".join(filter(None, [
        "クエリの SQL か行動テストを触った（{}）".format(_listed(modules)) if modules else "",
        "掃引の正典を触った（{}）".format(_listed(s_canon)) if s_canon else ""]))
    if modules:
        out.append(Finding(
            True,
            "pwsh -NoProfile -File tools/clb/sql_sweep.ps1 -Mode Rows -Only " + ",".join(modules),
            sweep_why, TESTS_NOTE))
    elif s_canon:
        out.append(Finding(True, "pwsh -NoProfile -File tools/clb/sql_sweep.ps1 -Mode Rows",
                           sweep_why, TESTS_NOTE))
    else:
        out.append(Finding(False, "tools/clb/sql_sweep.ps1",
                           "クエリの SQL も行動テストも掃引の正典も触っていない", TESTS_NOTE))

    if unknown:
        out.append(Finding(False, "（`-Only` に載せなかった名前）",
                           "実在する `*.Query.sql` が無い: {}".format(_listed(unknown)),
                           "**消したなら `sql_sweep.ps1` のラチェット表の行も消す。"
                           "新しく足したなら表に行を足してから流す**"))
    return out


Runner = Callable[[List[str]], Optional[str]]


def _git(args: List[str]) -> Optional[str]:
    try:
        out = subprocess.run(["git", "-c", "core.quotepath=false"] + args, cwd=REPO_ROOT,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
    except (OSError, subprocess.CalledProcessError):
        return None
    return out.stdout.decode("utf-8", errors="replace")


def changed_files(base: str, runner: Runner = _git) -> Optional[List[str]]:
    """`base` からの差分。取れなければ None。

    **4 系統を足す**——コミット済み・作業ツリー・段階済み・**まだ追跡していない新しいファイル**。
    最後を落とすと、**新しいクエリの SQL を足した回に「触っていない」と報告する**
    （`git diff` は追跡ファイルしか見ない）。

    **削除も拾う。** 消した回こそ掃引したい回であり、
    **`-Only` に載せない**判断は `decide` が実在の集合と突き合わせて行う。
    """
    calls = [["diff", "--name-only", base + "...HEAD"],
             ["diff", "--name-only"],
             ["diff", "--name-only", "--cached"],
             ["ls-files", "--others", "--exclude-standard"]]
    rels = set()
    for call in calls:
        out = runner(call)
        if out is None:
            return None
        for line in out.splitlines():
            if line.strip():
                rels.add(line.strip())
    return sorted(rels)


def report(base: str, changed: Optional[Sequence[str]],
           known: Optional[Set[str]] = None) -> Tuple[int, List[str]]:
    """印字する行と終了コードを返す（純粋関数）。**印字そのものを検査できるように分けてある。**"""
    if changed is None:
        return 2, ["which_gates: 差分を取れませんでした。**判定していません**"
                   "（`{}` が実在しないか、git が動きません）。".format(base)]
    if not changed:
        return 2, ["which_gates: `{}` との差分が **0 件**です。**判定していません**。".format(base),
                   "          `--base` が自分自身かもしれません。"
                   "**直前の回を見るなら `--base HEAD~1`、ブランチなら `--base main` を指定してください。**"]
    lines = ["== この回に流す計器（`{}` との差分 {} ファイル）==".format(base, len(changed))]
    for f in decide(changed, known):
        lines.append("{}  {}".format("流す　　" if f.run else "流さない", f.command))
        lines.append("          理由: {}".format(f.why))
        lines.append("          ほか: {}".format(f.note))
    lines.append("**判定の正典は 31 §6。** ここは差分に当てられる分だけを当てている。流すのは人である。")
    return 0, lines


def main() -> int:
    parser = argparse.ArgumentParser(
        description="この回に流すべき計器（制約ノックアウト・SQL 掃引）を差分から決めて印字する。"
                    "流しはしない。判定の正典は docs/31 §6。")
    parser.add_argument("--base", default="main", help="比較の起点（既定: main）")
    parser.add_argument("--selftest", action="store_true", help="判定そのものを検査する")
    args = parser.parse_args()
    if args.selftest:
        return _selftest()
    code, lines = report(args.base, changed_files(args.base), set(query_modules()))
    for line in lines:
        print(line)
    return code


def _selftest() -> int:
    ng = []
    counted = {}

    # --- ① 判定（`decide`）。**コマンド文字列を丸ごと当てる** ---------------
    K_RUN = "pwsh -NoProfile -File tools/clb/knockout.ps1"
    K_SKIP = "tools/clb/knockout.ps1"
    S_SKIP = "tools/clb/sql_sweep.ps1"
    KNOWN = {"GeneralLedger", "JournalBook"}

    def sweep(names=""):
        base = "pwsh -NoProfile -File tools/clb/sql_sweep.ps1 -Mode Rows"
        return base + (" -Only " + names if names else "")

    cases = [
        ([], K_SKIP, S_SKIP),
        (["docs/README.md"], K_SKIP, S_SKIP),
        (["Designer/ddl/012_x.sql"], K_RUN, S_SKIP),
        (["Designer/migrations/0030_x.sql"], K_RUN, S_SKIP),
        (["Designer/ddl/README.md"], K_SKIP, S_SKIP),             # .sql でなければ当たらない
        (["BusinessApp/BusinessApp.TestSupport/SchemaKnockout.cs"], K_RUN, S_SKIP),
        (["BusinessApp/BusinessApp.KnockoutCli/Program.cs"], K_RUN, S_SKIP),
        # **掃引の正典は掃引の側へ倒れる**（同じフォルダにあるので取り違えやすい）
        (["BusinessApp/BusinessApp.TestSupport/SqlMutationProbe.cs"], K_SKIP, sweep()),
        (["tools/clb/sql_mutate.py"], K_SKIP, sweep()),
        (["Designer/Design/Modules/Accounting/Books/GeneralLedger.Query.sql"],
         K_SKIP, sweep("GeneralLedger")),
        (["BusinessApp/BusinessApp.Schema.Tests/JournalBookQueryTests.cs"],
         K_SKIP, sweep("JournalBook")),
        (["BusinessApp/BusinessApp.Schema.Tests/DateFormatGuardTests.cs"], K_SKIP, S_SKIP),
        # **隣の棚**は当たらない（前置を広げた壊し方を捕まえる）。
        # **名前は実在するものを使う**——知らない名前だと `unknown` に落ちて、
        # 前置が壊れていても同じ「流さない」になり、検体が空振りする
        (["Designer/seed/GeneralLedger.Query.sql"], K_SKIP, S_SKIP),
        (["BusinessApp/BusinessApp.AccountingCore.Tests/GeneralLedgerQueryTests.cs"],
         K_SKIP, S_SKIP),
        (["docs/GeneralLedger.Query.sql"], K_SKIP, S_SKIP),
        (["docs/GeneralLedgerQueryTests.cs"], K_SKIP, S_SKIP),
        (["Designer/ddl/012_x.sql",
          "Designer/Design/Modules/A/JournalBook.Query.sql"], K_RUN, sweep("JournalBook")),
        (["Designer/Design/Modules/A/JournalBook.Query.sql",
          "BusinessApp/BusinessApp.Schema.Tests/JournalBookQueryTests.cs"],
         K_SKIP, sweep("JournalBook")),
        (["Designer/Design/Modules/A/GeneralLedger.Query.sql",
          "Designer/Design/Modules/A/JournalBook.Query.sql"],
         K_SKIP, sweep("GeneralLedger,JournalBook")),
    ]
    for changed, want_k, want_s in cases:
        got = decide(changed, KNOWN)
        if len(got) < 2:
            ng.append("decide({}): 少なくとも 2 行返すはずが {} 行".format(changed, len(got)))
            continue
        if got[0].command != want_k or got[0].run != (want_k == K_RUN):
            ng.append("decide({}): knockout の行が {!r} のはずが {!r}"
                      .format(changed, want_k, got[0].command))
        if got[1].command != want_s or got[1].run != (want_s != S_SKIP):
            ng.append("decide({}): sql_sweep の行が {!r} のはずが {!r}"
                      .format(changed, want_s, got[1].command))
    counted["判定の検体"] = len(cases)

    # **実在しないモジュールを `-Only` に載せない**（印字したコマンドが必ず落ちる形）
    ghost = decide(["BusinessApp/BusinessApp.Schema.Tests/GhostQueryTests.cs"], KNOWN)
    if "Ghost" in ghost[1].command:
        ng.append("decide: 実在しないモジュールを -Only に載せている: {}".format(ghost[1].command))
    if len(ghost) < 3 or "Ghost" not in ghost[2].why:
        ng.append("decide: 載せなかった名前を報告していない（黙って落としている）")

    # **DDL と正典を両方触ったら、両方を理由に挙げる**
    both = decide(["Designer/ddl/x.sql",
                   "BusinessApp/BusinessApp.TestSupport/SchemaKnockout.cs"], KNOWN)[0]
    if "DDL" not in both.why or "正典" not in both.why:
        ng.append("decide: DDL と正典を両方触ったのに片方しか理由に出ない: {}".format(both.why))

    # **当てられない判定を、印字で必ず名指しする。定数を自分自身と比べない**
    skipped = decide([], KNOWN)
    if "フェーズの区切り" not in skipped[0].note or "拒まれること" not in skipped[0].note:
        ng.append("decide: knockout の行が、当てられない判定を言っていない")
    if "-Mode Tests" not in skipped[1].note:
        ng.append("decide: sql_sweep の行が -Mode Tests を言っていない")
    if "分かかる" not in decide(["Designer/ddl/x.sql"], KNOWN)[0].note:
        ng.append("decide: 流す側で所要（分かかる）を言っていない")
    if "ほか 2 件" not in _listed(["a", "b", "c", "d", "e"]) or "a・b・c" not in _listed(list("abcde")):
        ng.append("_listed: 検体の名前か、省いた件数が落ちている")

    # --- ② 印字（`report`）。**関門の外に置かない** -------------------------
    code, lines = report("main", None)
    if code != 2 or not any("判定していません" in l for l in lines):
        ng.append("report: git を読めないときに 2 と「判定していません」を返さない")
    code, lines = report("main", [])
    if code != 2 or not any("--base HEAD~1" in l for l in lines):
        ng.append("report: 差分 0 件のときに 2 と、次に打つ `--base` を言っていない")
    code, lines = report("main", ["docs/README.md"], KNOWN)
    if code != 0:
        ng.append("report: 判定できたのに 0 を返さない")
    if sum(1 for l in lines if l.startswith("流す") or l.startswith("流さない")) != 2:
        ng.append("report: 流す・流さないの行が 2 本出ていない: {}".format(lines))
    if not any("理由:" in l for l in lines) or not any("ほか:" in l for l in lines):
        ng.append("report: 理由か「ほか」の行が落ちている")
    counted["印字の検体"] = 4

    # --- ③ 差分の取り方（`changed_files`）。**4 系統を 1 つずつ**守る -------
    def fake(mapping: Dict[str, str]) -> Runner:
        return lambda args: mapping.get(" ".join(args), "")

    QSQL = "Designer/Design/Modules/A/GeneralLedger.Query.sql"
    systems = {
        "コミット済み": "diff --name-only main...HEAD",
        "作業ツリー": "diff --name-only",
        "段階済み": "diff --name-only --cached",
        "未追跡": "ls-files --others --exclude-standard",
    }
    for label, key in systems.items():
        got = changed_files("main", fake({key: QSQL + "\n"}))
        if got != [QSQL]:
            ng.append("changed_files: {} だけに載せたのに拾えない（{}）".format(label, got))
        elif not decide(got, KNOWN)[1].run:
            ng.append("changed_files: {} で拾ったのに sql_sweep が流す側にならない".format(label))
    if changed_files("main", lambda a: None) is not None:
        ng.append("changed_files: 読めないときに None を返していない")
    for key in systems.values():
        runner = (lambda k: (lambda a: None if " ".join(a) == k else ""))(key)
        if changed_files("main", runner) is not None:
            ng.append("changed_files: {} が読めないのに None を返さない".format(key))
    counted["差分の系統"] = len(systems)

    # --- ④ 実データ。**実物の git を 1 回通し、表を両側から突き合わせる** ---
    real = changed_files("HEAD~1", _git)
    if real is None:
        ng.append("changed_files: 実物の git で差分を取れない（**None は 0 件ではない**）")
    elif not real:
        ng.append("changed_files: 実物の git で 0 件（`HEAD~1` との差分が無いのは考えにくい）")
    else:
        # **返った行が実在する**ことまで見る（quotepath が既定に戻ると壊れた名前が返る）
        missing = [r for r in real if not os.path.exists(os.path.join(REPO_ROOT, *r.split("/")))]
        deleted = _git(["diff", "--name-only", "--diff-filter=D", "HEAD~1...HEAD"]) or ""
        gone = set(deleted.split())
        unexplained = [r for r in missing if r not in gone]
        if unexplained:
            ng.append("changed_files: 実在しないパスが返った（quotepath が効いていない？）: {}"
                      .format(unexplained[:3]))
    counted["実物の差分"] = len(real or [])

    # **日本語の名前が壊れずに返ることを見る。** `core.quotepath` が既定へ戻ると
    # `"docs/\346\227\245..."` の形になり、**件数も接頭辞一致も静かにずれる**。
    # **限界: この機では `-c core.quotepath=false` を外しても赤くならない**
    # ——リポジトリのローカル設定が既に `false` を持っているため。
    # **この検査が効くのは、その設定を持たない機で clone したとき**である（そこが本来の危険）
    tracked = _git(["ls-files"]) or ""
    japanese = [r for r in tracked.splitlines() if any(ord(c) > 0x7F for c in r)]
    if not japanese:
        ng.append("追跡ファイルに日本語名が 1 つも無い（このリポジトリでは考えにくい。読み取りが死んでいないか）")
    elif any("\\3" in r or r.startswith('"') for r in japanese):
        ng.append("日本語のパスがエスケープされて返っている（quotepath が効いていない）: {}"
                  .format(japanese[0]))

    def under(rel):
        p = os.path.join(REPO_ROOT, *rel.rstrip("/").split("/"))
        return os.path.isdir(p) or os.path.isfile(p)

    for rel in DDL_DIRS + KNOCKOUT_CANON + SWEEP_CANON + (QUERY_SQL_DIR, QUERY_TESTS_DIR):
        if not under(rel):
            ng.append("定数が実在しない（置き場が動いた？）: {}".format(rel))

    # **実数と厳密に比べる**（下限だと、走査が痩せても増えても気づけない）
    real_modules = query_modules()
    if len(real_modules) != QUERY_SQL_COUNT:
        ng.append("`*.Query.sql` が {} 本ある（表は {} 本）。**増減したら表も直す**: {}"
                  .format(len(real_modules), QUERY_SQL_COUNT, real_modules))
    elif not decide([QUERY_SQL_DIR + real_modules[0] + QUERY_SQL_SUFFIX],
                    set(real_modules))[1].run:
        ng.append("実在のクエリ SQL を渡しても sql_sweep が流す側にならない")
    counted["実在のモジュール"] = len(real_modules)

    if changed_files("no-such-ref-for-selftest") is not None:
        ng.append("changed_files: 実在しない ref で None を返していない")

    for m in ng:
        print("NG  " + m)
    print("which_gates: " + ("すべて期待どおり（" if not ng else "{} 件が期待と違う（".format(len(ng)))
          + "・".join("{} {}".format(k, v) for k, v in counted.items()) + "）")
    return 1 if ng else 0


if __name__ == "__main__":
    sys.exit(main())
