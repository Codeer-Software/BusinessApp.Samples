#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""pre_merge_commit_selftest.py — `pre-merge-commit` の判定を、**本物の `git merge` に呼ばせて**確かめる.

**フックを呼ぶのは git である。だから検体でも git に呼ばせる**——手で状態を作って直接呼ぶ検体は、
**本番には存在しない状態**を検査していることがある（[qa/03 の L-63](../../docs/qa/03_テストで漏らした実例.md)。
初版はそれで「一度も効いていない」を言わなかった）。

**結果まで見る。** 印字だけを見ると、**字を残したまま到達不能にする書き換え**——
`migrate.ps1 -Verify` を流す行の手前に `exit` を置く、`pre-commit` への委譲を殺す——が素通りする。
**そこで、稼働 DB の同値検査（`pwsh`）と、全段への委譲（`pre-commit`）に切り株を置き、
`PRE_MERGE_COMMIT_EXPLAIN` を付けずに本物のマージを流して、切り株が呼ばれたかを見る。**

**撃った検体は「撃とうとした名前」ではなく「見て通った表明」で数える**——
名前だけを積むと、**中身を別物に差し替えても一覧が変わらない**。

使い方:
    python tools/git-hooks/pre_merge_commit_selftest.py                # 全検体
    python tools/git-hooks/pre_merge_commit_selftest.py -v             # 表明を 1 行ずつ印字する
    python tools/git-hooks/pre_merge_commit_selftest.py --hook <写し>  # 壊した写しを撃つ（31 §1 の「複製を壊す」）

終了コード: 0 = 全部緑／1 = 失敗あり
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

HOOK = Path(__file__).resolve().parent / "pre-merge-commit"
REPO_ROOT = HOOK.parent.parent.parent

# **軽い経路が流す段。** フックの `verify_stage` と同じ字を、**こちらの手で書く**
# ——フックの変数から導くと、フックを書き換えたときに期待値も一緒に動いて釣り合ってしまう。
VERIFY_ARGS = ["-NoProfile", "-File", "tools/clb/migrate.ps1", "-Verify"]

IDENT = ["-c", "user.name=selftest", "-c", "user.email=selftest@example.com"]


class Failure(Exception):
    pass


class Report:
    """**通った表明だけを積む。** 最後に、期待する一覧とそっくり突き合わせる。"""

    def __init__(self, verbose: bool, quiet: bool = False) -> None:
        self.passed: list[str] = []
        self.failures: list[str] = []
        self.verbose = verbose
        self.quiet = quiet  # 対照実験の内側で使う——**わざと壊した検体の NG は印字しない**

    def ok(self, case: str, claim: str) -> None:
        self.passed.append(f"{case}／{claim}")
        if self.verbose:
            print(f"  緑 {case}／{claim}")

    def ng(self, case: str, claim: str, detail: str = "") -> None:
        self.failures.append(f"{case}／{claim}")
        if self.quiet:
            return
        print(f"  NG {case}／{claim}")
        for line in detail.splitlines():
            print(f"     {line}")

    def check(self, case: str, claim: str, cond: bool, detail: str = "") -> bool:
        if cond:
            self.ok(case, claim)
        else:
            self.ng(case, claim, detail)
        return cond


def clean_env(extra: dict[str, str] | None = None) -> dict[str, str]:
    """**git のローカル環境変数と `GITHEAD_*` を落とした環境。**

    `githooks(5)` が「他のリポジトリで git を呼ぶなら消せ」と明記しているもの
    （消さないと、リンクワークツリーから呼ばれたとき `GIT_DIR` が絶対パスで渡り、
    **使い捨てのはずの git が本番のリポジトリに向く**）と、
    **外側のマージが渡す `GITHEAD_<sha>`**（残すと、内側の検体が外側のぶんも数える）を落とす。
    """
    names = subprocess.run(
        ["git", "rev-parse", "--local-env-vars"],
        capture_output=True, text=True, check=True,
    ).stdout.split()
    env = {k: v for k, v in os.environ.items()
           if k not in names and not k.startswith("GITHEAD_")}
    env.pop("PRE_MERGE_COMMIT_SELFTEST", None)
    env.pop("PRE_MERGE_COMMIT_EXPLAIN", None)
    if extra:
        env.update(extra)
    return env


