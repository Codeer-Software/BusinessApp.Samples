---
title: Project.md（CLB デザインプロジェクト固有ルール）
status: current
scope: 会計コア
audience: [開発]
growth: append
updated: 2026-08-31
supersedes: []
related: [CLAUDE.md, ../docs/README.md, ../docs/09_画面の原則.md, ../docs/decisions/0026-画面は役割で分け玄関のページフレームを置く.md]
---
# Project.md（CLB デザインプロジェクト固有ルール）

このデザインプロジェクト固有の前提を書く。`ClaudeCodeForDesigner/` の汎用ルールはここに書かない。
企画・仕様・進捗は `../docs/`（索引: `../docs/README.md`）、判断の経緯は `../docs/decisions/` にある。

## 接続先 DB / データソース

- **BusinessAppSQLite**（本アプリの唯一のデータソース）
  - 種別: SQLite / 実体は `<REPO_ROOT>/LocalData/db/` 配下（Git 追跡外）
  - `AllowCliSqlAccess: true`（ローカル専用 DB。本番を指さない）
  - 認証部品の `app_users` と、マイグレーションの台帳 `schema_migrations` も同居する。
    **一時ファイルの `temporary_files` は未作成**（`appsettings.json` は参照しているので、
    `FileField` / `ImageField` を置くならテーブルを先に作る）
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

## フォルダ規約（何をどこに置くか）

**`Design/Modules/` のトップレベルのフォルダ＝アプリ（部品）**（開発者の決定。2026-08-28。
「Modules 下のトップレベルのフォルダはアプリにしましょう」）。
**フォルダ名の割り当ては設計判断**——`Accounting/`（会計コア）・`Partners/`（取引先）・
`Platform/`（どの部品にも属さないもの。`AppUser` 等）。
アプリの中の分け方（`Books/` `Journals/` `Masters/` `Settings/`）はその下に置く。

- **フォルダ（アプリ）と PageFrame（役割ごとの導線）は別の軸である。一致させない**
  （[ADR-0026](../docs/decisions/0026-画面は役割で分け玄関のページフレームを置く.md)）。
  前回プロジェクトは両者を一致させようとして、フレームを 7 回作り直したうえに
  「勘定科目は業務マスタのフレームだがシステムマスタのフォルダ」という食い違いが残った
- **モジュール名はデザイン全体でフラットな名前空間**なので、フォルダを動かしても参照は壊れない。
  ただし移動後は `designcheck` → deploy → 実機まで通す

## レイアウト規約（画面の見た目）

**何をどう見せるかと、その理由は [`../docs/09_画面の原則.md`](../docs/09_画面の原則.md) が持つ。**
ここには **CLB での書き方**だけを置く（同じ規則を 2 か所に書かない）。

| 原則（09 が持つ） | CLB での書き方 |
|---|---|
| 金額列は右詰め・3 桁カンマ | `Format: "#,0"`。**`text-align` では効かない**・**列見出し（`<th>`）に class が付かない**（[qa/01 D-15・D-16](../docs/qa/01_CLB静かな失敗.md)）。フォーム入力欄と列見出しは左のまま |
| 識別子は左詰め | 何も付けない（既定） |
| ○/— フラグ列は中央寄せ | Boolean に `TrueText: "○"` / `FalseText: "—"` を付ける |
| 検索条件は既定で開く | `SearchLayouts[""].Layout.IsExpanderDefaultOpened: true`（CLB の一般則 `LayoutGuidelines.md` とは逆の選択）。**検索欄を持つレイアウトだけ**——空の検索レイアウトを開くと空箱が出る（2026-08-30 に 9 モジュールへ適用） |
| 帳簿を並べ替えさせない | 列の `CanUserSort: false`。**PageFrame 側でも切る**（両方書く） |
| 必須の欄に赤い `*` | 詳細レイアウトの**ラベル側**の要素に `"ClassName": "required-label"`（`app.css` の `::after` が印を出す）＋ フォームの先頭行に `RequiredLegendLabel`。**一覧・明細表の見出しには付けられない**——`ListElement.ClassName` は `<td>` にしか付かない（qa/01 D-16）ので、**見出しの文字列そのものに `*` を入れる**（「勘定科目 *」。したがって明細の印だけ黒い） |
| ボタンの色は 3 値だけ | `Variant` に [09 §4](../docs/09_画面の原則.md) の 3 値以外を書かない |
| 押せないボタンを灰色にしない | **まだ無い。** 押せなくする手段は `IsViewOnly` か `IsVisible: false` で、`ButtonField` に `disabled` は無い（[qa/01 D-01・F-14](../docs/qa/01_CLB静かな失敗.md)）。半透明にするなら `app.css` に `opacity: .45`（**Bootstrap 既定の `.65` より薄く**）。**`cursor` は効かない**——`IsViewOnly` は `pointer-events: none` になる |

**CLB 固有の寸法・組み方**（09 には無い。ここだけが持つ）

- **検索レイアウトの行は `IsWrap: true` を標準**にする。1 行は 3 組（ラベル＋入力）まで
- **「ラベル列 + 入力列」の 2 カラム行では、ラベル列に `VerticalAlignment: "Middle"` を必ず設定**
- ラベル列の幅は「最長ラベルの文字数 × 18px + 40px」を目安に、フォーム内で揃える（下限 96px）
- **参照フィールドの幅**: 詳細レイアウトで取引先を入れるカラムは **440px**
  （既定幅では収まらない。**幅は行ではなく列に効く**ので、その行の合計を他の行と揃える。「作業中に得た知見」の 2026-08-26）。
  **いま適用してあるのは取引先の詳細だけ**で、一覧や仕訳明細には幅を付けていない

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
  `creator` / `updater` 列は型が未決なので、当面モジュールに持たせない
  （**優良な電子帳簿のチェックシート対応表を書くときに、記録事項の棚卸しと一緒に決める**。qa/02 R5-08）。
