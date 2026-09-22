#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""docs_only.py — このコミットが **ミューテーションの段の入力を 1 つも変えていない**かを判定する.

**0 を返した回は、コミット前フックがミューテーションの段を飛ばす**（[ADR-0068](../../docs/decisions/0068-docsの文書だけを変えた回はミューテーションの段を流さない.md)）。
飛ばすのはその 1 段だけで、**ほかの段はすべて流れる**——`dotnet test`（38 秒）と行セットの掃引（10 秒）は
合わせて 48 秒で、**判定が壊れたときに鳴るものとして残す**ほうが得である。

**判定は足し算で書く。** 「変わったパスが**全部**不活性なら飛ばす」であって、
「`.cs` が無ければ飛ばす」ではない。**引き算で書くと、新しい種類の入力が増えた日に既定が
「飛ばす」へ倒れる**——C# のテストは `*.cs` 以外も読んでいる。

**`docs/` の下でも、テストが読む文書は不活性ではない**（`DOCS_READ_BY_TESTS`）。
**ミューテーションの対象プロジェクトのテストが、設計文書の表を読んで突き合わせている**からである。
**その一覧が腐らないよう、自己検査が C# の側を走査する**——
**文書のパスらしい文字列を書いている C# が増えたら赤くなる。**

**本番の判定は、git が渡す環境をそのまま使う。** `git commit -a` や `git commit -- <パス>` のとき、
**git は一時の索引を作って `GIT_INDEX_FILE` で渡す**——落とすと、**コミットに入る `.cs` を見落とす**
（2026-09-22 に実測）。**外側の環境を落とすのは、使い捨てのリポジトリを撃つ検体の側だけ**である。

使い方:
    python tools/git-hooks/docs_only.py            # 判定して印字する（リポジトリのルートで打つ）
    python tools/git-hooks/docs_only.py --selftest # 判定・一覧の鮮度・段の配線を検査する

終了コード: 0 = 入力を変えていない（飛ばしてよい）／1 = 変えている（流す）／2 = 判定できない（流す）
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _gitenv import clean_env  # noqa: E402  （使い捨てのリポジトリを撃つときだけ使う）

sys.stdout.reconfigure(encoding="utf-8")

HOOKS_DIR = Path(__file__).resolve().parent
REPO_ROOT = HOOKS_DIR.parent.parent
STAGE = HOOKS_DIR / "mutation_stage.sh"
PRE_COMMIT = HOOKS_DIR / "pre-commit"
SOURCE_DIR = REPO_ROOT / "BusinessApp"

# **不活性**＝ミューテーションの段の入力になりえない、ということ。
# **迷うものは載せない**——載せなければ流す側に倒れる。
INERT_PREFIX = "docs/"
INERT_SUFFIX = ".md"

# **`docs/` の下でも、これはテストが読む**（＝段の入力である）。
DOCS_READ_BY_TESTS = {
    "docs/10_会計ドメイン設計.md",        # 不変条件のカタログ
    "docs/40_優良な電子帳簿の対応表.md",  # 制度要件のカタログ
    "docs/13_取引先設計.md",              # 入場券等の 1 文の正典（画面の字と突き合わせる）
    "docs/qa/04_実機操作テスト.md",       # 実機の台本（確認文の期待値を画面の組む文と突き合わせる）
}

# **上の一覧を書いてよい C# は、この 4 本だけである。**
# **増えたら自己検査が赤くなる**——一覧を手で守ると、次に文書を読むテストを書いた日に黙って穴が開く。
DOC_READERS = {
    "BusinessApp.AccountingCore.Tests/Conventions/InvariantTraceabilityConvention.cs",
    "BusinessApp.AccountingCore.Tests/Conventions/RequirementTraceabilityConvention.cs",
    "BusinessApp.AccountingCore.Tests/Journals/JournalViolationCodesTests.cs",
    "BusinessApp.AccountingCore.Server.Tests/Journals/Presentation/JournalAmendmentEndpointTests.cs",
}

# ブロブのモード。**シンボリックリンク（120000）と gitlink（160000）は不活性と言い切れない**
BLOB_MODES = {"100644", "100755"}
ABSENT_MODE = "000000"  # 足した側・消した側

