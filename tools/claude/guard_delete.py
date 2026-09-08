#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""guard_delete.py — Claude Code の PreToolUse フック。**失うことを止める。**

開発者の指示（2026-09-07）: `rm` の代わりに使える削除コマンドを作り、`rm` は deny にする。
**ごみ箱送り（`trash.ps1`）は確認なしで実行してよい**（同日。**戻せるから**）。
**`Write`（全上書き）も止める**（同日。**「どっちかというと Write が怖い。trash よりずっと怖い」**）。

やること
--------
1. **削除にあたるコマンドは、当たり先によらず拒む。**
   代わりに `tools/claude/trash.ps1`（ごみ箱送り）を使えと理由文で案内する。
   **語彙の正典はこのファイルの正規表現である**（散文で列挙しない）。
2. **この repo の `trash.ps1` を 1 本だけ呼ぶコマンドは通す。**
3. **保護対象への `Write` を拒む**（当たり先の絶対パスで判定する）。
4. **保護対象の名前に触れる上書きのコマンドも拒む**（`sed -i`・`Copy-Item`・`> ファイル` など）。
5. **どの経路であっても、保護対象に触れるものは拒む。**

`Write` と `Edit` の線引き
--------------------------
**`Write` は削除より強い。** ごみ箱は前の中身を残すが、上書きは残さない。
しかも `Write` は中身を照合しないので、**想定と違うファイルでも黙って通る**。

**`Edit` はこのフックでは見ない。** `old_string` の照合があるので**取り違えでは壊れない**からである。
ただし**照合を通す字は Read すれば手に入るので、意図すれば `Edit` でも空にできる**——
だから保護対象の `Edit` は `settings.json` の `ask` に載せてある（拒みはしない。
追跡外の環境設定を直すのは正当な作業で、塞ぐと自律作業が止まる）。

守りたいもの
------------
追跡ファイルは**コミット済みであれば** git が復元手段になる。
戻せない・戻すのが現実的でないのは追跡外のものと、**コミットしていない変更**である
（後者を消す `git reset --hard` などをこのフックが拒むのは、そのためである）。
**一覧と載せる基準は `protected_paths.json` が持つ**——`trash.ps1` と同じ 1 ファイルを読む。

なぜ許可リストではなくフックか
------------------------------
Claude Code の Bash 権限は**コマンド文字列の前方一致**で判定する。
`cd` を挟む・変数展開する・`find -exec` を使う、といった書き方は同じ規則で絞れない。
**パスで許可を絞るのは事故防止にはなっても、境界の保証にはならない。**
**序列と役割分担は ADR-0044 が持つ。**

