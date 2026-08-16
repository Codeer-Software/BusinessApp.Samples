# CLB「静かな失敗」機械検査ルール

CLB には「エラーにも designcheck の指摘にもならず、静かに壊れる」挙動が多数ある（`docs/12_CLB改善提案/` の A 群）。
本書はそれらを**全数機械検査できる形**に落とした仕様書であり、`Designer/tools/lint_design.py`（デザインリンタ）の実装根拠になる。
リンタを書く人（またはエージェント）は**本書だけを見て実装できる**ことを目標にしている。
ルールは「1 つの症状 = 1 つの ID」で、`designcheck` が既に検出するものは原則入れない（重複するのは説明価値がある場合だけ）。
運用は「designcheck → lint_design.py → sql → 実機」の順。lint は**警告ゼロを目標**にし、正当な例外は抑制コメントではなく本書へ「誤検知の可能性」として書き足す。
既存の `Designer/tools/check_navigation.py`（FB-023/FB-042 の検査）は CLB-013 の実装済み部分であり、lint_design.py に統合してよい。

## 検査対象ファイルの所在

| 種別 | パス |
|---|---|
| モジュール定義 | `Designer/Design/Modules/**/*.mod.json` |
| モジュールスクリプト | `Designer/Design/Modules/**/*.mod.cs` |
| クエリ SQL | `Designer/Design/Modules/**/*.Query.sql` |
| ExecuteSqlField の SQL | `Designer/Design/Modules/**/<Module>.<Field>.sql`（例 `FiscalYear.CarryOverSql.sql`） |
| ページフレーム | `Designer/Design/PageFrames/*.frm.json` |
| CSS | `Designer/Design/app.css` |
| DDL | `Designer/ddl/*.sql` |

## 実装の下ごしらえ（共通インデックス）

全ルールがこの 4 つのインデックスに乗る。**先にこれを作ってからルールを足す**こと。

1. **JSON 再帰ウォーカ**: レイアウトは `Layout.Rows[].Columns[].Layout` が**再帰的に入れ子**になる（実測で 3 段まで）。
   「深さ固定のパス」で書くと取りこぼす。**キー名で全 dict を再帰探索する**汎用関数（`walk(obj, path) -> (path, key, value)`）を作る。
   本書で `Fields[].Layout.HorizontalAlignment` のようなパスを書いている箇所も、実装は再帰探索でよい。
2. **モジュールインデックス**: モジュール名 → `{DataSourceName, DbTable, CanCreate/Update/Delete, Fields[]（Name/TypeFullName/DbColumn/各プロパティ）,
   DetailLayouts, ListLayouts, SearchLayouts, LinkFieldNames}`。
   フィールド型は `TypeFullName` の末尾（`...Repository.Design.DateFieldDesign` → `DateField`）で判定する。
3. **DDL インデックス**: `Designer/ddl/*.sql` を素朴にパースし
   `テーブル名 → {列名 → 宣言型(生文字列)}` / `ビュー名の集合` / `トリガー(名前, INSTEAD OF 種別, 対象)` / `生成列の集合` を作る。
   `CREATE TABLE IF NOT EXISTS (\w+)\s*\((.*?)\n\);` を DOTALL で取り、括弧の深さを数えながら `,` で列定義を分割する
   （`DECIMAL(10,2)` や `CHECK(...)` の中のカンマで割らないため）。`ALTER TABLE ... ADD COLUMN` も拾う。
4. **フレームインデックス**: フレーム名 → `{Left.Links[], TopPageModuleDesign, OtherPageModuleDesigns[], TopPageModule(レガシー)}`。
   ページ登録は `Links` / `TopPageModuleDesign` / `OtherPageModuleDesigns` の 3 か所に散っているので、必ず 3 つとも見る。

出力は `ルールID / 重大度 / ファイル:位置 / メッセージ` の 1 行 1 件とし、終了コードは「高が 1 件以上あれば 1」。

---

## 実装優先度

| 群 | ルール |
|---|---|
| **高（まず作る）** | CLB-001 / CLB-002 / CLB-003 / CLB-004 / CLB-005 / CLB-006 / CLB-007 / CLB-008 / CLB-009 / CLB-010 / CLB-011 / CLB-012 / CLB-013 / CLB-014 |
| **中** | CLB-015 / CLB-016 / CLB-017 / CLB-018 / CLB-019 / CLB-020 / CLB-021 / CLB-022 / CLB-023 / CLB-024 / CLB-025 / CLB-026 / CLB-027 / CLB-028 |
| **低** | CLB-029 / CLB-030 / CLB-031 / CLB-032 / CLB-033 / CLB-034 / CLB-035 / CLB-036 |

---

## ルール一覧

### CLB-001 `HorizontalAlignment` の旧値（`Left` / `Right`）

- **概要**: 1.3 系で廃止された列挙値がエラーにならず既定 `Start` に化ける。
- **症状**: 右寄せしたはずのラベル・金額が左寄せで描画される。designcheck は 0 件で通過し、
  さらにデザイナで保存すると旧値が `Start` に**固定化**されて意図が永久に失われる。
- **検査対象**: `*.mod.json` / `*.frm.json`
- **検出方法**: JSON を再帰探索し、キー名が `HorizontalAlignment` の値を取る
  （実在パス例: `DetailLayouts.<name>.Layout.Rows[].Columns[].HorizontalAlignment`、
  `SearchLayouts.<name>.Layout.Rows[].Columns[].HorizontalAlignment`、入れ子の `...Columns[].Layout.Rows[].Columns[].HorizontalAlignment`）。
- **違反の条件**: 値が `Start` / `Center` / `End` / `Stretch` / `""`（未指定）以外。とくに `Left` `Right`。
- **誤検知の可能性**: なし（有効値は 4 つで確定）。同型のキー `VerticalAlignment` は別列挙（`Top/Middle/Bottom/Stretch` 系）なので
  同じ集合で検査しないこと。
- **重大度**: 高
- **出典**: FB-031 / Project.md 2026-07-20

### CLB-002 `DateField` / `DateTimeField` が `TEXT` 列にマップされている