class Lab:
    """使い捨ての作業場。フックの写し・切り株・リポジトリを置く。"""

    def __init__(self, root: Path, hook: Path) -> None:
        self.root = root
        self.hook = hook
        self.marks = root / "marks"
        self.marks.mkdir()
        self.bin = root / "bin"
        self.bin.mkdir()
        self.hooks = root / "hooks"
        self.hooks.mkdir()
        self.n = 0

        # 稼働 DB の同値検査の切り株。**引数をそのまま書き出す**
        self._stub(self.bin / "pwsh", f'printf "%s\\n" "$@" > "{self.marks.as_posix()}/pwsh.args"\n')
        # 締めくくりのコミットを受けるフック（core.hooksPath 側）
        self._stub(self.hooks / "pre-commit", f'touch "{self.marks.as_posix()}/pre-commit.hook"\n')
        self.install_hook()

    def _stub(self, path: Path, body: str) -> None:
        path.write_text("#!/bin/sh\n" + body, encoding="utf-8", newline="\n")
        path.chmod(0o755)

    def install_hook(self, mutate=None) -> None:
        """フックの写しを `core.hooksPath` に置く。`mutate` があれば中身を 1 か所壊す。"""
        text = self.hook.read_text(encoding="utf-8")
        if mutate is not None:
            text = mutate(text)
        target = self.hooks / "pre-merge-commit"
        target.write_text(text, encoding="utf-8", newline="\n")
        target.chmod(0o755)

    def env(self, extra: dict[str, str] | None = None) -> dict[str, str]:
        e = clean_env(extra)
        e["PATH"] = str(self.bin) + os.pathsep + e.get("PATH", "")
        return e

    def mark(self, name: str) -> Path:
        return self.marks / name

    def clear_marks(self) -> None:
        for p in self.marks.iterdir():
            p.unlink()

    def new_repo(self, name: str) -> "Repo":
        self.n += 1
        return Repo(self, self.root / f"{self.n:02d}-{name}")


class Repo:
    def __init__(self, lab: Lab, path: Path) -> None:
        self.lab = lab
        self.path = path
        path.mkdir(parents=True)
        self.git("init", "-q", ".")
        self.git("config", "core.hooksPath", str(lab.hooks))
        # **全段へ倒れたときの委譲先。** フックは `$repo_root/tools/git-hooks/pre-commit` を exec する
        stub_dir = path / "tools" / "git-hooks"
        stub_dir.mkdir(parents=True)
        lab._stub(stub_dir / "pre-commit", f'touch "{lab.marks.as_posix()}/exec-delegate"\n')
        self.write("f", "base")
        self.git("add", "f")
        self.commit("最初のコミット")
        self.main = self.git("symbolic-ref", "--short", "HEAD").stdout.strip()

    # --- git を打つ ---

    def git(self, *args: str, check: bool = True, env_extra: dict[str, str] | None = None):
        return subprocess.run(
            ["git", *IDENT, *args],
            cwd=self.path, capture_output=True, text=True, encoding="utf-8",
            errors="replace", env=self.lab.env(env_extra), check=check,
        )

    def out(self, *args: str) -> str:
        return self.git(*args).stdout.strip()

    def commit(self, message: str) -> None:
        self.git("commit", "-q", "-m", message)

    def write(self, name: str, body: str) -> None:
        (self.path / name).write_text(body + "\n", encoding="utf-8", newline="\n")

    def branch_with(self, branch: str, name: str, body: str = "x") -> None:
        self.git("checkout", "-q", "-b", branch)
        self.write(name, body)
        self.git("add", name)
        self.commit(branch)
        self.git("checkout", "-q", self.main)

    def merge(self, *args: str, explain: bool = False, env_extra: dict[str, str] | None = None):
        extra = dict(env_extra or {})
        if explain:
            extra["PRE_MERGE_COMMIT_EXPLAIN"] = "1"
        r = self.git("merge", "--no-ff", "-m", "merge", *args, check=False, env_extra=extra)
        return r

    # --- 見るもの ---

    def head(self) -> str:
        return self.out("rev-parse", "HEAD")

    def parents(self) -> list[str]:
        return self.out("rev-list", "--parents", "-n", "1", "HEAD").split()[1:]


