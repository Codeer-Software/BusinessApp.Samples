---
title: Project.md（CLB デザインプロジェクト固有ルール）
status: current
scope: 会計コア
audience: [開発]
growth: append
updated: 2026-08-23
supersedes: []
related: [CLAUDE.md, ../docs/README.md]
---
# Project.md（CLB デザインプロジェクト固有ルール）

このデザインプロジェクト固有の前提を書く。`ClaudeCodeForDesigner/` の汎用ルールはここに書かない。
企画・仕様・進捗は `../docs/`（索引: `../docs/README.md`）、判断の経緯は `../docs/decisions/` にある。

## 接続先 DB / データソース

- **BusinessAppSQLite**（本アプリの唯一のデータソース）
  - 種別: SQLite / 実体は `<REPO_ROOT>/LocalData/db/` 配下（Git 追跡外）
  - `AllowCliSqlAccess: true`（ローカル専用 DB。本番を指さない）
  - 認証テーブル（`app_users`）・一時ファイル（`temporary_files`）も同居する
- サーバ側 `BusinessApp.Server/appsettings(.Development).json` にも同名データソースの定義が要る
  （変更時はサーバ再起動）

## 命名規約

- **テーブル・列: snake_case 英語**（例 `journal_entries.entry_date`）。PK は `id` INTEGER 自動採番
- **モジュール・フィールド: PascalCase 英語**（例 `JournalEntry` / `EntryDate`）。
  画面表示名（`DisplayName`）は日本語
- CLB のシステム予約名（`Id` / `LogicalDelete` / `OptimisticLocking` / `CreatedAt` / `UpdatedAt` /
  `Creator` / `Updater`）はその綴りのまま使う
- 主要テーブル名の正典は `../docs/04_会計ドメイン設計.md`

> テンプレート由来の `AppUser` モジュールだけはフィールド名が日本語（`ユーザー識別名` 等）である。
> 認証部品の資産なので**改名しない**。新規モジュールは上記の規約に従う。

## レイアウト規約（画面の見た目）

- **金額列は右詰め・3 桁カンマ区切り**（`Format: "#,0"`）。フォーム入力欄は左詰めのまま
- **一覧の ○/— フラグ列は中央寄せ**。Boolean は `TrueText: "○"` / `FalseText: "—"` を付ける
- **ボタンの色は 2 色**: 通常操作は Primary（青）、破壊的・不可逆な操作は Danger（赤）
- **検索レイアウトの行は `IsWrap: true` を標準**にする。1 行は 3 組（ラベル＋入力）まで
- **「ラベル列 + 入力列」の 2 カラム行では、ラベル列に `VerticalAlignment: "Middle"` を必ず設定**
- ラベル列の幅は「最長ラベルの文字数 × 18px + 40px」を目安に、フォーム内で揃える（下限 96px）
- **参照フィールドの幅**（全画面共通）: 取引先を入れるカラムは **440px**、
  勘定科目を入れるカラムは **320px**（どちらも既定幅では収まらない）

## 業務ルール（会計の不変条件）

正典は `../docs/04_会計ドメイン設計.md`。ここには CLB 実装に直接効くものだけを再掲する。

- **計上済み（posted）の仕訳は変更・削除しない。** 訂正・取消は反対仕訳で行う
  （`../docs/decisions/0004-優良な電子帳簿への準拠と仕訳の不変性.md`）
- 金額は整数円。貸借が一致しない伝票は計上できない
- 締め済み期間への計上・訂正はできない
- 損益科目の仕訳行には部門が必須（貸借科目の行は任意）
- 税率・控除割合・閾値は**マスタ参照**。ハードコード禁止
- **会計ロジックは CLB スクリプトに書かない。** `AccountingCore`（C#）に置く
  （`../docs/decisions/0008-CLBとCSharpライブラリの責務分担.md`）

## デプロイ手順

```powershell
pwsh -NoProfile -File tools/clb/deploy.ps1
```

`Designer/Design` 一式を zip 化して `LocalData/designs/App.zip` に配置する。
FileWatcher が検知して hot-reload する（デザイナ GUI「送信」の代替）。

