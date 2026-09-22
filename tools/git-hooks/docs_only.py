#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""docs_only.py — このコミットが **ミューテーションの段の入力を 1 つも変えていない**かを判定する.

**0 を返した回は、コミット前フックがミューテーションの段を飛ばす**（[ADR-0068](../../docs/decisions/0068-docsの文書だけを変えた回はミューテーションの段を流さない.md)）。
飛ばすのはその 1 段だけで、**ほかの段はすべて流れる**——`dotnet test`（38 秒）と行セットの掃引（10 秒）は
合わせて 48 秒で、**判定が壊れたときに鳴るものとして残す**ほうが得である。

**判定は足し算で書く。** 「変わったパスが**全部**不活性なら飛ばす」であって、
「`.cs` が無ければ飛ばす」ではない。**引き算で書くと、新しい種類の入力が増えた日に既定が
「飛ばす」へ倒れる**——C# のテストは `*.cs` 以外も読んでいる（`Designer/Design/**` の JSON・
`.editorconfig`・`.gitattributes`・`*.csproj`・`BusinessApp.slnx`）。

**`docs/` の下でも、テストが読む文書は不活性ではない**（`DOCS_READ_BY_TESTS`）。
**ミューテーションの対象プロジェクトのテストが、設計文書の表を読んで突き合わせている**からである。
**その一覧が腐らないよう、自己検査が C# の側を走査して突き合わせる。**

**比べる相手は `HEAD` である。** 差が全部不活性なら、**`HEAD` のコミット前フックが、
この段の入力を同じ姿で検査している**（ADR-0067 と同じ筋。**失うものも同じ**——
`--no-verify`・rebase・cherry-pick・`--amend` で作った `HEAD` では前提が切れる）。

使い方:
    python tools/git-hooks/docs_only.py            # 判定して印字する
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
from _gitenv import clean_env  # noqa: E402  （フックの中から別のリポジトリで git を撃つための環境）

sys.stdout.reconfigure(encoding="utf-8")

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
STAGE = Path(__file__).resolve().parent / "mutation_stage.sh"
SOURCE_DIR = REPO_ROOT / "BusinessApp"

# **不活性**＝ミューテーションの段の入力になりえない、ということ。
# **迷うものは載せない**——載せなければ流す側に倒れる。
INERT_PREFIX = "docs/"
INERT_SUFFIX = ".md"

# **`docs/` の下でも、これはテストが読む**（＝段の入力である）。
# **自己検査が C# の側を走査して、この一覧と突き合わせる**ので、読む文書が増えれば赤くなる。
DOCS_READ_BY_TESTS = {
    "docs/10_会計ドメイン設計.md",        # 不変条件のカタログ（InvariantTraceabilityConvention・JournalViolationCodesTests）
    "docs/40_優良な電子帳簿の対応表.md",  # 制度要件のカタログ（RequirementTraceabilityConvention）
}

# ブロブのモード。**シンボリックリンク（120000）と gitlink（160000）は不活性と言い切れない**
# ——名前が `.md` でも中身が別のものを指しうる
BLOB_MODES = {"100644", "100755"}
ABSENT_MODE = "000000"  # 足した側・消した側


def is_inert(path: str) -> bool:
    return (path.startswith(INERT_PREFIX)
            and path.endswith(INERT_SUFFIX)
            and path not in DOCS_READ_BY_TESTS)


def staged_changes(cwd: Path) -> list[tuple[str, str, str]] | None:
    """これからコミットする差分を（旧モード・新モード・パス）で返す。判定できないときは None。

    **`--raw` で引くのはモードを見るため**、**`--no-renames` を落とさないのは
    既定の `git diff` が改名を 1 行にまとめて消えた側を隠すため**（2026-09-22 に実測）。
    """
    def git(*args: str):
        return subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True,
                              encoding="utf-8", errors="replace", env=clean_env())

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
            return None  # 読めない形——判定できない
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

def _git(cwd: Path, *args: str) -> None:
    """**検体の中の git も、外側の環境を落としてから撃つ。**

    落とさないと、`git commit -a` が渡す絶対パスの `GIT_INDEX_FILE` に引きずられて
    **本番のリポジトリの索引を壊す**（2026-09-22 に実測）。
    """
    subprocess.run(["git", "-c", "user.name=selftest", "-c", "user.email=selftest@example.com",
                    *args], cwd=cwd, check=True, capture_output=True, text=True,
                   encoding="utf-8", errors="replace", env=clean_env())


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


def _docs_referenced_by_sources() -> set[str]:
    """**C# の中で、文書を名指ししている字を集める。**

    拾うのは `"docs/……md"` と `Path.Combine(…, "docs", "……md")` の形である。
    **注記の中の相対リンク**（`"../../../docs/…"`）**は拾わない**——引用符の直後が `docs/` でないからである。
    """
    found: set[str] = set()
    direct = re.compile(r'"(docs/[^"]*\.md)"')
    combined = re.compile(r'"docs",\s*"([^"]*\.md)"')
    for path in SOURCE_DIR.rglob("*.cs"):
        parts = set(path.parts)
        if {"obj", "bin"} & parts or any(p.startswith("StrykerOutput") for p in path.parts):
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        found.update(direct.findall(text))
        found.update(f"docs/{name}" for name in combined.findall(text))
    return found


