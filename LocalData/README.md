# LocalData — 実行環境データ置き場

> 2026-07-09 に `C:\Codeer.LowCode.Blazor.Local\` からプロジェクト配下へ移設（経緯: `docs/decisions/0017`）。
> サーバ・デザイナ CLI が実行時に読み書きするデータをここに集約する。

| ディレクトリ | 内容 | Git |
|---|---|---|
| `db/` | SQLite 本体（`accounting_v1.db`） | 追跡しない |
| `designs/` | デザインデプロイ先（`App.zip`。FileWatcher が監視・hot-reload） | 追跡しない |
| `font/` | PDF 出力用フォント（`NotoSansJP.ttf`・SIL OFL 1.1。ライセンス全文は同フォルダの `LICENSE`） 。**無いと全 PDF 出力が失敗する** | **コミット**（環境構築の罠解消のため） |
| `storages/` | FileField の添付ファイル実体 | 追跡しない |
| `backup/` | DB のバックアップ（手動スナップショット） | 追跡しない |

## 参照している設定（パスを変える場合はここを全部直す）

- `BusinessApp/BusinessApp.Server/appsettings.Development.json` — 接続文字列 / FileStorages.Directory / DesignFileDirectory / FontFileDirectory
- `Designer/Design/designer.settings.Development.json` — designcheck / sql CLI の接続先・デプロイ先
- `Designer/tools/deploy.ps1` — `$Workspace` / `$Destination` 既定値（`$PSScriptRoot` 相対なので書き換え不要）

パスは絶対パスで記述している（`dotnet run` の作業ディレクトリに依存させないため）。
**上記 2 つの設定ファイルは環境固有のため Git 追跡外**（経緯: `docs/decisions/0025`）。新環境では同じ場所にある `*.sample` をコピーして `<REPO_ROOT>` を実パスに置換する。API キー等の秘密はこれらのファイルには書かず .NET User Secrets へ（`docs/decisions/0024`）。

## まっさら DB の再構築手順

1. サーバを停止し、`db/accounting_v1.db` を `backup/` へ退避（または削除）
2. `Designer/ddl/*.sql` を番号順に sql CLI（`BusinessApp.Designer.exe sql ... --file`）で適用
3. `Designer/tools/deploy.ps1` でデザインをデプロイし、サーバを起動
4. ユーザー・部門・役職者は DDL seed 済み（`085_org_users.sql` ほか）。admin/admin でログイン可能
