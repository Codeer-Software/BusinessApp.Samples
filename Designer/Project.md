---
title: Project.md（プロジェクト固有ルール）
status: current
scope: 全体
audience: [開発]
updated: 2026-08-17
supersedes: []
related: []
---
# Project.md（プロジェクト固有ルール）

このデザインプロジェクト固有の前提を書く。
`ClaudeCodeForDesigner/` の汎用ルールはここには書かない（プロジェクト固有のことだけ）。
企画・仕様・進捗は `../docs/`（索引: `../docs/README.md`）、判断の経緯は `../docs/decisions/` にある。

## 接続先 DB / データソース

- **BusinessAppSQLite**（本アプリの唯一のデータソース。→ `docs/decisions/0001`）
  - 種別: SQLite / 接続先: `<リポジトリ>\LocalData\db\business-app_v1.db`（2026-07-09 に C:\Codeer.LowCode.Blazor.Local から移設 → `docs/decisions/0017`・`LocalData/README.md`）
  - `AllowCliSqlAccess: true`（ローカル専用 DB。本番を指さない）
  - 認証テーブル（app_users）・一時ファイル（temporary_files）も同居
- 旧 `SampleSQLite`（sqlite_sample_auth.db）は**フェーズ A-0（2026-07-05）で撤去済み**。BusinessAppSQLite 以外に会計テーブルを置かないこと
- サーバ側: `BusinessApp.Server/appsettings(.Development).json` にも同名データソースの定義が必要（変更時はサーバ再起動）

## 命名規約

- テーブル・列: snake_case 英語（例 `journal_entries.entry_date`）。DatabaseGuidelines 標準に従い PK は `id` INTEGER 自動採番
- モジュール・フィールド: PascalCase 英語（例 `JournalEntry` / `EntryDate`）。画面表示名（DisplayName）は日本語
- 主要テーブル名は `docs/04_会計ドメイン設計.md` の定義が正
- CLB システムフィールドは予約名（`Id` / `CreatedAt` / `Creator` / `UpdatedAt` / `Updater`）を使用

## レイアウト規約（画面の見た目）

- **参照フィールドの幅**（2026-08-09 ユーザー指示・全画面共通）:
  - **取引先**（Partner 参照）が入るカラムは **440px**
  - **案件 / プロジェクト**（Project 参照）が入るカラムは **640px**
  - 理由: どちらも将来どんな長さの文字列が入るか読めないため、既定幅では収まらない。
    **これらのフィールドを新しい画面に追加するときは、常にこの幅に合わせる**
- Main の左サイドバーは幅 300px、ブランド文字は FontSize 15
- 金額列は右詰め・3桁カンマ区切り（`#,0`）。フォーム入力欄は左詰めのまま
  （一覧は `ListElement.ClassName = "amount-cell"`）
- **一覧の ○/— フラグ列は中央寄せ**（`ListElement.ClassName = "flag-cell"`）。
  見出し（「現預金科目」等）に対して中身が 1 文字なので、左詰めだと列との対応が読み取りにくい。
  Boolean は `TrueText: "○"` / `FalseText: "—"` を付ける（フレーム側が読み取り専用なら
  チェックボックスではなくこの文字で描画される）
- ボタンの色は 2 色規約（ADR-0027）: 通常操作は Primary（青）、破壊的・不可逆な操作は Danger（赤）
- 検索条件のラベル幅は 18px/字 で見積もる（ADR-0020〜0022 の UI レビュー規約）

## 業務ルール（会計の不変条件）

- 金額は整数円。貸借一致しない伝票は確定不可。締め済み期間（fiscal_periods.status=closed）の仕訳は作成・変更・削除不可
- 税抜経理。消費税行はシステム生成（is_tax_line=1）でユーザー直接編集不可
- 税率・税区分・経過措置控除割合・制度閾値はマスタ参照（ハードコード禁止）
- 残高＝期首残高＋仕訳集計（残高キャッシュ無し）。帳票 SQL は `status='posted'` のみ集計
- 詳細は `docs/04_会計ドメイン設計.md` §9 の実装チェックリスト

## 既存資産

- `Modules/MasterSystem/AppUser.mod.json`（Cookie 認証・admin/admin）・`PageFrames/Main.frm.json` — 認証テンプレート由来。AppUser はフェーズ A-0 で BusinessAppSQLite へ切替。テンプレート由来の旧 `Home.mod.json` は Shell/PortalHome・PortalSidebar（業務ポータル＝ADR-0042/0045）へ発展し、旧 Home／ExpenseHome／SalesHome／AccountingHome／AdminHome は全廃。Main は PortalHome 着地＋Left をモジュール型サイドバー（PortalSidebar）で置換
- 承認フロー・経費申請の正典: `ClaudeCodeForDesigner/Samples/PatternShowcaseAuth/` と `../references/SampleProject_AuthPatterns/`（後者はベンダー提供サンプルのローカル参照コピー。再配布権未確認のため Git 追跡外＝ADR-0038。リポジトリを clone した環境には存在しない）

## デプロイ手順（zip packing — 2026-07-05 実測確立済み）

- **`pwsh -NoProfile -File Designer/tools/deploy.ps1` を実行するだけ**（designcheck を通してから）
- zip 仕様（GUI 製 App.zip の実測に基づく）: エントリ名は**バックスラッシュ区切り**・ディレクトリエントリ無し・`app.clprj` はルート直下・`designer.settings*.json` は含めない。書きかけ検知を避けるため一時ファイルに作成してから `Move-Item` で配置
- 検証結果: サーバ起動時読み込み◯／稼働中の FileWatcher hot-reload ◯（ブラウザ再読み込みで反映を確認済み）
- 注意: `*.mod.cs`（スクリプト）変更・DB スキーマ変更はサーバ再起動が必要
- サーバ起動: `dotnet run --project BusinessApp/BusinessApp.Server --launch-profile http`（http://localhost:5085）

## 作業中に得た知見（追記していく）

- 2026-08-16: **読み取り専用のクエリモジュール（CanCreate/CanUpdate/CanDelete が全部 false）では ButtonField の OnClick が発火しない**（実測・ADR-0065）。ボタンがグレーアウトし、クリックしても何も起きない。試算表にドリルダウンを付けようとして最初に踏んだ。**代わりに AnchorTagField の `OnClick` を使う**——こちらは発火し、`Module` と `Url` を空にしておけば既定のナビゲーションが走らないので、スクリプトの `NavigateTo` がそのまま効く。クエリ一覧に行アクションを足すときの定石。関連して:
  - **ListLayouts の `OnAfterInitialization` は行ごとに発火し、その行のクエリ結果（`Xxx.Value`）が読める**（クエリモジュールでも同じ）
  - **リスト内のアンカーは `IsVisible = false` では消えない。** 消したい行は SQL 側でリンク文字の列（例 `drill_label`）を空文字にし、アンカーの `TitleVariable` にバインドする。**このとき `TitleText` は空文字にすること**——`TitleVariable` の値が空だと `TitleText` にフォールバックして結局表示される
  - **QueryField の `QuerySetting.Parameters` に出力列（`IsParameter: false`）を宣言しないと designcheck が「カラム 'xxx' が存在しません」で落ちる。** SQL に SELECT 列を足すだけでは足りない
