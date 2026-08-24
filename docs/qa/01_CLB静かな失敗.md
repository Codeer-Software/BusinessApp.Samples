---
title: CLB「静かな失敗」チェックリスト
status: current
scope: 全体
audience: [開発]
growth: append
updated: 2026-08-23
supersedes: []
related: [../11_CLB改善提案/README.md]
---
# CLB「静かな失敗」チェックリスト

CLB には**エラーにも `designcheck` の指摘にもならず、静かに壊れる**挙動が多数ある。
本書はそれらを 1 症状 1 ルールで並べたもので、レビューと機械検査（`tools/clb/lint_design.py`）の根拠になる。

> **出所**: 前回プロジェクト（`BusinessApp_old`）で実測・確立した知見を、アプリ非依存の部分だけ引き継いだ。
> ランタイム・デザイナは前回 1.3.18 まで、本プロジェクトは **1.3.20** で確認する。
> **本プロジェクトで再確認していないものは「前回実測」と明記してある。** 1.3.20 で挙動が変わっている
> 可能性があるため、踏んだら確認日と版を添えて本書を更新する。

**運用の順序**: `designcheck` → `lint_design.py` → `sql` で DB 確認 → 実機（ブラウザ）。
`designcheck` が緑でも動作は何も保証されない。

**機械検査の対象**（`tools/clb/lint_design.py`。コミット前フックで自動実行）:
A-01（揃えの旧値）・A-06（整数書式）・B-01（`try`/`catch`）・C-01（`OrderBy` の `.Value`）・
D-05（`ModulePageType`）・D-07（複製した検索条件の指し先）・D-09（検索行の折り返し）・
D-10（ラベル列の縦揃え）・F-01（`OnValidateInput`）・F-09（予約名のデザイン型）
＋ 論理削除の不在（本プロジェクトの方針）。**それ以外は人が見る。**

## A. 型・書式（値が黙って落ちる）

| # | 症状 | ルール |
|---|---|---|
| A-01 | 右寄せしたはずの列が左寄せになる。デザイナで保存すると意図が永久に失われる | `HorizontalAlignment` の有効値は `Start` / `Center` / `End` / `Stretch`。**旧値 `Left` / `Right` はエラーにならず `Start` に化ける**（前回実測 1.3.4 で変更） |
| A-02 | 画面は正常なのに帳票 SQL だけ全滅する | SQLite の日付列は **`DATE` / `DATETIME` / `TIME` で宣言する**。`TEXT` にすると CLB が US 書式（`08/14/2026`）で書き、`date()` が NULL を返す |
| A-03 | 月初の朝だけ集計が 1 か月ずれる | SQL の `date('now')` は **UTC**。必ず `date('now','localtime', ...)` とし、`'localtime'` を**先頭の修飾子**に置く。`CURRENT_TIMESTAMP` / `CURRENT_DATE` も UTC |
| A-04 | 範囲の下端で行が消える／期間解決が失敗する | `DATE` 列の正規形は **`YYYY-MM-DD 00:00:00`**（CLB が書く形）。`ModuleSearcher` の日付比較も時刻付きで渡されるため、時刻なしの行が辞書順比較で落ちる。seed の日付リテラルにも ` 00:00:00` を付ける |
| A-05 | UI には表示されるのに保存されない | `DateField.Value` への代入は **`DateOnly.FromDateTime(...)` 必須**。`DateTime` を入れると保存に乗らない |
| A-06 | ゼロ埋めが実行時に落ち、以降のハンドラが止まる | スクリプトの数値は `decimal` に統一される。**`ToString("D3")` 等の整数専用書式は実行時に `Format specifier was invalid.`**。文字列補間 `$"{n:000}"` を使う |
| A-07 | 「1.25」のような端数が出て同値判定が壊れる | スクリプトの `/` は**整数どうしでも decimal 除算**。切り捨てたいなら `Math.Floor(...)` を通すか **`int` で受ける** |
| A-08 | 本人なのに本人と判定されない／親行が見つからない | id の比較は **`$"{a.Value}" == $"{b.Value}"`** で文字列化してから行う。動的型なので型が違うと `==` が黙って false になる。金額・日付の比較は普通に `==` `<` でよい |