def text_of(result) -> str:
    return (result.stdout or "") + (result.stderr or "")


# ---------------------------------------------------------------------------
# 検体
# ---------------------------------------------------------------------------


def case_explain(rep: Report, lab: Lab, case: str, build, merge_args: list[str],
                 why: str, path_word: str, extra_claims=None) -> None:
    """EXPLAIN で撃ち、**理由の字・経路の字・マージが成立しないこと**を見る。

    **理由まで見る**——経路だけ見ると、**別の理由で全段へ落ちていても緑になる**（初版の壊れ方）。
    """
    repo = lab.new_repo(case)
    build(repo)
    before = repo.head()
    r = repo.merge(*merge_args, explain=True)
    out = text_of(r)
    rep.check(case, f"理由「{why}」を印字する", why in out, out)
    rep.check(case, f"経路「{path_word}」へ倒れる", f"EXPLAIN: {path_word}" in out, out)
    rep.check(case, "EXPLAIN はマージを成立させない", repo.head() == before, out)
    if extra_claims:
        extra_claims(rep, case, repo, out)


def case_light_runs_verify(rep: Report, lab: Lab) -> None:
    """**軽い経路が、稼働 DB の同値検査を本当に流すか。**

    ADR-0067 が軽い経路に残した唯一の段である。**印字だけを見ると、
    行を残したまま到達不能にする書き換え**（直前に `exit` を置く等）**が素通りする。**
    """
    case = "軽い経路が同値検査を流す"
    repo = lab.new_repo(case)
    repo.branch_with("topic", "g")
    lab.clear_marks()
    r = repo.merge("topic")
    out = text_of(r)
    rep.check(case, "マージが成立する", r.returncode == 0, out)
    rep.check(case, "2 つの親を持つ", len(repo.parents()) == 2, str(repo.parents()))
    args_file = lab.mark("pwsh.args")
    ran = args_file.read_text(encoding="utf-8").split() if args_file.exists() else []
    rep.check(case, f"同値検査を {' '.join(VERIFY_ARGS)} で流す", ran == VERIFY_ARGS, f"渡ったもの: {ran}")
    rep.check(case, "全段へは委譲しない", not lab.mark("exec-delegate").exists())


def case_full_delegates(rep: Report, lab: Lab) -> None:
    """**全段へ倒れたとき、`pre-commit` へ本当に委譲するか。** これが消えると、
    「main に入る瞬間だけ誰も見ていない」という、このフックを作った動機そのものが戻る。"""
    case = "全段が pre-commit へ委譲する"
    repo = lab.new_repo(case)
    repo.branch_with("topic", "g")
    repo.write("h", "main が先へ進む")
    repo.git("add", "h")
    repo.commit("main を進める")
    lab.clear_marks()
    r = repo.merge("topic")
    out = text_of(r)
    rep.check(case, "委譲先の pre-commit が呼ばれる", lab.mark("exec-delegate").exists(), out)
    rep.check(case, "同値検査は流さない（軽い経路に入っていない）", not lab.mark("pwsh.args").exists(), out)
    rep.check(case, "マージが成立する", r.returncode == 0, out)