- 2026-08-16: **クエリモジュール間で検索条件を引き継ぐには URL パラメータ ＋ `OnSearchInitialization`**（ADR-0065）。遷移元が `?initialize_search=true&drill_account=3&drill_from=2026-04-01` のような URL を組み立てて `NavigateTo`、遷移先の `OnSearchInitialization` で `NavigationService.GetUniqueQueryParameters()` を読んで `SearchValue` / `SearchMin` に移す。**`?initialize_search=true` を付けないとフックが発火しない**（サイドバーのリンクに CLB が自動で付けるのと同じパラメータ）
- 2026-08-16: **ブラウザ自動操作の合成クリックは CLB（Blazor）のリンク・ボタンに届かないことがある**（実測）。座標クリックも要素 ref 経由のクリックも無反応で、既存の実績ある「開く」リンクですら動かなかった。**`javascript_tool` で `element.click()` を呼ぶと正しく動く**。実機検証で「押しても何も起きない」ときは、実装を疑う前にこの方法で確かめること
  - 2026-08-18 追測: **どこが届かないかには傾向がある**。サイドバーのメニュー項目・ログアウト・一覧行の「開く」は座標クリックがほぼ通らず `element.click()`（テキストノードの**親要素**を叩く）が要る。一方、**確認ダイアログのボタン**（「実行」「承認する」「起票する」等）は座標クリックで通る
- 2026-08-16: **`Logger.Warn` は画面にトーストとして出る**（実測）。行ごとに発火するフックで使うと画面が埋まり、**その下にあるボタン・リンクを覆ってクリックできなくなる**。診断ログは一時的に入れて必ず外す

- 2026-08-16: **「この画面で選ばせてよい科目か」は表示区分では表せない。マスタに用途フラグを持たせる**（ADR-0063）。固定資産台帳の計上科目の候補が「科目区分＝資産」だけで絞られていて現金・売掛金まで選べた（この科目は**償却仕訳の貸方**になるので、誤選択すると現金が毎年静かに減る。内部振替＝税区分対象外なので消費税集計表にも出ない）。表示区分（流動資産／有形固定資産…）は**財務諸表のどこに並べるか**であって台帳の対象とは一致しない（台帳は「非償却」資産も持てる／投資その他の資産は台帳対象外）。`is_cash_equivalent`（ADR-0055）と同じく `is_fixed_asset_account` を足した。**フラグは「候補の絞り込み」と「実行時の関門」の 2 か所で効かせる**——候補を絞るのは選ばせない工夫であって、旧データやフラグを外されたマスタはガードでしか止められない
- 2026-08-16: **参照系フィールドの候補を条件で絞っても、既存レコードの現在値は選択肢に残る**（実測）。候補外になった値を持つ行を開いても表示が空にならず、末尾に現在値が足される。「絞り込みを厳しくすると過去データの表示が壊れる」心配は不要（ただし**検索**フォームでは候補外の値で絞り込めなくなる）

- 2026-08-16: **`IsUpdateProtected` は「導出値である」という意味ではない**（ADR-0062）。これは**更新時だけ**の保護なので、新規作成時は編集できてしまう。検収の検収額・消費税額が「明細から自動計算」と書いてあるのに新規では手入力でき、しかも `OnDataChanged` が無いため消費税が再計算されず、明細合計と一致しない検収が保存できた。**他項目から計算される値はレイアウト要素の `IsViewOnly: true` で読み取り専用にする**（新規・更新の両方で効く）。`IsUpdateProtected` はサーバ側の保険として併用してよいが、それだけでは足りない
- 2026-08-16: **CLB スクリプトは `try` / `catch` / `finally` を使えない**（`_specs/Scripts.md` の非対応構文）。したがって**再入ガードのフラグは DB 検索など例外を出しうる処理の外側に置く**。内側に置くと例外時にフラグが `true` のまま固着し、以後その画面で導出計算が二度と走らなくなる（`Acceptance.Lines_OnDataChanged` を「明細への書き戻しだけをガードで囲み、ヘッダを更新する再計算はガードの外」に組み替えた）
- 2026-08-16: **確認ダイアログは「(A) 取り消す導線が無い」「(B) 1 回で複数件を作る／消す」のときだけ出す**（ADR-0062 の規約）。同じ画面に取消ボタンがある単票操作には出さない——押し直せるものに出すと利用者がダイアログを読まなくなり、本当に危険な確認が効かなくなる。肯定ボタンの文言は操作の動詞にする（「OK」ではなく「確定する」「起票する」）。`LoadingService` はダイアログより後（既知の罠）
- 2026-08-16: **`OnValidateInput` は使わない**（FB-044・実測）。`false` を返すと保存は止まるが**画面には何も出ず、サーバへの通信も起きない**（`SetError` も描画されなかった）＝「ボタンを押しても何も起きない」。入力の即時フィードバックは挙動が安定している `OnDataChanged` で行い、**関門はサーバ側**（`ModuleDataIO` の override）に置く。サーバで `LowCodeException.Create(メッセージ)` を投げると**その文言がそのままトーストに出る**ので理由は伝わる
- 2026-08-16: **`PasswordField` は 1 フィールドにつき「入力欄＋確認欄（`.password-confirm`）」を必ず描画する**（出し分け不可・プレースホルダは英語固定）。3 欄のパスワード変更画面を作ると 6 欄並ぶので、`app.css` で `[data-module="..."] .password-confirm { display:none }` して一致チェックを自前で行う（FB-045）。逆に**ユーザー管理のように SubmitButton で保存する画面では確認欄が機能する**ので隠さない
- 2026-08-16: **CLB は SQLite の生成列（`GENERATED ALWAYS AS ... VIRTUAL`）を認識しない**（FB-046・実測。designcheck が「カラムが存在しません」）。`PRAGMA table_info` が生成列を返さないため。**「同じ行から導ける値」を列として持ちたいときは実列＋トリガー保守**にする（`select_label`＝ddl/280 と同型。モジュール側は `IsUpdateProtected:true` + `IgnoreModification:true` で読むだけ）
- 2026-08-16: **SQLite の `date('now')` は UTC**。JST とは 9 時間ずれるので、毎月 1 日の 0:00〜9:00 は「前月」を数え、`DateTime.Today`（ローカル）を使う画面側と 1 ヶ月ずれる（実測: UTC 2026-08-15 / ローカル 2026-08-16）。**Query の SQL では必ず `date('now','localtime')`**。修飾子の順序は `'localtime'` を先に置く（`date('now','localtime','start of month')`）
- 2026-08-16: **認証テーブルがビューのときは `INSTEAD OF UPDATE` トリガーも要る**（ADR-0059）。`PasswordCheckUserTableInfo.TableName` は `active_app_users`（ビュー）を指しており、seed の INSERT 用トリガーはあったが UPDATE 用が無く、パスワード変更 API がビュー越しに `hash`/`salt` を書けなかった。`app_users` を直接名指しすると「認証テーブルは設定で差し替えられる」契約が壊れるので、ビュー側にトリガーを足す（ddl/570）