- **概要**: 列の宣言型で保存書式が変わり、`TEXT` だと US 書式（`08/14/2026`）で書かれる。
- **症状**: CLB 経由の読み書きは往復で一致するので**画面は正常に見える**。SQLite の `date()` が NULL を返すため、
  期間比較・ソート・GROUP BY を含む帳票 SQL だけが**エラーなしで全滅**する。発見は「帳票の数字がおかしい」からの逆算になる。
- **検査対象**: `*.mod.json` × DDL
- **検出方法**: モジュールインデックスの `DbTable` と各 `Fields[].DbColumn` を DDL インデックスに突き合わせ、宣言型を得る。
  `TypeFullName` が `DateFieldDesign` → 宣言型が `DATE` であること、`DateTimeFieldDesign` → `DATETIME` であること、
  `TimeFieldDesign` → `TIME` であることを要求する（大文字小文字は無視）。
  補助検出（DDL 単体）: 列名が `_date$` / `_at$` / `_on$` / `^date_` にマッチするのに宣言型が `TEXT` の列も報告する。
- **違反の条件**: 宣言型が `TEXT` / `VARCHAR` / `CHAR` / 型なし。
- **誤検知の可能性**: `DbTable` が**ビュー**のとき（`active_app_users`・`my_application_view` 等）は列型を追えないのでスキップし、
  「型を確認できなかった」として別枠で列挙する。年月だけを保持する `YYYY-MM` 文字列列（`IsYearMonthOnly` の相手）は
  意図的に TEXT のことがあるので、フィールド側 `IsYearMonthOnly: true` は除外してよい。
- **重大度**: 高
- **出典**: FB-007 / FB-041 / ADR-0055 / Project.md 2026-07-05・2026-08-14

### CLB-003 SQL の `'now'` が UTC のまま（`'localtime'` 無し・修飾子の順序違い）

- **概要**: SQLite の `date('now')` は UTC。JST とは 9 時間ずれる。
- **症状**: 毎月 1 日の 0:00〜9:00 だけ「前月」を数える。画面側は `DateTime.Today`（ローカル）なので、
  ポータルの件数と一覧の件数が月初の朝だけ 1 ヶ月ずれる。エラーは出ない。
- **検査対象**: `*.Query.sql` / `<Module>.<Field>.sql`（DDL は対象外）
- **検出方法**: `'now'` の全出現について、それを含む関数呼び出しの引数リスト（直前の `(` から対応する `)` まで）を取り出し、
  `'localtime'` を含むか、含む場合それが `'now'` の**直後の修飾子**かを判定する。
  併せて `CURRENT_TIMESTAMP` / `CURRENT_DATE` の出現も報告する（これらも UTC）。
- **違反の条件**: ① 引数リストに `'localtime'` が無い ② `'localtime'` が他の修飾子より後ろにある
  （`date('now','start of month','localtime')` は誤り。正: `date('now','localtime','start of month')`）
  ③ `CURRENT_TIMESTAMP` / `CURRENT_DATE` を使っている。
- **誤検知の可能性**: 意図的に UTC を使う箇所（監査ログの UTC 記録など）。現状そのような用途は無い。
  DDL の `DEFAULT (datetime('now'))` は CLB/SQLite の慣例なので DDL を対象から外すこと。
- **重大度**: 高
- **出典**: ADR-0060 決定 5 / Project.md 2026-08-16

### CLB-004 `OnValidateInput` が設定されている

- **概要**: `OnValidateInput` が `false` を返すと保存が**無言で**止まる。
- **症状**: 「登録ボタンを押しても何も起きない」。`.invalid-feedback` も描画されず、トーストも出ず、サーバへ通信も飛ばない。
  `SetError` を呼んでも描画されなかった（実測）。利用者は理由を知る手段が無い。
- **検査対象**: `*.mod.json`
- **検出方法**: 全 `Fields[].OnValidateInput` を見る（`ListFieldDesign` の中にも同名プロパティがある。再帰探索で拾う）。
- **違反の条件**: 値が空文字列でない（ハンドラが登録されている）。
- **誤検知の可能性**: 将来 CLB 側が「無言で止める」挙動を直したら本ルールは廃止する。
  現状の本リポジトリでは非空は 0 件なので、**新規混入の検出器**として働く。
- **重大度**: 高
- **出典**: FB-044 / ADR-0059 / Project.md 2026-08-16

### CLB-005 `OrderBy` / `OrderByDescending` のラムダに `.Value` が無い

- **概要**: Variable 規約違反だが例外にならず、**ソートが無効**になる。
- **症状**: `Limit(1)` と組み合わせた採番が常に同じ値を返す（伝票番号が 4 件重複した実例）。「最新 1 件」が最新でなくなる。
- **検査対象**: `*.mod.cs`
- **検出方法**: 正規表現 `OrderBy(?:Descending)?\(\s*(\w+)\s*=>\s*([^()]*?)\s*\)` で本体式を取り、
  末尾が `.Value` で終わるかを見る。
- **違反の条件**: ラムダ本体が `.Value` で終わっていない（例: `e => e.JournalNo`）。
- **誤検知の可能性**: 本体に `?.` や三項演算子を含む複雑な式は正規表現が途中で切れる可能性がある。
  切れた場合も「`.Value` で終わらない」として報告し、人が見て判断する（現状そのような式は無い）。
- **重大度**: 高
- **出典**: FB-002 / DOC-002 / Project.md 2026-07-06

### CLB-006 スクリプトの `ref` / `out` 引数

- **概要**: CLB スクリプトでは `ref`/`out` の書き戻しが呼び出し元に伝わらない。
- **症状**: コンパイルエラーにも実行時エラーにもならず、**値だけが落ちる**（消費税額の合計が常に 0 になった実例）。
- **検査対象**: `*.mod.cs`
- **検出方法**: 正規表現 `[(,]\s*(?:ref|out)\s+\w` で、括弧内に現れる `ref` / `out` トークンを検出する
  （メソッド定義側・呼び出し側の両方が同じ形で引っかかる）。
- **違反の条件**: 1 件でもマッチしたら違反。
- **誤検知の可能性**: 文字列リテラルやコメント内の "ref"。行頭が `//` の行と、`"` で囲まれた区間を除外してから走らせる。
- **重大度**: 高
- **出典**: FB-039 / ADR-0055 / Project.md 2026-08-14

### CLB-007 スクリプトの `try` / `catch` / `finally`

