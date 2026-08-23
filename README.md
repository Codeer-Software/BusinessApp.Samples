---
title: BusinessApp — CLB 製 会計コア
status: current
scope: 全体
audience: [開発]
updated: 2026-08-23
supersedes: []
related: [docs/README.md]
---
# BusinessApp — CLB 製 会計コア

ASP.NET Core ＋ [Codeer.LowCode.Blazor](https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Manual)（CLB）
で構築する、**財務会計の「会計コア」部品**。

「取引を仕訳として記録し、法定帳簿と決算書に変換し、消費税額を計算し、法定期間保存する」までを担う。
経費精算・販売・購買・証憑管理・承認フローは**別部品**であり、本リポジトリでは扱わない。

- 複式簿記の仕訳・仕訳帳・総勘定元帳・補助簿・試算表・B/S・P/L
- 消費税（税抜経理・個別対応方式・インボイスの経過措置・税率ごとの端数処理）
- 優良な電子帳簿の要件（訂正削除履歴・帳簿間の相互関連性・検索）
- 固定資産台帳と減価償却、月次締め・年度繰越

企画意図・スコープ・設計は [`docs/`](docs/README.md) を参照。

## 技術スタック

| 項目 | 内容 |
|---|---|
| ランタイム | .NET 8 / ASP.NET Core + Blazor WebAssembly（CLB 1.3.20、Extras 0.5.0） |
| DB | SQLite（`LocalData/db/`・Git 追跡外） |
| 認証 | Cookie 認証 |
| 会計ロジック | **C# ライブラリ**（`BusinessApp.AccountingCore`）。CLB は薄いプレゼンテーション層 |

## リポジトリ構成

```
BusinessApp.slnx            ソリューション
BusinessApp/                ランタイム（Server / Client / Client.Shared / Designer(CLI) / LicenseRegisterCli）
Designer/                   CLB デザインワークスペース
  Design/                     デザインプロジェクト本体（Modules / PageFrames / Enums / Resources）
  ddl/                        DB スキーマ + seed（番号順に適用）
LocalData/                  実行時データ（DB・デザイン zip・添付）→ LocalData/README.md
tools/                      開発スクリプト（デプロイ・検査）→ tools/README.md
docs/                       企画・仕様・設計・ADR・リサーチ → docs/README.md
CLAUDE.md                   Claude Code 向けミッションブリーフ
```

## セットアップ（新環境）

前提: Windows / .NET 8 SDK。デザイン編集まで行う場合は CLB デザイナ 1.3.20 も必要。

1. **環境固有設定の作成** — 次の 3 つは環境ごとに内容が異なるため Git 追跡外にしてある。
   新しい環境では手で用意する
   - `BusinessApp/BusinessApp.Server/appsettings.Development.json` — DB 接続文字列、
     デザイン zip の配置先、ファイルストレージの場所（雛形は未整備）
   - `Designer/Design/designer.settings.Development.json` — デザイナ CLI の接続先（雛形は未整備）
   - `.claude/settings.local.json` — Claude Code を使う場合のみ。
     雛形の `.claude/settings.local.json.sample` をコピーしてプレースホルダを置換する
2. **DB 構築** — `Designer/ddl/*.sql` を番号順に適用する
3. **デザインのデプロイ** — `pwsh -NoProfile -File tools/clb/deploy.ps1`
4. **起動**

   ```powershell
   dotnet run --project BusinessApp/BusinessApp.Server --launch-profile http
   ```

   → http://localhost:5085 を開き `admin` / `admin` でログインする。

API キー等の秘密は .NET User Secrets に置く。設定ファイルにもドキュメントにも書かない。

## 開発の流れ

- 画面は `Designer/Design/` の CLB デザイン（`*.mod.json`）で作る。
  **会計の計算・検証・集計は C# ライブラリに置く**（CLB スクリプトに書かない）
- 検証は `designcheck`（デザイン妥当性）→ `dotnet test`（会計ロジック）→ `sql`（DB 整合）→
  `deploy.ps1` で反映してブラウザ実機確認、の順
- デザイン反映は hot-reload されるが、**`*.mod.cs`・スキーマ変更時はサーバ再起動が必要**
- コミット前に `python tools/docs/lint_secrets.py` を流す（公開リポジトリのため）
- 詳細な運用ルールは [`CLAUDE.md`](CLAUDE.md)・`Designer/CLAUDE.md`・`Designer/Project.md`

## ドキュメント索引

| 場所 | 内容 |
|---|---|
| [`docs/README.md`](docs/README.md) | 企画・ペルソナ・機能スコープ・会計ドメイン設計・実装計画の索引 |
| [`docs/decisions/`](docs/decisions/README.md) | 意思決定ログ（ADR） |
| [`docs/research/`](docs/research/) | 制度リサーチ（出典 URL と確認日つき） |
| [`Designer/Project.md`](Designer/Project.md) | CLB デザイン固有ルール（DB・命名・レイアウト・デプロイ） |
| [`CLAUDE.md`](CLAUDE.md) | Claude Code 自律構築のミッションブリーフ |