- 2026-08-14: **範囲検索（Date/Number 等）は NULL 行を必ず落とす。`AllowEmptySearch` は救いにならない**（実測・ADR-0057）。`SearchMin` を入れると `>= SearchMin` が発行され、その列が NULL の行は除外される。共通プロパティ `AllowEmptySearch: true` を立てても挙動は変わらない（範囲検索フィールドでは観測可能な効果が無い。空欄検索でも全件出るので「空欄のとき IS NULL」でもない）。検索レイアウトの `Operator: "Or"` も OR で繋ぐ相手（IS NULL 条件）を作れないため無意味。**「未設定＝上限なし／継続中」を意味する列で「進行中だけ」の既定フィルタを作りたいときは、状態列を別に持たせるか一覧を Query モジュール化するしかない**（→ FB-043）
- 2026-08-14: **SelectField の OR 検索（`AllowOrSearch: true`）は複数選択のチェックボックス群として描画され、既定値はスクリプトの `Status.SearchValues = リスト` で入る**（実測・ADR-0057）。`SearchValue`（単数）ではなく `SearchValues`。条件がチェックボックスとして見えるので「既定で隠している」ことが利用者に伝わる
- 2026-08-14: **検索行に条件を詰めすぎると `IsWrap` でラベルと入力欄が泣き別れる**（実測）。ラベル列は `VerticalAlignment: Middle`・入力列は上端なので、行が折り返したり行の高さが伸びたりすると縦位置がずれて別々の行に見える。**1 行は 3 組（ラベル＋入力）まで**を目安にし、**OR 検索の SelectField のような縦長のコントロールは単独行**に置く（`Quote` の実績値: 日付 84+340・件名 66+640・状態 66+210・取引先 84+440・案件 66+640・部門 66+280）
- 2026-08-14: **`OnSearchInitialization` を設定すると、サイドバーのリンクに `?initialize_search=true` が自動で付く**（実測）。設定前は付かない。逆に言えば、スクリプトから `NavigationService.GetModuleUrl(...)` で一覧へ戻すと**パラメータが付かないので既定条件は効かない**（削除後の一覧遷移など。見積・受注も同じ挙動）

- 2026-07-23: **`LoadingService.StartLoading()` を `MessageBox.Show()` の前に開始しない**（実測）。ローディングオーバーレイが確認ダイアログの上に重なり、ボタンが押せなくなる。順序は「ガード検索 → ダイアログ → using loading → 本処理」。