- **概要**: CLB スクリプトは例外処理構文に非対応（`_specs/Scripts.md` の非対応構文）。
- **症状**: 「例外時に必ずフラグを戻す」つもりの再入ガードが書けない。ガードを内側に置くと例外でフラグが `true` に固着し、
  以後その画面で導出計算が二度と走らなくなる（`Acceptance.Lines_OnDataChanged` で実測）。
- **検査対象**: `*.mod.cs`
- **検出方法**: 正規表現 `\b(try|catch|finally)\s*[({]`（コメント・文字列除去後）。
- **違反の条件**: 1 件でもマッチしたら違反。あわせて「再入ガードのフラグを DB 検索の外側に置く」ことを
  メッセージで案内する。
- **誤検知の可能性**: `finally` を含む識別子（`finallyDone` 等）は `\b...\s*[({]` の条件で除外される。
- **重大度**: 高
- **出典**: Project.md 2026-08-16 / `_specs/Scripts.md`

### CLB-008 `DateField.Value` へ `DateTime` を代入している

- **概要**: `DateOnly.FromDateTime()` が必須。`DateTime` を入れると保存されない。
- **症状**: **UI には正しく表示される**のに保存データに値が乗らず、NOT NULL 違反で落ちるか、静かに NULL のまま残る。
- **検査対象**: `*.mod.cs` × `*.mod.json`
- **検出方法**: モジュールの `DateFieldDesign` フィールド名の集合 F を作り、各 `f ∈ F` について
  正規表現 `\b{f}\.Value\s*=\s*([^;]+);` で右辺を取る。
- **違反の条件**: 右辺に `DateTime` を含み、かつ `DateOnly` を含まない
  （`DateOnly.FromDateTime(DateTime.Today)` は正当なので `DateOnly` を含めば OK）。
- **誤検知の可能性**: 右辺が `DateTimeField.Value`（別フィールドの値）を参照する場合に文字列 `DateTime` を含みうる。
  その場合も型は合わないので報告して問題ない。
- **重大度**: 高
- **出典**: FB-003 / DOC-006 / Project.md 2026-07-07

### CLB-009 `this.IsViewOnly = true` にしたモジュールでボタンが死ぬ

- **概要**: モジュール全体を閲覧専用にすると `ButtonField` も `pointer-events: none` になり、クリックが無反応になる。
- **症状**: ボタンは disabled 表示にならず通常の見た目・`cursor:pointer` のままで、クリックしても
  ハンドラが呼ばれずコンソールエラーもネットワーク要求も出ない。「確定済み伝票の取消ボタン」が全滅した実例あり。
- **検査対象**: `*.mod.cs` × `*.mod.json`
- **検出方法**: mod.cs に `(this\.)?IsViewOnly\s*=\s*true` があるモジュールについて、
  そのモジュールの `ButtonFieldDesign` / `SubmitButtonFieldDesign` / `AnchorTagFieldDesign` フィールドのうち
  DetailLayouts に配置されているものを列挙し、同じ mod.cs に `\b{ボタン名}\.IsViewOnly\s*=\s*false` があるかを見る。
- **違反の条件**: 明示解除の無いボタンが 1 つでもある。
  （推奨は「入力項目を 1 つずつ `IsViewOnly` にする」方式＝`JournalEntry.LockPostedFields()`。
  その方式を採っている場合は `this.IsViewOnly = true` 自体が無いのでマッチしない）
- **誤検知の可能性**: 閲覧専用時に本当に押させたくないボタンがある場合。その場合も「押せないのに見た目は有効」なので
  `IsVisible = false` にすべきであり、報告は妥当。
- **重大度**: 高
- **出典**: FB-030 / FB-035（2026-08-14 追記）/ ADR-0056 / Project.md 2026-07-20・2026-08-14

### CLB-010 表示専用モジュール（`DbTable` 空）の Detail にボタンがあるのに `IsViewOnly = false` 解除が無い

- **概要**: `DbTable: ""` のモジュールは Detail で既定がビュー専用になり、ボタンが `pointer-events: none` で描画される。
- **症状**: CLB-009 と同じ「見た目は有効・実は無反応」。designcheck も沈黙する。ポータル・取込・一括実行系の画面が丸ごと死ぬ。
- **検査対象**: `*.mod.json` × `*.mod.cs`
- **検出方法**: `DbTable` が空（または `DataSourceName` が空）のモジュールで、DetailLayouts に
  `ButtonFieldDesign` / `SubmitButtonFieldDesign` のフィールドが配置されているものを抽出し、
  対応する mod.cs に `IsViewOnly\s*=\s*false` があるかを見る（`Detail_OnAfterInit` 冒頭に置くのが定石）。
- **違反の条件**: 解除の記述が無い（mod.cs 自体が無い場合も違反）。
- **誤検知の可能性**: ボタンが `IsVisible=false` 前提の飾りである場合。実例は無い。
- **重大度**: 高
- **出典**: FB-035 / ADR-0042 / Project.md 2026-08-02

### CLB-011 一覧リンクの `ModulePageType` が `List` なのに詳細遷移を許している

- **概要**: `List` のままだと `/Module/{id}` のルートが登録されず、詳細 URL が真っ白になる。
- **症状**: 行クリック・直接 URL とも空白ページ。エラーも console 出力も無い。
- **検査対象**: `*.frm.json`
- **検出方法**: 各フレームの `Left.Links[]` / `TopPageModuleDesign` / `OtherPageModuleDesigns[]` について
  `ModulePageType` と `ListPageDesign.ListFieldDesign.CanNavigateToDetail` を突き合わせる。
- **違反の条件**: `ModulePageType == "List"` かつ `CanNavigateToDetail == true`（正しくは `"Auto"`）。
- **誤検知の可能性**: なし。`Detail` × `CanNavigateToDetail:true` の組（現状 4 件）は詳細直開きなので対象外。
- **重大度**: 高
- **出典**: Project.md 2026-07-21

### CLB-012 リンクの `SearchCondition.ModuleName` が遷移先モジュールと食い違う

- **概要**: リンクを複製して作った画面で、絞り込み条件のモジュール名が複製元のまま残る。
- **症状**: 条件が**黙って捨てられ全件表示**になる（「精算処理待ち」ビューが全件になった実例）。
  フレームリンク側の `SearchCondition` はモジュール側 `ListPageFieldDesign.SearchCondition` を上書きするため、
  モジュール側が正しくても効かない。