## B. スクリプトの言語制約（ロード時または実行時に落ちる）

| # | 制約 | 代替 |
|---|---|---|
| B-01 | `try` / `catch` / `finally` が使えない | 再入ガードのフラグは**例外を出しうる処理（DB 検索）の外側**に置く。内側だと例外時に固着する |
| B-02 | ユーザー定義関数の `ref` / `out` が呼び出し元に伝わらない（前回実測・静かな失敗） | 戻り値が 2 つ要るならモジュールレベル変数で受け渡す。**.NET 組み込みの `int.TryParse(s, out x)` 等は正常に動く** |
| B-03 | 式メソッド `=>` と複数引数ラムダが使えない | ブロック体で書く。ソートは手書き。単一引数ラムダは `ModuleSearcher` の条件メソッド内のみ可 |
| B-04 | 名前付き引数 `name: value` が使えない | 位置引数で渡す |
| B-05 | Null 条件インデクサ `?[]` が使えない | `?.` と `??` は使える |
| B-06 | `List<ModuleBase>` を引数型にできない（`List 不正なタイプです`） | 引数なしのヘルパにして自前検索するか、`List<decimal>` 等に詰め替える |
| B-07 | `ModuleSearcher.Execute()` の結果を `List<object>` 引数で受けると**無言で停止**する（前回実測） | `Execute()` の結果を引数で持ち回らない |
| B-08 | `AddRows()` の多重定義解決が外れて実行時に落ちる（前回実測・`designcheck` は緑） | `AddRows(int)` と `AddRows(List<Module>)` があり、引数の静的型が int に決まらないとリスト側へ流れて null が渡る。**リテラル・その場のメンバアクセス・1 文で確定する三項演算子**の形にする |
| B-09 | フィールド値と C# の `DateTime` を `<` `>` で比較できない | `yyyy-MM-dd` の ISO 文字列に寄せて `string.CompareOrdinal` で比べる |

## C. API の落とし穴

| # | 症状 | ルール |
|---|---|---|
| C-01 | 採番が常に同じ番号を返す | `OrderBy` / `OrderByDescending` のラムダは**必ず `.Value` まで書く**。付けないとエラーにならず**ソートが無効**になる |
| C-02 | 並び順が黙って 1 本だけになる | `OrderBy` は内部で `SortConditions.Clear()` を呼ぶ。**2 本目以降は `ThenBy` / `ThenByDescending`** |
| C-03 | トーストは成功なのに DB に残る | 子を持つモジュールの `Delete()` は FK 制約で失敗して `false` を返す。**戻り値を必ず検査**する。明細を 1 行ずつ消してから親を消す |
| C-04 | 「保存したはずの値が戻る」 | `ListField.OnDataChanged` の中で無条件に導出計算すると、外から書き込んだ値が同じイベント内で潰れる。**導出してよい行かを条件付ける** |
| C-05 | 「勝手に値が入る」 | `OnDataChanged` は**スクリプトで作ったモジュールにも発火**する。既定と違う値にしたいなら明示的に代入する |
| C-06 | 行に値を入れる前に集計が走る | `ListField.AddRow()` はその場で `OnDataChanged` を発火させる（前回実測） |
| C-07 | 新規作成中の行を消すと保存が落ちる | `DeleteAllRows()` は未保存行（`@temporary:guid`）まで削除キューに入れる。**1 行ずつ `DeleteRow`** する |
| C-08 | `FormatException`（サーバ 500） | 新規未保存の Id は `@temporary:guid`。数値列の検索条件に渡さない。`IsNewData` でガードする |
| C-09 | 削除しても再読込で復活する | `ListField` の行削除はメモリ上の操作。保存契機が無いホストでは DB に届かない |
| C-10 | `ReloadWithLock()` が無い | 現在の公開 API に存在しない。`Reload()` を使う |
| C-11 | 未保存のモジュールで `Reload()` を呼ぶと例外 | `IsNewData` でガードする |
| C-12 | `ExecuteAsync` に渡したパラメータで実行時例外（`The member p1 of type ParamAndRawDbTypeName cannot be used as a parameter value`） | **`IDbAccessor` は Query と Execute でパラメータ辞書の型が違う。** `QueryAsync` は `Dictionary<string, ParamAndRawDbTypeName>`、`ExecuteAsync` / `InsertAsync` は `Dictionary<string, object>`（生値）。C# の `new() { ... }` は代入先の型に合わせるので、包んだまま書いてもコンパイルが通り、実行時に Dapper が落とす（2026-08-24 実測 1.3.20） |