- zip 仕様: エントリ名は**バックスラッシュ区切り**・ディレクトリエントリ無し・
  `app.clprj` はルート直下・`designer.settings*.json` は含めない
- **`*.mod.cs`（スクリプト）変更・DB スキーマ変更はサーバ再起動が必要**
- サーバ起動: `dotnet run --project BusinessApp/BusinessApp.Server --launch-profile http`

## 作業中に得た知見（追記していく）

CLB 全般の「静かな失敗」は `../docs/qa/01_CLB静かな失敗.md` に集約してある。
ここには**このプロジェクト固有の実測**だけを書く。

- 2026-08-23: 初期状態は `EmptyAuth` テンプレート（`AppUser` ＋ `Home` の 2 モジュール）。
- 2026-08-24: `sql` CLI は **`--out` を省くと結果 JSON が標準出力に来る**。PowerShell から呼ぶときは
  `ProcessStartInfo.ArgumentList` に 1 引数ずつ足して `RedirectStandardOutput` で受ける。
  `Start-Process -ArgumentList` だと `--query` 内の `'...'` が壊れて `incomplete input` になる。
  この形なら SQL ファイルも結果ファイルも作らずに済む（CLAUDE.md §3-2-2）。
- 2026-08-24: デザイナ exe は WinExe なので、PowerShell の `&` で呼ぶと**待たずに戻る**。
  終了コードを見るには `Start-Process -Wait -PassThru` か `Process.WaitForExit()` を使う。
- 2026-08-24: **デザイン enum は複数形で名づける**（`TaxationTypes` / `RateKinds`）。enum 名は
  モジュール・PageFrame と同じ型名空間に入るため、単数形だと同名のフィールドと衝突し、
  `designcheck` に「スクリプトではフィールド名が優先されます」と指摘される。
- 2026-08-24: **予約名フィールドは規定のデザイン型で作る。** `OptimisticLocking` は
  `OptimisticLockingFieldDesign` ＋ `IncrementVersion: true`（SQLite）。型が違うと
  designcheck 緑・HTTP 200 のまま更新だけが失敗する（qa/01 F-09）。
  `creator` / `updater` 列は型が未決なので、当面モジュールに持たせない（フェーズ 6 の監査ログで決める）。
- 2026-08-24: マスタは**物理削除させない**（`CanDelete: false`。ADR-0006「削除ではなく無効化」）。
  一覧の削除ボタンは PageFrame の `Link.ListPageDesign.ListFieldDesign.CanDelete` でも切る。
- 2026-08-24: 一覧の既定の並び順は PageFrame の `Link...SearchCondition.SortConditions` で指定する。
  指定しないと**降順で出る**（マスタでは使いものにならない）。
- 2026-08-24: **ヘッダ＋明細（仕訳伝票と明細）の正典**（`Docs/AppPatterns/header_detail.md`）:
  明細表は **`ListField`**（`DetailListField` ではない。名前に反するので最頻出の誤り）。
  子の親 FK は **`IdFieldDesign` ＋ `IsManualInput: false`**（`NumberField` は不可）。
  親の詳細レイアウトに `ListField` を置き、`SearchCondition.Condition` に
  `FieldMatchCondition` → `FieldVariableMatchCondition`（`SearchTargetVariable` = 子の FK、
  `Variable` = 親の `Id.Value`）を入れて逆引きする。**列定義は子モジュールの `ListLayouts[""].Elements`**。
  親の Submit で子の Add/Update/Delete が 1 トランザクションにまとまる。
  親詳細の `LimitCount` は全件（`0` にすると明細が消える）。
- 2026-08-24: **LinkField の候補ダイアログは、一覧画面とは別に絞り込みと並び順を持つ。**
  フィールド側の `SearchCondition` に `SortConditions` と `Condition` を設定する。
  マスタを指す LinkField は `IsActive.Value = true` で絞る（`is_active` は「入力候補に出すか」の意味。ADR-0006）。
  設定しないと**無効にした科目が候補に出てしまい、降順で並ぶ**。
  `designcheck` は findings 0。DB には `app_users` のみ存在し、`temporary_files` は未作成
