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
- 2026-07-07: **API 経由で logout した後に login.html へ直行するとログイン不能（400・"Login failed"）**。antiforgery トークンはユーザー束縛で、発行ミドルウェアは SPA フォールバック GET（`/` や `/Main/...`）でしか走らない（login.html は静的ファイル＝UseStaticFiles が短絡するため未更新。admin 束縛の古いトークンが残る）。**対処: logout 後に一度 `/` を GET してから login.html へ**（サイドバーの Logout リンク経由の通常フローは自然に `/` を踏むため影響なし＝アプリの不具合ではない）。パスワード誤りと切り分けるには 400（antiforgery）と 401（credential）をサーバログ or fetch で確認
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