## D. 表示・レイアウト

| # | 症状 | ルール |
|---|---|---|
| D-01 | ボタンが見た目は有効なのに無反応 | `this.IsViewOnly = true` にすると `ButtonField` も `pointer-events:none` になる。**入力項目を 1 つずつ `IsViewOnly` にする**か、ボタンだけ `IsViewOnly = false` で解除する |
| D-02 | 表示専用モジュールの Detail でボタンが押せない | `DbTable` 空 **かつ CRUD 3 フラグが全て false** のとき既定がビュー専用になる。`Detail_OnAfterInit` 冒頭で `IsViewOnly = false;` |
| D-03 | 読み取り専用クエリモジュールで `ButtonField` の `OnClick` が発火しない | 代わりに `AnchorTagField` の `OnClick` を使う（`Module` と `Url` を空にする） |
| D-04 | `AnchorTagField` の `OnClick` が無反応 | `AnchorTag` は `OnClick` 指定でも `href` を持ち、サーバ往復を伴うハンドラが href ナビゲーションとのレースに負ける。**スクリプト遷移は `LabelField` + `OnClick`** |
| D-05 | 詳細 URL が真っ白 | 一覧→詳細のリンクは `ModulePageType: "Auto"`。`"List"` だと `/Module/{id}` のルートが登録されない |
| D-06 | メニューから消したモジュールが真っ白 | `OtherPageModuleDesigns` に登録する |
| D-07 | 条件が黙って捨てられ全件表示になる | リンクを複製したら `ListPageDesign.ListFieldDesign.SearchCondition.ModuleName` を遷移先に直す |
| D-08 | 非表示にしたフィールドの位置に空白が残る | 固定幅カラムは空 div として残る。`app.css` で `.grid-column:not(:has(.field-layout)){display:none}` |
| D-09 | 検索欄が右に見切れる | 検索レイアウトの行は **`IsWrap: true` を標準**にする。1 行は 3 組（ラベル＋入力）まで |
| D-10 | ラベルだけがセル上端に張り付く | 「ラベル列 + 入力列」の 2 カラム行では、**ラベル列に `VerticalAlignment: "Middle"` を必ず設定** |
| D-11 | 行ごとのスタイル制御 | `ListLayouts[""].OnAfterInitialization` は**行モジュールごとに発火**する。`ClassName` の付与は効くが、`IsVisible` の切替は反映されない |

## E. 検索

| # | 症状 | ルール |
|---|---|---|
| E-01 | 検索の初期値が入らない | 検索ページは `.Value` ではなく **`SearchValue`**（単一）／**`SearchMin` / `SearchMax`**（範囲）で動く |
| E-02 | OR 検索の既定値が入らない | `AllowOrSearch: true` の `SelectField` は **`SearchValues`（複数形）** |
| E-03 | `OnSearchInitialization` が発火しない | URL に `?initialize_search=true` が要る。サイドバーの Link 経由なら自動で付く。スクリプトの `GetModuleUrl` では付かない |
| E-04 | ホストされた検索で発火しない | `SearchFieldDesign.SearchInitializationTriggerUrlParameter` に `initialize_search` を設定する |
| E-05 | 候補が空で選べない | 検索レイアウトに `LinkFieldNames` のパス（`A.B` 形式）を置かない |
| E-06 | 未設定（NULL）の行が必ず落ちる | 範囲検索は `>=` を発行するため NULL 行が除外される。`AllowEmptySearch` は効かない。「未設定＝上限なし」を表現したいなら状態列を別に持つ |
| E-07 | 検索条件の Select 連動が効かない | 親の `OnSearchDataChanged` で `親.Value = 親.SearchValue;` をコピーする |
| E-08 | 現在ユーザーで絞りたい | `FieldVariableMatchCondition` の `Variable` に **`CurrentUser.Id.Value`** を直接書ける |