# ミューテーションの段が撃つもの（**ラチェットの値も含めて釘付けする**。正典は mutation_stage.sh）
STAGE_CALLS = [
    ("BusinessApp/BusinessApp.AccountingCore.Tests",
     "stryker --project BusinessApp.AccountingCore.csproj --break-at 84 --threshold-low 84 --reporter progress"),
    ("BusinessApp/BusinessApp.AccountingCore.Server.Tests",
     "stryker --project BusinessApp.AccountingCore.Server.csproj --break-at 94 --threshold-low 94 --reporter progress"),
    ("BusinessApp/BusinessApp.Partners.Tests",
     "stryker --project BusinessApp.Partners.csproj --break-at 92 --threshold-low 92 --reporter progress"),
    ("BusinessApp/BusinessApp.Partners.Server.Tests",
     "stryker --project BusinessApp.Partners.Server.csproj --break-at 95 --threshold-low 95 --reporter progress"),
    ("BusinessApp/BusinessApp.ServerSupport.Tests",
     "stryker --project BusinessApp.ServerSupport.csproj --break-at 93 --threshold-low 93 --reporter progress"),
]


def is_inert(path: str) -> bool:
    return (path.startswith(INERT_PREFIX)
            and path.endswith(INERT_SUFFIX)
            and path not in DOCS_READ_BY_TESTS)


def staged_changes(cwd: Path, env: dict[str, str] | None) -> list[tuple[str, str, str]] | None:
    """これからコミットする差分を（旧モード・新モード・パス）で返す。判定できないときは None。

    **`env` は本番では None**（git が渡した環境をそのまま使う）。**検体だけ `clean_env()` を渡す。**
    `--raw` はモードを見るため、`--no-renames` は改名の両側を数えるため、`-z` は引用を抑えるため、
    `diff.relative=false` は下のディレクトリから打たれてもパスが欠けないためである。
    """
    def git(*args: str):
        return subprocess.run(["git", "-c", "diff.relative=false", *args], cwd=cwd,
                              capture_output=True, text=True, encoding="utf-8",
                              errors="replace", env=env)

    try:
        if git("rev-parse", "--verify", "HEAD").returncode != 0:
            return None  # 最初のコミット——比べる相手がいない
        r = git("diff", "--cached", "--raw", "-z", "--no-renames", "HEAD")
        if r.returncode != 0:
            return None
    except OSError:
        return None

    fields = [f for f in r.stdout.split("\0") if f]
    # **奇数なら読めていない。** 改名の検出が効くと 1 件が（印・元・先）の 3 つになる——
    # **`zip` は黙って余りを捨てる**ので、**数が合わないことを先に見る**（2026-09-22 に踏んだ）
    if len(fields) % 2 != 0:
        return None
    changes: list[tuple[str, str, str]] = []
    for meta, path in zip(fields[0::2], fields[1::2]):
        if not meta.startswith(":"):
            return None
        parts = meta[1:].split()
        if len(parts) < 2:
            return None
        changes.append((parts[0], parts[1], path))
    return changes


def judge(changes: list[tuple[str, str, str]] | None) -> tuple[int, str]:
    """終了コードと、印字する理由。**「流さない」も必ず声に出す。**"""
    if changes is None:
        return 2, "差分を引けない（最初のコミットか、git が動かない） → 流す"
    if not changes:
        return 1, "差分が 0 件 → 流す"

    odd = [(old, new, p) for old, new, p in changes
           if {old, new} - BLOB_MODES - {ABSENT_MODE}]
    if odd:
        old, new, p = odd[0]
        return 1, f"ふつうのファイルでないものがある（{p} のモードが {old}→{new}） → 流す"

    others = [p for _, _, p in changes if not is_inert(p)]
    if others:
        head = "・".join(others[:3]) + ("ほか" if len(others) > 3 else "")
        return 1, f"ミューテーションの段の入力が {len(others)} 件ある（{head}） → 流す"
    return 0, f"変わったのは不活性な文書 {len(changes)} 件だけ → ミューテーションの段は流さない"


# ---------------------------------------------------------------------------
# 自己検査
# ---------------------------------------------------------------------------

# **検体のリポジトリが本番のフックを拾わないようにする。**
# `core.hooksPath` を絶対パスでグローバルに置いている環境や、`GIT_TEMPLATE_DIR` があると、
# **使い捨てのリポジトリの `git commit` が本番のフック一式を流す**（2026-09-22 に実測。最悪は無限再帰）。
FIXTURE_GIT = ["-c", "user.name=selftest", "-c", "user.email=selftest@example.com",
               "-c", "core.hooksPath=", "-c", "init.templateDir="]


def _git(cwd: Path, *args: str) -> None:
    subprocess.run(["git", *FIXTURE_GIT, *args], cwd=cwd, check=True, capture_output=True,
                   text=True, encoding="utf-8", errors="replace", env=clean_env())


