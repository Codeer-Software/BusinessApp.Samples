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
2. **この repo の「戻せる道具」を 1 本だけ呼ぶコマンドは通す**
   （`RECOVERABLE_SCRIPTS`。ごみ箱送りと、稼働 DB の退避・復元。ADR-0046）。
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
**`allow` は確認のプロンプトを飛ばす。** そのあと `settings.json` の `deny`・`ask` が
**まだ評価されるのかは分かっていない。**

- **見たもの**: 公式ドキュメントの「Hooks reference」（`code.claude.com/docs/en/hooks`。2026-09-08）。
  `permissionDecision: "deny"` と「無言は承認ではない」は書かれているが、
  **`"allow"` を返したときに権限規則がどうなるかは書かれていない。**
- **見ていないもの**: 以前ここが根拠にしていた「Configure permissions」のページ。
  **その記述を読み直していないので、「評価される」が誤りだとまでは言えない**——
  **どちらとも確かめられていない**、が正確なところである。

**だから危ないほう——`allow` を返した時点で他の守りは何も残らない——を前提に設計する。**

返すのは、**コマンド全体が `RECOVERABLE_SCRIPTS` の 1 本を呼ぶだけ**のときに限る。
判定は 3 つを重ねる。**`RECOVERABLE_ONLY` は先頭しか見ない**ので、後ろに何か付いている印を
別に数える必要がある。