- 2026-07-22: **一覧の行単位の条件付きハイライトは「ListLayout の行イベント＋app.css の `:has()`」で実現できる**（実測）。`ListLayouts[""].OnAfterInitialization` は一覧の**行モジュールごと**に発火するので、そこで条件判定してフィールドに `ClassName` を付け、CSS 側で `[list-module="モジュール名"] table tbody tr:has(.クラス) > * { --bs-table-bg: 色; --bs-table-striped-bg: 色; }` と行（tr）全体に効かせる（背景は Bootstrap の CSS 変数経由。フィールドの `BackgroundColor` 直接設定はセル内要素しか塗れない）。実装例: BankStatementLine の未起票行の黄色ハイライト
- 2026-07-22: クエリ専用モジュールの検索条件は「`IsParameter:true` パラメータ＋同名 `DbColumn` のフィールド＋SearchLayout 配置」の3点セット（正典: ReceiptList）。**検索の初期値は `SearchLayouts[""].OnSearchInitialization` で `SearchValue` に設定**する（サイドバー Link 経由の `?initialize_search=true` でのみ発火）。実装例: ReceivableBalance の「入金済を除く」既定
- 2026-07-05: インボイス経過措置は令和8年度改正で4段階（80/70/50/30%）に変更済み。事前知識と異なっていた——**税制は必ずリサーチしてから実装する**こと（→ `docs/research/2026-07_税制・会計制度リサーチ.md`）
- 2026-07-05: **認証のデータソースは `app.clprj` の `CurrentUserModuleDesignName`（=AppUser モジュール）の `DataSourceName` から解決される**（`CookieAuthentication.GetDataSourceName()`）。app_users が空ならサーバ起動時に admin/admin が自動作成される
- 2026-07-05: 認証 DB を切り替えるとブラウザの旧セッション Cookie が「アプリへのアクセス権限がありません」エラーを出す。**対処: `POST /api/account/logout`**（JS なら `X-ANTIFORGERY-TOKEN` Cookie（非 HttpOnly）をヘッダに付けて fetch）→ login.html が出る
- 2026-07-07: **API 経由で logout した後に login.html へ直行するとログイン不能（400・"Login failed"）**。antiforgery トークンはユーザー束縛で、発行ミドルウェアは SPA フォールバック GET（`/` や `/Main/...`）でしか走らない（login.html は静的ファイル＝UseStaticFiles が短絡するため未更新。admin 束縛の古いトークンが残る）。**対処: logout 後に一度 `/` を GET してから login.html へ**（サイドバーの Logout リンク経由の通常フローは自然に `/` を踏むため影響なし＝アプリの不具合ではない）。パスワード誤りと切り分けるには 400（antiforgery）と 401（credential）をサーバログ or fetch で確認
- 2026-08-19: **DATE 列の正規形は `YYYY-MM-DD 00:00:00`（CLB が書く形）**（ADR-0074・実測）。CLB が `ModuleSearcher` の日付比較に渡すパラメータも時刻付きなので、**時刻の無い行だけが範囲の下端で黙って落ちる**（`'2026-09-01' >= '2026-09-01 00:00:00'` が辞書順で偽）。**時刻を落とす方向に揃えてはいけない**——逆に全部の下端比較が壊れる。seed DDL に日付リテラルを書くときは ` 00:00:00` を付ける。既存データは `Designer/ddl/811`、再発検出は不変条件 `F06`。なお**自作 SQL（QueryField 等）の日付比較・GROUP BY は引き続き `date(列)` で正規化**すること（2026-07-05 の知見「混在しても辞書順比較は成立する」は誤りだったので置き換えた）
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
- 2026-07-06: **システム移行時の開始残高は「導入初年度の期首」に置く**（過去年度に期首も仕訳も無い状態でその年度から繰越を実行すると、翌期の期首残高が空データで洗い替えられ帳簿が破壊される。総合テストで実測 → CarryOver_OnClick に空年度ガードを実装済み）
- 2026-07-07: **月末日付の境界罠は実発現を確認**（新規振替伝票 2026-07-31 で「取引日に対応する月次期間がありません」を実測。EndDate と同日の >= 比較が日付書式の辞書順比較で偽になる）。**対策: 年度・期間・経過措置の期間解決はすべて「対象日の月初日」で行う**（JournalEntry/ExpenseRequest/Acceptance/Receipt を修正済み。CashEntry/RecurringRun/BankImport/JournalImport/JournalTemplate/VendorInvoice は当初から月初日方式）。新規スクリプトでも必ずこの方式を使うこと
- 2026-07-07: **表示専用モジュール（DataSourceName ""）の this.Submit() は機能しない**（BankImport で実測: 例外も出ず何も起きない）。ListField の行を保存するには行インスタンス単位で row.Submit() する
- 2026-07-07: **DateField への代入は DateOnly.FromDateTime(…) 必須**（DateTime を代入すると UI には表示されるが保存データに乗らず NOT NULL 違反になる。BankImport で実測）
- 2026-07-07: **スクリプトから new して Submit するモジュールは CanCreate:true 必須**（false だとサーバ側で "This module data cannot be created" と拒否される。UI の新規作成だけ塞ぎたい場合は PageFrame Link の UseNavigateToCreate:false と ListField の CanCreate:false で行う）
- 2026-07-07: ブラウザ自動操作で Blazor のボタンが座標クリックで空振りすることがある → **JS の btn.click() が確実**（承認待ち「開く」が無反応に見えた原因もこれ。アプリ側の不具合ではなかった）
- 2026-07-07: **検索フォームの SelectField は JS の `el.value=…; dispatchEvent('change')` では Blazor に値が伝わらないことがある**（見た目は変わるが SearchValue が更新されず、検索パラメータが NULL のまま＝0件に見える）。総勘定元帳で実測・診断 SQL で確定。**実クリック＋キーボード（Down/Up）操作なら確実に伝わる**。自動テストで検索セレクトを操作するときはキーボード方式を使うこと（アプリ側の不具合ではない）
- 2026-07-07: **PDF 出力（Excel.Report.PDF）は `C:\Codeer.LowCode.Blazor.Local\Font\NotoSansJP.ttf` が環境要件**。Server の `CustomFontResolver`（Program.cs で登録済み）は `appsettings.Development.json` の `FontFileDirectory` から `{フェイス名}.ttf` を読み、無ければ `NotoSansJP.ttf` にフォールバックする。ディレクトリ自体が無いと「Object reference not set」で全 PDF 変換が失敗する（請求書 PDF で実測）。**セットアップ: `C:\Windows\Fonts\NotoSansJP-VF.ttf` を上記パスへコピー**（PdfSharp 6.2.3 は TTC を解析できないが可変フォント TTF は可。ハーネスで日本語描画まで検証済み）。このファイルは Git 管理外の環境要件なので新環境では手動セットアップが必要
- 2026-07-07: ブラウザからのファイルダウンロードは **Chrome が同一サイトの連続自動ダウンロードをブロックする**（初回は落ちるが2回目以降は成功トーストが出てもファイルが現れない。Excel/PDF とも実測）。アプリの不具合ではない——実利用では Chrome が許可プロンプトを出す。自動テストでダウンロードを検証するときは初回の1発で判定すること
- 2026-07-08: **【解決】ExecuteSqlField の Parameters は SQL の @プレースホルダを「フィールド名」ではなく「DB 列名（DbColumn）」で解決する**（ISSUE-0001 の真因）。フィールド名と列名が違うフィールド（NextYearId / next_year_id）は SQL 側を `@next_year_id` にしないとバインドされず、SQLite の「Must add values for the following parameters」で Submit ごとロールバックする。フィールド名=列名なら偶然動くため気づきにくい。**ExecuteSqlField の SQL を書くときはプレースホルダを必ず DB 列名にする**（翌期繰越で実測解決・row_id 洗い替えと Σ=0 を確認）
- 2026-07-08: **他ユーザー宛レコードを new→Submit すると、INSERT 成功でも戻り値が true にならない**（Notification で実測。DataReadCondition（自分宛のみ）により Submit 後の再読込が 0 件になるため。再検索による成否確認も同じ読取条件で不可能）。読取ゲート付きモジュールへの代理作成では **Submit 戻り値で成否判定しない**こと（検証は DB で行う）
- 2026-07-08: **新規生成直後の子行（Id=@temporary）を条件に ModuleSearcher で数値列を検索すると FormatException**（サーバ 500。承認フローの CurrentApprover 再計算で実測——新規申請では例外がクライアントで握り潰されて見えないが、実費確定の再承認経路では赤トーストで顕在化した）。**DB 検索の前に `$"{id}".StartsWith("@temporary")` ガード**を入れ、メモリ行から解決する
- 2026-07-08: **ChildModuleField の子は「親の FK 列」（例: expense_request.approval_flow_id）で紐づき、子行が無い親では子モジュールが一切実体化されない**（画面に子のUI・ボタンが出ない。子スクリプトの OnAfterInitialization も走らない）。さらに**未保存の子モジュールのフィールドを親や他インスタンスから参照すると「〜操作が存在しません」エラー**（メソッド呼び出しは通る）。スクリプトで「保存済み親＋新しい子」を作るときは、①子を単独で new→Submit ②FK 列に実列バインドした NumberField（例: ApprovalFlowIdRaw）経由で親にリンク ③子の状態は「未開始」を表す値（承認フローでは Status="Draft"）にして、子側スクリプトがそれを初回扱いする——の3点セット（経費複製で確立）
- 2026-07-08: **ブラウザ自動操作でトーストの有無を判定材料にしない**。CLB のトーストは JS の `[class*=toast]`/`[role=alert]` セレクタで拾えないことがあり、表示時間も短くスクリーンショットでは取り逃す（ISSUE-0001 調査で「トーストが出ていない」と誤認 → 実際は毎回出ていたとユーザーが目視確認）。**挙動判定は DB の実データ・サーバログ・戻り値で行う**。MessageBox の JS クリック可否は未確定（誤った知見を一度書いて撤回）
- 2026-07-16: **参照ドロップダウンの合成表示（番号＋取引先＋件名）は select_label 列＋DBトリガー保守が定石**（DisplayTextVariable は単一変数のため）。アプリ層フック（mod.cs）だと SES 一括生成・定期請求実行など複数の INSERT 経路すべてに保守が必要で漏れる。AFTER INSERT/UPDATE トリガー＋取引先名変更の連動トリガーなら経路非依存（ddl/280 で quotes/sales_orders/acceptances/invoices に適用済み。モジュール側は IsUpdateProtected:true + IgnoreModification:true の TextField を定義してレイアウトには置かない）
- 2026-07-16: **認証のユーザーテーブルは SQL ビューを指定できる**（PasswordCheckUserTableInfo.TableName=active_app_users で退職者のログイン拒否を実現・実測）。注意: Server の初期ユーザー seed（CreateInitialUserAsync）は同じ TableName に INSERT するため、**ビューに INSTEAD OF INSERT トリガーが必須**（無いと新規 DB 構築で admin 作成が失敗する。ddl/290 で対応・INSERT 転送を実測検証済み）
- 2026-07-16: **スクリプトの表示制御条件で使うフィールド（レイアウト外）は DetailLayouts の DataOnlyFields に登録しないとロードされず null になる**（ExpenseRequest.Creator で実測: 削除ボタンの IsSameId 判定が常に false。FB-005 の別面——「DB から取り直す」以外に、読むだけなら DataOnlyFields 登録でも解決する）
- 2026-07-16: **フレームリンクの ListFieldDesign.SearchCondition はモジュール側 ListPageFieldDesign の SearchCondition を上書きする**。リンクを複製して作った画面は SearchCondition.ModuleName が複製元のままだと**条件が黙って捨てられる**（精算処理待ちビューで実測: 全件表示になった）。リンク複製時は ModuleName と条件の両方を必ず直す
- 2026-07-16: **Designer exe は GUI アプリなので PowerShell から呼ぶと待たずに戻る**（sql / designcheck の --out ファイルが「無い」ように見える）。**stdout をパイプ（`2>&1 | Out-Null`）すれば終了まで待つ**。この形を常用すること
- 2026-07-16: **CLB スクリプトの this.Delete() はモジュール自身の削除に有効**（ExpenseRequest 詳細画面の削除ボタンで実測）。ただし ModuleSearcher の結果行や ChildModule への .Delete()/.DeleteSelfWithHistory() は無反応（3方式とも実測）＝**親からの子のカスケード削除はスクリプトでは不可**。承認フロー行が残る場合は SQL で掃除するか、一覧から見えない設計（MyApplication の Cancelled 除外等）で吸収する
- 2026-07-19: **検索インスタンス（ModuleSearcher の結果）への .Delete() は「子持ちモジュールでは失敗して false を返す」が正しい理解**（7/16 の「無反応」知見の精緻化）。子なしモジュール（BankStatementLine・JournalLine 等）では物理削除が成功する。子持ち（JournalEntry 等）では子の FK 制約で SQLite Error 19 になり false（DeleteTogether のカスケードは UI 削除のみ）。**正解パターン: 明細を1行ずつ Delete()（各戻り値を検証）→ 親を Delete()**。さらに削除対象を FK 参照している列（例: vendor_invoices.accrual_entry_id / payment_entry_id）は**先に null にして Submit してから**削除する（失敗時は参照を復元）。仕入先請求書の未払計上取消で完全1周を実機検証済み
- 2026-07-19: **ロール別フレーム構成（ADR-0028）の実装要点**: ①ルート Main は表示専用モジュール RoleDispatch（OnAfterInitialization で CurrentUser.Role により NavigateTo）による振り分け専用にする——CLB の「ルートフレームは全員アクセス可の1枚」制約とロール排他ゲートの両立策 ②TopPageModuleDesign とサイドバーリンクが同一モジュールを指すと URL セグメント重複（designcheck が検出）→ TopPage 側に ModuleUrlSegment="Top" を与える ③サイドバーの Title「グループ/リンク」階層は**折りたたみ式**（実測。段階的開示に使える）
- 2026-07-19: **スクリプトの一覧ページ戻りは `NavigateTo(NavigationService.GetModuleUrl("Module"))` でフレーム非依存にする**（固定パス "/Sales/Quote" 等はフレーム改名・再編で静かに 404 になる。詳細遷移の GetModuleDataUrl(module, id) 同様、現在フレーム内で解決される）
- 2026-07-20: **CLB を 1.3.4（ランタイム）/1.3.6（デザイナ）へ更新済み。1.2 との破壊的差分を2つ実測**: ①モジュール 	his.IsViewOnly=true にすると **ButtonField の OnClick が発火しなくなる**（見た目は通常・エラーなし。「閲覧専用＋取消ボタン」パターンが全滅——操作ボタンだけ Button.IsViewOnly=false を明示して解除する。Quote/Acceptance/Receipt で適用済み・FB-030） ②HorizontalAlignment の列挙が Start/Center/End/Stretch になり、**旧値 Left/Right はエラーにならず既定 Start に化ける**（デザイナ保存で固定化される。リポジトリは全箇所変換済み・新規レイアウトでは必ず新値を使う・FB-031）
- 2026-07-21: **一覧→詳細遷移するフレームリンクの ModulePageType は "Auto"（"List" だと詳細 URL が真っ白）**。CanNavigateToDetail:true でも "List" のままでは `/Module/{id}` のルートが登録されず、行クリック・直接 URL とも空白ページになる（明細一覧の詳細新設で実測。エラーも console 出力も無い静かな失敗）。既存の見積・受注等が Auto なのはこのため
- 2026-07-21（7/22 訂正・精緻化）: **ListField の「自動ロード」では OnDataChanged がそもそも発火しない**（当初「発火するが描画されない」と誤解していたが、正しくは不発。this.NotifyStateChanged() でも変わらず——実測）。一方 **スクリプトからの明示 Reload() は待機され、直後に Rows が参照でき、OnDataChanged も発火する**。したがって①初期表示に使うサマリ・スナップショットは OnAfterInitialization で ListField.Reload() を明示的に呼んで**その場で**計算する（BankPosting サマリ／BankImport スナップショットで確立）②ロード完了フックとして OnDataChanged に依存する設計は禁止（BankImport の削除同期が「開き直した画面では効かない」バグになった実例。7/22 ユーザー発見）
- 2026-07-21: **銀行明細 v3（ISSUE-0003）の実装パターン**: 未確定の作業データは本番テーブルに status で同居させず**専用テーブル（bank_statement_preview）に分離**する（全参照箇所の「preview 除外し忘れ」構造を根絶）。一覧の編集・行削除は**スナップショット差分同期**（OnDataChanged で snapshotValid ガード付きロード済み ID を記録→保存時に「スナップショットにあってメモリに無い ID」を Delete()）。ルール/AI のメモリ適用は SuggestedAccount 設定→直後に SuggestionSource を rule/ai で上書き（OnDataChanged が manual を書くより後勝ち）
- 2026-07-21（**7/23 訂正**）: ~~ListLayout の OnBeforeInitialization/OnAfterInitialization は行モジュールには配られない~~ → **`ListLayouts[""].OnAfterInitialization` は行モジュールごとに発火する**（7/23 実測: BankStatementLine の未起票行ハイライトで、条件付き `ClassName` 付与が行単位で反映されることを確認。ListPage 文脈・ランタイム 1.3.4）。7/21 の「発火しない」結論は、検証対象が**ボタンの IsVisible 切替**で、それがリストセルに反映されなかったことによる誤判定の可能性が高い。実務指針: ①**フィールドの ClassName / Color 等のスタイル変更は行イベントで効く**（行ハイライトに使える） ②**行の状態に応じたボタンの出し分けは引き続き不可扱い**（IsVisible 反映は未確認のまま。行操作ボタンはハンドラ冒頭の状態ガード＋トーストで無害化、または専用 ListLayout を切る=Notification.HomeUnread の方式）
- 2026-07-25: **一覧の列幅は行モジュール ListLayouts の ListElement.Width（px・未設定=auto）で固定できる**（数量70/単位70/単価110/金額120/税区分150・摘要のみ auto にして「摘要をぐっと広く」を実現。ヘッダは同じ ListElement から生成されるので自動で揃う。Quote/SalesOrder/InvoiceLine で実測）
- 2026-07-25: **SelectField を IsRequired:true にしても検索フォームの空選択肢（=絞り込みなし）は消えない**（請求書の部門検索で実測）。編集側の空欄禁止は EmptyCandidateType を NotExist にせず「IsRequired＋初期値＋非経理 IsViewOnly」で実現し、検索用の Null 空選択肢は残すのが正解（NotExist は検索の「指定なし」まで消すリスクがある）
- 2026-07-25: **行単位の条件付きスタイル（ListLayouts OnAfterInitialization で ClassName 付与→app.css の tr:has()）は罫線にも有効**。P/L のカテゴリ境界太線（小計行=下2px・段階利益行=太字＋薄背景）を BankStatementLine の背景ハイライトと同パターンで実装（border は CSS 変数でなく通常の border-bottom で効く）
- 2026-07-26: **`ListLayouts[""].OnAfterInitialization` は「詳細ページ埋め込みの ListField」の行でも発火する**（7/23 の ListPage 文脈に続き実測。RecurringRun 詳細内の PlanLines＝RecurringRunPlan の生成予定行ハイライトで確認）。ただし埋め込み一覧のルート div に `list-module` 属性は付かないため、**CSS のスコープは親詳細ページの `data-module`（例: `[data-module="RecurringRun"] table tbody tr:has(.row-planned)`）で取る**
- 2026-07-26: **モジュールスクリプト定義のメソッドは「別モジュールのスクリプト」からインスタンス経由で呼べる**（実測: FbExport が `partner.AccountValidationError()`＝PartnerBank.mod.cs のメソッドを呼ぶ。designcheck も型チェックを通し、ランタイム 1.3.4 で動作）。`new PartnerBank()` で作った素のインスタンスのメソッド（`IsDigitsLen`/`KanaError`）も呼べる。**検証ロジック等の共通化は「データを所有するモジュールに正典メソッドを置き、他モジュールから呼ぶ」で C# 拡張なしに実現できる**（取引先口座の全銀検証で確立）
- 2026-07-26: **ブラウザ自動操作の form_input（JS 合成イベント）で複数フィールドを連続設定すると「.Value は更新されるが変更フラグが立たない」フィールドが生じ、Submit が部分 UPDATE になる**（実測: 5欄設定→登録で検証は通過（=Value は見えている）したのに DB は 3 欄のみ保存・2 欄 NULL）。実キーボード入力なら全欄正しく保存される＝アプリの不具合ではない。**自動テストでフォーム保存を検証するときは実クリック＋実タイピングを使い、保存結果は DB で確認する**（FB-005/トースト不可視と同族の automation 罠）
- 2026-08-02（**→ 2026-08-05 の項で更新済み。権限・遷移規約は現行そちらが正**）: **部品アーキテクチャ体制（ADR-0040/0041）が現行の正**。フレームは部品×対象者の14枚・権限は部門メンバーシップ×sysadminフラグ（`docs/10_部品アーキテクチャ.md` が正典）。実装規約: ①新モジュールは部品フォルダに置き下方向以外の参照を作らない ②部品にメニューを足すときは全変種フレームへ反映（docs/10 §5 マトリクス） ③権限判定はキャッシュ列（HasSalesAccess/HasAccountingAccess/IsApprover/IsSysAdmin）のみ ④部品跨ぎ遷移は `/{Frame}/Top`
- 2026-08-02（**→ 2026-08-05 の ADR-0043 で更新**: 導出キャッシュは is_approver（ddl/400）と is_director（ddl/450）の2本のみ。sales/accounting/expense/timesheet は直接フラグでトリガー無し・部門種別 dept_type は廃止列）: **フレームの UserReadCondition は AppUser の列しか参照できない**（関連テーブル不可 = FB-034）。権限の導出値は AppUser のキャッシュ列に DB トリガーで転記する（ddl/380。全再計算は ddl/385）。**メンバー行・部門種別を SQL 直叩きで変えてもトリガーが追随する**（デモ掃除 SQL 安全）
- 2026-08-02: **表示専用モジュール（ホーム類）を指すフレームリンク／TopPage は、複製元由来の `SortFieldVariable`（Id.Value 等）を空にする**こと（残すと designcheck が「変数がモジュールに存在しません」— 実測）。また **git 復元をともなう検証（削除可能性テスト等）は必ず先にコミットしてから**（未コミット編集が checkout で消える事故を実際に起こした）
- 2026-08-02 夜（**→ 2026-08-05 の ADR-0045 で更新**: タイルは全廃。業務導線は Main 左サイドバー（PortalSidebar）に一本化。フレーム追加時は ①PortalSidebar にリンク（表示条件＋変種解決） ②必要ならポータル表示項目（docs/13 §3・Portal*Data） ③新フレーム先頭に「ホーム」）: **ログイン導線は業務ポータル（ADR-0042）が現行の正**。全員 Main/PortalHome に着地しタイルで部品を選ぶ。フレーム追加時は ①PortalHome にタイル追加 ②新フレーム先頭に「ホーム」リンク。実測知見3件: ①**表示専用モジュールの Detail はビュー専用扱いになりボタンが pointer-events:none でクリック不能**（エラーなし）— `Detail_OnAfterInit` 冒頭で `IsViewOnly = false;`（FB-035。FB-030 の同族） ②**rename-module はレガシー `TopPageModule` プロパティを追従しない**（TopPageModuleDesign.Module は追従・designcheck も沈黙）— rename 後に旧名 grep（FB-036） ③**IsVisible=false のフィールドは Width 指定カラムが空のまま残り歯抜けになる** — app.css で `[data-module="X"] .grid-column:not(:has(.field-layout)){display:none}`（FB-037）
- 2026-08-03: **検索フォームの行は `IsWrap: true` を全モジュール標準とする**（幅が足りない時だけ折り返す・広い画面では無変化。1344〜1514px で請求書等の検索欄が右に見切れて横スクロールになる実測不具合への恒久対応。全 48 モジュールへ一括適用済み——新モジュールでも必ず `IsWrap: true` で作る）。あわせて**ラベル・ボタンの固定幅列は「テキスト実測幅＋余裕」を確保**（折返し実測: ExpenseRequest の添付ラベル 140→160/240、通知の未読に戻す列 110→130、H2 センタータイトルは 3 分割 auto 列だと約 1/3 幅しか取れず狭幅で折れる→タイトル列に Width 明示。検出は「rect.height >= lineHeight×1.85」の JS スイープが有効）
- 2026-08-02 夜: **サーバ手動起動は BusinessApp.Server プロジェクトディレクトリを CWD にする**（Content root = 起動時 CWD。リポジトリ直下で exe を直叩きすると appsettings が読めず、デザイン未ロード＝全ログインが 500 `GetConnection: not found in ()` になる — 実測）。`dotnet run --project` 方式（上記デプロイ手順）ならこの罠は無い
- 2026-08-05: **3軸分離・部課階層・ポータル本格化（ADR-0043/0044/0045）が現行の正**（docs/10 改訂版・docs/13 が正典）。8/2 記載の読み替え: ①権限判定は直接フラグ（HasSalesAccess/HasAccountingAccess/CanUseExpense/CanUseTimesheet）＋導出キャッシュは IsApprover のみ（トリガーは ddl/400・再計算は ddl/385。部門種別 dept_type は廃止） ②部品跨ぎ遷移は `/{Frame}` 素URL（/Top 全廃。着地=TopPageModuleDesign・セグメント Start） ③フレーム追加時は PortalSidebar にリンク＋PortalHome/Portal*Data に表示項目 ④departments は部課2階層（parent_id。伝票部門は NodeType='dept' のみ・既定値 CurrentUser.所属部） ⑤承認の役職解決は walk-up（自課の課長→部長繰上げ→経理代替） ⑥通知は基盤 Shell/Notification.Send() に一元化
- 2026-08-05: **サイドバーのモジュール置換（SideBarDesign.ModuleName）の実測知見**: ①描画は ModuleRenderer 経由で `data-module` 属性が付かない。CSS はデスクトップ `.sidebar`／モバイル複製 `.sidebar-nav` × `.field-layout` でスコープする ②標準の Home/Links/Logout は消える（ログアウトは `NavigationService.Logout()` を **LabelField の OnClick** で自前実装。**AnchorTag は OnClick 指定でも href=/Main/ を持ち、サーバ往復を伴う OnClick が href ナビゲーションとのレースで負けて無反応になることがある**＝実測。スクリプト遷移するサイドバー項目は Label+OnClick 方式に統一） ③権限で IsVisible=false にしたリンクの空行は `.grid-row:not(:has(.field-layout)){display:none}` で畳む（PortalHome の非表示項目も同じ）
- 2026-08-05: **check_navigation.py は新遷移規約対応版**（リテラル/補間 NavigateTo の フレーム×セグメント検査・`Resolve〇〇()` リゾルバ関数の戻り値解析）。スクリプトで変種フレームを解決するときは `frame = "X"` 代入か `string Resolve〇〇Frame()`（return "X" 形式）で書くと静的検査が効く
- 2026-08-06: **レビュー第9弾（ADR-0046）の実装知見**: ①案件（Project）の書込は UserWriteCondition「経理∨部長」——「部長である」事実は is_director キャッシュ（ddl/450・department_members の director 行から導出。is_approver=ddl/400 と同型）。ProjectView は廃止し Project 本体に一本化（SES 精算条件は非経理に IsViewOnly） ②年度表示は fiscal_years.label（「第18期（2026年度）」形式・トリガー保守=ddl/430）を年度参照 SelectField 14箇所で参照——年度の表示を変えるときはこの1列 ③経費の「申請中」一覧は my_application_view（ddl/440・approval_flow に expense_request を JOIN）で件名・金額列を実現し、表示は進行中＋却下のみ（承認済・キャンセルは出さない） ④**CurrentUser の SelectField の DisplayText は候補未ロードだと空**（実測バグ）——スクリプトで表示名が要るときは該当マスタを ModuleSearcher で取り直す
- 2026-08-12: **`ListField.OnDataChanged` の中でグリッドの値を無条件に導出計算すると、外から書き込んだ値が同じイベント内で潰れる**（改善候補 A-1 の真因・実測）。`Invoice.Lines_OnDataChanged` が `金額 = 数量 × 単価` を常時走らせていたため、スクリプトの `dst.Amount.Value = 検収額` が**その代入自身が呼び戻したハンドラ**によって数量×単価に戻されていた（`inLinesHandler` ガードは自分の中の代入しか守らない）。**導出計算は「導出してよい行／フィールドか」を必ず条件付ける**（本件は `AcceptanceLineRef == null`＝手入力行に限定）。症状は「保存したはずの値が戻る」で、designcheck も例外も出ない静かな失敗
- 2026-08-12: **`ListField.CanCreate/CanDelete: false` は UI の行追加・削除ボタンを消すだけで、スクリプトからの `AddRows()`／`Submit()` は従来どおり通る**（検収明細＝受注明細の写しで実測）。「行は自動生成のみ・利用者は特定列だけ編集」という明細を作るときの定石: `CanCreate/CanDelete: false` ＋ 編集させない列に `IsUpdateProtected: true` ＋ `IsFocusSkip: true`（Tab で飛ばす）。`ListField.CanUpdate` は true のままにする
- 2026-08-12: **マスタ既定値は「既定として使う」列をマスタに持たせて引く**（ADR-0050 で確立・ユーザー指摘）。税区分の既定を `SALES_10` とコードに書くのは「2026 年はたまたま 10% が普通」という前提の埋め込みで、税制改正に設定変更で追随できなくなる。`tax_categories.default_for`（NULL/'sales'/'purchase'・部分 UNIQUE インデックスで 1 件保証）を引く形にし、スクリプト・SQL の `code='SALES_10'` を一掃した。**税率・税区分・閾値・勘定科目の類はマスタ化してハードコードしない**（CLAUDE.md §3 の具体化）
- 2026-08-13: **`OnDataChanged` はスクリプトで作ったモジュールにも発火する。「値をセットしない」は「既定が入らない」を意味しない**（ADR-0053・実データで確認）。`JournalEntry.Lines_OnDataChanged` → `ApplyLineDefaults()` が、他モジュールから `new JournalEntry()` で作った伝票にも効いて勘定科目マスタの既定税区分を入れていた。そのため「税区分をセットしなければ対象外になる」という前提の実装（ADR-0052 決定 2）は成り立っておらず、減価償却の貸方に取得時の課税仕入が付いたままだった。**既定と違う値にしたいなら明示的に代入する**（内部振替は `MarkAllLinesOutOfScope()` で上書き）。A-1（`Invoice.Lines_OnDataChanged` が書き込んだ値を潰した件）と同じ性質の罠で、**症状が「勝手に値が入る／勝手に戻る」のときはハンドラの発火を疑う**
- 2026-08-13: **`RegenerateTaxLines()` は入力額（税込）のまま 1 回だけ呼ぶ**（ADR-0053）。税抜化済みの行に再適用すると二重に税抜化される。`SaveEntry` が「税行の生成は確定時のみ・下書きは入力そのまま」にしているのはこのため（下書き保存→確定、確定失敗→再確定 の順路で必ず踏む罠だった）。スクリプトから下書きを経ずに確定する経路（入出金起票）は `GenerateTaxLinesOnce()` を使う。**外税でこれを呼んではいけない**——本体行が税込のまま税行が増えて貸借が崩れる
- 2026-08-12 夜: **「無い」を NULL と専用マスタ行の 2 通りで表さない**（ADR-0052）。税区分は `NULL` と `OUT_OF_SCOPE`（対象外）が同じ意味を持ち、集計 SQL が片方を落として不具合になった（B-5）。**同じ概念の表現は 1 つに寄せ、DB の `NOT NULL` で保証する**。SQLite は `ALTER TABLE` で `NOT NULL` を後付けできないのでテーブル再構築が要る（手順: サーバ停止 → DB コピー → `sqlite_master` でインデックス/トリガ/被参照 FK を洗う → 再構築 → **`ATTACH` で新旧を全列 `EXCEPT` 双方向突合** → `integrity_check`/`foreign_key_check` → 否定テスト）。AUTOINCREMENT の `sqlite_sequence` は RENAME で引き継がれる
- 2026-08-12 夜: **勘定科目の既定税区分を「税区分が空の行」の穴埋めに使ってはいけない**（ADR-0052・実測で発見）。科目の既定は「その科目の典型的な取引」に対する既定でしかない。減価償却の貸方（工具器具備品）に取得時の `PUR_10` が付いて課税仕入が 114,375 円過少になり、前受収益の按分振替の貸方（SaaS売上高）に `SALES_10` が付いて課税売上を二重計上した。**正しい規約は「各経路は税に意味のある行に必ず税区分を明示する。明示されなかった行は対象外」**（`JournalEntry.MarkRemainingLinesOutOfScope()` を全 15 経路が `Submit()` 直前に呼ぶ）。例外は入出金起票——相手科目そのものが経済的実体なので、そこだけ科目の既定を明示的に入れる（一律 対象外にすると受取利息＝非課税売上を取りこぼし課税売上割合が狂う）
- 2026-08-12 夜: **帳票で「差引」を出すときは、逆側に立った金額も別列で見せる**（ADR-0052）。消費税集計表は `accounts.dc_normal` を基準に符号を付けて差引にしたが、赤黒訂正と売上返品・値引（＝申告で別掲する「対価の返還等」）は DB 上区別できない。差引だけにすると別掲すべき事実が消えるので「戻し」列を併設し、判断材料を人に渡す。**率の表示は切り捨て**（課税売上割合が四捨五入で 99.995%→「100.0%」になり非課税売上の存在が消えた実測あり）
- 2026-08-12: **消費税は「税率ごとに集計 → 税率ごとに 1 回だけ端数処理」**（インボイス制度の「一の適格請求書につき、税率ごとに1回の端数処理」に対応）。行ごとに切り捨てて合算すると 1 円ずれる。実装は `List<decimal> rates` / `List<int> bases` で税率をキーに集計してから最後にまとめて計算する（見積・受注・検収・請求で共通）。なお **CLB スクリプトは `List<ModuleBase>` を引数型にできない**（designcheck が「List 不正なタイプです」）ので、この種のヘルパは引数なしで自前検索する形にする（`List<decimal>` などのローカル変数は問題なし）
- 2026-08-14: **「有効／無効」フラグは、選ぶ場所で効かないなら「表示」フラグと名乗る**（ADR-0054）。勘定科目・取引先の `IsActive` は参照フィールドの検索条件（`SearchTargetVariable: "IsActive.Value"`）に埋め込まれていて「候補に出ない＝選べない」が、**参照フィールドから引かれていないモジュール（定型仕訳）には同じ効き方が構造的に無い**。同じ名前で効き方だけ違うのは、利用者に「防いだつもり」の錯覚を与える。名前を実態（`ShowInList`＝一覧に出すか）に合わせ、廃止したいものは削除で消す、と用途を二分する。**新しいマスタに有効フラグを足すときは「そのフラグが効く場所はどこか」を先に確認する**。あわせて **Boolean フィールドは新規作成時の初期値が未チェック**なので、既定 ON にしたいなら `Detail_OnAfterInit` の `IsNewData` 分岐で明示的に `true` を入れる（入れ忘れると「作った直後に一覧から消える」等の静かな不具合になる）
- 2026-08-14: **スクリプトの `ref`/`out` 引数は呼び出し元に伝わらない**（ADR-0055・実測。FB-039）。`int PostOne(Draft d, ref int tax)` のように書いてもメソッド内の代入が戻らず、例外もエラーも出ない（消費税額の合計が常に 0 になった）。**戻り値が 2 つ要るときはモジュールレベル変数で受け渡す**。同じ日に踏んだ静かな失敗があと 2 つある: **`ListField` の行削除はメモリ上の操作**で保存しないと DB に届かない（画面から消えても再読込で復活。「変更を保存」で DB 側と画面側の行を突き合わせて明示的に `Delete()` する＝FB-040）、**日付列を `TEXT` で作ると CLB が `08/14/2026` 形式で書き込み SQLite の `date()` が NULL を返す**（日付は `DATE`・日時は `DATETIME` で宣言する＝FB-041）
- 2026-08-14: **「未設定」を許さない列は、保険の穴埋めを全経路の `Submit()` 直前に置く**（ADR-0056。税区分 ADR-0052 と同じ型）。`journal_lines.department_id` を NOT NULL 化し、`JournalEntry.FillMissingDepartments()` を 15 経路すべてで呼ぶ（税行は `ParentLineNo` から親行の部門を継ぎ、残りは部門マスタの `is_common`＝全社共通で埋める）。**保険は「入力を促す」役目を持たない**——損益科目の行に正しい部門を入れさせるのは各画面の責任で、人が画面にいる経路（振替伝票・入出金起票・仕入先請求書・銀行の一括起票）は空ならエラーで止め、機械経路は元データから転記する。この二段構えでないと「全部が全社共通で埋まって誰も気づかない」状態になる
- 2026-08-14: **確定済みレコードの一部フィールドだけを後から編集させたいときは専用画面を作る**（ADR-0056）。列単位の編集可否は `ListLayout` の要素の `IsViewOnly` で指定できるが、**レイアウトはスクリプトから切り替えられない**（ListField の script API に `LayoutName` が無い）ので、同じグリッドで「下書きは全項目編集／確定済みは一部だけ編集」は作れない。編集させたい列だけを開けた別レイアウト（JournalLine の `DeptEdit`）を、専用のホストモジュールから使う。**明細だけ保存しても親行は `UPDATE` されない**ので、監査証跡（`UpdatedAt`/`Updater`）が要るなら親に明示代入してから `Submit()` する
- 2026-08-14: **`SearchCondition` で「現在ログインユーザー」を直接参照できる**（`FieldVariableMatchCondition` の `Variable: "CurrentUser.Id.Value"`）。隠しフィールドにユーザー ID を入れて突き合わせる回避策は不要。「自分が作った行だけ見せる」ステージング画面（入出金起票の下書き）はこれ一発で書ける
- 2026-08-14: **`this.IsViewOnly = true`（モジュール全体）はボタンまで押せなくする**（実測。FB-035 の追記）。確定済み伝票を閲覧専用にする用途で使っていたが、そこに「部門・プロジェクトを修正する」ボタンを置いたら `pointer-events: none` で無反応になった。**閲覧専用にしたいのが入力項目だけなら、項目を 1 つずつ `IsViewOnly` にする**（`JournalEntry.LockPostedFields()`）。ただし**レイアウトに入力項目を足したら列挙も更新する**——足し忘れると確定済み伝票が編集できてしまう（安全側の既定と逆になるので、レイアウト変更時の確認事項）
- 2026-08-14: **「メニューには出さないが URL では開く」画面は `OtherPageModuleDesigns` に登録する**（実測。FB-042）。PageFrame の `Left.Links` から消しただけだと、遷移しても**コンテンツが真っ白**になる（サイドバーだけ描画・エラーもログも無し）。他画面から遷移するサブ画面（振替伝票 →「部門・プロジェクトを修正する」）はこの登録が要る。**遷移先の伝票などは URL のクエリパラメータで渡す**（`?entry=123` → `NavigationService.GetUniqueQueryParameters()` で受け取る）。表示専用モジュールは Id を持てないので、この受け渡しが定石