def _fixture(root: Path, name: str) -> Path:
    """**本物の `git add` で索引を作る。** 手で作ったパスの一覧を判定に渡すと、
    **本番の入力（`git diff --cached`）を一度も通らない検査**になる（qa/03 の L-63）。"""
    repo = root / name
    (repo / "docs").mkdir(parents=True)
    _git(root, "init", "-q", str(repo))
    (repo / "docs" / "始め.md").write_text("最初\n", encoding="utf-8", newline="\n")
    (repo / "code.cs").write_text("// 最初\n", encoding="utf-8", newline="\n")
    _git(repo, "add", "-A")
    _git(repo, "commit", "-q", "-m", "最初")
    return repo


def _stage_file(repo: Path, path: str, body: str) -> None:
    target = repo / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(body, encoding="utf-8", newline="\n")
    _git(repo, "add", path)


def _fixture_judge(repo: Path) -> tuple[int, str]:
    return judge(staged_changes(repo, clean_env()))


# --- C# の走査（文書のパスらしい文字列を書いているファイルを数える）---

LITERAL = re.compile(r'@"[^"]*"|"(?:\\.|[^"\\])*"')
COMBINE = re.compile(r'"docs",\s*"([^"]*\.md)"')


def _looks_like_doc_path(literal: str) -> bool:
    body = literal.lstrip("@").strip('"')
    return body == "docs" or ("docs" in body and ".md" in body)


def _scan_sources() -> tuple[set[str], set[str]]:
    """（文書のパスらしい字を書いている C#、そこで名指しされた文書）を返す。

    **注記の行は外す**（`//`・`///`・`*`）——`<see href="../../../docs/…">` のような出典は読み取りではない。
    **これで拾えるのは、`"docs"`／`"docs/…md"`／`@"docs\\…md"`／`"../…/docs/…md"` のどれか**である。
    **`.csproj` の `EmbeddedResource` や `.razor` は見ていない**（いまは該当が無い。ADR-0068 の帰結）。
    """
    files: set[str] = set()
    named: set[str] = set()
    for path in SOURCE_DIR.rglob("*.cs"):
        if {"obj", "bin"} & set(path.parts) or any(x.startswith("StrykerOutput") for x in path.parts):
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        hit = False
        for line in text.splitlines():
            s = line.lstrip()
            if s.startswith("//") or s.startswith("*") or s.startswith("/*"):
                continue
            if any(_looks_like_doc_path(m.group(0)) for m in LITERAL.finditer(line)):
                hit = True
        if hit:
            files.add(path.relative_to(SOURCE_DIR).as_posix())
            named.update(m.group(0).lstrip("@").strip('"')
                         for m in LITERAL.finditer(text)
                         if _looks_like_doc_path(m.group(0)) and m.group(0).strip('"@') != "docs")
            named.update(f"docs/{n}" for n in COMBINE.findall(text))
    return files, {n for n in named if n.startswith("docs/")}


# --- 段を本当に流す（切り株を置いて、呼ばれたかどうかを見る）---