def case_conflict(rep: Report, lab: Lab) -> None:
    """**衝突したマージは、このフックを通らない。** 締めくくりの `git commit` を `pre-commit` が受ける
    ——「`MERGE_HEAD` がある経路はすべて全段」という安全論の要である。"""
    case = "衝突"
    repo = lab.new_repo(case)
    repo.git("checkout", "-q", "-b", "topic")
    repo.write("f", "あちら")
    repo.git("commit", "-q", "-am", "topic")
    repo.git("checkout", "-q", repo.main)
    repo.write("f", "こちら")
    repo.git("commit", "-q", "-am", "main")
    lab.clear_marks()
    r = repo.merge("topic")
    out = text_of(r)
    rep.check(case, "衝突してマージが止まる", r.returncode != 0, out)
    git_dir = Path(repo.out("rev-parse", "--absolute-git-dir"))
    rep.check(case, "MERGE_HEAD が書かれる", (git_dir / "MERGE_HEAD").exists())
    rep.check(case, "未解決の項目が残る", repo.out("ls-files", "-u") != "")
    rep.check(case, "pre-merge-commit は呼ばれない", "[pre-merge-commit]" not in out, out)
    repo.write("f", "解決した")
    repo.git("add", "f")
    lab.clear_marks()
    repo.commit("解決")
    rep.check(case, "締めくくりのコミットを pre-commit が受ける", lab.mark("pre-commit.hook").exists())
    rep.check(case, "締めくくりは 2 つの親を持つマージである", len(repo.parents()) == 2, str(repo.parents()))


def case_direct(rep: Report, lab: Lab, case: str, env_extra, why: str, rc: int,
                path_word: str = "全段") -> None:
    """**git には作れない場合だけ、写しを直接呼ぶ。**

    マージの外・先端の木を引けない・木を書けない——どれも `git merge` では作れない。
    ここは終了コードまで見る（`git merge` 経由では git が自分の 1 を返すので見えない）。
    """
    repo = lab.new_repo(case)
    extra = env_extra(repo) if callable(env_extra) else env_extra
    env = lab.env({**extra, "PRE_MERGE_COMMIT_EXPLAIN": "1"})
    r = subprocess.run(
        ["sh", str(lab.hooks / "pre-merge-commit")],
        cwd=repo.path, capture_output=True, text=True, encoding="utf-8",
        errors="replace", env=env, check=False,
    )
    out = text_of(r)
    rep.check(case, f"理由「{why}」を印字する", why in out, out)
    rep.check(case, f"経路「{path_word}」へ倒れる", f"EXPLAIN: {path_word}" in out, out)
    rep.check(case, f"終了コードが {rc}", r.returncode == rc, f"{r.returncode}\n{out}")


def build_same(repo: Repo) -> None:
    repo.branch_with("topic", "g")


def build_ahead(repo: Repo) -> None:
    repo.branch_with("topic", "g")
    repo.write("h", "main が先へ進む")
    repo.git("add", "h")
    repo.commit("main を進める")


def build_octopus(repo: Repo) -> None:
    repo.branch_with("t1", "g")
    repo.branch_with("t2", "h")


def claim_prints_verify_stage(rep: Report, case: str, repo: Repo, out: str) -> None:
    # **札まで含めて見る**——`飛ばした段:` の行にも同じ字が出るので、字だけを探すと
    # 「流す段」の行を消しても緑になる
    rep.check(case, "軽い経路で流す段を札つきで印字する",
              "軽い経路で流す段: tools/clb/migrate.ps1 -Verify" in out, out)


def run_cases(rep: Report, lab: Lab) -> None:
    case_explain(rep, lab, "木が同一", build_same, ["topic"],
                 "木がブランチ先端と同一", "軽い経路", claim_prints_verify_stage)
    case_light_runs_verify(rep, lab)

    case_explain(rep, lab, "main が先へ進んでいた合流", build_ahead, ["topic"],
                 "木がブランチ先端と異なる", "全段")
    case_full_delegates(rep, lab)

    case_explain(rep, lab, "オクトパス", build_octopus, ["t1", "t2"],
                 "GITHEAD_* が 2 個", "全段")

    case_conflict(rep, lab)

    case_direct(rep, lab, "マージの外", {}, "GITHEAD_* が 0 個", 10)
    # **縮退させない**——全ゼロの sha を使うと、`GITHEAD_[0-9]` のような絞り込みでも拾えてしまう
    case_direct(rep, lab, "先端の木を引けない",
                {"GITHEAD_9f1c4d2ab73e58061f2d4c9ba87e30d5c6104fab": "なにもない"},
                "取り込む先端の木を引けない", 10)
    # **壊れた索引を本当に置く。** 無いファイルを指すと git は「空の索引」として扱い、
    # `git write-tree` が空の木で成功してしまう（最初に書いたときそれで素通りした）
    broken = lab.root / "broken-index"
    broken.write_bytes(b"this is not a git index")
    case_direct(rep, lab, "これからコミットする木を書けない",
                lambda repo: {f"GITHEAD_{repo.head()}": "先端は引ける",
                              "GIT_INDEX_FILE": str(broken)},
                "これからコミットする木を書けない", 10)

    # 軽い経路の終了コードは、`git merge` 経由では見えない（git が 1 を返す）ので直接呼ぶ
    repo = lab.new_repo("軽い経路の終了コード")
    case = "軽い経路の終了コード"
    env = lab.env({f"GITHEAD_{repo.head()}": "自分", "PRE_MERGE_COMMIT_EXPLAIN": "1"})
    r = subprocess.run(["sh", str(lab.hooks / "pre-merge-commit")], cwd=repo.path,
                       capture_output=True, text=True, encoding="utf-8", errors="replace",
                       env=env, check=False)
    rep.check(case, "軽い経路へ入る", "EXPLAIN: 軽い経路" in text_of(r), text_of(r))
    rep.check(case, "終了コードが 11（0 を返すとマージが成立してしまう）",
              r.returncode == 11, f"{r.returncode}\n{text_of(r)}")