`allow` を返すときの制約
------------------------
**`allow` は確認のプロンプトを飛ばす。** ただし**フックの判定は権限規則を迂回しない**——
`deny` と `ask` はフックが何を返しても評価される（Claude Code 公式ドキュメント
「Configure permissions」。2026-09-08 に Claude が確認）。それでも**返すのは、
コマンド全体がこの repo の `trash.ps1` を 1 本呼ぶだけ**のときに限る。
連結（`;` `&` `|` 改行 `` ` `` `$(`）が 1 つでもあれば `allow` にせず、通常の権限判定へ降りる。

限界（承知のうえで残す）
------------------------
- **コマンドの判定はシェルを解釈しない。** 変数に入れてから消す・パスを組み立てる形はすり抜ける。
  **「うっかり」を止める装置であって、悪意を止める装置ではない。**
- **引用の中も区別しないので過剰検出する**（`git grep "Remove-Item"` のような検索も拒む）。
  **そちらへ倒してある**——見逃した削除は戻せないが、過剰な拒否は語の表記を変えれば済む。
- **閉じた上書きの経路は限られる。** `Write` ツールと、保護対象の名前が出るシェルのコマンドだけである。
  **名前を出さずに上書きする形**（変数・相対パスの組み立て）は通る。
- **上書きは `settings.json` の `deny` では拒めない**（理由と出典は ADR-0044）。
  **このフックが起動しなければ、上書きは拒否ではなく確認（`ask`）に落ちる。**
  `ask` が覆うのは `Edit`・`Write`・`NotebookEdit` で、**`MultiEdit` はどちらも覆わない**
  （このフックの `matcher` にも無い。いまのセッションには配られていない道具である）。
- **パスの突き合わせは `realpath` までで、`\\?\` 前置きは追わない。**
- **フックが実際に起動するか**（`python` が PATH にあるか等）は、このファイルの自己検査では保証できない。

標準入力: Claude Code のフック入力 JSON。`Bash`・`PowerShell` は `tool_input.command`、
          `Write` は `tool_input.file_path` を見る（相対パスは `cwd` で解決する）。
標準出力: 拒否するとき deny、ごみ箱送りの削除のとき allow。それ以外は何も出さない
          （＝通常の権限判定に落ちる）。**そこは「開発者への確認」とは限らない**——
          `settings.json` の許可には `Bash(python:*)` のような白紙の行があり、
          取りこぼしはそのまま実行になる（**その許可は絞らないと決めた**——
          docs/decisions/0044-削除はごみ箱送りに一本化しrmを機械で止める.md の採らなかった案）。
"""

import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8")  # Windows の既定は CP932 で、理由文が化ける
except Exception:
    pass

CANON = Path(__file__).with_name("protected_paths.json")
REPO_ROOT = Path(__file__).resolve().parents[2]
SETTINGS = REPO_ROOT / ".claude" / "settings.json"

TRASH_SCRIPT = "tools/claude/trash.ps1"
TRASH_COMMAND = f"pwsh -NoProfile -File {TRASH_SCRIPT} <パス>"

# コマンド名の前後に来てよい字。`/bin/rm`・`\rm`・`"rm"` のような書き方も当てる。
_BEFORE = r"(^|[\s;&|(/\\\"'])"
_AFTER = r"([\s;&|)\"']|$)"

# 完全削除の語。**この並びが語彙の正典である**——正規表現・`settings.json` の `deny`・
# 自己検査の検体の 3 つを、すべてここから導く。**散文でも表でも二重に持たない**
# （`deny` の語がこの並びと切れていて、語を消しても鳴らなかった。自己レビューで判明。2026-09-08）。
DELETION_WORDS = (
    "rm", "rmdir", "del", "erase", "unlink", "shred", "truncate",
    "Remove-Item", "ri", "rd", "Clear-Content", "Clear-Item",
)

# 完全削除。Bash と PowerShell の両方を見る。
DELETION = re.compile(
    _BEFORE + "(" + "|".join(DELETION_WORDS) + ")" + _AFTER,
    re.IGNORECASE,
)

# `find ... -delete` と `find ... -exec rm` は上の語形に当たらないので別に見る。
FIND_DELETE = re.compile(r"\bfind\b.*(-delete\b|-exec\s+rm\b)", re.IGNORECASE)

# .NET / COM を直に呼ぶ形（`[IO.File]::Delete(...)`・`(Get-Item x).Delete()`）。
DOTNET_DELETE = re.compile(r"(\[[\w.]*IO\.\w+\]::Delete|\.Delete\(\s*\))", re.IGNORECASE)

# Python から消す形（`Bash(python:*)` が許可されているので、ここで拾う）。
# **`send2trash` も入れてある**——ごみ箱送りではあるが、**保護対象の判定を通らない**。
# 止めたいのは「完全削除」ではなく「`trash.ps1` を経由しない削除」である。
PYTHON_DELETE = re.compile(
    r"(shutil\.rmtree|os\.(remove|unlink|rmdir|removedirs)|\.unlink\(|send2trash)", re.IGNORECASE
)

# **git 自身の破壊コマンド。** 「Git で戻せないか」を基準に据えた以上、
# コミットしていない変更を消す git は、rm と同じ重さで扱う。
GIT_DESTRUCTIVE = re.compile(
    r"\bgit\b[^;&|]*?\s(clean\b|reset\s+--hard\b|stash\s+(drop|clear)\b|checkout\s+--\s)",
    re.IGNORECASE,
)

# ファイルへのリダイレクト（`> 先` `>> 先`）。**`>` を素で拾わない。**
# `2>/dev/null`・`2>&1`・`'->'`・`>=`・`> 1` は上書きではないのに当たっていて、
# **保護対象の名前が出るだけの `ls` や `cat` まで拒んでいた**（2026-09-08 に実測。4 回踏んだ）。
# 外したのは 3 種類——**矢印と比較演算子**（前後の字で見る）、**捨て先**（`/dev/null`・`$null`）、
# **数だけの右辺**（`> 1`。日付の入ったファイル名 `> 2026.log` は数だけではないので残る）。
# **残る過剰検出**: 変数どうしの比較（`if (a > b)`）は当たる。**そちらへ倒したままにする**——
# 見逃した上書きは戻せないが、過剰な拒否は書き方を変えれば済む。
REDIRECT = r"(?<![-=<>!])>{1,2}\s*(?![&=])(?!/dev/null\b)(?!\$null\b)(?!\d+(?![\w./\\-]))\S"

# **中身を置き換える形。** これ単体では拒まない——**保護対象の名前が出たときだけ**拒む
# （`Write` ツール以外にも上書きの道はいくらでもあり、全部を止めると作業が成り立たない）。
OVERWRITE = re.compile(
    _BEFORE + r"(cp|copy|Copy-Item|mv|move|Move-Item|tee|Set-Content|Out-File|New-Item"
    r"|Expand-Archive|unzip|dd)" + _AFTER
    + r"|\bsed\s+-i|-OutFile\b|" + REDIRECT + r"|\bopen\([^)]*['\"][wa]",
    re.IGNORECASE,
)

# **戻せる道具。** ごみ箱送り（前の中身を残す）と、稼働 DB の退避・復元
# （消さず、上書きの前に必ず退避する）。**規則は docs/30_作業のルール.md §10。**
# **足してよいのは「取り返しがつく」ことをスクリプト自身が保証している道具だけ**である——
# allow は権限判定そのものを飛ばすので、1 本増やすたびに確認の外へ出る面積が広がる。
RECOVERABLE_SCRIPTS = (TRASH_SCRIPT, "tools/clb/db_snapshot.ps1")

# 戻せる道具への言及（保護対象の検査にかけるため、ゆるく拾う）。
RECOVERABLE_MENTION = re.compile(
    r"(^|[\s;&|(])(pwsh|powershell)\b[^;&|]*("
    + "|".join(re.escape(s.rsplit("/", 1)[-1]) for s in RECOVERABLE_SCRIPTS)
    + r")",
    re.IGNORECASE,
)

# **コマンド全体が、この repo の戻せる道具を 1 本呼ぶだけ**か（allow を出してよい形）。
RECOVERABLE_ONLY = re.compile(
    r"^\s*(pwsh|powershell)(\.exe)?\s+"
    r"((-|--)\w[\w-]*\s+)*"
    r"-File\s+[\"']?(\./)?("
    + "|".join(re.escape(s).replace("/", r"[\\/]") for s in RECOVERABLE_SCRIPTS)
    + r")[\"']?(\s|$)",
    re.IGNORECASE,
)

# 連結して別のことをするコマンド。**改行とバッククォートと $( を含める**——
# 改行を数え落とすと、2 行目以降の任意コマンドが allow に乗る。
CHAINED = re.compile(r"[;&|\n\r`]|\$\(")

MSYS_ABSOLUTE = re.compile(r"^/([A-Za-z])/(.*)$")


def load_entries():
    """正典の行をそのまま返す。読めない・形が違うなら例外（fail-closed の入口）。"""
    entries = json.loads(CANON.read_text(encoding="utf-8"))["protected"]
    if not entries:
        raise ValueError("protected が空である")
    for entry in entries:
        if not entry.get("path") or not entry.get("why"):
            raise ValueError(f"path または why が無い行がある: {entry}")
        if entry.get("kind") not in ("dir", "file"):
            raise ValueError(f"kind が dir でも file でもない行がある: {entry}")
    return entries


def basename_of(path_value: str) -> str:
    return path_value.replace("\\", "/").rstrip("/").split("/")[-1]


def load_protected():
    """[(コマンド文字列に探す正規表現, 理由), ...] を返す。読めなければ例外。"""
    patterns = []
    for entry in load_entries():
        # コマンド文字列にはリポジトリからの相対でも絶対でも書かれうるので、
        # **末尾の名前**を探す。前後に名前が続くもの（LocalDataX・.gitignore）は外す。
        patterns.append((
            re.compile(r"(?<![A-Za-z0-9_.\-])" + re.escape(basename_of(entry["path"])) + r"(?![A-Za-z0-9_.\-])",
                       re.IGNORECASE),
            entry["why"],
        ))
    return patterns


def from_msys(path_value: str) -> str:
    """Git Bash 形の絶対パス（`/c/Users/…`）を Windows 形へ直す。

    **この形で来ることがある**（Bash ツール経由の `cwd` など）。直さずに `abspath` へ渡すと
    `C:\\c\\Users\\…` という別の場所へ着地し、**保護判定が黙って外れる**（fail-open）。
    Windows でだけ変換する——他の OS では `/c/…` が本物の絶対パスでありうるため。
    """
    matched = MSYS_ABSOLUTE.match(path_value)
    if matched and os.name == "nt":
        return matched.group(1) + ":\\" + matched.group(2).replace("/", "\\")
    return path_value


def normalized(path_value: str) -> str:
    r"""比較用の絶対パス。**区切りまで含めて比べる**ため、末尾の区切りは落とす。

    `realpath` まで通すので、**8.3 の短縮名とリンクは実体へ寄る**（実在するものに限る）。
    `\\?\` 前置きは追わない（限界）。
    """
    return os.path.normcase(os.path.realpath(from_msys(path_value))).rstrip("\\/")


def within(target: str, root) -> bool:
    """`target`（正規化済み）が `root` の配下か、`root` そのものか。"""
    root_path = normalized(str(root))
    return target == root_path or target.startswith(root_path + os.sep)


def main_repo_root(script_repo_root):
    """**worktree から実行されたときの、本体のリポジトリの根。** それ以外は `None`。

    `.claude/worktrees/` はこの repo が実際に使う作業形態である（[30 §6](../../docs/30_作業のルール.md)）。
    そこからは `../../../LocalData` のように**本体を相対で指せてしまい**、
    しかも**コマンド文字列に保護対象の名前が出ない**ので、文字列側の判定では止まらない。
    `trash.ps1` の `Get-ProtectedRoots` が先に塞いだ穴で、**同じ穴が Write 側に残っていた**
    （自己レビューで実証。2026-09-08）。

    **`.git` がファイルのときだけ git を呼ぶ**——linked worktree（と submodule）の印である。
    ふだんの Write に子プロセスの費用をかけないため。git が無ければ守る範囲が狭まるだけで、本体は動く。
    """
    marker = Path(script_repo_root) / ".git"
    if not marker.is_file():
        return None
    try:
        proc = subprocess.run(
            ["git", "-C", str(script_repo_root), "rev-parse",
             "--path-format=absolute", "--git-common-dir"],
            capture_output=True, text=True, timeout=10,
        )
    except Exception:
        return None
    if proc.returncode != 0 or not (proc.stdout or "").strip():
        return None
    common = Path(proc.stdout.strip().splitlines()[0]).parent  # <本体>/.git → <本体>
    if within(normalized(str(common)), script_repo_root):
        return None
    return common


def decide_write(file_path: str, cwd: str, roots=None):
    """`Write`（全上書き）の当たり先を見る。(decision, reason) を返す。

    **コマンド文字列と違い、当たり先が 1 つに決まる**ので、名前ではなく解決した絶対パスで見る。

    **起点は 1 つとは限らない。** worktree から実行されたときは本体のリポジトリも守る
    （`roots` は検査のために外から渡せる形にしてある。既定は「このファイルの repo」だけで、
    **根の外を指されたときにだけ**本体を解決する）。
    """
    if not file_path:
        return None, None

    try:
        entries = load_entries()
    except Exception as exc:  # 正典が読めないなら、読めないまま上書きさせない
        return "deny", (
            f"守るものの正典（{CANON.name}）を読めなかった: {exc}。"
            "読めないまま上書きは通さない（tools/claude/guard_delete.py）。"
        )

    base = from_msys(cwd) if cwd else str(REPO_ROOT)
    resolved = from_msys(file_path)
    target = normalized(resolved if os.path.isabs(resolved) else os.path.join(base, resolved))

    if roots is None:
        roots = [REPO_ROOT]
        if not within(target, REPO_ROOT):
            main_root = main_repo_root(REPO_ROOT)
            if main_root is not None:
                roots.append(main_root)

    for root in roots:
        for entry in entries:
            protected_path = normalized(str(Path(root) / entry["path"]))
            if target == protected_path or target.startswith(protected_path + os.sep):
                return "deny", (
                    f"{entry['why']}。**Write は前の中身を残さない**ので通さない"
                    "（tools/claude/guard_delete.py）。**直すなら Edit を使う**（確認が入る）。"
                    "**新しく作るなら雛形から複製する**（`cp` / `Copy-Item`。docs/30_作業のルール.md §10）。"
                )
    return None, None


USE_TRASH = (
    f"ごみ箱へ送る削除コマンドを使う: {TRASH_COMMAND}"
    "（ごみ箱からは戻せるので、確認を待たずに実行してよい。規則は docs/30_作業のルール.md §10）。"
    "**削除ではなく検索などでこの語に触れただけなら、語の表記を変えて書き直す**"
    "（判定はコマンド文字列への正規表現で、引用の中を区別しない。過剰検出側に倒してある）。"
)


def decide(command: str):
    """(decision, reason) を返す。判定しないときは (None, None)。"""
    raw_deletion = bool(
        DELETION.search(command)
        or FIND_DELETE.search(command)
        or DOTNET_DELETE.search(command)
        or PYTHON_DELETE.search(command)
    )
    git_destructive = bool(GIT_DESTRUCTIVE.search(command))
    overwrite = bool(OVERWRITE.search(command))
    mentions_recoverable = bool(RECOVERABLE_MENTION.search(command))
    if not (raw_deletion or git_destructive or overwrite or mentions_recoverable):
        return None, None

    try:
        protected = load_protected()
    except Exception as exc:  # 正典が読めないなら、読めないまま消させない
        return "deny", (
            f"守るものの正典（{CANON.name}）を読めなかった: {exc}。"
            "読めないまま削除・上書きは通さない（tools/claude/guard_delete.py）。"
        )

    for pattern, why in protected:
        if pattern.search(command):
            reason = (
                f"{why}。この操作は許可しない（tools/claude/guard_delete.py）。"
                "作業用の複製が要るなら、サブエージェントの worktree かスクラッチパッドを使う。"
            )
            # **保護対象でも、次の一手を示す**——ここが一番踏みやすい経路である。
            if raw_deletion:
                reason += " 消したいものが保護対象でないなら、" + USE_TRASH
            return "deny", reason

    if raw_deletion:
        return "deny", (
            "**削除はこの repo の削除コマンドだけで行う**（保護対象の判定を通すため）。"
            "rm・Remove-Item・shutil.rmtree などは、ごみ箱送りかどうかによらず使わない。"
        ) + USE_TRASH

    if git_destructive:
        return "deny", (
            "コミットしていない変更を消す git のコマンド（clean・reset --hard・stash drop 等）は使わない。"
            "**消えたら git でも戻せない**（tools/claude/guard_delete.py）。"
            "要るなら開発者に相談する。ファイルを消したいだけなら " + USE_TRASH
        )

    if not mentions_recoverable:
        return None, None  # 保護対象に触れない上書きは、このフックの仕事ではない

    if CHAINED.search(command) or not RECOVERABLE_ONLY.match(command):
        # allow は権限判定そのものを飛ばすので、**戻せる道具を 1 本呼ぶだけ**の形にしか出さない。
        return None, None

    return "allow", (
        "戻せる道具なので通す（ごみ箱送り、または稼働 DB の退避・復元。"
        "どちらも前の中身を残す。tools/claude/guard_delete.py）。"
    )


T = f"pwsh -NoProfile -File {TRASH_SCRIPT}"          # 検体を短く書くための別名
S = "pwsh -NoProfile -File tools/clb/db_snapshot.ps1"

# 期待する判定。**この表がこのフックの仕様である。**
# 守る道具にテストが無いと、書き換えたときに「止めているつもりで素通り」になる。
SELFTEST = [
    # --- 削除の語彙。**1 語ずつ検体を置く**（消えても誰も気づかない語を作らない）
    ("rm work/x", "deny"),
    ("rmdir work", "deny"),
    ("del work\\x", "deny"),
    ("erase work\\x", "deny"),
    ("unlink work/x", "deny"),
    ("shred -u work/x", "deny"),
    ("truncate -s 0 work/x", "deny"),
    ("Remove-Item work/x", "deny"),
    ("Get-ChildItem work | ri", "deny"),
    ("rd /s /q work", "deny"),
    ("Clear-Content work/x", "deny"),
    ("Clear-Item work/x", "deny"),
    ("find . -name '*.tmp' -exec rm {} ;", "deny"),
    ("[System.IO.Directory]::Delete('work', $true)", "deny"),
    ("pwsh -c \"(Get-Item work).Delete()\"", "deny"),
    ("python -c \"import shutil; shutil.rmtree('work')\"", "deny"),
    ("python -c \"import os; os.remove('work/x')\"", "deny"),
    ("python -c \"from pathlib import Path; Path('x').unlink()\"", "deny"),
    ("find . -name '*.tmp' -delete", "deny"),
    # コマンド名の前が / \ " でも当てる
    ("/bin/rm -rf work", "deny"),
    ('"rm" -rf work', "deny"),
    ("\\rm -rf work", "deny"),
    # スクラッチパッド配下も例外にしない（**絶対パスは検体に書かない**。CLAUDE.md §5）
    ("rm -rf work/scratchpad/clone", "deny"),
    ("rm -rf work\\scratchpad\\rev8", "deny"),
    # --- git 自身の破壊コマンド
    ("git clean -xdf", "deny"),
    ("git reset --hard HEAD~1", "deny"),
    ("git stash drop", "deny"),
    ("git stash clear", "deny"),
    ("git checkout -- docs/README.md", "deny"),
    # --- 上書き。**保護対象の名前が出たときだけ**拒む
    ("sed -i 's/a/b/' LocalData/db/x.db", "deny"),
    ("Copy-Item -Force x LocalData/db/x.db", "deny"),
    ("python -c \"open('LocalData/db/x.db','w')\"", "deny"),
    ("Invoke-WebRequest http://example.com -OutFile Designer/LocalEnvironment.md", "deny"),
    ("echo x > .claude/settings.local.json", "deny"),
    ("New-Item -Force Designer/Design/designer.settings.Development.json", "deny"),
    # 保護対象に触れない上書きは、このフックの仕事ではない
    ("sed -i 's/a/b/' docs/README.md", None),
    ("Copy-Item -Force a b", None),
    ("echo x > work/out.txt", None),
    ("cp a b", None),
    # **リダイレクトに見えるだけの字は上書きではない**（2026-09-08。保護対象の名前が出ていても拒まない。
    # 実測でこの 4 つの形が拒まれ、`ls` や `cat` すら通らなかった）
    ("ls LocalData/ 2>/dev/null", None),
    ("cat LocalData/README.md 2>&1", None),
    ("python -c \"print('->', 'LocalData')\"", None),
    ("pwsh -c \"Get-ChildItem LocalData 2>$null\"", None),
    ("python -c \"assert n >= 1\" # LocalData", None),
    ("python -c \"assert len(sys.argv) > 1\" # LocalData", None),
    # **本物のリダイレクトは拾う**（追い書きも、標準エラーの振り向け先も上書きである）
    ("echo x > LocalData/db/x.db", "deny"),
    ("echo x >> LocalData/db/x.db", "deny"),
    ("dotnet run 2> LocalData/db/x.db", "deny"),
    ("echo x > LocalData/db/2026.log", "deny"),   # 数で始まる名前は「数だけの右辺」ではない
    # --- ごみ箱送りは通す
    (f"{T} BusinessApp/foo.cs", "allow"),
    (f"{T} -DryRun docs/x.md", "allow"),
    (f"{T} .gitignore", "allow"),
    (f"{T} LocalDataX/foo", "allow"),
    (f"pwsh -NoProfile -File ./{TRASH_SCRIPT} work/x", "allow"),
    # --- **allow は権限判定を飛ばすので、trash を 1 本呼ぶだけの形にしか出さない**
    (f"{T} a && rm -rf b", "deny"),          # 連結の先が削除なら拒む
    (f"{T} a && curl example.com", None),    # 連結は allow にせず通常の判定へ降ろす
    (f"{T} a\ngit push origin main", None),  # **改行も連結である**
    (f"cd work\n{T} a", None),
    (f"{T} a | tee log.txt", None),
    (f"{T} $(cat list.txt)", None),
    # 別の場所の trash.ps1 を装う形は allow にしない
    ("pwsh -NoProfile -File work/scratch/trash.ps1 anything", None),
    ('pwsh -NoProfile -Command "Invoke-WebRequest http://x -OutFile y" # trash.ps1', None),
    # --- 稼働 DB の退避・復元も戻せる道具である（消さず、上書きの前に必ず退避する）
    (f"{S} -Save", "allow"),
    (f"{S} -Save -Name before-migration-0023", "allow"),
    (f"{S} -Restore -Name before-migration-0023", "allow"),
    (f"{S} -List", "allow"),
    (f"{S} -Save && rm -rf b", "deny"),   # 連結の先が削除なら拒む
    (f"{S} -List | tee log.txt", None),   # 連結は allow にせず通常の判定へ降ろす
    ("pwsh -NoProfile -File work/scratch/db_snapshot.ps1 -Restore -Name x", None),
    # --- 削除でも上書きでもないものには触れない
    ("cat LocalData/README.md", None),
    ("cat tools/claude/trash.ps1", None),
    ("pwsh -NoProfile -File tools/clb/migrate.ps1 -Verify", None),
    ("git status --short", None),
    ("git stash list", None),
    ("dotnet build", None),
]

# `Write` の当たり先の期待。**正典に載っているものは、直すなら Edit を使う。**
WRITE_SELFTEST = [
    ("LocalData/db/x.db", "deny"),
    ("LocalData/designs/App.zip", "deny"),
    (".git/config", "deny"),
    (".claude/settings.local.json", "deny"),
    ("Designer/.claude/settings.local.json", "deny"),
    ("Designer/LocalEnvironment.md", "deny"),
    ("BusinessApp/BusinessApp.Server/appsettings.Development.json", "deny"),
    ("Designer/Design/designer.settings.Development.json", "deny"),
    # 名前が前方一致するだけのものは止めない（区切りまで見ているか）
    ("LocalDataX/foo.txt", None),
    (".gitignore", None),
    ("BusinessApp/BusinessApp.Server/appsettings.json", None),
    # 追跡ファイルは止めない（git で戻せる）
    ("docs/README.md", None),
    ("tools/claude/trash.ps1", None),
]


def _check_canon(failed: int) -> int:
    """正典の各行が、**ごみ箱送りの形でも・`Write` でも**止まるか。

    生の削除（`rm ...`）で試すと当たり先によらず deny になるので、
    **保護判定を殺しても緑になる**。ここは trash と Write の形で投げなければ意味がない。

    削除側で突き合わせるのは `why` そのものではなく**末尾の名前**である。
    このフックはコマンド文字列に末尾の名前を探すので、名前が同じ行は同じ理由文を返す
    （**拒む結論は同じで、正確な行はパスで見る側が判定する**）。
    """
    for entry in load_entries():
        name = basename_of(entry["path"])
        decision, reason = decide(f"{T} {entry['path']}")
        if decision != "deny" or name not in (reason or ""):
            failed += 1
            print(f"NG  正典に載っているのに保護されない（削除）: {entry['path']}（{decision}）")

        # **Write は当たり先が 1 つに決まる**ので、行ごとの理由文まで突き合わせられる。
        decision, reason = decide_write(entry["path"], str(REPO_ROOT))
        if decision != "deny" or entry["why"] not in (reason or ""):
            failed += 1
            print(f"NG  正典に載っているのに保護されない（Write）: {entry['path']}（{decision}）")

        # **worktree から本体を指す形。** 根が 1 つだと素通りする（`trash.ps1` が先に塞いだ穴）。
        # `roots` を渡して純粋に判定させる——実際の worktree を作らずに検体を置けるようにしてある。
        worktree = REPO_ROOT / ".claude" / "worktrees" / "probe"
        relative = os.path.join("..", "..", "..", entry["path"])
        decision, reason = decide_write(relative, str(worktree), roots=[worktree, REPO_ROOT])
        if decision != "deny" or entry["why"] not in (reason or ""):
            failed += 1
            print(f"NG  worktree から本体を指す Write が止まらない: {entry['path']}（{decision}）")
    return failed


def _check_wiring(failed: int) -> int:
    """**関門が settings.json に配線されているか。**

    フックの登録や deny を消しても検査が緑のままなら、関門は配線ごと外せてしまう。

    **保証していないもの**: フックが**本番の形で**起動すること（`shell: "bash"` から
    `python "$CLAUDE_PROJECT_DIR/..."` が解決されるか。`_check_entrypoint` は
    `sys.executable` と絶対パスで起こしており、本番とは別物である）、`defaultMode` の設定。
    """
    try:
        settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
    except Exception as exc:
        print(f"NG  {SETTINGS} を読めない: {exc}")
        return failed + 1

    pre = settings.get("hooks", {}).get("PreToolUse", [])

    # **道具ごとに配線を表明する。** まとめて「どこかに guard_delete.py がある」で見ると、
    # **削除側（`Bash|PowerShell`）の枝を丸ごと外しても緑になる**（自己レビューで実証。2026-09-08）。
    for tool in ("Write", "Bash", "PowerShell"):
        wired = False
        for entry in pre:
            if tool not in str(entry.get("matcher", "")):
                continue
            for hook in entry.get("hooks", []):
                if hook.get("type") == "command" and "guard_delete.py" in str(hook.get("command", "")):
                    wired = True
        if not wired:
            failed += 1
            print(f"NG  settings.json の PreToolUse に、{tool} を guard_delete.py へ回す配線が無い")

    permissions = settings.get("permissions", {})
    deny = set(permissions.get("deny", []))
    ask = set(permissions.get("ask", []))

    # **削除の控えを、語彙の正典（`DELETION_WORDS`）と両方向で結ぶ。**
    # 以前はここが代表の 3 語しか見ておらず、**正典から語を消しても・deny から規則を消しても
    # 鳴らなかった**（2026-09-08 に塞いだ）。**どちらの語形（Bash / PowerShell）で控えるかは問わない**
    # ——`unlink` は Bash にしか、`Clear-Item` は PowerShell にしか無い語だからである。
    single_word_rules = {}
    for rule in deny:
        matched = re.fullmatch(r"(Bash|PowerShell)\((\S+):\*\)", str(rule))
        if matched:
            single_word_rules.setdefault(matched.group(2).lower(), []).append(str(rule))

    for word in DELETION_WORDS:
        if word.lower() not in single_word_rules:
            failed += 1
            print(f"NG  settings.json の deny に {word} の控えが無い（フックが落ちたときの控え）")

    known_words = {word.lower() for word in DELETION_WORDS}
    for word, rules in sorted(single_word_rules.items()):
        if word not in known_words:
            failed += 1
            print(f"NG  settings.json の deny に、削除の語彙に無い 1 語の規則がある: {'・'.join(rules)}"
                  "（guard_delete.py の DELETION_WORDS と揃える）")

    # git 自身の破壊コマンドは 1 語ではないので、名指しで見る（`GIT_DESTRUCTIVE` の代表）。
    for required in ("Bash(git clean:*)", "PowerShell(git clean:*)"):
        if required not in deny:
            failed += 1
            print(f"NG  settings.json の deny に {required} が無い（フックが落ちたときの控え）")

    # **控えは生成規則で突き合わせる。** 部分文字列で見ると `Edit(LocalDataX/**)` でも緑になる。
    # **`Write` の行は控えにならない**——権限判定はファイルを書く道具を `Edit(パス)` の形だけで見て、
    # その 1 行が `Write` にも当たる。だから上書きを拒むのはこのフックだけで、
    # フックが落ちたときに残るのは、この `ask` の行による確認である。
    # **形まで見る。** フォルダに `Edit(<path>)`、ファイルに `Edit(<path>/**)` は**何も覆わない**のに、
    # どちらかがあればよい形で見ると緑になる（同じく自己レビューで実証。2026-09-08）。
    for entry in load_entries():
        path = entry["path"]
        required = f"Edit({path}/**)" if entry["kind"] == "dir" else f"Edit({path})"
        if required not in ask:
            failed += 1
            print(f"NG  settings.json の ask に {required} が無い（{entry['kind']} に要る形）")

    # **当たらない形の規則を置かない。** ファイルの権限判定は `Edit(パス)` と `Read(パス)` しか見ず、
    # `Write(パス)` などは受け付けられたうえで参照されない（起動時に警告が出るだけで、
    # 検査は緑のままだった。2026-09-07 に実際に置いてしまった。経緯は ADR-0044）。
    for bucket, rules in (("allow", permissions.get("allow", [])),
                          ("deny", deny), ("ask", ask)):
        for rule in rules:
            if any(str(rule).startswith(f"{tool}(")
                   for tool in ("Write", "NotebookEdit", "MultiEdit", "Glob")):
                failed += 1
                print(f"NG  settings.json の {bucket} に、権限判定が参照しない形の規則がある: {rule}"
                      "（ファイルの規則は Edit(パス) / Read(パス) で書く）")
    return failed


def _run_hook(payload, argv=None, cwd=None, env=None):
    """**本体（main）を実際に動かす。** 判定関数だけを検査すると、入口を殺しても緑になる。

    `argv` を渡すと、その並びで起こす（**本番と同じ形**——シェル越しの起動を試すため）。
    `cwd` は**このプロセスの作業ディレクトリ**であって、payload の `cwd` とは別物である。
    """
    proc = subprocess.run(
        argv or [sys.executable, str(Path(__file__).resolve())],
        input=json.dumps(payload, ensure_ascii=False),
        capture_output=True, text=True, encoding="utf-8",
        cwd=cwd, env=env,
    )
    out = (proc.stdout or "").strip()
    if not out:
        return None
    return json.loads(out)["hookSpecificOutput"]["permissionDecision"]


def _check_entrypoint(failed: int) -> int:
    protected = str(REPO_ROOT / "LocalData" / "db" / "x.db")
    cases = [
        ({"tool_name": "Write", "cwd": str(REPO_ROOT),
          "tool_input": {"file_path": protected}}, "deny"),
        ({"tool_name": "Write", "cwd": None,
          "tool_input": {"file_path": protected}}, "deny"),
        # **cwd が null で相対パス**——`or ""` を落とすと "None" という場所に着地して素通りする
        ({"tool_name": "Write", "cwd": None,
          "tool_input": {"file_path": "LocalData/db/probe.db"}}, "deny"),
        ({"tool_name": "Write", "cwd": str(REPO_ROOT),
          "tool_input": {"file_path": str(REPO_ROOT / "docs" / "README.md")}}, None),
        ({"tool_name": "Bash", "cwd": str(REPO_ROOT),
          "tool_input": {"command": "rm -rf work"}}, "deny"),
        ({"tool_name": "Bash", "cwd": str(REPO_ROOT),
          "tool_input": {"command": f"{T} work/x"}}, "allow"),
        # **Write の payload は command を持たない**——入口の振り分けを消すと、ここが None に落ちる
        ({"tool_name": "Read", "cwd": str(REPO_ROOT),
          "tool_input": {"file_path": protected}}, None),
    ]
    for payload, expected in cases:
        actual = _run_hook(payload)
        if actual != expected:
            failed += 1
            print(f"NG  入口: 期待 {expected} / 実際 {actual}: {payload['tool_name']} "
                  f"{payload['tool_input']}")

    # **プロセスの `cwd` と交ざっていないか。** 既定の起点は「このファイルの repo 根」であって
    # 呼ばれた場所ではない。**repo の中から起こしている限り、両者は同じ値なので区別できない**
    # ——repo の外から起こして初めて表明できる（自己レビューで判明。2026-09-08）。
    outside = tempfile.gettempdir()
    actual = _run_hook({"tool_name": "Write", "cwd": None,
                        "tool_input": {"file_path": "LocalData/db/probe.db"}}, cwd=outside)
    if actual != "deny":
        failed += 1
        print(f"NG  入口: repo の外から起こすと、相対パスの保護が外れる（実際 {actual}）")
    return failed


def _check_production_entrypoint(failed: int) -> int:
    """**本番と同じ形でフックが起きるか。**

    `settings.json` が書いている `command` と `shell` をそのまま使って起こす。
    `_check_entrypoint` は `sys.executable` と絶対パスで呼ぶので、**`python` が PATH から
    消えても、`$CLAUDE_PROJECT_DIR` が解決されなくても緑のまま**である
    （自己レビューで判明。2026-09-08）。ここが赤いなら、**上書きの拒否は「確認」に落ち、
    削除の拒否は `deny` に並べた語だけになる。**
    """
    try:
        settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
    except Exception as exc:
        print(f"NG  {SETTINGS} を読めない: {exc}")
        return failed + 1

    wirings = set()
    for entry in settings.get("hooks", {}).get("PreToolUse", []):
        for hook in entry.get("hooks", []):
            command = str(hook.get("command", ""))
            if "guard_delete.py" in command:
                wirings.add((command, str(hook.get("shell") or "bash")))
    if not wirings:
        print("NG  settings.json に guard_delete.py を呼ぶ PreToolUse フックが無い")
        return failed + 1

    # フックは `$CLAUDE_PROJECT_DIR` を Claude Code から受け取る。ここでは自分で与える。
    env = dict(os.environ, CLAUDE_PROJECT_DIR=str(REPO_ROOT))
    payload = {"tool_name": "Write", "cwd": str(REPO_ROOT),
               "tool_input": {"file_path": "LocalData/db/probe.db"}}
    for command, shell in sorted(wirings):
        try:
            actual = _run_hook(payload, argv=[shell, "-c", command], env=env)
        except Exception as exc:
            failed += 1
            print(f"NG  本番の形でフックを起こせない（shell={shell}）: {command}（{exc}）")
            continue
        if actual != "deny":
            failed += 1
            print(f"NG  本番の形で起こすと拒まない（shell={shell}・期待 deny / 実際 {actual}）: {command}")
    return failed


def selftest() -> int:
    global CANON  # 下で正典の差し替えを試すため（関数の先頭でしか宣言できない）
    failed = 0
    for command, expected in SELFTEST:
        actual, _ = decide(command)
        if actual != expected:
            failed += 1
            print(f"NG  期待 {expected} / 実際 {actual}: {command!r}")

    # **語彙の正典に、検体の無い語を作らない。** 語を足したときに検体を忘れると、
    # その語だけ「拒んでいるつもり」で書き換えられる（表の冒頭が課している約束を機械で守る）。
    for word in DELETION_WORDS:
        probe = re.compile(_BEFORE + re.escape(word) + _AFTER, re.IGNORECASE)
        if not any(expected == "deny" and probe.search(command) for command, expected in SELFTEST):
            failed += 1
            print(f"NG  削除の語 {word} に、deny を期待する検体が SELFTEST に無い")

    # **理由文も表明する。** ADR-0044 は「拒むときに次の一手を示す」を関門の中核に置いている。
    _, reason = decide("rm work/x")
    if TRASH_SCRIPT not in (reason or ""):
        failed += 1
        print(f"NG  削除を拒む理由文に {TRASH_SCRIPT} が出ていない")

    # `Write` の当たり先。**相対・Windows 形の絶対・Git Bash 形の絶対**のどれでも同じ判定になるか。
    # **形が違うだけで判定が外れると、保護は無いのと同じ**である。
    for relative, expected in WRITE_SELFTEST:
        windows_absolute = str(REPO_ROOT / relative)
        forms = [relative, windows_absolute, windows_absolute + os.sep]
        if os.name == "nt":
            forms.append("/" + windows_absolute[0].lower() + windows_absolute[2:].replace("\\", "/"))
        for target in forms:
            actual, _ = decide_write(target, str(REPO_ROOT))
            if actual != expected:
                failed += 1
                print(f"NG  Write: 期待 {expected} / 実際 {actual}: {target!r}")

    # **`cwd` を実際に使っているか。** 常に repo 根で解決していると、下 2 件が外れる。
    for cwd, relative, expected in [
        (str(REPO_ROOT / "LocalData" / "db"), "x.db", "deny"),
        (str(REPO_ROOT / "docs"), "README.md", None),
        ("", "LocalData/db/x.db", "deny"),  # cwd が空なら repo 根で解決する
    ]:
        actual, _ = decide_write(relative, cwd)
        if actual != expected:
            failed += 1
            print(f"NG  cwd: 期待 {expected} / 実際 {actual}: cwd={cwd!r} path={relative!r}")

    # **根の足し方。** 本体のリポジトリで別の根を足すと、判定が本体の外へ広がる。
    if (REPO_ROOT / ".git").is_dir() and main_repo_root(REPO_ROOT) is not None:
        failed += 1
        print("NG  worktree ではないのに、別の根を足している")

    failed = _check_canon(failed)
    failed = _check_wiring(failed)
    failed = _check_entrypoint(failed)
    failed = _check_production_entrypoint(failed)

    # 正典が読めないときに素通りしないか（fail-open にしていないか）。
    # **この一手は表では書けない**——表は正典が読める前提の判定しか並べられないため。
    readable, CANON = CANON, CANON.with_name("protected_paths.json.存在しない")
    try:
        if decide("rm -rf work/tmp")[0] != "deny":
            failed += 1
            print("NG  正典を読めないのに削除を通した")
        if decide_write("docs/README.md", str(REPO_ROOT))[0] != "deny":
            failed += 1
            print("NG  正典を読めないのに上書きを通した")
    finally:
        CANON = readable

    print("guard_delete: すべて期待どおり" if failed == 0 else f"guard_delete: {failed} 件が期待と違う")
    return 1 if failed else 0


def main() -> None:
    if "--selftest" in sys.argv:
        sys.exit(selftest())

    try:
        payload = json.load(sys.stdin)
    except Exception:
        return  # 読めない入力は判定しない（フック自身が作業を止めない）

    tool_input = payload.get("tool_input", {})
    if str(payload.get("tool_name", "")) == "Write":
        # `cwd` は無い・null のことがある。`or ""` にしないと "None" という名前の場所に着地する。
        decision, reason = decide_write(str(tool_input.get("file_path") or ""),
                                        str(payload.get("cwd") or ""))
    else:
        decision, reason = decide(str(tool_input.get("command") or ""))
    if decision is None:
        return

    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": decision,
            "permissionDecisionReason": reason,
        }
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
