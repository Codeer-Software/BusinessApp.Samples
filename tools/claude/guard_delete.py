#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""guard_delete.py — Claude Code の PreToolUse フック。完全削除を拒み、ごみ箱送りへ寄せる。

開発者の指示（2026-09-07。逐語）:
  「rm は開発者への確認プロンプトが出て自律作業が止まってしまうため使わないでください、と
    繰り返し言ってきたが、学習情報において rm が無数に使われているためか、どうしても
    rm を使ってしまうようだ。しかし、無制限に rm を許可することはしたくない。
    そこで、rm の代わりに使える削除コマンドを作ろうと考えた」
  併せて「settings.json において、rm は ask ではなく deny としてください」。

  **ここから先は Claude の設計である**——約束では守れないものは機械に守らせる。
  完全削除をやめてごみ箱送りにすれば取り違えても戻せるので、
  **削除のたびに確認で止める必要が無くなる**（無確認で通してよい、という判断は
  上の「自律作業が止まる」という目的から導いたもので、逐語の指示ではない。ADR-0044）。

やること
--------
1. **完全削除にあたるコマンドは、当たり先によらず拒む。**
   代わりに `tools/claude/trash.ps1`（ごみ箱送り）を使えと理由文で案内する。
   **語彙の正典はこのファイルの `DELETION` ほかの正規表現である**（散文で列挙しない）。
2. **この repo の `trash.ps1` を 1 本だけ呼ぶコマンドは通す。**
3. **どちらであっても、保護対象に触れるものは拒む。**

守りたいもの（**Git で戻せないもの**）
--------------------------------------
追跡ファイルは**コミット済みであれば** git が復元手段になるので、消えても戻せる。
戻せないのは追跡外のものと、**コミットしていない変更**である
（後者を消す `git reset --hard` などをこのフックが拒むのは、そのためである）。
**一覧と載せる基準は `protected_paths.json` が持つ**——`trash.ps1` と同じ 1 ファイルを読む。

なぜ許可リストではなくフックか
------------------------------
Claude Code の Bash 権限は**コマンド文字列の前方一致**で判定する。
`Bash(rm -rf <スクラッチパッド>/*)` のような許可を書いても、`cd` を挟む・変数展開する・
`find -exec` を使う、といった書き方は同じ規則で捕まらない。
**パスで許可を絞るのは事故防止にはなっても、境界の保証にはならない。**
フックならコマンド文字列全体を見られるので、少なくとも「保護対象の名前に触れる削除」を
書き方によらず止められる。**序列と役割分担は ADR-0044 が持つ。**

