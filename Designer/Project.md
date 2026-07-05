# Project.md（プロジェクト固有ルール）

このデザインプロジェクト固有の前提を書く。
`ClaudeCodeForDesigner/` の汎用ルールはここには書かない（プロジェクト固有のことだけ）。
企画・仕様・進捗は `../docs/`（索引: `../docs/README.md`）、判断の経緯は `../docs/decisions/` にある。

## 接続先 DB / データソース

- **AccountingSQLite**（本アプリの唯一のデータソース。→ `docs/decisions/0001`）
  - 種別: SQLite / 接続先: `C:\Codeer.LowCode.Blazor.Local\Data\accounting_v1.db`
  - `AllowCliSqlAccess: true`（ローカル専用 DB。本番を指さない）
  - 認証テーブル（app_users）・一時ファイル（temporary_files）も同居
- 旧 `SampleSQLite`（sqlite_sample_auth.db）はフェーズ A-0 で撤去予定。**会計テーブルを置かないこと**
- サーバ側: `AccountingApp.Server/appsettings(.Development).json` にも同名データソースの定義が必要（変更時はサーバ再起動）

## 命名規約

- テーブル・列: snake_case 英語（例 `journal_entries.entry_date`）。DatabaseGuidelines 標準に従い PK は `id` INTEGER 自動採番
- モジュール・フィールド: PascalCase 英語（例 `JournalEntry` / `EntryDate`）。画面表示名（DisplayName）は日本語
- 主要テーブル名は `docs/04_会計ドメイン設計.md` の定義が正
- CLB システムフィールドは予約名（`Id` / `CreatedAt` / `Creator` / `UpdatedAt` / `Updater`）を使用

## 業務ルール（会計の不変条件）

- 金額は整数円。貸借一致しない伝票は確定不可。締め済み期間（fiscal_periods.status=closed）の仕訳は作成・変更・削除不可
- 税抜経理。消費税行はシステム生成（is_tax_line=1）でユーザー直接編集不可
- 税率・税区分・経過措置控除割合・制度閾値はマスタ参照（ハードコード禁止）
- 残高＝期首残高＋仕訳集計（残高キャッシュ無し）。帳票 SQL は `status='posted'` のみ集計
- 詳細は `docs/04_会計ドメイン設計.md` §9 の実装チェックリスト

## 既存資産

- `Modules/AppUser.mod.json`（Cookie 認証・admin/admin）・`Home.mod.json`・`PageFrames/Main.frm.json` — 認証テンプレート由来。AppUser はフェーズ A-0 で AccountingSQLite へ切替
- 承認フロー・経費申請の正典: `ClaudeCodeForDesigner/Samples/PatternShowcaseAuth/` と `../references/SampleProject_AuthPatterns/`

## デプロイ手順（zip packing — 2026-07-05 実測確立済み）

- **`pwsh -NoProfile -File Designer/tools/deploy.ps1` を実行するだけ**（designcheck を通してから）
- zip 仕様（GUI 製 App.zip の実測に基づく）: エントリ名は**バックスラッシュ区切り**・ディレクトリエントリ無し・`app.clprj` はルート直下・`designer.settings*.json` は含めない。書きかけ検知を避けるため一時ファイルに作成してから `Move-Item` で配置
- 検証結果: サーバ起動時読み込み◯／稼働中の FileWatcher hot-reload ◯（ブラウザ再読み込みで反映を確認済み）
- 注意: `*.mod.cs`（スクリプト）変更・DB スキーマ変更はサーバ再起動が必要
- サーバ起動: `dotnet run --project AccountingApp/AccountingApp.Server --launch-profile http`（http://localhost:5085）

## 作業中に得た知見（追記していく）