- **検査対象**: `*.frm.json`
- **検出方法**: 各リンクの `Module` と `ListPageDesign.ListFieldDesign.SearchCondition.ModuleName` を比較する。
  同様に `SearchCondition.Condition.Children[].SearchTargetVariable` が指すフィールドが `Module` に実在するかも見る。
- **違反の条件**: `SearchCondition.ModuleName` が非空かつ `Module` と不一致。
  または `SearchTargetVariable` の先頭要素が遷移先モジュールのフィールド名に無い。
- **誤検知の可能性**: なし（リンクの一覧は必ず `Module` のデータを出す）。
- **重大度**: 高
- **出典**: Project.md 2026-07-16

### CLB-013 遷移先フレームに Module×セグメントが未登録（真っ白）

- **概要**: フレーム跨ぎリンク・`NavigateTo` の遷移先が、遷移先フレームのページ定義に無いと画面が真っ白になる。
- **症状**: URL は変わりサイドバーだけ描画され、コンテンツ領域が**エラーなしで空**。console にもサーバログにも何も出ない。
  「リンクを消す」＝「開けなくする」なので、メニュー整理で簡単に踏む。
- **検査対象**: `*.frm.json` × `*.mod.cs`
- **検出方法**: **既に `Designer/tools/check_navigation.py` が実装済み**。検査 A〜D は次のとおり:
  A) 各フレームの `Links` / `TopPageModuleDesign` / `OtherPageModuleDesigns` が指すモジュールの実在
  B) `Link.PageFrame` が非空のとき、遷移先フレームの登録に `Module`×`ModuleUrlSegment` が存在するか
  C) mod.cs の `NavigateTo("/Frame/Seg")` と `NavigateTo($"/{frame}/Seg")`（`frame = "X"` 代入と `Resolve〇〇()` の
     `return "X";` を候補として収集）のフレーム実在とセグメント登録
  D) `GetModuleUrl("X")` / `GetModuleDataUrl("X")` の X が、呼び出し元モジュールを登録している**全フレーム**で解決可能か
- **違反の条件**: 上記いずれかの不一致。`ModuleUrlSegment` が空のときの既定セグメントは**モジュール名**。
  フレーム素の URL（`/Frame`）は `TopPageModuleDesign` 着地なのでセグメント検査をしない。
- **誤検知の可能性**: D は「そのフレームからはそのコードパスに到達しない」ことがあるため warn 止まり。
- **重大度**: 高
- **出典**: FB-023 / FB-042 / ADR-0056 / Project.md 2026-08-05

### CLB-014 OR 検索フィールドに `SearchValue`（単数）を代入している

- **概要**: `AllowOrSearch: true` の SelectField の検索既定値は `SearchValues`（複数形）で入る。
- **症状**: 単数形に代入しても例外は出ず、**既定条件が効かない**（チェックボックス群が全部未選択のまま）。
  「既定で隠しているつもりのもの」が一覧に出る。
- **検査対象**: `*.mod.cs` × `*.mod.json`
- **検出方法**: モジュールの `AllowOrSearch: true` のフィールド名集合 F を作り、
  各 `f ∈ F` について `\b{f}\.SearchValue\s*=` を検索する（`SearchValues` にマッチしないよう `\b` 境界を使う。
  実装では `{f}\.SearchValue\s*=` の直前に `s` が来ないことを確認する）。
- **違反の条件**: マッチしたら違反（`SearchValues` に直す）。
- **誤検知の可能性**: なし。
- **重大度**: 高
- **出典**: ADR-0057 / Project.md 2026-08-14

### CLB-015 表示専用モジュールで `this.Submit()` を呼んでいる

- **概要**: `DataSourceName` / `DbTable` が空のモジュールの `Submit()` は**戻り値も例外も無く何も起きない**。
- **症状**: 保存したつもりのデータが 1 件も書かれない。行の保存は `row.Submit()` が正解。
- **検査対象**: `*.mod.cs` × `*.mod.json`
- **検出方法**: `DbTable` が空のモジュールの mod.cs で `(^|[^.\w])(this\.)?Submit\s*\(` を検索する
  （`row.Submit()` / `je.Submit()` のような**レシーバ付き**呼び出しは除外するため、直前が `.` でないことを条件にする）。
- **違反の条件**: 自モジュールに対する `Submit()` 呼び出しが 1 件でもある。
- **誤検知の可能性**: なし（表示専用モジュールに保存先が無いのは構造上の事実）。
- **重大度**: 中
- **出典**: FB-004 / DOC-005 / Project.md 2026-07-07

### CLB-016 表示専用ホストの `ListField` に `CanDelete: true`

- **概要**: 行削除はメモリ上の操作で、保存契機の無い表示専用ホストでは DB に届かない。
- **症状**: ゴミ箱ボタンを押すと**画面からは消える**が、再読込・画面再訪で**行が復活する**。
  利用者は削除したつもりで離席するので、データを消し損ねる方向の事故になる。designcheck では検出されない。
- **検査対象**: `*.mod.json` × `*.mod.cs`
- **検出方法**: `DbTable` が空のモジュールの `Fields[]` から `ListFieldDesign` を抽出し、`CanDelete` を見る。
- **違反の条件**: `CanDelete: true`。ただし mod.cs に「DB 側の行と画面の行を突き合わせて明示的に `Delete()` する」
  実装（`Delete()` 呼び出しを含む保存メソッド）がある場合は warn に落としてよい。
- **誤検知の可能性**: `CashEntry.SaveListEdits` のように差分同期を自前実装している場合（現状の唯一の正当例）。
- **重大度**: 中
- **出典**: FB-040 / ADR-0055 / Project.md 2026-08-14

### CLB-017 Boolean の DB 既定が 1 なのに新規作成で未チェックになる

- **概要**: CLB の Boolean フィールドは**新規作成時の初期値が常に未チェック**で、DB の `DEFAULT 1` は効かない。
- **症状**: 「既定 ON のつもり」のフラグが OFF で保存され、作った直後に一覧から消える等の静かな不具合になる。
- **検査対象**: `*.mod.json` × DDL × `*.mod.cs`
- **検出方法**: `BooleanFieldDesign` のフィールドについて DDL の宣言を引き、`DEFAULT 1` を持つ列にマップされているものを抽出。
  対応する mod.cs に `\b{フィールド名}\.Value\s*=\s*true` があるか（`IsNewData` 分岐内が望ましい）を見る。