def _run_stage(root: Path, decision: int, fail_on: str = "") -> tuple[int, str, list[tuple[str, str]]]:
    """段を流し、（終了コード・出力・切り株が受けた（作業ディレクトリ, 引数））を返す。

    **`docs_only.py` の切り株は引数も書き出す**——書き出さないと、
    **呼び出しに `--selftest` を足すだけで毎回飛ぶ**書き換えが素通りする（2026-09-22 に実測）。
    """
    work = root / f"stage-{decision}-{fail_on or 'ok'}"
    (work / "tools" / "git-hooks").mkdir(parents=True)
    (work / "bin").mkdir()
    shutil.copy(STAGE, work / "tools" / "git-hooks" / "mutation_stage.sh")
    argv_log = work / "docs_only.argv"
    (work / "tools" / "git-hooks" / "docs_only.py").write_text(
        "import sys\n"
        f"open(r'{argv_log}', 'a', encoding='utf-8').write(' '.join(sys.argv[1:]) + '\\n')\n"
        f"sys.exit({decision})\n", encoding="utf-8", newline="\n")
    log = work / "dotnet.log"
    stub = work / "bin" / "dotnet"
    # **1 本だけ落とせるようにする。** 全部落とすと、**`set -e` を消しても最後の 1 本で非 0 になる**ので、
    # **段の途中で止まらなくなった壊し方を捕まえられない**（2026-09-22 のノックアウトで緑のまま通った）
    fail = f'case "$*" in *{fail_on}*) exit 3 ;; esac\n' if fail_on else ""
    stub.write_text(f'#!/bin/sh\nprintf "%s\\t%s\\n" "$PWD" "$*" >> "{log.as_posix()}"\n'
                    + fail + "exit 0\n", encoding="utf-8", newline="\n")
    stub.chmod(0o755)
    _git(root, "init", "-q", str(work))  # 段は git のルートへ移るので、リポジトリにしておく
    for directory, _ in STAGE_CALLS:
        (work / directory).mkdir(parents=True, exist_ok=True)
    env = clean_env({"PATH": str(work / "bin") + os.pathsep + os.environ.get("PATH", "")})
    r = subprocess.run(["sh", "tools/git-hooks/mutation_stage.sh"], cwd=work, env=env,
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    called = []
    if log.exists():
        for line in log.read_text(encoding="utf-8").splitlines():
            where, _, args = line.partition("\t")
            called.append((Path(where).name, args))
    argv = argv_log.read_text(encoding="utf-8").splitlines() if argv_log.exists() else []
    out = (r.stdout or "") + (r.stderr or "") + f"\n引数: {argv}"
    return r.returncode, out, called


# --- 本番の起動経路（git にフックから呼ばせる）---

def _run_as_hook(root: Path, name: str, commit_args: list[str]) -> str:
    """**git にフックとして呼ばせ、判定の印字を受け取る。**

    検体が `judge()` を直呼びするだけだと、**`git commit -a` が渡す一時の索引を通らない**
    （2026-09-22 に踏んだ。qa/03 の L-63 と同じ型）。
    """
    repo = _fixture(root, name)
    (repo / "tools" / "git-hooks").mkdir(parents=True)
    shutil.copy(Path(__file__).resolve(), repo / "tools" / "git-hooks" / "docs_only.py")
    shutil.copy(HOOKS_DIR / "_gitenv.py", repo / "tools" / "git-hooks" / "_gitenv.py")
    hooks = root / f"hooks-{name}"
    hooks.mkdir()
    verdict = repo / "verdict.txt"
    (hooks / "pre-commit").write_text(
        "#!/bin/sh\n"
        f'python tools/git-hooks/docs_only.py > "{verdict.as_posix()}" 2>&1\n'
        f'echo "exit=$?" >> "{verdict.as_posix()}"\n', encoding="utf-8", newline="\n")
    (hooks / "pre-commit").chmod(0o755)
    _git(repo, "config", "core.hooksPath", str(hooks))
    _stage_file(repo, "docs/始め.md", "書き足した\n")
    (repo / "code.cs").write_text("// 索引に入れていない変更\n", encoding="utf-8", newline="\n")
    subprocess.run(["git", "-c", "user.name=selftest", "-c", "user.email=selftest@example.com",
                    "commit", "-q", "-m", "試し", *commit_args],
                   cwd=repo, capture_output=True, text=True, encoding="utf-8",
                   errors="replace", env=clean_env())
    return verdict.read_text(encoding="utf-8") if verdict.exists() else "（フックが動かなかった）"


EXPECTED = [
    "docs/始め.md と docs/下/新しい.md だけなら 0 を返す",
    "そのとき理由に 2 件と言う",
    "code.cs が混ざれば 1 を返す",
    "そのとき code.cs を名指しする",
    "docs/道具.cs（docs の下の .cs）は流す",
    "docsツール/覚書.md（docs で始まる別のディレクトリ）は流す",
    "code.cs を消した回も流す",
    "docs/始め.md を消しただけの回は 0 を返す",
    "索引に入れていない code.cs は数えない",
    "code.cs を docs/code.md へ改名した回も流す",
    "そのとき消えた code.cs を名指しする",
    "テストが読む docs/10 を変えた回は流す",
    "そのとき docs/10 を名指しする",
    "docs/link.md（シンボリックリンク）は不活性と認めない",
    "そのときモード 120000 を言う",
    "差分が 0 件なら流す",
    "HEAD が無いときは流す（判定できない）",
    "git が動かないときも流す",
    "外側の GIT_DIR が漏れても判定は変わらない",
    "外側のリポジトリを壊さない",
    "git commit -a で入る code.cs を見落とさない",
    "索引どおりの git commit では飛ばす",
    "文書のパスを書いている C# は DOC_READERS と一致する",
    "そこで名指しされる文書は DOCS_READ_BY_TESTS と一致する",
    "飛ばす回は dotnet を 1 度も呼ばない",
    "そのとき飛ばした理由を印字し、0 で終わる",
    "飛ばす回は docs_only を引数なしで呼ぶ",
    "流す回は 5 つのプロジェクトへ、決めた引数で撃つ",
    "そのとき作業ディレクトリも決めたとおりである",
    "最初の 1 本が落ちたら、そこで止まって非 0 で終わる",
    "pre-commit は mutation_stage.sh を呼んでいる",
]


def selftest() -> int:
    failures = 0
    passed: list[str] = []

    def check(claim: str, cond: bool, detail: str = "") -> None:
        nonlocal failures
        if cond:
            passed.append(claim)
        else:
            failures += 1
            print(f"  NG {claim}")
            for line in str(detail).splitlines():
                print(f"     {line}")

    with tempfile.TemporaryDirectory(prefix="docs-only-selftest-") as tmp:
        root = Path(tmp)

        repo = _fixture(root, "不活性だけ")
        _stage_file(repo, "docs/始め.md", "書き足した\n")
        _stage_file(repo, "docs/下/新しい.md", "新しい\n")
        code, why = _fixture_judge(repo)
        check("docs/始め.md と docs/下/新しい.md だけなら 0 を返す", code == 0, why)
        check("そのとき理由に 2 件と言う", "2 件" in why, why)

        repo = _fixture(root, "csが混ざる")
        _stage_file(repo, "docs/始め.md", "書き足した\n")
        _stage_file(repo, "code.cs", "// 変えた\n")
        code, why = _fixture_judge(repo)
        check("code.cs が混ざれば 1 を返す", code == 1, why)
        check("そのとき code.cs を名指しする", "code.cs" in why, why)

        repo = _fixture(root, "docsの下のcs")
        _stage_file(repo, "docs/道具.cs", "// docs の下\n")
        check("docs/道具.cs（docs の下の .cs）は流す", _fixture_judge(repo)[0] == 1)

        repo = _fixture(root, "紛らわしい名前")
        _stage_file(repo, "docsツール/覚書.md", "別のディレクトリ\n")
        check("docsツール/覚書.md（docs で始まる別のディレクトリ）は流す", _fixture_judge(repo)[0] == 1)

        repo = _fixture(root, "消した")
        _git(repo, "rm", "-q", "code.cs")
        check("code.cs を消した回も流す", _fixture_judge(repo)[0] == 1)

        repo = _fixture(root, "文書を消した")
        _git(repo, "rm", "-q", "docs/始め.md")
        check("docs/始め.md を消しただけの回は 0 を返す", _fixture_judge(repo)[0] == 0)

        repo = _fixture(root, "未ステージのcs")
        _stage_file(repo, "docs/始め.md", "書き足した\n")
        (repo / "code.cs").write_text("// 索引に入れていない\n", encoding="utf-8", newline="\n")
        check("索引に入れていない code.cs は数えない", _fixture_judge(repo)[0] == 0)

        repo = _fixture(root, "改名")
        _git(repo, "mv", "code.cs", "docs/code.md")
        code, why = _fixture_judge(repo)
        check("code.cs を docs/code.md へ改名した回も流す", code == 1, why)
        check("そのとき消えた code.cs を名指しする", "code.cs" in why, why)

        repo = _fixture(root, "テストが読む文書")
        _stage_file(repo, "docs/10_会計ドメイン設計.md", "| I-01 | 変えた |\n")
        code, why = _fixture_judge(repo)
        check("テストが読む docs/10 を変えた回は流す", code == 1, why)
        check("そのとき docs/10 を名指しする", "docs/10_会計ドメイン設計.md" in why, why)

        repo = _fixture(root, "リンク")
        blob = subprocess.run(["git", "hash-object", "-w", "--stdin"], cwd=repo, input="../code.cs",
                              capture_output=True, text=True, check=True,
                              env=clean_env()).stdout.strip()
        _git(repo, "update-index", "--add", "--cacheinfo", f"120000,{blob},docs/link.md")
        code, why = _fixture_judge(repo)
        check("docs/link.md（シンボリックリンク）は不活性と認めない", code == 1, why)
        check("そのときモード 120000 を言う", "120000" in why, why)

        repo = _fixture(root, "差分なし")
        code, why = _fixture_judge(repo)
        check("差分が 0 件なら流す", code == 1, why)

        empty = root / "空"
        empty.mkdir()
        _git(root, "init", "-q", str(empty))
        code, why = _fixture_judge(empty)
        check("HEAD が無いときは流す（判定できない）", code == 2, why)
        check("git が動かないときも流す", judge(None)[0] == 2)

        # --- 隔離（外側の git の環境が漏れても、検体が本番を壊さない）---
        victim = _fixture(root, "隔離の犠牲")
        before = (victim / ".git" / "index").read_bytes()
        dirty = {"GIT_DIR": str(victim / ".git"), "GIT_INDEX_FILE": str(victim / ".git" / "index")}
        os.environ.update(dirty)
        try:
            repo = _fixture(root, "隔離")
            _stage_file(repo, "docs/始め.md", "外側が汚れていても書ける\n")
            code, why = _fixture_judge(repo)
            check("外側の GIT_DIR が漏れても判定は変わらない", code == 0, why)
            check("外側のリポジトリを壊さない", (victim / ".git" / "index").read_bytes() == before)
        finally:
            for k in dirty:
                os.environ.pop(k, None)

        # --- 本番の起動経路（git がフックとして呼ぶ）---
        out = _run_as_hook(root, "commit-a", ["-a"])
        check("git commit -a で入る code.cs を見落とさない",
              "code.cs" in out and "exit=1" in out, out)
        out = _run_as_hook(root, "commit-plain", [])
        check("索引どおりの git commit では飛ばす",
              "流さない" in out and "exit=0" in out, out)

        # --- 一覧の鮮度（C# の側を走査する）---
        files, named = _scan_sources()
        check("文書のパスを書いている C# は DOC_READERS と一致する", files == DOC_READERS,
              f"C# 側: {sorted(files)}\n一覧  : {sorted(DOC_READERS)}")
        check("そこで名指しされる文書は DOCS_READ_BY_TESTS と一致する", named == DOCS_READ_BY_TESTS,
              f"C# 側: {sorted(named)}\n一覧  : {sorted(DOCS_READ_BY_TESTS)}")

        # --- 段の配線（切り株を置いて、本当に流す）---
        rc, out, called = _run_stage(root, 0)
        check("飛ばす回は dotnet を 1 度も呼ばない", called == [], f"{called}\n{out}")
        check("そのとき飛ばした理由を印字し、0 で終わる",
              "この段は流さない" in out and rc == 0, f"{rc}\n{out}")
        check("飛ばす回は docs_only を引数なしで呼ぶ", "引数: ['']" in out, out)

        rc, out, called = _run_stage(root, 1)
        want = [(Path(d).name, a) for d, a in STAGE_CALLS]
        check("流す回は 5 つのプロジェクトへ、決めた引数で撃つ",
              [a for _, a in called] == [a for _, a in want], f"{called}\n{out}")
        check("そのとき作業ディレクトリも決めたとおりである",
              [d for d, _ in called] == [d for d, _ in want], f"{called}\n{out}")

        rc, out, called = _run_stage(root, 1, fail_on="AccountingCore.csproj")
        check("最初の 1 本が落ちたら、そこで止まって非 0 で終わる",
              rc != 0 and len(called) == 1, f"終了コード {rc}／呼ばれた {len(called)} 本\n{out}")

    # --- フックがその段を呼んでいるか（字で見る。限界は ADR-0068 の帰結）---
    check("pre-commit は mutation_stage.sh を呼んでいる",
          "sh tools/git-hooks/mutation_stage.sh" in PRE_COMMIT.read_text(encoding="utf-8"))

    if passed != EXPECTED:
        missing = [c for c in EXPECTED if c not in passed]
        extra = [c for c in passed if c not in EXPECTED]
        print("  NG 母数: 通った表明の一覧が期待と違う")
        for c in missing:
            print(f"     通らなかった/撃たれなかった: {c}")
        for c in extra:
            print(f"     一覧に無いものが通った: {c}")
        if not missing and not extra:
            print("     顔ぶれは同じだが並びが違う（一覧は撃つ順に書く）")
        failures += 1

    if failures:
        print(f"docs_only -SelfTest: {failures} 件失敗")
        return 1
    print(f"docs_only -SelfTest: OK（表明 {len(passed)} 件がすべて期待どおり）")
    return 0


def main() -> int:
    if "--selftest" in sys.argv[1:]:
        return selftest()
    # **本番は git が渡した環境をそのまま使う**（`git commit -a` の一時索引を見るため）
    code, why = judge(staged_changes(Path.cwd(), None))
    print(f"[docs_only] {why}")
    return code


if __name__ == "__main__":
    sys.exit(main())
