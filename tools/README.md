---
title: tools — 開発スクリプト
status: current
scope: 全体
audience: [開発]
updated: 2026-08-25
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
| [`server/wait-server.ps1`](server/wait-server.ps1) | 開発サーバ（`http://localhost:5085`）の起動を待つ |
| [`clb/sql.ps1`](clb/sql.ps1) | `sql` CLI のラッパ。結果 JSON を標準出力に返し、**一時ファイルを作らない** |
| [`clb/migrate.ps1`](clb/migrate.ps1) | **DB マイグレーションのランナー**（ADR-0020）。`-Adopt` / `-Apply` / `-Status` / `-Verify`。書き方は [`Designer/migrations/README`](../Designer/migrations/README.md) |
| [`clb/designcheck.ps1`](clb/designcheck.ps1) | `designcheck` のラッパ。結果は固定パスに上書きし続ける |
| [`clb/lint_design.py`](clb/lint_design.py) | **CLB デザインの静的検査**。`designcheck` が緑でも壊れるもの（[qa/01](../docs/qa/01_CLB静かな失敗.md)）のうち JSON とスクリプトで判るものを検出する |
| [`clb/scaffold_module.py`](clb/scaffold_module.py) | モジュール定義の足場作り。生成後は `Design/Modules/*.mod.json` が正典 |
| [`git-hooks/pre-commit`](git-hooks/pre-commit) | コミット前の検証。`git config core.hooksPath tools/git-hooks` で有効にする |
| [`docs/lint_docs.py`](docs/lint_docs.py) | **ドキュメント規約の検査**（[docs/00 §6](../docs/00_ドキュメント規約.md)）。フロントマター・リンク切れ・索引の突合・**current でない文書へのコード参照** |
| [`docs/lint_secrets.py`](docs/lint_secrets.py) | **公開リポジトリ向けの混入検査**。追跡ファイルに絶対パス・ユーザー名・接続文字列・API キー・秘密鍵が無いかを検査する |
| [`docs/lint_secrets_allow.txt`](docs/lint_secrets_allow.txt) | 上記の誤検知抑制リスト |

## PDF のテキスト抽出（制度リサーチで使う）

国税庁などの一次情報は PDF が多い。**リポジトリに仮想環境は作らず**、uv の一時環境で読む
（パッケージは uv のキャッシュに入り、グローバル環境を汚さない。開発者の方針 2026-08-25）:

```powershell
# 例: 抽出スクリプトを書いて uv で走らせる（pypdf は実行のたびに一時環境へ解決される）
uv run --with pypdf python <スクリプト.py> <対象.pdf> <出力.txt> <開始ページ> <終了ページ>
```

抽出スクリプト自体は使い捨てなのでスクラッチパッドに置く（[CLAUDE.md](../CLAUDE.md) §3-2-2）。
Python の依存が恒常的に増えてきたら、そのとき `pyproject.toml` ＋ uv 管理の venv へ移行を検討する。

## よく使うコマンド

```powershell
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

上の 4 つ（`lint_secrets` / `lint_docs` / `lint_design` / `dotnet test`）は
**コミット前フックが自動で流す**。有効にするのは clone 後の 1 回だけ。

```powershell
git config core.hooksPath tools/git-hooks
```

`*.mod.cs`（CLB スクリプト）を変更した場合と DB スキーマを変更した場合は、
deploy だけでは反映されない。**サーバの再起動が必要**。