def case_isolation(rep: Report, lab: Lab) -> None:
    """**外側の環境が漏れても、判定が変わらないこと。**

    全段へ倒れたマージは `exec` で `pre-commit` を起こし、`pre-commit` はこの検査を流す。
    `exec` は環境を保つので、**外側のマージの `GITHEAD_*` と `GIT_DIR` がそのまま入ってくる。**
    """
    case = "隔離"
    victim = lab.new_repo("隔離の犠牲")
    before_head = victim.head()
    before_refs = victim.out("for-each-ref")

    dirty = {
        # 非縮退の sha（全ゼロだと、絞り込みを緩めた書き換えを見逃す）
        "GITHEAD_3b7ac09f5182de64a1c7b8093ef25d6a04117cde": "外側のマージ",
        "GIT_DIR": str(victim.path / ".git"),
        "GIT_INDEX_FILE": str(victim.path / ".git" / "index"),
    }
    os.environ.update(dirty)
    try:
        repo = lab.new_repo("隔離")
        repo.branch_with("topic", "g")
        before = repo.head()
        r = repo.merge("topic", explain=True)
        out = text_of(r)
        rep.check(case, "外側の GITHEAD_* を数えない", "木がブランチ先端と同一" in out, out)
        rep.check(case, "外側の環境でも軽い経路へ入る", "EXPLAIN: 軽い経路" in out, out)
        rep.check(case, "EXPLAIN はマージを成立させない", repo.head() == before, out)
        rep.check(case, "外側の GIT_DIR のリポジトリを動かさない",
                  victim.head() == before_head and victim.out("for-each-ref") == before_refs,
                  f"{before_head} → {victim.head()}")
    finally:
        for k in dirty:
            os.environ.pop(k, None)


def case_control(rep: Report, lab: Lab) -> None:
    """**この検査が赤くなれることを、この検査自身が示す。**

    **壊していない写しが緑**なだけでは、表明が空でも緑である。
    **写しの判定を 1 か所壊し、「木が同一」が赤になること**まで見る。
    """
    case = "対照実験"
    lab.install_hook(lambda t: t.replace("s/^GITHEAD_", "s/^NOSUCHVAR_", 1))
    try:
        probe = Report(verbose=False, quiet=True)
        repo = lab.new_repo("対照実験")
        repo.branch_with("topic", "g")
        before = repo.head()
        r = repo.merge("topic", explain=True)
        out = text_of(r)
        probe.check(case, "理由", "木がブランチ先端と同一" in out)
        probe.check(case, "経路", "EXPLAIN: 軽い経路" in out)
        probe.check(case, "不動", repo.head() == before)
        rep.check(case, "先端の取得を壊すと「木が同一」が赤になる",
                  len(probe.failures) == 2, f"失敗 {len(probe.failures)} 件: {probe.failures}")
    finally:
        lab.install_hook()


# ---------------------------------------------------------------------------

