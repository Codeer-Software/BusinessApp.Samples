---
title: BusinessApp — CLB 製 財務会計アプリ（デモ）
status: current
scope: 全体
audience: [開発, 営業]
updated: 2026-08-16
supersedes: []
related: []
---
# BusinessApp — CLB 製 財務会計アプリ（デモ）

ASP.NET Core + [Codeer.LowCode.Blazor](https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Manual)（CLB）で構築した、中小 IT 受託企業向けの"全部入り"財務会計アプリ。

- 複式簿記の仕訳・元帳・試算表・財務諸表（B/S・P/L・C/F）・月次推移
- 販売（請求・入金・定期請求）／購買（買掛・支払）／経費精算・承認ワークフロー
- プロジェクト別損益・工数原価などの管理会計（IT 受託特化）
- AI 領収書 OCR・資金繰り予測・インボイス番号照合 ほか

企画意図・機能一覧・デモの流れは [`docs/`](docs/README.md) を参照（デモ台本: [`docs/09_デモ台本.md`](docs/09_デモ台本.md)）。

## 技術スタック

| 項目       | 内容                                                                          |
| ---------- | ----------------------------------------------------------------------------- |
| ランタイム | .NET 8 / ASP.NET Core + Blazor（CLB 1.3.18、Extras 0.5.0、ApexCharts 0.25.3） |
| DB         | SQLite（`LocalData/db/business-app_v1.db`・Git 追跡外）                         |
| 認証       | Cookie 認証                                                                   |
| AI 連携    | プロバイダ切替式（Mock／Claude／AzureOpenAI）。既定は Mock でキー不要         |

## リポジトリ構成

```
BusinessApp.slnx            ソリューション
BusinessApp/                ランタイム（Server / Client / Client.Shared / Designer(CLI) / LicenseRegisterCli）
Designer/                   CLB デザインワークスペース
  Design/                   デザインプロジェクト本体（Modules / PageFrames / Enums / Resources）
  ddl/                      DB スキーマ + seed（番号順に適用）
  tools/deploy.ps1          デザインを zip 化して稼働サーバへ反映（hot-reload）
LocalData/                  実行時データ（DB・デザイン zip・添付・フォント）→ LocalData/README.md
docs/                       企画・仕様・設計・ADR・テストシナリオ → docs/README.md
CLAUDE.md                   Claude Code 向けミッションブリーフ（自律構築の指示書）
```

## セットアップ（新環境）

前提: Windows / .NET 8 SDK。デザイン編集まで行う場合は CLB デザイナ 1.3.18 も必要。

1. **環境固有設定の生成** — 以下の `*.sample` を同名（`.sample` 抜き）でコピーし、中の `<REPO_ROOT>` を実パスに置換（経緯: `docs/decisions/0025`）
   - `BusinessApp/BusinessApp.Server/appsettings.Development.json.sample`
   - `Designer/Design/designer.settings.Development.json.sample`
   - `.claude/settings.local.json.sample`（Claude Code を使う場合のみ）
2. **DB 構築** — `Designer/ddl/*.sql` を番号順に適用（手順の詳細: [`LocalData/README.md`](LocalData/README.md)）。ユーザー・部門は seed 済み
3. **デザインのデプロイ** — `Designer/tools/deploy.ps1` を実行（`LocalData/designs/App.zip` が生成される）
4. **起動**

   ```powershell
   dotnet run --project BusinessApp/BusinessApp.Server --launch-profile http
   ```

   → http://localhost:5085 を開き `admin` / `admin` でログイン（admin はシステム管理専任で、ユーザー・部門・承認設定のみ操作可。会計・販売などの業務画面は seed 済みユーザー——例: 経理は `soumu_bucho`——でログインする。パスワードはユーザー名と同一。権限モデルの詳細: [`docs/10_部品アーキテクチャ.md`](docs/10_部品アーキテクチャ.md)）

AI 機能を実プロバイダで動かす場合の API キーは .NET User Secrets に置く（`docs/decisions/0024`）。設定ファイルに書かないこと。

## 開発の流れ

- 画面・データ・業務ロジックは `Designer/Design/` の CLB デザイン（`*.mod.json` / `*.mod.cs`）で作る。C# 拡張は CLB 単体で不可能な部分のみ（AI・外部 API 等 → `BusinessApp.Server/Services/`）
- 検証は CLB デザイナ付属 CLI で: `designcheck`（デザイン妥当性）→ `sql`（DB 整合）→ `deploy.ps1` で反映しブラウザ実機確認
- デザイン反映は hot-reload されるが、**スクリプト（`*.mod.cs`）・スキーマ変更時はサーバ再起動が必要**
- 詳細な運用ルールは `Designer/CLAUDE.md`・`Designer/Project.md`

## ドキュメント索引

| 場所                                          | 内容                                                           |
| --------------------------------------------- | -------------------------------------------------------------- |
| [`docs/README.md`](docs/README.md)            | 企画・ペルソナ・機能スコープ・会計ドメイン設計・進捗台帳の索引 |
| [`docs/decisions/`](docs/decisions/README.md) | 意思決定ログ（ADR）                                            |
| [`docs/tests/`](docs/tests/)                  | E2E・利用シナリオ・体験シナリオ（全仕訳期待値付き）            |
| [`Designer/Project.md`](Designer/Project.md)  | ワークスペース固有ルール（DB・命名・デプロイ手順）             |
| [`CLAUDE.md`](CLAUDE.md)                      | Claude Code 自律構築のミッションブリーフ（最上位指示書）       |