- **違反の条件**: DB 既定が 1 なのに mod.cs で `true` を明示代入していない。
- **誤検知の可能性**: DB 既定 1 が「SQL 経由で作る行のための既定」で、画面からは OFF 開始が正しいケース。
  その場合は「意図的」と判断して本書に例外を書き足す。
- **重大度**: 中
- **出典**: ADR-0054 / Project.md 2026-08-14

### CLB-018 導出値を `IsUpdateProtected` だけで守っている

- **概要**: `IsUpdateProtected` は**更新時だけ**の保護で、新規作成時は編集できる。
- **症状**: 「明細から自動計算」と説明された金額が新規作成画面では手入力でき、
  再計算ハンドラが無ければ明細合計と一致しない伝票がそのまま保存できる（検収の検収額・消費税額で実測）。
- **検査対象**: `*.mod.json`
- **検出方法**: `IsUpdateProtected: true` のフィールドについて、DetailLayouts 内でそのフィールドを指す
  レイアウト要素（再帰探索で `...Layout.FieldName == フィールド名`）を探し、その要素の `IsViewOnly` を見る。
- **違反の条件**: レイアウトに配置されていて、その要素の `IsViewOnly` が `true` でない。
- **誤検知の可能性**: **高い**。`IsUpdateProtected` には「作成後は変更不可（コード・伝票番号など）」という
  正当な用途があり、それらは新規時に入力できて当然。**導出値かどうかは人が判断する**必要があるので、
  本ルールは warn 相当（「新規作成時は編集できるが、それは意図どおりか？」）として出す。
  レイアウトに置かれていないフィールド（`select_label` 等の `IgnoreModification: true` 系）は対象外。
- **重大度**: 中
- **出典**: ADR-0062 / Project.md 2026-08-16

### CLB-019 範囲検索フィールドに `AllowEmptySearch: true`

- **概要**: 範囲検索フィールド（Date / DateTime / Number / Time）では `AllowEmptySearch` に観測可能な効果が無い。
- **症状**: 「未設定行も含めたい」という意図で立てても**何も変わらない**（NULL 行は常に落ちる）。
  設定が効いているつもりで検索結果を信じてしまう。
- **検査対象**: `*.mod.json`
- **検出方法**: `TypeFullName` が `DateFieldDesign` / `DateTimeFieldDesign` / `NumberFieldDesign` / `TimeFieldDesign` の
  フィールドの `AllowEmptySearch` を見る。
- **違反の条件**: `true`。（NULL を含めたいなら状態列を別に持つか一覧を Query モジュール化するしかない＝ADR-0057）
- **誤検知の可能性**: 将来 CLB が `IncludeNullInRangeSearch` 相当を実装したら見直す。
- **重大度**: 中
- **出典**: FB-043 / DOC-017 / ADR-0057

### CLB-020 検索レイアウトにリンク先参照フィールド（`A.B` 形式）を置いている

- **概要**: `LinkFieldNames` のパス（`SalesOrderRef.PartnerRef` 等）は検索レイアウトでは候補がロードされない。
- **症状**: designcheck は 0 件で通過し、検索画面にドロップダウンも描画されるが**候補が空**で値を選べない＝実質検索不能。
  エラー・警告・ログなし。
- **検査対象**: `*.mod.json`
- **検出方法**: `SearchLayouts` を再帰探索し、レイアウト要素の `FieldName` に `.` を含むものを検出する。
  併せて `LinkFieldNames` にそのパスが登録されているかも確認する（登録されていれば確実にこの罠）。
- **違反の条件**: `SearchLayouts` 配下の `FieldName` に `.` が含まれる。
- **誤検知の可能性**: なし（自モジュールのフィールド名に `.` は使わない命名規約）。
- **重大度**: 中
- **出典**: FB-033 / Project.md 2026-07-23

### CLB-021 `ExecuteSqlField` の `@プレースホルダ` が Parameters と一致しない

- **概要**: SQL 内の `@名` は**フィールド名ではなく DB 列名（`Parameters[].Name`）**で解決される。
- **症状**: バインドされず「Must add values for the following parameters」で **Submit 全体がロールバック**する。
  どのフィールドの問題かは示されない。フィールド名＝列名のときだけ偶然動くので、命名が食い違った瞬間に壊れる。
- **検査対象**: `*.mod.json` × `<Module>.<Field>.sql`
- **検出方法**: `ExecuteSqlFieldDesign` のフィールドについて、SQL 本体を
  `ExecuteSqlSetting.SqlText`（空なら同ディレクトリの `<Module>.<Field>.sql`）から読み、
  正規表現 `@(\w+)` で使用中のプレースホルダ集合 P を取る。
  `ExecuteSqlSetting.Parameters[].Name` の集合 N と突き合わせる。
  さらに N の各要素が、そのモジュールのどれかのフィールドの `DbColumn` と一致するかを確認する。
- **違反の条件**: ① `P - N` が空でない（未定義プレースホルダ＝ロールバック）
  ② N の要素がモジュールのどの `DbColumn` とも一致しない（とくに**フィールド名**と一致してしまっている場合は確実に誤り）
  ③ `N - P` が空でない（未使用パラメータ。warn）
- **誤検知の可能性**: SQL 内のコメント・文字列リテラルに含まれる `@` を除外すること。
  SQLite のメールアドレス等のリテラルに `@` が現れうる。
- **重大度**: 中
- **出典**: FB-001 / DOC-001 / ISSUE-0001 / Project.md 2026-07-08

### CLB-022 `CanCreate: false` のモジュールをスクリプトが `new` して `Submit` している

- **概要**: `CanCreate: false` は UI とスクリプトの**両方**を塞ぐ。
- **症状**: サーバが "This module data cannot be created" で拒否する（トーストは出るが、
  代理生成のように結果を確認していない経路では静かに欠落する）。
- **検査対象**: `*.mod.json` × `*.mod.cs`
- **検出方法**: `CanCreate: false` のモジュール名集合 M を作り、全 mod.cs を `new\s+({M})\s*\(` で検索する。
- **違反の条件**: マッチしたら違反。正しい塞ぎ方は「`CanCreate: true` のまま、
  フレームリンクの `UseNavigateToCreate: false` ＋ `ListField.CanCreate: false`」。