- 2026-08-24: マスタは**物理削除させない**（`CanDelete: false`。「削除ではなく無効化」——[docs/08_マスタ台帳](../docs/08_マスタ台帳.md)）。
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
  マスタを指す LinkField は `IsActive.Value = true` で絞る（`is_active` は「入力候補に出すか」の意味。[docs/08_マスタ台帳](../docs/08_マスタ台帳.md)）。
  設定しないと**無効にした科目が候補に出てしまい、降順で並ぶ**。
  `designcheck` は findings 0。DB に `temporary_files` は未作成（「接続先 DB / データソース」も参照）。

  > **2026-08-30 追記——本則は「入力の欄」にだけ掛かる。** 同日の朝の時点では
  > 振替伝票の伝票と明細の 6 本が絞られておらず、上に書いた症状がそのまま起きていた。
  > フェーズ 2.5 の B で全部に当てた（並び順も付けた）。**絞らない側は 2 種類ある。**
  >
  > | 絞らないもの | 理由 |
  > |---|---|
  > | **帳簿の検索の欄**（仕訳帳・総勘定元帳の会計年度・科目・補助科目・部門・取引先） | **無効にしたマスタで過去を引けなくなる**（[qa/01 X-05](../docs/qa/01_CLB静かな失敗.md)）。検索は「これから入力するもの」ではない。**並び順は付ける**——無指定だと降順で、勘定科目 105 件が逆順に並ぶ（2026-08-31 に付けた） |
  > | **取引先の「名寄せの親」** | 名寄せは「同一人格の表明」であって入力候補ではなく、無効にした取引先を親にする場面が正常に起こる（[ADR-0028](../docs/decisions/0028-名寄せの親は同一人格の表明であり深さ1に固定する.md)） |
  >
  > **登録番号の取引先を選ぶ欄は 2026-08-31 に消えた**——入力を取引先の詳細へ移し、
  > 親 FK を `IdFieldDesign` にしたので、絞る対象そのものが無くなった（[docs/07 §3-4](../docs/07_取引先設計.md)）。
  > **「例外は名寄せの親だけ」と書いていた**（2026-08-30）が、帳簿の 8 本が抜けていた
  > （自己レビューで発見。qa/02 R25-25）——**破れている本則に但し書きを被せると、
  > 次の人が「他は守られている」と読む。**
- 2026-08-26: **デザイン JSON は `json.load` → `json.dumps(indent=2, ensure_ascii=False)` で
  往復できる**（3 つのモジュールで実測。**末尾の改行を除いてバイト単位に一致する**）。手で JSON を書き換えるより安全。
  ただし **Python の text mode は Windows で CRLF を書く**ので、`open(path,'wb')` で
  LF のまま書く（`.gitattributes` が正規化する前に `designcheck` と差分が汚れる）。
  **末尾の改行だけは往復しない**（`json.dumps` は付けない。2026-08-30 に 1 ファイル落として気づいた）。
  **末尾改行の有無はファイルごとに違う**ので、読んだときの状態を覚えて同じように書き戻す
  （揃えようとすると、無関係な 1 行が次の差分に混ざる）。
- 2026-08-26: **レイアウトの `Width` は行ではなくレイアウト全体に効く。** 1 つの列に
  幅を付けると、同じ位置にある他の行の入力欄まで一緒に動く（実測。CLB 1.3.20）。
  幅を付ける前は自動の列が伸びて**登録ボタンが画面の外に出ていた**ので、
  取引先の詳細は 140+300+140+440 = 1020px に**行ごとの合計を揃えて**収めた。
  `designcheck` は緑のままなので、実機で見る。
- 2026-08-30: **`IsRequired: true` は「利用者が埋める必須欄」の意味に限る。** 画面が自動で入れて
  `IsViewOnly` にしてある欄（伝票の `Status`・明細の `LineNo`・`FiscalYear`）には立てない。
  立てると CLB の「必須項目です」が、**利用者に直しようのない欄について**先に鳴り、
  サーバ側の整った文言に永久に到達しない（`FiscalYear` で実際に起きた。qa/02 R24-25）。
  DB の `NOT NULL` と関門は別に守っているので、外しても穴は開かない。
  **この 1 意味に揃えてあるから、`lint_design.py` が「必須欄のラベルに印があるか」を素直に検査できる。**
- 2026-08-30: **`PageFrame` の `Link.Title` に `/` を書くとサイドバーが階層になる**
  （`_specs/PageFrame.md`。多階層も可）。**1 件しかない群を作らない**——
  階層にすると 1 クリック増えるだけなので、振替伝票と他フレームへのリンクは最上位のままにした。
- 2026-08-30: **1 行しか持たないモジュールは、`Link` の `ModulePageType: "Detail"` ＋ `Id: "1"` で
  一覧を挟まずに開く**（自社情報。`CHECK (id = 1)` が 1 行を保証する。ADR-0005）。
- 2026-08-30: **`LinkField` の候補は、選んだ勘定科目で補助科目を絞れていない**（未解決）。
  明細行の中で**兄弟のフィールド**（同じ行の `Account`）を参照する条件になるので、
  `FieldVariableMatchCondition` の `Variable` に何を書けば行の中を指せるかが分かっていない。
  **判断は [05 のフェーズ 2.5 の B](../docs/05_実装計画と現在地.md) が持つ。**