def _run_stage(root: Path, decision: int) -> tuple[str, list[str]]:
    """**段を本当に流して、`dotnet` が呼ばれたかを見る。**

    `docs_only.py` を「その答えを返すだけの切り株」に、`dotnet` を「引数を書き出す切り株」に差し替える。
    **字を読むだけの検査では、判定の反転も、5 段のうち 4 段を消すのも素通りした**（2026-09-22 のレビュー）。
    """
    work = root / f"stage-{decision}"
    (work / "tools" / "git-hooks").mkdir(parents=True)
    (work / "bin").mkdir()
    shutil.copy(STAGE, work / "tools" / "git-hooks" / "mutation_stage.sh")
    (work / "tools" / "git-hooks" / "docs_only.py").write_text(
        f"import sys\nsys.exit({decision})\n", encoding="utf-8", newline="\n")
    log = work / "dotnet.log"
    stub = work / "bin" / "dotnet"
    stub.write_text(f'#!/bin/sh\nprintf "%s\\n" "$*" >> "{log.as_posix()}"\n',
                    encoding="utf-8", newline="\n")
    stub.chmod(0o755)
    for project in ("BusinessApp.AccountingCore.Tests", "BusinessApp.AccountingCore.Server.Tests",
                    "BusinessApp.Partners.Tests", "BusinessApp.Partners.Server.Tests",
                    "BusinessApp.ServerSupport.Tests"):
        (work / "BusinessApp" / project).mkdir(parents=True)
    env = clean_env({"PATH": str(work / "bin") + os.pathsep + os.environ.get("PATH", "")})
    r = subprocess.run(["sh", "tools/git-hooks/mutation_stage.sh"], cwd=work, env=env,
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    called = log.read_text(encoding="utf-8").splitlines() if log.exists() else []
    return (r.stdout or "") + (r.stderr or "") + f"\n（終了コード {r.returncode}）", called


EXPECTED = [
    "不活性な文書だけなら 0 を返す",
    "そのとき理由に件数を言う",
    "`.cs` が 1 つでも混ざれば 1 を返す",
    "そのとき混ざったパスを名指しする",
    "`docs/` の下でも `.md` でなければ流す",
    "`docs` で始まる別のディレクトリには当たらない",
    "`.cs` を消した回も流す",
    "不活性な文書を消しただけの回は 0 を返す",
    "索引に入れていない `.cs` は数えない（コミットに入らない）",
    "`.cs` を docs の `.md` へ改名した回も流す",
    "そのとき消えた `.cs` を名指しする",
    "テストが読む文書（docs/10）を変えた回は流す",
    "そのときその文書を名指しする",
    "docs の下のシンボリックリンクは不活性と認めない",
    "そのときモードを言う",
    "差分が 0 件なら流す",
    "HEAD が無いときは流す（判定できない）",
    "git が動かないときも流す",
    "外側の GIT_DIR が漏れても判定は変わらない",
    "外側のリポジトリを壊さない",
    "C# が読む文書は DOCS_READ_BY_TESTS と一致する",
    "飛ばす回は dotnet を 1 度も呼ばない",
    "そのとき飛ばした理由を印字する",
    "流す回は 5 つのプロジェクトすべてに dotnet stryker を撃つ",
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
        code, why = judge(staged_changes(repo))
        check("不活性な文書だけなら 0 を返す", code == 0, why)
        check("そのとき理由に件数を言う", "2 件" in why, why)

        repo = _fixture(root, "csが混ざる")
        _stage_file(repo, "docs/始め.md", "書き足した\n")
        _stage_file(repo, "code.cs", "// 変えた\n")
        code, why = judge(staged_changes(repo))
        check("`.cs` が 1 つでも混ざれば 1 を返す", code == 1, why)
        check("そのとき混ざったパスを名指しする", "code.cs" in why, why)

        repo = _fixture(root, "docsの下のcs")
        _stage_file(repo, "docs/道具.cs", "// docs の下\n")
        check("`docs/` の下でも `.md` でなければ流す", judge(staged_changes(repo))[0] == 1)

        repo = _fixture(root, "紛らわしい名前")
        _stage_file(repo, "docsツール/覚書.md", "別のディレクトリ\n")
        check("`docs` で始まる別のディレクトリには当たらない", judge(staged_changes(repo))[0] == 1)

        repo = _fixture(root, "消した")
        _git(repo, "rm", "-q", "code.cs")
        check("`.cs` を消した回も流す", judge(staged_changes(repo))[0] == 1)

        repo = _fixture(root, "文書を消した")
        _git(repo, "rm", "-q", "docs/始め.md")
        check("不活性な文書を消しただけの回は 0 を返す", judge(staged_changes(repo))[0] == 0)

        repo = _fixture(root, "未ステージのcs")
        _stage_file(repo, "docs/始め.md", "書き足した\n")
        (repo / "code.cs").write_text("// 索引に入れていない\n", encoding="utf-8", newline="\n")
        check("索引に入れていない `.cs` は数えない（コミットに入らない）",
              judge(staged_changes(repo))[0] == 0)

        # **改名は両側を数える**——既定の `git diff` は改名を 1 行にまとめ、消えた側を隠す
        repo = _fixture(root, "改名")
        _git(repo, "mv", "code.cs", "docs/code.md")
        code, why = judge(staged_changes(repo))
        check("`.cs` を docs の `.md` へ改名した回も流す", code == 1, why)
        check("そのとき消えた `.cs` を名指しする", "code.cs" in why, why)

        # **テストが読む文書は、`docs/` の下でも不活性ではない**
        repo = _fixture(root, "テストが読む文書")
        _stage_file(repo, "docs/10_会計ドメイン設計.md", "| I-01 | 変えた |\n")
        code, why = judge(staged_changes(repo))
        check("テストが読む文書（docs/10）を変えた回は流す", code == 1, why)
        check("そのときその文書を名指しする", "docs/10_会計ドメイン設計.md" in why, why)

        # **名前が `.md` でも、ふつうのファイルでなければ不活性と言えない**
        repo = _fixture(root, "リンク")
        blob = subprocess.run(["git", "hash-object", "-w", "--stdin"], cwd=repo, input="../code.cs",
                              capture_output=True, text=True, check=True,
                              env=clean_env()).stdout.strip()
        _git(repo, "update-index", "--add", "--cacheinfo", f"120000,{blob},docs/link.md")
        code, why = judge(staged_changes(repo))
        check("docs の下のシンボリックリンクは不活性と認めない", code == 1, why)
        check("そのときモードを言う", "120000" in why, why)

        repo = _fixture(root, "差分なし")
        code, why = judge(staged_changes(repo))
        check("差分が 0 件なら流す", code == 1, why)

        empty = root / "空"
        empty.mkdir()
        _git(root, "init", "-q", str(empty))
        code, why = judge(staged_changes(empty))
        check("HEAD が無いときは流す（判定できない）", code == 2, why)
        check("git が動かないときも流す", judge(None)[0] == 2)

        # --- 隔離（外側の git の環境が漏れても、判定も検体も壊れない）---
        # **git は `pre-commit` に `GIT_INDEX_FILE` を渡す**（`git commit -a` では絶対パス）。
        # **落とさずに使い捨てのリポジトリで git を撃つと、本番の索引を壊す**（2026-09-22 に実測）
        victim = _fixture(root, "隔離の犠牲")
        before_refs = subprocess.run(["git", "for-each-ref"], cwd=victim, capture_output=True,
                                     text=True, env=clean_env()).stdout
        before_index = (victim / ".git" / "index").read_bytes()
        dirty = {"GIT_DIR": str(victim / ".git"), "GIT_INDEX_FILE": str(victim / ".git" / "index")}
        os.environ.update(dirty)
        try:
            repo = _fixture(root, "隔離")
            _stage_file(repo, "docs/始め.md", "外側が汚れていても書ける\n")
            code, why = judge(staged_changes(repo))
            check("外側の GIT_DIR が漏れても判定は変わらない", code == 0, why)
            after_refs = subprocess.run(["git", "for-each-ref"], cwd=victim, capture_output=True,
                                        text=True, env=clean_env()).stdout
            check("外側のリポジトリを壊さない",
                  after_refs == before_refs and (victim / ".git" / "index").read_bytes() == before_index)
        finally:
            for k in dirty:
                os.environ.pop(k, None)

        # --- 一覧の鮮度（C# が読む文書が増えたら赤くする）---
        referenced = _docs_referenced_by_sources()
        check("C# が読む文書は DOCS_READ_BY_TESTS と一致する", referenced == DOCS_READ_BY_TESTS,
              f"C# 側: {sorted(referenced)}\n一覧  : {sorted(DOCS_READ_BY_TESTS)}")

        # --- 段の配線（切り株を置いて、本当に流して見る）---
        out, called = _run_stage(root, 0)
        check("飛ばす回は dotnet を 1 度も呼ばない", called == [], f"{called}\n{out}")
        check("そのとき飛ばした理由を印字する", "この段は流さない" in out, out)

        out, called = _run_stage(root, 1)
        want = ["BusinessApp.AccountingCore.csproj", "BusinessApp.AccountingCore.Server.csproj",
                "BusinessApp.Partners.csproj", "BusinessApp.Partners.Server.csproj",
                "BusinessApp.ServerSupport.csproj"]
        got = [line.split("--project ")[1].split()[0] for line in called if "--project " in line]
        check("流す回は 5 つのプロジェクトすべてに dotnet stryker を撃つ",
              got == want and all(c.startswith("stryker ") for c in called),
              f"呼ばれたもの: {called}\n{out}")

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
    code, why = judge(staged_changes(REPO_ROOT))
    print(f"[docs_only] {why}")
    return code


if __name__ == "__main__":
    sys.exit(main())
