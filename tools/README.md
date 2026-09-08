---
title: tools — 開発スクリプト
status: current
scope: 全体
audience: [開発]
updated: 2026-09-08
supersedes: []
related: [../docs/README.md]
---
# tools — 開発スクリプト

リポジトリの開発・検証に使う自作スクリプト置き場。**すべて Git 追跡下**なので、
絶対パス・ユーザー名・秘密情報を書かないこと（パスは `$PSScriptRoot` からの相対で解決する）。

> 依存パッケージ（Playwright の `npm install` 等）はここではなく `Designer/tools/`（Git 追跡外）に置く。
> これは CLB デザインワークスペースの規約（`Designer/CLAUDE.md`）に従うため。

## 一覧

| スクリプト | 用途 |
|---|---|
| [`clb/deploy.ps1`](clb/deploy.ps1) | `Designer/Design` を zip 化して `LocalData/designs/App.zip` に配置する（デザイナ GUI「送信」の代替。FileWatcher が hot-reload） |
| [`claude/trash.ps1`](claude/trash.ps1) | **ファイル・フォルダをごみ箱へ送る。`rm` の代わりに使う唯一の削除コマンド**（[ADR-0044](../docs/decisions/0044-削除はごみ箱送りに一本化しrmを機械で止める.md)・[30 §10](../docs/30_作業のルール.md)）。複数指定・ワイルドカード・`-DryRun` に対応する。**絶対パスへ解決してから保護対象を拒む**。`-SelfTest` で保護判定を検査する（コミット前フックが毎回流す） |
| [`claude/guard_delete.py`](claude/guard_delete.py) | **失うことを止める** PreToolUse フック。**削除にあたるコマンドは当たり先によらず拒み、代わりに `trash.ps1` を使えと理由文で示す**。**保護対象への `Write`（全上書き）も拒む**（`Edit` は照合があるので拒まない）。`--selftest` で仕様表を検査する（コミット前フックが毎回流す） |
| [`claude/protected_paths.json`](claude/protected_paths.json) | **削除と上書きから守るものの正典。** 上の 2 つが同じこの 1 ファイルを読む（**載せる基準と読み方はファイル冒頭の `_README`** が持つ） |
| [`clb/db_snapshot.ps1`](clb/db_snapshot.ps1) | **稼働 DB の退避と復元**（[ADR-0046](../docs/decisions/0046-稼働DBの退避と復元を戻せる道具に閉じる.md)・[30 §10](../docs/30_作業のルール.md)）。`-Save` / `-Restore` / `-List`。**写しは `VACUUM INTO` で取る**（ファイルの複製では直前の更新が欠ける）。**何も消さず、戻す前に必ず現状を退避する**ので、`trash.ps1` と同じく確認を待たずに実行してよい |
| [`server/wait-server.ps1`](server/wait-server.ps1) | 開発サーバ（`http://localhost:5085`）の起動を待つ |
| [`clb/sql.ps1`](clb/sql.ps1) | `sql` CLI のラッパ。結果 JSON を標準出力に返し、**一時ファイルを作らない** |
| [`clb/migrate.ps1`](clb/migrate.ps1) | **DB マイグレーションのランナー**（ADR-0020）。`-Adopt` / `-Apply` / `-Status` / `-Verify`。書き方は [`Designer/migrations/README`](../Designer/migrations/README.md) |
| [`clb/designcheck.ps1`](clb/designcheck.ps1) | `designcheck` のラッパ。結果は固定パスに上書きし続ける |
| [`clb/lint_design.py`](clb/lint_design.py) | **CLB デザインの静的検査**。`designcheck` が緑でも壊れるもの（[qa/01](../docs/qa/01_CLB静かな失敗.md)）のうち JSON とスクリプトで判るものを検出する。`--selftest` で**検査そのものを検査する**（関門を殺す・error を warn に格下げする・`main()` の配線を消す・検体を空にする・**言うべき直し方を薄める**、の 5 通りで鳴ることを確かめてある） |
| [`clb/scaffold_module.py`](clb/scaffold_module.py) | モジュール定義の足場作り。生成後は `Design/Modules/*.mod.json` が正典 |
| [`git-hooks/pre-commit`](git-hooks/pre-commit) | コミット前の検証。`git config core.hooksPath tools/git-hooks` で有効にする |
| [`docs/lint_docs.py`](docs/lint_docs.py) | **ドキュメント規約の検査**（[docs/00 §6](../docs/00_ドキュメント規約/README.md)）。フロントマター・リンク切れ・索引の突合・**current でない文書へのコード参照**・**`current` の本文から `superseded` へのリンク**・**節への参照の指し先に節が実在するか**・**`updated:` の鮮度**（作業ツリーと履歴の両方）・**条項を [80 §3](../docs/80_参照法令一覧.md) の記法で書いているか**・**日付で発効する条番号の切替が残っていないか**（30 日前までは件数を印字するだけ、30 日前から warn、発効日以後は error。[ADR-0043](../docs/decisions/0043-日付で発効する条番号の切替を機械の関門に置き除外は行の印で表す.md)）。`--selftest` で検査そのものを検査する |
| [`docs/doclint/`](docs/doclint/__init__.py) | 上の中身。`model.py`（設定値・`Doc`・git・読み込み）／`checks.py`（検査の本数は数えない。**正典は `ALL_CHECKS`** で、`selftest.py` が突合する）／`selftest.py`（関門の検査）。**入口は `lint_docs.py` のまま** |
| [`docs/lint_secrets.py`](docs/lint_secrets.py) | **公開リポジトリ向けの混入検査**。追跡ファイルに絶対パス・ユーザー名・接続文字列・API キー・秘密鍵が無いかを検査する |
| [`docs/lint_secrets_allow.txt`](docs/lint_secrets_allow.txt) | 上記の誤検知抑制リスト |