## F. 保存・権限

| # | 症状 | ルール |
|---|---|---|
| F-01 | 保存ボタンを押しても何も起きない | `OnValidateInput` は `false` を返すと**無言で保存を止める**（表示もサーバ通信もない）。**使わない**。関門はサーバ側に置く |
| F-02 | 検証が呼ばれない | 標準の `SubmitButtonFieldDesign` は `OnClick` を持たず、**スクリプトを一切通らない**。検証が要るなら `ButtonField` + `OnClick` + `this.Submit()` |
| F-03 | 保存したデータが 1 件も書かれない | 表示専用モジュール（`DbTable` 空）の `this.Submit()` は何も起きない。行単位で `row.Submit()` する |
| F-04 | スクリプトの `new` が拒否される | `CanCreate: false` は UI とスクリプトの両方を塞ぐ。UI だけ塞ぐなら PageFrame の `UseNavigateToCreate: false` ＋ `ListField.CanCreate: false` |
| F-05 | 新規は編集できてしまう | `IsUpdateProtected` は**更新時だけ**の保護。導出値はレイアウト要素の **`IsViewOnly: true`** で守る |
| F-06 | 追加はできるのに直せない・消せない | `DataWriteCondition` は画面側で評価される。条件が参照する列がレイアウトにも `DataOnlyFields` にも無いと null になり、常に偽になる |
| F-07 | 画面のインスタンスの値が黙って落ちる | 別インスタンスで `Submit()` した後は、画面のインスタンスに値を入れ直してから表示を組み直す |
| F-08 | `Submit()` の失敗に気づけない | 戻り値は `bool?`（`null`=送信なし / `false`=失敗 / `true`=成功）。**必ず検査**して失敗を通知する |
| F-09 | 一覧も詳細も正しく出るのに、更新すると必ず「更新に失敗しました」。`designcheck` は緑、`POST /api/module_data` は 200、サーバログにも何も出ない | **予約名フィールドは専用のデザイン型でなければならない**（2026-08-24 実測 1.3.20）。`OptimisticLocking` を `NumberFieldDesign`、`Creator` / `Updater` を `NumberFieldDesign` にしていたのが原因。`OptimisticLocking` は `OptimisticLockingFieldDesign` ＋ **SQLite では `IncrementVersion: true`**、`Creator` / `Updater` は `TextFieldDesign` にする |
| F-10 | 新規作成したマスタが最初から無効になり、入力候補に出ない | **Boolean の初期値は DB の `DEFAULT` を見ない。**画面は必ず false 始まりになる（2026-08-24 実測 1.3.20）。`DetailLayout.OnAfterInitialization` で `IsNewData` ガードを付けて代入する。新規判定は `Id.Value == null` ではなく `IsNewData` |
| F-11 | サーバ側の関門で親子を見ようとすると、子の `ModuleSubmitData` が 1 つも無い | **ヘッダ＋明細の保存は `ModuleSubmitData` 1 つにまとまって届く。**親も子も同じ `Add` / `Update` に混ざって入る（2026-08-24 実測 1.3.20）。`ModuleSubmitData.ModuleName` ではなく **`ModuleData.Name`** で見分ける |
| F-12 | サーバ側の関門で読んだ値が既定値（日付が `0001-01-01`、明細が 0 件）になる | **`ModuleData` には変更されたフィールドしか入らない。**画面で触っていない項目は `Fields` に存在しない（`Id` と `OptimisticLocking` は常に来る）。書き込みたいフィールドも**無ければ自分で作る**必要がある（2026-08-24 実測 1.3.20）。<br>**送られてきた差分だけで業務検証をしてはいけない。** 既存データを開いて 1 項目だけ変えた保存では、他の項目が差分に載らず必ず誤判定する。いったん保存させてから DB を読み直して検証し、違反なら例外を投げて巻き戻す |
| F-13 | 保存後に状態を変える更新が DB のトリガに弾かれる | 不変にした行（計上済みの仕訳など）は、**最初から最終状態で書くと子行を足せない**。「下書きで書く → 読み直して検証 → 状態を進める」の順にする。`SubmitAsync` の中で投げた例外は保存ごと巻き戻る（2026-08-24 実測。下書き行も採番も残らないことを確認） |
| F-14 | 書き込み条件から外れた行で、ボタンを押しても<b>何も起きない</b> | `DataWriteCondition` を満たさない行は、入力欄がラベル表示に変わり、`ListField` の追加・削除も消える（読み取りは通る）。**ただしボタンフィールドは残り、押しても無反応になる**（2026-08-24 実測 1.3.20）。`OnAfterInitialization` で `IsVisible = false` にして消す |