`allow` を返すときの制約
------------------------
**`allow` は権限判定そのものを飛ばす。** `settings.json` の deny も効かなくなるので、
**コマンド全体がこの repo の `trash.ps1` を 1 本呼ぶだけ**のときに限る。
連結（`;` `&` `|` 改行 `` ` `` `$(`）が 1 つでもあれば `allow` にせず、通常の権限判定へ降りる。

限界（承知のうえで残す）
------------------------
判定はコマンド文字列への正規表現であり、シェルを解釈しない。
変数に入れてから消す・パスを分割して組み立てる、といった書き方はすり抜ける。
**これは「うっかり」を止める装置であって、悪意を止める装置ではない。**
**パスを解決したうえでの本当の境界は `trash.ps1` が見る**——こちらは実際の削除の直前に、
絶対パスへ直してから「保護対象の配下か・保護対象を内側に含むか」を判定する。

**引用の中も区別しないので、過剰検出する**（`git grep "Remove-Item"` のような検索も拒む）。
**そちらへ倒してある**——見逃した削除は戻せないが、過剰な拒否は語の表記を変えれば済む。
理由文にその直し方を書いてある。

標準入力: Claude Code のフック入力 JSON（tool_input.command を見る）。
標準出力: 拒否するとき deny、ごみ箱送りの削除のとき allow。それ以外は何も出さない
          （＝通常の権限判定に落ちる）。**そこは「開発者への確認」とは限らない**——
          `settings.json` の許可には `Bash(python:*)` のような白紙の行があり、
          取りこぼした削除はそのまま実行になる（`docs/README.md` の保留リスト）。
"""

import json
import re
import sys
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8")  # Windows の既定は CP932 で、理由文が化ける
except Exception:
    pass

CANON = Path(__file__).with_name("protected_paths.json")
SETTINGS = Path(__file__).resolve().parents[2] / ".claude" / "settings.json"

TRASH_SCRIPT = "tools/claude/trash.ps1"
TRASH_COMMAND = f"pwsh -NoProfile -File {TRASH_SCRIPT} <パス>"

# コマンド名の前に来てよい字。`/bin/rm`・`\rm`・`"rm"` のような書き方も当てる。
_BEFORE = r"(^|[\s;&|(/\\\"'])"
_AFTER = r"([\s;&|)\"']|$)"

# 完全削除。Bash と PowerShell の両方を見る。
DELETION = re.compile(
    _BEFORE
    + r"(rm|rmdir|del|erase|unlink|shred|truncate"
    r"|Remove-Item|ri|rd|Clear-Content|Clear-Item)"
    + _AFTER,
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

# ごみ箱送りの削除コマンドへの言及（保護対象の検査にかけるため、ゆるく拾う）。
TRASH_MENTION = re.compile(r"(^|[\s;&|(])(pwsh|powershell)\b[^;&|]*trash\.ps1", re.IGNORECASE)

# **コマンド全体がこの repo の trash.ps1 を 1 本呼ぶだけ**か（allow を出してよい形）。
TRASH_ONLY = re.compile(
    r"^\s*(pwsh|powershell)(\.exe)?\s+"
    r"((-|--)\w[\w-]*\s+)*"
    r"-File\s+[\"']?(\./)?tools[\\/]claude[\\/]trash\.ps1[\"']?(\s|$)",
    re.IGNORECASE,
)

# 連結して別のことをするコマンド。**改行とバッククォートと $( を含める**——
# 改行を数え落とすと、2 行目以降の任意コマンドが allow に乗る。
CHAINED = re.compile(r"[;&|\n\r`]|\$\(")


def load_protected():
    """[(コマンド文字列に探す正規表現, 理由), ...] を返す。読めなければ例外。"""
    canon = json.loads(CANON.read_text(encoding="utf-8"))
    entries = canon["protected"]
    if not entries:
        raise ValueError("protected が空である")

    patterns = []
    for entry in entries:
        if not entry.get("path") or not entry.get("why"):
            raise ValueError(f"path または why が無い行がある: {entry}")
        # コマンド文字列にはリポジトリからの相対でも絶対でも書かれうるので、
        # **末尾の名前**を探す。前後に名前が続くもの（LocalDataX・.gitignore）は外す。
        name = entry["path"].replace("\\", "/").rstrip("/").split("/")[-1]
        patterns.append((
            re.compile(r"(?<![A-Za-z0-9_.\-])" + re.escape(name) + r"(?![A-Za-z0-9_.\-])",
                       re.IGNORECASE),
            entry["why"],
        ))
    return patterns


USE_TRASH = (
    f"ごみ箱へ送る削除コマンドを使う: {TRASH_COMMAND}"
    "（ごみ箱からは戻せるので、開発者の確認を待たずに実行してよい。"
    "規則は docs/30_作業のルール.md §10）。"
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
    mentions_trash = bool(TRASH_MENTION.search(command))
    if not (raw_deletion or git_destructive or mentions_trash):
        return None, None

    try:
        protected = load_protected()
    except Exception as exc:  # 正典が読めないなら、読めないまま消させない
        return "deny", (
            f"守るものの正典（{CANON.name}）を読めなかった: {exc}。"
            "読めないまま削除は通さない（tools/claude/guard_delete.py）。"
        )

    for pattern, why in protected:
        if pattern.search(command):
            reason = (
                f"{why}。この削除は許可しない（tools/claude/guard_delete.py）。"
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

    if CHAINED.search(command) or not TRASH_ONLY.match(command):
        # allow は権限判定そのものを飛ばすので、**trash.ps1 を 1 本呼ぶだけ**の形にしか出さない。
        return None, None

    return "allow", "ごみ箱へ送る削除（完全削除しない。tools/claude/guard_delete.py）。"


T = f"pwsh -NoProfile -File {TRASH_SCRIPT}"  # 検体を短く書くための別名

# 期待する判定。**この表がこのフックの仕様である。**
# 守る道具にテストが無いと、書き換えたときに「止めているつもりで素通り」になる。
SELFTEST = [
    # --- 保護対象に触れる削除は、書き方によらず拒む
    ("rm -rf LocalData/db", "deny"),
    ("rm LocalData/designs/App.zip", "deny"),
    ("Remove-Item -Recurse LocalData", "deny"),
    ('rm -rf "LocalData"', "deny"),
    ("rm Designer/LocalEnvironment.md", "deny"),
    ("rm .claude/settings.local.json", "deny"),
    ("rm BusinessApp/BusinessApp.Server/appsettings.Development.json", "deny"),
    ("rm Designer/Design/designer.settings.Development.json", "deny"),
    ("rm -rf .git", "deny"),
    ("find . -name '*.tmp' -path './LocalData/*' -delete", "deny"),
    ("[System.IO.File]::Delete('LocalData/db/x.db')", "deny"),
    (f"{T} LocalData/db", "deny"),
    (f"{T} Designer/LocalEnvironment.md", "deny"),
    (f"{T} .claude/settings.local.json", "deny"),
    (f"{T} BusinessApp/BusinessApp.Server/appsettings.Development.json", "deny"),
    (f"{T} Designer/Design/designer.settings.Development.json", "deny"),
    (f"{T} .git/config", "deny"),
    # --- 完全削除の語彙。**1 語ずつ検体を置く**（消えても誰も気づかない語を作らない）
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
    # コマンド名の前が / \ " でも当てる
    ("/bin/rm -rf work", "deny"),
    ('"rm" -rf work', "deny"),
    ("\\rm -rf work", "deny"),
    # --- git 自身の破壊コマンド
    ("git clean -xdf", "deny"),
    ("git reset --hard HEAD~1", "deny"),
    ("git stash drop", "deny"),
    ("git stash clear", "deny"),
    ("git checkout -- docs/README.md", "deny"),
    # --- スクラッチパッド配下も例外にしない（**絶対パスは検体に書かない**。CLAUDE.md §5）
    ("rm -rf work/scratchpad/clone", "deny"),
    ("rm -rf work\\scratchpad\\rev8", "deny"),
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
    # --- 削除でないものには触れない
    ("cat LocalData/README.md", None),
    ("cat tools/claude/trash.ps1", None),
    ("pwsh -NoProfile -File tools/clb/migrate.ps1 -Verify", None),
    ("git status --short", None),
    ("git stash list", None),
    ("dotnet build", None),
]


def _check_canon(failed: int) -> int:
    """正典の各行が、**ごみ箱送りの形でも**止まるか。理由文まで突き合わせる。

    生の削除（`rm ...`）で試すと当たり先によらず deny になるので、
    **保護判定を殺しても緑になる**。ここは trash の形で投げなければ意味がない。

    突き合わせるのは `why` そのものではなく**末尾の名前**である。
    このフックはコマンド文字列に末尾の名前を探すので、
    `.claude/settings.local.json` と `Designer/.claude/settings.local.json` のように
    名前が同じ行は同じ理由文を返す（**拒む結論は同じで、正確な行は trash.ps1 が見る**）。
    """
    for entry in json.loads(CANON.read_text(encoding="utf-8"))["protected"]:
        name = entry["path"].replace("\\", "/").rstrip("/").split("/")[-1]
        decision, reason = decide(f"{T} {entry['path']}")
        if decision != "deny" or name not in (reason or ""):
            failed += 1
            print(f"NG  正典に載っているのに保護されない: {entry['path']}（{decision}）")
    return failed


def _check_wiring(failed: int) -> int:
    """**関門が settings.json に配線されているか。**

    フックの登録や deny を消しても検査が緑のままなら、関門は配線ごと外せてしまう。
    """
    try:
        settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
    except Exception as exc:
        print(f"NG  {SETTINGS} を読めない: {exc}")
        return failed + 1

    hooks = json.dumps(settings.get("hooks", {}), ensure_ascii=False)
    if "guard_delete.py" not in hooks:
        failed += 1
        print("NG  settings.json の PreToolUse に guard_delete.py が登録されていない")

    deny = set(settings.get("permissions", {}).get("deny", []))
    for required in ("Bash(rm:*)", "PowerShell(Remove-Item:*)", "Bash(git clean:*)"):
        if required not in deny:
            failed += 1
            print(f"NG  settings.json の deny に {required} が無い（フックが落ちたときの控え）")
    return failed


def selftest() -> int:
    global CANON  # 下で正典の差し替えを試すため（関数の先頭でしか宣言できない）
    failed = 0
    for command, expected in SELFTEST:
        actual, _ = decide(command)
        if actual != expected:
            failed += 1
            print(f"NG  期待 {expected} / 実際 {actual}: {command!r}")

    failed = _check_canon(failed)
    failed = _check_wiring(failed)

    # 正典が読めないときに素通りしないか（fail-open にしていないか）。
    # **この一手は表では書けない**——表は正典が読める前提の判定しか並べられないため。
    readable, CANON = CANON, CANON.with_name("protected_paths.json.存在しない")
    try:
        if decide("rm -rf work/tmp")[0] != "deny":
            failed += 1
            print("NG  正典を読めないのに削除を通した")
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

    command = str(payload.get("tool_input", {}).get("command", ""))
    decision, reason = decide(command)
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
