# 0017: 実行環境データを C:\Codeer.LowCode.Blazor.Local からプロジェクト配下 LocalData/ へ移設

- 日付: 2026-07-09 ／ 状態: 採用・実施済み ／ 起票: ユーザー要望（「C ドライブ直下に置く必要ないですよね？」）

## 決定

サーバ・CLI が実行時に読み書きするデータ（SQLite DB・デザインデプロイ zip・PDF フォント・添付ファイル実体・バックアップ）を、リポジトリ直下の `LocalData/` に集約する。構成と Git 追跡方針は `LocalData/README.md` が正。

## 理由

1. **プロジェクトの自己完結**: アプリの動作に必要なものが 1 ディレクトリツリーに収まり、環境の把握・複製・掃除が容易になる。
2. **C:\ 直下の共有ディレクトリとの分離**: `C:\Codeer.LowCode.Blazor.Local` は CLB デザイナ製品がサンプル DB を展開する共有置き場でもあり、本アプリ固有のデータを混在させない方が安全（誤削除・取り違え防止）。
3. **フォントの罠の根治**: `NotoSansJP.ttf` が無い環境では全 PDF 出力が Object reference エラーで失敗する（Project.md 知見 2026-07-07）。フォントをリポジトリにコミット（SIL OFL・約 9.2MB・リモート無しのローカルリポジトリなのでサイズ影響は軽微）することで、新環境セットアップの必須手順を 1 つ消した。

## 実施内容

- 参照パスの変更: `appsettings.Development.json`（接続文字列／FileStorages／DesignFileDirectory／FontFileDirectory）・`designer.settings.Development.json`（接続文字列／DeployInfo）・`tools/deploy.ps1`（既定 Destination）
- パスは**絶対パス**で記述（`dotnet run` の作業ディレクトリ差で相対パスが狂う事故を避ける）。別マシン移設時の書き換え箇所は `LocalData/README.md` に列挙
- Git: `db/` `designs/` `storages/` `backup/` は ignore、`font/` と README はコミット
- 旧 DB は `LocalData/backup/accounting_v1_pre-reorg_2026-07-09.db` に退避（ユーザー側でも別途バックアップ済み）。C:\ 側の本アプリ DB は削除。CLB 製品のサンプル DB 群（sqlite_sample 等）は製品の管轄なので触っていない

## 影響・注意

- デザイナ GUI から「送信」する場合は、GUI 側のデプロイ先設定も `LocalData\designs` を指す（designer.settings.Development.json の DeployInfo を GUI も読むため自動で一致）
- `AccountingApp.Designer/App.xaml.cs` にも旧パスのリテラルがあるが、これは**デザイナ製品の新規プロジェクトテンプレート展開用**で本アプリの実行時参照ではないため変更しない