- `RECOVERABLE_ONLY`——先頭がその道具の呼び出しであること
- `CHAINED`——連結（`;` `&` `|` 改行 `` ` `` `$(`）と**リダイレクト（`<` `>`）**が 1 つも無いこと
- `OVERWRITE`——上書きの語が 1 つも無いこと（`-OutFile` のような引数の形を落とすため）

**実測したのは「このフックが `allow` を返した」までである**（2026-09-08 の自己レビュー）——
`pwsh … db_snapshot.ps1 -Save -Name x > tools/claude/guard_delete.py` が `allow`、
`| tee log.txt` は落ちるのに `> out.txt` は通る、という非対称があった。
**そこから先どこまで守りが外れるかは、上の未確認に掛かっている。**
`allow` が権限規則を飛ばすなら関門そのものへ書き込めるし、飛ばさないなら
`Bash(pwsh:*)` の白紙の許可が同じ結果を出す。**どちらでも塞ぐべきなので塞いだ。**

限界（承知のうえで残す）
------------------------
- **コマンドの判定はシェルを解釈しない。** 変数に入れてから消す・パスを組み立てる形はすり抜ける。
  **「うっかり」を止める装置であって、悪意を止める装置ではない。**
- **引用の中も区別しないので過剰検出する**（`git grep "Remove-Item"` のような検索も拒む）。
  **そちらへ倒してある**——見逃した削除は戻せないが、過剰な拒否は語の表記を変えれば済む。
- **閉じた上書きの経路は限られる。** `Write` ツールと、**`OVERWRITE_WORDS` / `OVERWRITE_FORMS` に
  載っている語が出て、かつ保護対象の名前も出る**シェルのコマンドだけである。
  **語彙に無い書き方**（`Set-ItemProperty`・エディタ・自作スクリプト経由など）と、
  **名前を出さずに上書きする形**（変数・相対パスの組み立て）は通る。
  **語彙は「よく使う形」であって網羅ではない**——踏んだら足す。
- **上書きは `settings.json` の `deny` では拒めない**（理由と出典は ADR-0044）。
  **このフックが起動しなければ、上書きは拒否ではなく確認（`ask`）に落ちる。**
  `ask` が覆うのは `Edit`・`Write`・`NotebookEdit` で、**`MultiEdit` はどちらも覆わない**
  （このフックの `matcher` にも無い。いまのセッションには配られていない道具である）。
- **パスの突き合わせは `realpath` までで、`\\?\` 前置きは追わない。**
- **`> 12345` のような数字だけの名前へのリダイレクトは見逃す**（`REDIRECT` が
  「数だけの右辺」を比較演算子として外すため。数字だけのファイル名は実在しうるが、
  比較演算子の過剰検出を取るほうを選んだ）。
- **worktree から本体を指す `Write` の守りは、`main_repo_root` が git を正しく読めることに乗っている。**
  自己検査は「その答えを `decide_write` が使うか」までを見る（実際の worktree は作らない）。

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

def _word_regex(words):
    """語の並びから「コマンド名として現れたら当てる」正規表現を作る。"""
    return re.compile(_BEFORE + "(" + "|".join(words) + ")" + _AFTER, re.IGNORECASE)


# 完全削除。Bash と PowerShell の両方を見る。
DELETION = _word_regex(DELETION_WORDS)

# **語ではない削除の形。** 名前を付けてあるのは、**1 つずつ検体で縛るため**である
# （`selftest` が名前ごとに「この形を殺すと赤くなるか」を見る）。
DELETION_FORMS = (
    # `find ... -delete` と `find ... -exec rm` は上の語形に当たらない。
    ("find -delete", r"\bfind\b.*(-delete\b|-exec\s+rm\b)"),
    # .NET / COM を直に呼ぶ形（`[IO.File]::Delete(...)`・`(Get-Item x).Delete()`）。
    (".NET の削除", r"(\[[\w.]*IO\.\w+\]::Delete|\.Delete\(\s*\))"),
    # Python から消す形（`Bash(python:*)` が許可されているので、ここで拾う）。
    # **`send2trash` も入れてある**——ごみ箱送りではあるが、**保護対象の判定を通らない**。
    # 止めたいのは「完全削除」ではなく「`trash.ps1` を経由しない削除」である。
    ("shutil.rmtree", r"shutil\.rmtree"),
    ("os の削除", r"os\.(remove|unlink|rmdir|removedirs)"),
    ("Path.unlink", r"\.unlink\("),
    ("send2trash", r"send2trash"),
)


def _forms_regex(forms):
    return re.compile("|".join(f"(?:{pattern})" for _, pattern in forms), re.IGNORECASE)


OTHER_DELETION = _forms_regex(DELETION_FORMS)

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

# **中身を置き換える語。** これ単体では拒まない——**保護対象の名前が出たときだけ**拒む
# （`Write` ツール以外にも上書きの道はいくらでもあり、全部を止めると作業が成り立たない）。
# **`ren`・`rename`・`Rename-Item`・`Tee-Object`・`Add-Content` の 5 語は 2026-09-08 に足した**——
# 自己レビューが「保護対象の名前を出しても通る形」として実測で挙げたものである。
OVERWRITE_WORDS = (
    "cp", "copy", "Copy-Item", "mv", "move", "Move-Item", "ren", "rename", "Rename-Item",
    "tee", "Tee-Object", "Set-Content", "Add-Content", "Out-File", "New-Item",
    "Expand-Archive", "unzip", "dd",
)

# 語ではない上書きの形。**削除側と同じく、名前ごとに検体で縛る。**
OVERWRITE_FORMS = (
    ("sed -i", r"\bsed\s+-i"),
    ("-OutFile", r"-OutFile\b"),
    ("curl -o", r"\bcurl\b[^;&|]*\s-[oO]\b"),
    ("リダイレクト", REDIRECT),
    ("open の書き込み", r"\bopen\([^)]*['\"][wa]"),
    (".NET の書き込み", r"\[[\w.]*IO\.\w+\]::(Write|Append|Create)\w*"),
)

OVERWRITE = re.compile(
    _word_regex(OVERWRITE_WORDS).pattern + "|" + _forms_regex(OVERWRITE_FORMS).pattern,
    re.IGNORECASE,
)

# **戻せる道具。** ごみ箱送り（前の中身を残す）と、稼働 DB の退避・復元
# （消さず、上書きの前に必ず退避する）。**打ち方は docs/30_作業のルール.md §10、
# ここに足してよい基準は ADR-0046 の帰結が持つ。**
# **第 2 要素は「当たり先をパスで受け取るか」。** `trash.ps1` は受け取るので、
# 同じコマンドに保護対象の名前が出たら拒む必要がある。`db_snapshot.ps1` は
# **パスを受け取る引数を持たない**——名前でしか当たり先を指せない道具に保護対象の名前の
# 検査をかけると、**同じコマンドで LocalData を読んだだけで拒まれる**（2026-09-08 に実測）。
# **この第 2 要素が正典である**（散文で「どの引数を取るか」を写さない）。
RECOVERABLE_SCRIPTS = (
    (TRASH_SCRIPT, True),
    ("tools/clb/db_snapshot.ps1", False),
)

# **削除とは関係のない、1 語の `deny`。** ここに無い 1 語の規則は自己検査が咎める
# （語彙から語を消したのに控えだけ残る、を捕まえるため）。**足すのは、削除でないと言い切れるものだけ。**
NON_DELETION_DENY = frozenset()


def _mention(scripts) -> re.Pattern:
    """`pwsh … <script>` への言及をゆるく拾う正規表現。"""
    return re.compile(
        r"(^|[\s;&|(])(pwsh|powershell)\b[^;&|]*("
        + "|".join(re.escape(path.rsplit("/", 1)[-1]) for path in scripts)
        + r")",
        re.IGNORECASE,
    )


# 戻せる道具への言及（allow を出してよい形かを見るため）。
RECOVERABLE_MENTION = _mention([path for path, _ in RECOVERABLE_SCRIPTS])

# **当たり先をパスで受け取る道具**への言及（保護対象の名前の検査にかけるため）。
PATH_TAKING_MENTION = _mention([path for path, takes_paths in RECOVERABLE_SCRIPTS if takes_paths])

# **コマンド全体が、この repo の戻せる道具を 1 本呼ぶだけ**か（allow を出してよい形）。
RECOVERABLE_ONLY = re.compile(
    r"^\s*(pwsh|powershell)(\.exe)?\s+"
    r"((-|--)\w[\w-]*\s+)*"
    r"-File\s+[\"']?(\./)?("
    + "|".join(re.escape(path).replace("/", r"[\\/]") for path, _ in RECOVERABLE_SCRIPTS)
    + r")[\"']?(\s|$)",
    re.IGNORECASE,
)

# **コマンドが「その 1 本だけ」で終わっていない印。** ここに 1 つでも当たれば `allow` を出さない。
# 改行・バッククォート・`$(` に加えて、**リダイレクトの `<` `>` も数える**
# （2026-09-08 の自己レビューが実測。`| tee log.txt` は落ちるのに `> out.txt` は `allow` を取り、
# **関門そのものへ書き込めた**。プロセス置換 `<(…)` も同じ穴で、`allow` の上に任意のコマンドが乗った）。
CHAINED = re.compile(r"[;&|<>\n\r`]|\$\(")

MSYS_ABSOLUTE = re.compile(r"^/([A-Za-z])/(.*)$")


def short(path_value) -> str:
    """表示用に repo からの相対へ畳む。**畳めないものはそのまま返す。**

    自己検査の失敗出力は文書へ貼られる（docs/31 §1 が検証段に据えている）。
    絶対パスにはユーザー名が入るので、そのまま貼ると公開リポジトリへ混ざる（CLAUDE.md §5）。
    """
    text = str(path_value)
    try:
        return str(Path(text).resolve().relative_to(REPO_ROOT)).replace("\\", "/")
    except Exception:
        return text


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
    raw_deletion = bool(DELETION.search(command) or OTHER_DELETION.search(command))
    git_destructive = bool(GIT_DESTRUCTIVE.search(command))
    overwrite = bool(OVERWRITE.search(command))
    mentions_recoverable = bool(RECOVERABLE_MENTION.search(command))

    # **保護対象の名前を探すのは、当たり先をパスで受け取る形のときだけ**である
    # （どの道具がそうかは `RECOVERABLE_SCRIPTS` の第 2 要素が持つ）。
    # パスを取らない道具なら、同じコマンドに `LocalData` が出てもこの道具の当たり先ではない。
    names_a_target = (raw_deletion or git_destructive or overwrite
                      or bool(PATH_TAKING_MENTION.search(command)))
    if not (names_a_target or mentions_recoverable):
        return None, None

    try:
        protected = load_protected()
    except Exception as exc:  # 正典が読めないなら、読めないまま消させない
        return "deny", (
            f"守るものの正典（{CANON.name}）を読めなかった: {exc}。"
            "読めないまま削除・上書きは通さない（tools/claude/guard_delete.py）。"
        )

    for pattern, why in (protected if names_a_target else []):
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

    if CHAINED.search(command) or overwrite or not RECOVERABLE_ONLY.match(command):
        # **戻せる道具を 1 本呼ぶだけ**の形にしか出さない（3 つの条件は docstring が持つ）。
        # `RECOVERABLE_ONLY` は先頭しか見ないので、**後ろに何か付いている印**を
        # `CHAINED` と `OVERWRITE` の 2 つで数える。片方だけだと `-OutFile` のような
        # 引数の形が素通りする（2026-09-08 の自己レビュー）。
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
    # **ごみ箱送りでも、この repo の道具を通らない削除は拒む**（保護対象の判定を通らないため）
    ("uv run --with send2trash python drop.py work/x", "deny"),
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
    # --- 上書き。**保護対象の名前が出たときだけ**拒む。
    # **語ごとに 1 つずつ置く**——`_check_vocabulary` が「その語を消すと、この検体が当たらなくなる」
    # ことを機械で確かめる。**検体があるだけでは足りない**（他の語が同じ検体に当たっていると、
    # 消しても鳴らない。**測った時点の上書きの語彙 13 語のうち 11 語**がその状態だった。2026-09-08）
    ("cp x LocalData/db/x.db", "deny"),
    ("copy x LocalData/db/x.db", "deny"),
    ("Copy-Item -Force x LocalData/db/x.db", "deny"),
    ("mv x LocalData/db/x.db", "deny"),
    ("move x LocalData/db/x.db", "deny"),
    ("Move-Item x LocalData/db/x.db", "deny"),
    ("ren LocalData/db/x.db y.db", "deny"),
    ("rename LocalData/db/x.db y.db", "deny"),
    ("Rename-Item LocalData/db/x.db y.db", "deny"),
    ("echo x | tee LocalData/db/x.db", "deny"),
    ("echo x | Tee-Object LocalData/db/x.db", "deny"),
    ("Set-Content LocalData/db/x.db 'x'", "deny"),
    ("Add-Content LocalData/db/x.db 'x'", "deny"),
    ("'x' | Out-File LocalData/db/x.db", "deny"),
    ("New-Item -Force Designer/Design/designer.settings.Development.json", "deny"),
    ("Expand-Archive a.zip LocalData/designs", "deny"),
    ("unzip a.zip -d LocalData/designs", "deny"),
    ("dd if=/dev/zero of=LocalData/db/x.db", "deny"),
    ("sed -i 's/a/b/' LocalData/db/x.db", "deny"),
    ("Invoke-WebRequest http://example.com -OutFile Designer/LocalEnvironment.md", "deny"),
    ("curl -o LocalData/db/x.db http://example.com", "deny"),
    ("echo x > .claude/settings.local.json", "deny"),
    ("python -c \"open('LocalData/db/x.db','w')\"", "deny"),
    ("pwsh -c \"[IO.File]::WriteAllText('LocalData/db/x.db','')\"", "deny"),
    # 保護対象に触れない上書きは、このフックの仕事ではない
    ("sed -i 's/a/b/' docs/README.md", None),
    ("Copy-Item -Force a b", None),
    ("echo x > work/out.txt", None),
    ("cp a b", None),
    # **リダイレクトに見えるだけの字は上書きではない**（2026-09-08。保護対象の名前が出ていても拒まない。
    # **実測でこの回に 4 回踏み**、`ls` や `cat` すら通らなかった。下に置いた形はその変種を含む）
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
    # **リダイレクトも「1 本で終わっていない」印である**——ここを数え落として、
    # 関門そのものへ書き込む形が allow を取っていた（2026-09-08）
    (f"{T} work/x > log.txt", None),
    (f"{S} -List > out.txt", None),
    (f"{S} -Save -Name x > tools/claude/guard_delete.py", None),
    (f"{S} -List < in.txt", None),
    (f"{S} -List < <(python evil.py)", None),
    # **上書きの語が引数に紛れている形**（連結の字が 1 つも無いので CHAINED では落ちない）
    (f"{S} -List -OutFile out.txt", None),
    (f"{T} work/x -OutFile out.txt", None),
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
    # **パスを取らない道具に、保護対象の名前の検査をかけない**（`RECOVERABLE_SCRIPTS` の第 2 要素）
    (f"cat LocalData/README.md\n{S} -Save", None),
    (f"{S} -Save -Name LocalData", "allow"),
    # **パスを受け取る道具は、従来どおり保護対象を拒む**
    (f"{T} LocalData/db/x.db", "deny"),
    (f"cat docs/README.md\n{T} LocalData/db/x.db", "deny"),
    # 連結の先が削除なら、道具の別によらず拒む
    (f"{S} -Save && rm -rf LocalData/db", "deny"),
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


def _triggered(command, deletion, other_deletion, overwrite) -> bool:
    """その検体が、まだ「関門の仕事」に当たるか（deny へ進む条件を 1 つでも満たすか）。

    **`PATH_TAKING_MENTION` も数える**——`trash.ps1` に保護対象を渡す形は、
    削除の語でも上書きの語でもなく、この言及だけで deny になるためである。
    """
    return bool(deletion.search(command) or other_deletion.search(command)
                or GIT_DESTRUCTIVE.search(command) or overwrite.search(command)
                or PATH_TAKING_MENTION.search(command))


def _check_vocabulary(failed: int) -> int:
    """**語彙の 1 つずつに、それを消したら鳴る検体があるか。**

    **表に検体が「ある」だけでは足りない。** 別の語が同じ検体に当たっていれば、
    その語を消しても表は緑のままである——**測った時点の上書きの語彙 13 語のうち 11 語と、
    `send2trash` がその状態だった**（自己レビューが実測。2026-09-08。
    型は docs/qa/03 の「消しても鳴らない死んだ条件」）。

    そこで**プロセスの中で語を 1 つ抜いた正規表現を作り**、deny を期待する検体のうち
    どれかが「当たらなくなる」ことを見る。抜いても全部が当たったままなら、その語は死んでいる。
    """
    deny_cases = [command for command, expected in SELFTEST if expected == "deny"]

    def exercised(deletion=DELETION, other_deletion=OTHER_DELETION, overwrite=OVERWRITE) -> bool:
        return any(not _triggered(c, deletion, other_deletion, overwrite) for c in deny_cases)

    # 抜く前は、deny の検体がすべて当たっていること（当たらない検体があるなら、
    # 下の「抜いたら当たらなくなった」が語のせいだと言えない）。
    if exercised():
        failed += 1
        print("NG  deny を期待する検体に、削除・上書きのどれにも当たらないものがある")

    for word in DELETION_WORDS:
        rest = tuple(w for w in DELETION_WORDS if w != word)
        if not exercised(deletion=_word_regex(rest)):
            failed += 1
            print(f"NG  削除の語 {word} は、消しても検体が 1 つも変わらない（死んだ語）")

    for name, _ in DELETION_FORMS:
        rest = tuple(f for f in DELETION_FORMS if f[0] != name)
        if not exercised(other_deletion=_forms_regex(rest)):
            failed += 1
            print(f"NG  削除の形「{name}」は、消しても検体が 1 つも変わらない（死んだ条件）")

    def overwrite_regex(words, forms):
        return re.compile(_word_regex(words).pattern + "|" + _forms_regex(forms).pattern,
                          re.IGNORECASE)

    for word in OVERWRITE_WORDS:
        rest = tuple(w for w in OVERWRITE_WORDS if w != word)
        if not exercised(overwrite=overwrite_regex(rest, OVERWRITE_FORMS)):
            failed += 1
            print(f"NG  上書きの語 {word} は、消しても検体が 1 つも変わらない（死んだ語）")

    for name, _ in OVERWRITE_FORMS:
        rest = tuple(f for f in OVERWRITE_FORMS if f[0] != name)
        if not exercised(overwrite=overwrite_regex(OVERWRITE_WORDS, rest)):
            failed += 1
            print(f"NG  上書きの形「{name}」は、消しても検体が 1 つも変わらない（死んだ条件）")

    return failed


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

    **本番の形での起動は `_check_production_entrypoint` が見る**（`settings.json` の
    `command` と `shell` をそのまま使う）。**ここが保証していないのは `defaultMode` の設定である。**
    """
    try:
        settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
    except Exception as exc:
        print(f"NG  {short(SETTINGS)} を読めない: {exc}")
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

    # **削除と関係のない 1 語の deny は、ここに名前を書いて外す。**
    # 逆向きの検査は「語彙から語を消したのに控えが残っている」を捕まえるためのもので、
    # `settings.json` の deny を「削除語しか置けない場所」に固定するのが目的ではない。
    known_words = {word.lower() for word in DELETION_WORDS} | NON_DELETION_DENY
    for word, rules in sorted(single_word_rules.items()):
        if word not in known_words:
            failed += 1
            print(f"NG  settings.json の deny に、削除の語彙にも除外にも無い 1 語の規則がある: "
                  f"{'・'.join(rules)}（guard_delete.py の DELETION_WORDS か NON_DELETION_DENY に足す）")

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
    # **落ちたことを「判定しない」と読まない。** 終了コードと標準エラーを捨てると、
    # フックが毎回トレースバックで落ちていても None が返り、None を期待する検体が
    # 全壊のまま緑になる（自己レビューが実測。2026-09-08）。
    if proc.returncode != 0 or (proc.stderr or "").strip():
        raise RuntimeError(
            f"フックが落ちた（exit {proc.returncode}）: {(proc.stderr or '').strip()[:400]}")
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
                  f"{ {k: short(v) for k, v in payload['tool_input'].items()} }")

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
        print(f"NG  {short(SETTINGS)} を読めない: {exc}")
        return failed + 1

    # **matcher ごとに撃つ。** `command` と `shell` は同じでも、**通る道具が違う**——
    # まとめて集合にすると 1 本に潰れ、`Bash` 経路が本番の形で起きることを表明できない
    # （自己レビューが実測。2026-09-08）。
    wirings = []
    for entry in settings.get("hooks", {}).get("PreToolUse", []):
        matcher = str(entry.get("matcher", ""))
        for hook in entry.get("hooks", []):
            command = str(hook.get("command", ""))
            if "guard_delete.py" in command:
                wirings.append((matcher, command, str(hook.get("shell") or "bash")))
    if not wirings:
        print("NG  settings.json に guard_delete.py を呼ぶ PreToolUse フックが無い")
        return failed + 1

    # 道具ごとの検体。**その matcher が実際に受け取る形**で投げる。
    payloads = {
        "Write": {"tool_name": "Write", "cwd": str(REPO_ROOT),
                  "tool_input": {"file_path": "LocalData/db/probe.db"}},
        "Bash": {"tool_name": "Bash", "cwd": str(REPO_ROOT),
                 "tool_input": {"command": "rm -rf work"}},
        "PowerShell": {"tool_name": "PowerShell", "cwd": str(REPO_ROOT),
                       "tool_input": {"command": "Remove-Item work"}},
    }
    # フックは `$CLAUDE_PROJECT_DIR` を Claude Code から受け取る。ここでは自分で与える。
    env = dict(os.environ, CLAUDE_PROJECT_DIR=str(REPO_ROOT))
    for matcher, command, shell in wirings:
        for tool, payload in payloads.items():
            if tool not in matcher:
                continue
            try:
                actual = _run_hook(payload, argv=[shell, "-c", command], env=env)
            except Exception as exc:
                failed += 1
                print(f"NG  本番の形でフックを起こせない（matcher={matcher}・{tool}）: {exc}")
                continue
            if actual != "deny":
                failed += 1
                print(f"NG  本番の形で起こすと拒まない（matcher={matcher}・{tool}・"
                      f"期待 deny / 実際 {actual}）")
    return failed


def selftest() -> int:
    global CANON, main_repo_root  # 下で差し替えを試すため（関数の先頭でしか宣言できない）
    failed = 0
    for command, expected in SELFTEST:
        actual, _ = decide(command)
        if actual != expected:
            failed += 1
            print(f"NG  期待 {expected} / 実際 {actual}: {command!r}")

    failed = _check_vocabulary(failed)

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
                print(f"NG  Write: 期待 {expected} / 実際 {actual}: {short(target)!r}")

    # **`cwd` を実際に使っているか。** 常に repo 根で解決していると、下 2 件が外れる。
    for cwd, relative, expected in [
        (str(REPO_ROOT / "LocalData" / "db"), "x.db", "deny"),
        (str(REPO_ROOT / "docs"), "README.md", None),
        ("", "LocalData/db/x.db", "deny"),  # cwd が空なら repo 根で解決する
    ]:
        actual, _ = decide_write(relative, cwd)
        if actual != expected:
            failed += 1
            print(f"NG  cwd: 期待 {expected} / 実際 {actual}: cwd={short(cwd)!r} path={relative!r}")

    # **根の足し方。** 本体のリポジトリで別の根を足すと、判定が本体の外へ広がる。
    if (REPO_ROOT / ".git").is_dir() and main_repo_root(REPO_ROOT) is not None:
        failed += 1
        print("NG  worktree ではないのに、別の根を足している")

    # **`main_repo_root` の答えを `decide_write` が本当に使うか。**
    # `_check_canon` は `roots` を手で渡すので**自動解決を一度も通らず**、
    # `main_repo_root` を `None` に潰しても全緑だった（自己レビューが実測。2026-09-08）。
    # 実際の worktree は作らない——**偽の根を返させて、判定がそこまで広がることを見る**。
    genuine_main_repo_root = main_repo_root
    with tempfile.TemporaryDirectory() as elsewhere:
        outside = os.path.join(elsewhere, "LocalData", "db", "x.db")
        try:
            main_repo_root = lambda _root: Path(elsewhere)  # noqa: E731
            if decide_write(outside, str(REPO_ROOT))[0] != "deny":
                failed += 1
                print("NG  main_repo_root が返した根を decide_write が使っていない"
                      "（worktree から本体を指す Write が素通りする）")
            main_repo_root = lambda _root: None  # noqa: E731
            if decide_write(outside, str(REPO_ROOT))[0] is not None:
                failed += 1
                print("NG  main_repo_root が None を返したのに、repo の外まで拒んでいる")
        finally:
            main_repo_root = genuine_main_repo_root

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