- **誤検知の可能性**: `new` しているが `Submit()` しない（読み取り用のインスタンス化）ケース。
  `new` から同一メソッド末尾までに `Submit(` が無ければ warn に落とす。
- **重大度**: 中
- **出典**: FB-012 / DOC-013 / Project.md 2026-07-07

### CLB-023 SQLite の生成列（`GENERATED ALWAYS AS`）を `DbColumn` に指定している

- **概要**: CLB のスキーマ取得は `PRAGMA table_info` を使うため、生成列を認識しない。
- **症状**: designcheck が「カラムが存在しません」と報告する（この 1 件は静かではないが、
  `sql` CLI からは普通に SELECT できて値も正しいため**原因が分からず時間を溶かす**）。
- **検査対象**: DDL × `*.mod.json`
- **検出方法**: DDL の列定義に `GENERATED\s+ALWAYS\s+AS` を含むものを集めて生成列集合 G を作る。
  `Fields[].DbColumn` が G に含まれるものを検出する。生成列が DDL に存在するだけでも info として報告する。
- **違反の条件**: `DbColumn` が生成列を指している。
- **誤検知の可能性**: なし。回避は「実列 ＋ `AFTER INSERT`/`AFTER UPDATE OF` トリガー」（`ddl/550` が実例）。
- **重大度**: 中
- **出典**: FB-046 / ADR-0061 / Project.md 2026-08-16

### CLB-024 子モジュールの FK 列に `NOT NULL` が付いている

- **概要**: ネスト Submit では子の FK が INSERT 時点で NULL のため、`NOT NULL` を付けると生成が失敗する。
- **症状**: 親子孫の一括 Submit が落ちる（`approval_flow_member` で実測）。
- **検査対象**: DDL × `*.mod.json`
- **検出方法**: 「子モジュール」の集合 C を作る＝どこかのモジュールの `Fields[]` にある
  `ListFieldDesign` / `DetailListFieldDesign` の `SearchCondition.ModuleName` に現れるモジュール。
  C の各モジュールの `DbTable` について、DDL の列定義が `NOT NULL` と `REFERENCES` を**同時に**持つものを検出する。
- **違反の条件**: 上記に該当し、かつその `REFERENCES` 先が親モジュールの `DbTable` である。
- **誤検知の可能性**: **高い**。単一階層の親子で、親が先に保存済みの経路しか無い場合は `NOT NULL` でも動く
  （本リポジトリにも正当な `NOT NULL REFERENCES` が多数ある）。
  そのため **多段ネスト（孫を持つ子）に限定して報告**し、それ以外は info 止まりにする。
- **重大度**: 中
- **出典**: FB-015 / DOC-004 / Project.md 2026-07-05

### CLB-025 `Delete()` の戻り値を検査していない

- **概要**: 検索インスタンスの `Delete()` は子の FK 制約で失敗して `false` を返す（`DeleteTogether` のカスケードは UI 削除のみ）。
- **症状**: 戻り値を見ないと「トーストは成功・DB には残存」の静かな失敗になる（実際に踏んだ）。
- **検査対象**: `*.mod.cs`
- **検出方法**: 正規表現 `^\s*[\w\.\[\]\(\)]+\.Delete\(\)\s*;\s*$`（行全体が `〜.Delete();` の文）を検出する。
  戻り値を使う書き方（`if (!x.Delete())` / `var ok = x.Delete()`）はこの形にならない。
  併せて、削除対象モジュールが `ListFieldDesign` の子を持つ（＝子持ち）場合は重大度を上げる。
- **違反の条件**: 戻り値を捨てた `Delete()` 文が存在する。
- **誤検知の可能性**: `this.Delete()`（自モジュールの削除・UI 経路）は成功する前提で書けるため warn 止まりでよい。
- **重大度**: 中
- **出典**: FB-028 / Project.md 2026-07-16・2026-07-19

### CLB-026 検索既定を持つ一覧へ `GetModuleUrl` で遷移している

- **概要**: `OnSearchInitialization` はサイドバーリンクに自動付与される `?initialize_search=true` でしか発火しない。
- **症状**: スクリプトからの一覧遷移（削除後の戻りなど）では**既定条件が効かず全件表示**になる。エラーは出ない。
- **検査対象**: `*.mod.json` × `*.mod.cs`
- **検出方法**: `SearchLayouts.<name>.OnSearchInitialization` が非空のモジュール名集合 S を作り、
  全 mod.cs を `GetModuleUrl\("({S})"\)` で検索する。
- **違反の条件**: マッチしたら warn（意図的に全件を見せたいこともあるため）。
  対処はクエリパラメータを自前で付けるか、既定条件を SearchCondition 側に持たせる。
- **誤検知の可能性**: あり（上記）。報告文に「既定条件は効かない。それでよいか」を明記する。
- **重大度**: 中
- **出典**: Project.md 2026-08-14

### CLB-027 `AnchorTagField` に `OnClick` を設定している

- **概要**: `AnchorTag` は `OnClick` 指定でも `href` を持ち、サーバ往復を伴う `OnClick` が href ナビゲーションとの
  レースに負けて無反応になることがある。
- **症状**: クリックしても何も起きない（あるいは意図しないページへ飛ぶ）。エラーは出ない。
- **検査対象**: `*.mod.json`
- **検出方法**: `TypeFullName` が `AnchorTagFieldDesign` のフィールドの `OnClick` を見る。
- **違反の条件**: `OnClick` が非空。スクリプト遷移する項目は `LabelField` + `OnClick` 方式に統一する。
- **誤検知の可能性**: `Url` が空で純粋にハンドラだけの用途なら競合しない可能性がある（未実測）ため warn。
- **重大度**: 中
- **出典**: Project.md 2026-08-05（ADR-0045 のサイドバー実装）

### CLB-028 `DbTable` がビューなのに `INSTEAD OF` トリガーが無い

- **概要**: 認証テーブル等をビューにすると、INSERT/UPDATE はトリガー無しでは通らない。
- **症状**: 新規 DB 構築で admin 作成が失敗する／パスワード変更 API がビュー越しに書けない（どちらも実測）。
  画面上は「更新に失敗しました」だけで、ビューが原因とは分からない。