# **通った表明の一覧が正典である。** 名前だけを積むと、中身を別物に差し替えても一覧が変わらない。
EXPECTED = [
    "木が同一／理由「木がブランチ先端と同一」を印字する",
    "木が同一／経路「軽い経路」へ倒れる",
    "木が同一／EXPLAIN はマージを成立させない",
    "木が同一／軽い経路で流す段を札つきで印字する",
    "軽い経路が同値検査を流す／マージが成立する",
    "軽い経路が同値検査を流す／2 つの親を持つ",
    "軽い経路が同値検査を流す／同値検査を -NoProfile -File tools/clb/migrate.ps1 -Verify で流す",
    "軽い経路が同値検査を流す／全段へは委譲しない",
    "main が先へ進んでいた合流／理由「木がブランチ先端と異なる」を印字する",
    "main が先へ進んでいた合流／経路「全段」へ倒れる",
    "main が先へ進んでいた合流／EXPLAIN はマージを成立させない",
    "全段が pre-commit へ委譲する／委譲先の pre-commit が呼ばれる",
    "全段が pre-commit へ委譲する／同値検査は流さない（軽い経路に入っていない）",
    "全段が pre-commit へ委譲する／マージが成立する",
    "オクトパス／理由「GITHEAD_* が 2 個」を印字する",
    "オクトパス／経路「全段」へ倒れる",
    "オクトパス／EXPLAIN はマージを成立させない",
    "衝突／衝突してマージが止まる",
    "衝突／MERGE_HEAD が書かれる",
    "衝突／未解決の項目が残る",
    "衝突／pre-merge-commit は呼ばれない",
    "衝突／締めくくりのコミットを pre-commit が受ける",
    "衝突／締めくくりは 2 つの親を持つマージである",
    "マージの外／理由「GITHEAD_* が 0 個」を印字する",
    "マージの外／経路「全段」へ倒れる",
    "マージの外／終了コードが 10",
    "先端の木を引けない／理由「取り込む先端の木を引けない」を印字する",
    "先端の木を引けない／経路「全段」へ倒れる",
    "先端の木を引けない／終了コードが 10",
    "これからコミットする木を書けない／理由「これからコミットする木を書けない」を印字する",
    "これからコミットする木を書けない／経路「全段」へ倒れる",
    "これからコミットする木を書けない／終了コードが 10",
    "軽い経路の終了コード／軽い経路へ入る",
    "軽い経路の終了コード／終了コードが 11（0 を返すとマージが成立してしまう）",
    "隔離／外側の GITHEAD_* を数えない",
    "隔離／外側の環境でも軽い経路へ入る",
    "隔離／EXPLAIN はマージを成立させない",
    "隔離／外側の GIT_DIR のリポジトリを動かさない",
    "対照実験／先端の取得を壊すと「木が同一」が赤になる",
]


def main() -> int:
    args = sys.argv[1:]
    verbose = "-v" in args
    hook = HOOK
    if "--hook" in args:
        hook = Path(args[args.index("--hook") + 1]).resolve()
    if not hook.exists():
        print(f"pre-merge-commit -SelfTest: フックが無い（{hook}）")
        return 1

    rep = Report(verbose)
    tmp = Path(tempfile.mkdtemp(prefix="pmc-selftest-"))
    try:
        lab = Lab(tmp, hook)
        run_cases(rep, lab)
        case_isolation(rep, lab)
        case_control(rep, lab)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    if rep.passed != EXPECTED:
        missing = [c for c in EXPECTED if c not in rep.passed]
        extra = [c for c in rep.passed if c not in EXPECTED]
        print("  NG 母数: 通った表明の一覧が期待と違う")
        for c in missing:
            print(f"     通らなかった/撃たれなかった: {c}")
        for c in extra:
            print(f"     一覧に無いものが通った: {c}")
        rep.failures.append("母数")

    if rep.failures:
        print(f"pre-merge-commit -SelfTest: {len(rep.failures)} 件失敗")
        return 1
    print(f"pre-merge-commit -SelfTest: OK（表明 {len(rep.passed)} 件がすべて期待どおり）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