## PDF のテキスト抽出（制度リサーチで使う）

国税庁などの一次情報は PDF が多い。**リポジトリに仮想環境は作らず**、uv の一時環境で読む
（パッケージは uv のキャッシュに入り、グローバル環境を汚さない。開発者の方針 2026-08-25）:

```powershell
# 例: 抽出スクリプトを書いて uv で走らせる（pypdf は実行のたびに一時環境へ解決される）
uv run --with pypdf python <スクリプト.py> <対象.pdf> <出力.txt> <開始ページ> <終了ページ>
```

抽出スクリプト自体は使い捨てなのでスクラッチパッドに置く（[30 §8](../docs/30_作業のルール.md)）。
Python の依存が恒常的に増えてきたら、そのとき `pyproject.toml` ＋ uv 管理の venv へ移行を検討する。

## よく使うコマンド

```powershell
# 消す（ごみ箱へ送る。rm は使わない。docs/30 §10）
pwsh -NoProfile -File tools/claude/trash.ps1 <パス> [<パス> ...]
pwsh -NoProfile -File tools/claude/trash.ps1 -DryRun <パス>

# デザインを稼働サーバへ反映（designcheck を通してから実行する）
pwsh -NoProfile -File tools/clb/deploy.ps1

# サーバ起動（別ターミナル）
dotnet run --project BusinessApp/BusinessApp.Server --launch-profile http

# 起動待ち
pwsh -NoProfile -File tools/server/wait-server.ps1 -TimeoutSec 60

# 公開前チェック（コミット前に流す）
python tools/docs/lint_secrets.py
python tools/docs/lint_secrets.py --staged

# CLB デザインの検査（designcheck の後に流す）
pwsh -NoProfile -File tools/clb/designcheck.ps1
python tools/clb/lint_design.py

# DB を触る（一時ファイルを作らない）
pwsh -NoProfile -File tools/clb/sql.ps1 -Query "SELECT COUNT(*) FROM accounts;"
pwsh -NoProfile -File tools/clb/sql.ps1 -File Designer/ddl/005_journals.sql
```

**コミット前フックが 8 段を自動で流す**（`tools/git-hooks/pre-commit`。段の正典はこの表）。

| 段 | 中身 |
|---|---|
| 1 | `check_frozen.py`（**凍結されたファイルの変更・削除・改名**。適用済みマイグレーションと `baseline/`。[ADR-0020](../docs/decisions/0020-スキーマは現在形の正典で持ち変更は差分で配る.md)） |
| 2 | 失うことを止める道具 3 つの自己検査（`guard_delete.py --selftest`・`trash.ps1 -SelfTest`・`db_snapshot.ps1 -SelfTest`。**前 2 つの正典は 1 つ**なので、両方がそれを読めているかもここで確かめる） |
| 3 | `lint_secrets.py`（秘密・絶対パスの混入） |
| 4 | `lint_docs.py --selftest` → `lint_docs.py`（ドキュメント規約） |
| 5 | `lint_design.py`（CLB デザインの静的検査） |
| 6 | `dotnet test`（テスト・カバレッジ・スキーマ） |
| 7 | `migrate.ps1 -Verify`（稼働 DB とスキーマ正典の同値。[ADR-0020](../docs/decisions/0020-スキーマは現在形の正典で持ち変更は差分で配る.md)） |
| 8 | `dotnet stryker`（ミューテーション。**5 プロジェクト**——会計コアの純粋層とサーバ層、取引先部品の純粋層とサーバ層、共有インフラ。[ADR-0012 §8](../docs/decisions/0012-テスト方針とカバレッジのゲート.md)・[ADR-0025 §6](../docs/decisions/0025-取引先を部品として分ける.md)） |

**マージが自動でコミットするときは `pre-merge-commit` から同じ 8 段へ委譲する**——
git はマージで `pre-commit` を呼ばないので、置かないと **`main` に入る瞬間だけ誰も見ていない**。

有効にするのは clone 後の 1 回だけ。

```powershell
git config core.hooksPath tools/git-hooks
```

`*.mod.cs`（CLB スクリプト）を変更した場合と DB スキーマを変更した場合は、
deploy だけでは反映されない。**サーバの再起動が必要**。