- **検査対象**: `*.mod.json` × DDL
- **検出方法**: DDL の `CREATE VIEW (\w+)` からビュー名集合 V を作る。`DbTable ∈ V` のモジュールについて、
  DDL に `CREATE TRIGGER ... INSTEAD OF INSERT ON {view}` / `INSTEAD OF UPDATE ON {view}` があるかを確認する。
  併せて `app.clprj` の `PasswordCheckUserTableInfo.TableName` がビューを指す場合も同じ検査をする。
- **違反の条件**: `CanCreate: true` なのに INSTEAD OF INSERT が無い／`CanUpdate: true` なのに INSTEAD OF UPDATE が無い。
- **誤検知の可能性**: 読み取り専用ビュー（`CanCreate/CanUpdate` とも false）は対象外。
- **重大度**: 中
- **出典**: ADR-0059 / Project.md 2026-07-16・2026-08-16

### CLB-029 レガシー `TopPageModule` が `TopPageModuleDesign.Module` と食い違う

- **概要**: `rename-module` は旧形式プロパティ `TopPageModule` を追従しない。
- **症状**: designcheck は findings 0 のまま。現ランタイムは `TopPageModuleDesign` を優先するので実害は出ていないが、
  旧プロパティを読む経路があれば静かに壊れる。
- **検査対象**: `*.frm.json`
- **検出方法**: トップレベルの `TopPageModule` と `TopPageModuleDesign.Module` を比較する。
- **違反の条件**: 両方非空で不一致。
- **誤検知の可能性**: なし。
- **重大度**: 低
- **出典**: FB-036 / ADR-0042

### CLB-030 検索行に `IsWrap: true` が付いていない

- **概要**: 本リポジトリの標準は「検索レイアウトの全行 `IsWrap: true`」。
- **症状**: 1344〜1514px 程度の画面幅で検索欄が右に見切れ、横スクロールが発生する（実測不具合）。
- **検査対象**: `*.mod.json`
- **検出方法**: `SearchLayouts.<name>.Layout.Rows[].IsWrap` を見る。
- **違反の条件**: `true` 以外（全 48 モジュールへ一括適用済みなので、非 true は新規混入）。
- **誤検知の可能性**: 意図的に 1 行固定にしたい行。実例は無い。
- **重大度**: 低
- **出典**: Project.md 2026-08-03

### CLB-031 検索行に入力欄を 4 組以上詰めている

- **概要**: 行が折り返すとラベル列（`VerticalAlignment: Middle`）と入力列（上端）が縦にずれ、泣き別れて見える。
- **症状**: ラベルと入力欄が別々の行に見え、どの欄が何の条件か分からなくなる。
- **検査対象**: `*.mod.json`
- **検出方法**: `SearchLayouts.<name>.Layout.Rows[]` の各行について `Columns[].Layout.FieldName` を集め、
  そのフィールドの `TypeFullName` が `LabelFieldDesign` でないものを数える。
- **違反の条件**: 1 行あたり 4 個以上。あわせて `AllowOrSearch: true` の SelectField（縦長のチェックボックス群）が
  他の入力欄と同じ行にある場合も報告する（単独行が推奨）。
- **誤検知の可能性**: 幅の狭い入力欄ばかりなら 4 組でも収まることがある。warn 止まりでよい。
- **重大度**: 低
- **出典**: Project.md 2026-08-14

### CLB-032 `PasswordField` の確認欄が意図せず並んでいる

- **概要**: `PasswordField` は 1 フィールドにつき `<input type=password>` を**必ず 2 つ**描画する（出し分け不可）。
- **症状**: 3 欄のパスワード変更画面が 6 欄になる。「現在のパスワード」にまで確認欄が付く。
  プレースホルダは英語固定（`Password` / `Password (confirmation)`）。
- **検査対象**: `*.mod.json` × `Designer/Design/app.css`
- **検出方法**: 同一 DetailLayout に `PasswordFieldDesign` のフィールドが 2 つ以上配置されているモジュールを検出し、
  `app.css` に `[data-module="{モジュール名}"]` と `.password-confirm` を含むルールがあるかを見る。
- **違反の条件**: 2 欄以上あるのに確認欄を隠す CSS が無い。
- **誤検知の可能性**: `SubmitButton` で保存するユーザー管理画面では確認欄が機能するので**隠さないのが正しい**。
  1 フィールドだけのモジュールは対象外にする。
- **重大度**: 低
- **出典**: FB-045 / ADR-0059 / Project.md 2026-08-16

### CLB-033 `IsVisible = false` にした固定幅カラムの穴埋め CSS が無い

- **概要**: フィールドを非表示にしても `Width` 指定の grid-column は空 div として残る。
- **症状**: 権限で非表示にしたボタンの位置に空白が並び、ボタンが中途半端な位置に浮く。
- **検査対象**: `*.mod.cs` × `*.mod.json` × `app.css`
- **検出方法**: mod.cs の `\b(\w+)\.IsVisible\s*=\s*false` からフィールド名を取り、
  そのフィールドが置かれたレイアウト列（`...Columns[]`）に `Width` が明示されているかを見る。
  さらに `app.css` に `[data-module="{モジュール名}"]` を含み `:not(:has(.field-layout))` を含むルールがあるかを確認する。
- **違反の条件**: 固定幅カラムのフィールドを非表示にしていて、畳み込み CSS が無い。
- **誤検知の可能性**: 同じ行の他の列が伸びる構成なら穴は目立たない。warn 止まり。
- **重大度**: 低
- **出典**: FB-037 / ADR-0042 / Project.md 2026-08-02・2026-08-05

### CLB-034 `CurrentUser.<SelectField>.DisplayText` を参照している

- **概要**: `CurrentUser` の SelectField は候補が未ロードだと `DisplayText` が空になる。
- **症状**: 通知本文・トーストの表示名が空文字になる（実測バグ）。例外は出ない。
- **検査対象**: `*.mod.cs`
- **検出方法**: 正規表現 `CurrentUser\.\w+\.DisplayText` を検索する。
  対象フィールドが `AppUser` モジュールの `SelectFieldDesign` であることを突き合わせると精度が上がる。
