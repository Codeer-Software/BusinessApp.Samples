---
title: tools — 開発スクリプト
status: current
scope: 全体
audience: [開発]
updated: 2026-08-23
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
| [`docs/lint_secrets.py`](docs/lint_secrets.py) | **公開リポジトリ向けの混入検査**。追跡ファイルに絶対パス・ユーザー名・接続文字列・API キー・秘密鍵が無いかを検査する |
| [`docs/lint_secrets_allow.txt`](docs/lint_secrets_allow.txt) | 上記の誤検知抑制リスト |

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
```

`*.mod.cs`（CLB スクリプト）を変更した場合と DB スキーマを変更した場合は、
deploy だけでは反映されない。**サーバの再起動が必要**。