## G. ブラウザ自動操作（アプリの不具合ではない）

| # | 症状 | 対処 |
|---|---|---|
| G-01 | クリックが Blazor に届かない | `javascript_tool` で `element.click()` を呼ぶ（テキストノードの**親要素**を叩く）。確認ダイアログのボタンは座標クリックで通る |
| G-02 | 5 欄入力したのに 3 欄しか保存されない | 合成イベントでは `.Value` は入るが**変更フラグが立たない**。実クリック＋実タイピングを使い、**結果は DB で確認**する |
| G-03 | `<input type=date>` が壊れる | JS で `el.value='YYYY-MM-DD'` を設定し `input`/`change` を dispatch する |
| G-04 | 検索の SelectField に値が伝わらない | 実クリック＋キーボード（Down/Up）で操作する |
| G-05 | トーストが検出できない | **判定材料にしない**。挙動の確認は DB・サーバログ・戻り値で行う |
| G-06 | ログインセッションが化ける | ブラウザのクッキー壺は 1 つ。並列で別ユーザーにログインしない |

## H. DB・スキーマ

| # | ルール |
|---|---|
| H-01 | 主キーは `INTEGER PRIMARY KEY AUTOINCREMENT`。`TEXT` で GUID を保存しない |
| H-02 | 親子の FK 列に `NOT NULL` を付けない（CLB は子の FK を後埋めする） |
| H-03 | CLB は SQLite の**生成列**（`GENERATED ALWAYS AS`）を認識しない（`PRAGMA table_info` が返さない）。実列＋トリガーで代替する |
| H-04 | `DbTable` にビューを指定するなら `INSTEAD OF INSERT` / `INSTEAD OF UPDATE` トリガーが要る |
| H-05 | スキーマ変更後は**サーバ再起動が必須**（列定義が static にキャッシュされる） |
| H-06 | `ExecuteSqlField` の `@プレースホルダ` は**フィールド名ではなく DB 列名**で解決される |

## J. デザイン enum

| # | 症状 | ルール |
|---|---|---|
| J-01 | `designcheck` が「列挙型名がモジュールのフィールド名と重複しています。スクリプトではフィールド名が優先されます」と報告する | **デザイン enum は複数形で名づける**（`TaxationTypes` / `RateKinds`）。enum 名はプロジェクト全体で 1 つの型名空間に入り、単数形だと同名のフィールドと必ず衝突する（2026-08-24 実測 1.3.20） |

## I. その他

| # | ルール |
|---|---|
| I-01 | `LoadingService.StartLoading()` を `MessageBox.Show()` より**前**に開始しない（オーバーレイがダイアログに重なって押せなくなる） |
| I-02 | `Logger.Warn` は画面にトーストとして出る。行ごとに発火するフックで使うと画面が埋まる |
| I-03 | `CurrentUser` の `SelectField` の `DisplayText` は候補未ロードだと空になる |
| I-04 | モジュールスクリプトのメソッドは**別モジュールからインスタンス経由で呼べる**（共通化に使える） |
| I-05 | 参照フィールドの候補を絞っても、**既存レコードの現在値は候補に残る**（過去データの表示は壊れない）。ただし検索フォームでは絞り込めなくなる |
| I-06 | `rename-*` の後は必ず `designcheck` を実行し、リンク越しの残存参照を手で直す |