- **違反の条件**: マッチしたら違反。表示名が要るなら該当マスタを `ModuleSearcher` で取り直す。
- **誤検知の可能性**: `TextField` 等の `DisplayText` は問題ない（型で絞れば除外できる）。
- **重大度**: 低
- **出典**: ADR-0046 / Project.md 2026-08-06

### CLB-035 `LoadingService.StartLoading()` が `MessageBox.Show()` より前にある

- **概要**: ローディングオーバーレイが確認ダイアログの上に重なり、ボタンが押せなくなる。
- **症状**: ダイアログは出ているのにクリックできず、操作が詰む。
- **検査対象**: `*.mod.cs`
- **検出方法**: メソッド単位に粗く分割し（`^\s*(?:public |private |protected )?[\w<>\[\]]+\s+\w+\s*\(.*\)\s*$` または
  `\n    }` を区切りに使う）、同一ブロック内で `StartLoading` の出現位置 < `MessageBox.Show` の出現位置 なら違反。
- **違反の条件**: 上記の順序。正しい順序は「ガード検索 → ダイアログ → using loading → 本処理」。
- **誤検知の可能性**: 分割が粗いため別メソッドをまたいで誤検出しうる。warn 止まりでよい。
- **重大度**: 低
- **出典**: Project.md 2026-07-23

### CLB-036 自作 SQL の日付比較が `date()` で正規化されていない

- **概要**: CLB は DATE 列へ `YYYY-MM-DD HH:MM:SS` で書き込み、seed は素の `YYYY-MM-DD` なので、
  辞書順比較が境界日で偽になりうる。
- **症状**: 月末日の伝票が「対応する月次期間がありません」になる等、**境界日だけ**外れる。
  ほとんどの日付で正しく動くので発見が遅れる。
- **検査対象**: `*.Query.sql` / `<Module>.<Field>.sql` × DDL
- **検出方法**: DDL インデックスから宣言型が `DATE` / `DATETIME` の列名集合 D を作る。
  SQL 内で `\b({D})\b\s*(=|>=|<=|>|<|BETWEEN)` にマッチする箇所を探し、
  その識別子の直前に `date(` / `datetime(` / `strftime(` が無いものを報告する。
  `GROUP BY` 句に生の列名が現れる場合も同様に報告する。
- **違反の条件**: DATE/DATETIME 列が正規化なしで比較・GROUP BY されている。
- **誤検知の可能性**: **高い**。列名がテーブルをまたいで重複する（`entry_date` 等）ため、
  実際には正規化不要な文脈も拾う。**warn 専用**とし、件数が多い場合はファイル単位のサマリにする。
- **重大度**: 低
- **出典**: FB-008 / DOC-007 / Project.md 2026-07-05・2026-07-06・2026-07-07

---

## 機械検査が難しいもの（人間／エージェントの目視に回す）

以下は静かな失敗として実在するが、**静的解析では「意図」と区別できない**ため、
リンタには載せず `docs/qa/` の目視チェックリスト・実機検証に回す。無理にルール化すると
警告が常時数百件出てリンタ全体が信用されなくなる。

| 事象 | 機械検査できない理由 | 出典 |
|---|---|---|
| `OnDataChanged` がスクリプト生成のモジュールにも発火し、勘定科目マスタの既定が勝手に入る | 「値をセットしない＝既定が入らない」という**前提の誤り**であり、コード上は正常に見える。どの経路でどの既定が入るかは実データを見ないと判定できない | ADR-0053 |
| `ListField.OnDataChanged` の無条件な導出計算が、外から書き込んだ値を同じイベント内で潰す | 「導出してよい行か」の条件はドメイン知識（手入力行か写し行か）。ハンドラの有無だけでは違反にならない | 2026-08-12 実測（改善候補 A-1） |
| レイアウトに出ていないフィールド・ChildModule の `.Value` が遅延ロードで null | 「その値が確実に要る処理か」がコードから判定できない。`DataOnlyFields` 登録漏れの検出も、スクリプトが参照するフィールドの静的抽出が必要で精度が出ない | FB-005 / DOC-003 |
| 判定の二重実装（`BuildPlan` と件数 SQL）が食い違う | C# と SQL の意味的同値性の検証になる。突合用の検証 SQL（`docs/tests/portal_billing_count_check.sql`）を実機チェックリストに入れる運用で担保する | ADR-0060 |
| 新規未保存の子行 Id（`@temporary:GUID`）を数値検索に渡して 500 | ガードが必要なのは「未保存の行を検索条件に使う経路」だけで、Id の出所を追う必要がある。データフロー解析が要る | FB-006 |
| 期間解決の日付比較で境界日（月末）が偽になる | 「月初日方式になっているか」はロジックの意味の問題。CLB-036 で拾えるのは正規化漏れだけ | FB-008 |
| `ListField` の自動ロードでは `OnDataChanged` が発火しない（明示 `Reload()` なら発火する） | 「ロード完了フックとして依存しているか」はハンドラの中身の意図次第 | Project.md 2026-07-21（7/22 訂正） |
| `ListLayouts` の行イベントで `IsVisible` を切り替えても反映されない（`ClassName` は効く） | 行イベント内のどの操作が反映されるかは実測ベースで、静的には区別できない | Project.md 2026-07-23 |
| 参照候補の絞り込みだけで実行時ガードが無い（旧データ・フラグ解除で誤選択が通る） | 「候補の絞り込み」と「実行時の関門」の対応関係はドメイン知識 | ADR-0063 |
| 削除前に他テーブルからの FK 参照を null にする順序 | 参照元の洗い出しは DDL から可能でも、「削除前に null にしているか」の順序検証は制御フロー解析が要る | FB-028 / Project.md 2026-07-19 |
| ブラウザ自動操作の罠（合成イベントで変更フラグが立たない・トーストが拾えない・SelectField に値が伝わらない） | デザインファイルの静的検査対象外（E2E スクリプト側の規約） | FB-021 / DOC-012 / Project.md 2026-07-26 |
| `HorizontalAlignment` 以外の未知の列挙値 | 有効値の一覧が `Designer/ClaudeCodeForDesigner/_defaults/`（再生成される生成物）にしか無い。将来 `_defaults/` から列挙値を機械的に抽出できれば CLB-001 を一般化できる（**拡張候補**） | FB-031 の一般化 |