- 2026-07-05: インボイス経過措置は令和8年度改正で4段階（80/70/50/30%）に変更済み。事前知識と異なっていた——**税制は必ずリサーチしてから実装する**こと（→ `docs/research/2026-07_税制・会計制度リサーチ.md`）
- 2026-07-05: **認証のデータソースは `app.clprj` の `CurrentUserModuleDesignName`（=AppUser モジュール）の `DataSourceName` から解決される**（`CookieAuthentication.GetDataSourceName()`）。app_users が空ならサーバ起動時に admin/admin が自動作成される
- 2026-07-05: 認証 DB を切り替えるとブラウザの旧セッション Cookie が「アプリへのアクセス権限がありません」エラーを出す。**対処: `POST /api/account/logout`**（JS なら `X-ANTIFORGERY-TOKEN` Cookie（非 HttpOnly）をヘッダに付けて fetch）→ login.html が出る
- 2026-07-05: CLB は DATE 列へ `YYYY-MM-DD HH:MM:SS` 形式で書き込む。seed の `YYYY-MM-DD` と混在しても辞書順比較は成立するが、**自作 SQL（QueryField 等）の日付比較・GROUP BY は `date(列)` で正規化**すること
- 2026-07-05: **SQLite の日付列は必ず `DATE` で宣言する（TEXT 禁止）**。TEXT だと CLB が DateOnly を InvariantCulture（MM/dd/yyyy）で文字列化し、範囲比較・ソートが破綻する（DatabaseGuidelines 記載。030 で踏んで DROP→再作成で是正した）。DATETIME/TIME も同様
- 2026-07-05: sql CLI でテーブルを DROP するとき、参照元がある場合はスクリプト先頭に `PRAGMA foreign_keys=OFF;`（CLB 接続は FK 有効）
- 2026-07-05: **親子・多段ネスト構造の FK 列に NOT NULL を付けない**。CLB は子レコードの FK を後埋めするため INSERT 時点で NULL になり得る（approval_flow_member で実測。REFERENCES は付けてよい）
- 2026-07-05: **ExecuteSqlField はスクリプトから実行できない**（全メンバー ScriptHide。マニュアル JP/db/execute_sql_field.md で確認）。擬似 Standalone 実行は「Timing: Update ＋ 非バインドフラグフィールド（NULL なら SQL 側 no-op ガード）＋ ボタンで フラグセット→Submit」で実現（FiscalYear.CarryOverSql が実例）
- 2026-07-05: 繰越利益剰余金は科目コード **3100 固定**（FiscalYear.CarryOverSql.sql が参照。科目コード変更時は要修正）
- 2026-07-05: ブラウザ自動操作で `<input type=date>` にキーボード入力すると壊れやすい。**JS で `el.value='YYYY-MM-DD'` を設定し `input`/`change` イベントを dispatch** するのが確実（Blazor バインドも発火する）
- 2026-07-06: **ModuleSearcher の OrderBy/OrderByDescending のラムダは必ず `.Value` を付ける**（`e => e.JournalNo` はエラーにならず**ソートが無効**になり、Limit(1) 採番が常に同じ番号を返す。伝票番号4件重複で実測。AddEquals 系と同じ Variable 規約）
- 2026-07-06: **ChildModule の Status/AttemptNo 等はメモリを信用せず DB で解決**（承認 Order の進行判定・CurrentApprover 再計算で実測。#60 の罠は「読む場所」すべてに効く）
- 2026-07-06: **期間解決の日付比較で「境界日（end_date と同日）の >=」は失敗しうる**（検索パラメータが時刻付き書式・seed が素の DATE 文字列だと辞書順で偽。RecurringRun の月末日解決で実測）。**期間・年度の解決は境界にならない日付（月初日等）で行う**。月末日付の手動起票が同じ罠を踏む可能性は要実測（バックログ）
- 2026-07-06: **レイアウトに出ていないフィールド（DataOnlyFields 含む）の `.Value` は信用しない**。ExpenseRequest.Creator（DataOnly の LinkField）が実行タイミングにより null で、部門引継ぎが欠落した実測バグ。スクリプトで確実に値が要る場合は **ModuleSearcher で自レコードを DB から取り直す**（#60 の罠の一般化。ApprovalFlow の通知でも同方式を採用）