## スクリプトの作法: id の比較は文字列化してから

**`a.Value == b.Value` で id を比べない。** CLB スクリプトの `.Value` は動的型で、
**型が違うと（long と int、boxed の decimal など）値が同じでも `==` が false になる**。
エラーは出ないので「本人なのに本人と判定されない」「親行が見つからない」という**静かな失敗**になる。

```csharp
// ✗ 型が違うと黙って false
if (cat.Id.Value == l.TaxCategory.Value) { ... }

// ○ 家の定石
if ($"{cat.Id.Value}" == $"{l.TaxCategory.Value}") { ... }
```

`ApprovalFlow.mod.cs` の `IsSameId(a, b)` がこの定石そのもの。使えるところでは使う。
**数値・日付の比較（合計の一致、日付の前後）は普通に `==` `<` で比べてよい**——
問題になるのは「同じ実体を指しているか」を id で確かめる場面である（2026-08-19・BUG-0399）。

## ピッカー（SelectField）の絞り込みと、いま入っている値

**詳細画面のピッカーは `IsActive = true` で絞る。検索条件のピッカーは絞らない。**
無効になったマスタで過去データを探せなくなるからである。

**フィルタで除外された値でも、いまレコードに入っている値は候補に残り、選択も保たれる**
（2026-08-19 実測: 使用中の取引先を一時的に無効にして、その取引先を指す案件の編集画面を開いたところ、
候補にその取引先が居て選択も維持された）。**過去データの表示が壊れる心配をせずに絞ってよい。**

部門は `IsActive = true` に加えて `NodeType = 'dept'`（部ノード）も要る（ADR-0056/0044）。
