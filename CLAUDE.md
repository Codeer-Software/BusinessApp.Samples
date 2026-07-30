# 財務会計アプリ 自律構築ミッション（CLAUDE.md）

> このファイルは、本リポジトリで **Fable（Claude Code）が自律的に本格財務会計アプリを構築する**ための最上位の指示書（ミッションブリーフ）。
> CLB の具体的な作り方（JSON / フィールド / レイアウト / 検証CLI）は**ここには書かない**。それらは `Designer/` 配下の既存ドキュメントに完備されており、本書はそこへ誘導する。
> **方針が固まったら Fable 自身が本ファイルと `Designer/Project.md` を更新してよい**（ただしミッションの芯＝§1 は変えない）。

## 0. 着手前に必ず読む（順番厳守）

1. **本ファイル全体**（ミッション・スコープ・運用ルール）
2. **`Designer/CLAUDE.md`** — CLB デザインワークスペースの運用ルール（ツールの使い方・ファイル配置・検証手順・スコープ規律）
3. **`Designer/ClaudeCodeForDesigner/CLAUDE.md`** — **CLB 仕様リファレンス（通読）**。フィールド型・レイアウト・スクリプト・`designcheck`/`sql`/`rename-*` CLI・生成リファレンス（`_defaults/`・`_samples/`・`_specs/`・`_field_catalog.md`・`_script_catalog.md`）の索引を兼ねる。**このフォルダ全体はデザイナが `ai-refresh` で再生成する生成物**（Git 追跡外・手で編集しない）
4. 補助として **CLB Web マニュアル**（人間向け解説。`ClaudeCodeForDesigner/` と対になる内容）: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Manual

設計作業の実体は **`Designer/Design/`**（`app.clprj` を持つデザインプロジェクト）。ここに `Modules/` `PageFrames/` `Resources/` を作る。

## 1. ミッション（芯・変更不可）

**ASP.NET + Codeer.LowCode.Blazor（以下 CLB）で、中小企業が実運用できる本格的な財務会計アプリを作る。**

- **目的は CLB の売り込み。** 伝えたいメッセージは2つ:
  1. 「CLB はここまでできる」（技術デモ）
  2. 「このアプリをテンプレートに少し手を加えれば、御社で実際に使える」（導入提案）
- したがって **可能な限り CLB で作る。** CLB 単体で実現できない部分に限り C#/ASP.NET を使う（§2）。
- **到達目標は「市販の財務会計専門ソフト以上」。**「これがあれば弥生会計や freee は要らない」と言えるレベル。**複式簿記の仕訳・財務諸表の作成まで含む"全部入り"**。市販ソフトの**代替**であり、共存・補助ツールではない。
- **ペルソナ: IT 受託ソフトハウス（従業員30〜80名／受託開発＋SES＋一部自社SaaS）。** このペルソナが満足する機能を盛り込む。Phase 0 でペルソナを具体化し文書化する（§4）。
  - 選定理由（Fable が方針を理解するために明記）: CLB の買い手＝ソフトを作る会社であり、デモの聴衆とペルソナが一致する。かつプロジェクト別損益・工数原価・受託×SaaS の売上計上など「市販ソフトが弱く CLB が強い」会計題材が豊富。

## 2. CLB 最優先の原則

- 画面・データ・業務ロジックは **CLB のモジュール（`*.mod.json`）／スクリプト（`*.mod.cs`）／SQL／クエリ／チャート** で作る。着手時はまず `Docs/AppPatterns/` の該当パターンと `Samples/` の実装を**正典として複製・改変**する（ゼロから JSON を組まない）。
- **C#/ASP.NET 拡張は「CLB 単体では不可能な所」だけ**に使う:
  - AI 領収書 OCR／勘定科目推定（LLM 呼び出し）
  - 外部 API 連携（例: インボイス番号の国税庁照合）— WASM 直叩きは CORS／APIキー秘匿で不可。**サーバ経由が定石**
  - 標準機能で足りない帳票 等
  - 実装先は**ランタイム側プロジェクト**: サーバ拡張＝`BusinessApp/BusinessApp.Server/Services/`（既存の `Services/AI/` が参考）、スクリプトから呼ぶ拡張サービス＝`BusinessApp/BusinessApp.Client.Shared/Services/` 配下。追加方法は `Designer/ClaudeCodeForDesigner/_specs/ScriptExtensions.md`。
- 「ノーコードで大半、高度な所だけ少量の C#」——この比率自体がデモの訴求。**C# に逃げる前に「CLB で本当に無理か」を必ず確認する。**

## 3. スコープと実装順序

**大原則: まず会計コアを完成させる。** その上に周辺業務、さらに差別化機能を載せる。1回の実行で全ては終わらない前提で、複数回に分けて前進する（§4）。

**スコープの現状（2026-07 時点）: フェーズ A（会計コア）／B（周辺業務）／B'（IT 受託特化の管理会計）／C（差別化: AI 領収書・資金繰り予測・インボイス照合）／D（全部入り: 月次推移・銀行取込・C/F 計算書・定型仕訳・仕訳 CSV 取込・買掛支払管理）の全機能が実装・実機検証済み。** 各フェーズの機能定義・優先度・見送り判断の正典は `docs/03_機能スコープ.md`、実装状況と次タスクは `docs/05_実装計画と進捗台帳.md`（本書には再掲しない。新機能を検討する際は必ず docs/03 を先に読むこと）。

### 範囲・優先の判断
- **税制・会計基準は最新をリサーチして実装し、改正にオープンな設計にする。** 消費税率・インボイス制度・電子帳簿保存法・法人税/所得税・各種控除などは毎年のように改正される。実装時点の最新制度を Web リサーチで確認し、「どんな改正がありうるか」も随時調べる。かつ**税率・税区分・閾値・勘定科目などはマスタ化してハードコードしない**（将来の改正に設定変更だけで追随できる設計にする）。
- 上記の網羅順・粒度・「今回見送るもの」（例: 連結会計・多通貨フル・電子申告直結・電子帳簿保存法の完全準拠 等）は **Fable が実装計画で決め、`Designer/Project.md` と計画ドキュメントに記録**する。中小 IT 企業の実運用に必要な範囲を優先。
- 承認・ワークフロー等は**認証前提**。既存 `Designer/ClaudeCodeForDesigner/Samples/PatternShowcaseAuth/` の承認フローを土台にする（ゼロから作らない）。

## 4. 自律運用ルール（複数回実行の前提）

### 自律と判断の原則
- **原則として自律的に動き、機能・仕様の意思決定まで自分で行う。** 網羅順・データモデル・画面構成・技術選択などは Fable が決めて進める。判断に迷ったら、止めるより**根拠を残して前進**する方を基本とする。
- **ただし「どうしてもユーザーに確認すべき」と感じたら、遠慮なく質問する。** 後戻りコストの大きい根本設計、不可逆な操作、要件の重大な曖昧さなどは、勘所を逃さず確認する。自律＝ユーザーを締め出すこと、ではない。
- **ユーザーが意見を問いかけてきたとき（「どう思う？」「実装する価値ある？」等）は、賛成であっても実装はまだ始めず、回答のみ行う。** その時点のユーザーは検討を深めたい段階にある。**実装に着手するのは、ユーザーが明示的に GO を出した後**（2026-07-25 ユーザー指示）。同じ依頼の中に「明確な指示」と「問いかけ」が混在する場合は、指示分は実装し、問いかけ分は回答で止める。
- **各機能・仕様は、常に最新情報をリサーチして決定する**（`WebSearch`／`WebFetch` を使う）。記憶や思い込みで会計・税務・CLB の仕様を決めない。

**完全自律で構築する。** 各回、以下のサイクルで前進する:

1. **状態を読む**: 計画・進捗台帳（下記）を読み、前回の続き＝次タスクを特定
2. **設計する**: 着手前に該当 `Docs/AppPatterns/` と `Samples/` を確認（§2）
3. **作る**: `Designer/Design/` に Modules／スクリプト／SQL／PageFrame を作成・編集
4. **検証する**（§5）: `designcheck` → `sql` で DB 整合 → 必要ならデプロイしてブラウザ実機確認
   - **CLB の改善に気づいたら都度 `docs/12_CLB改善提案/` に記録する**（回避策を作った・マニュアルに無い事実を実測した・静かな失敗を踏んだ、が記録タイミング。運用ルールは同 README）
5. **コミットする**: **Conventional Commits**。意味のある単位で。**ローカル Git のみ（リモート無し）**。ブランチの切り方・手戻りは自由
6. **記録する**: 進捗台帳・`Project.md`・仕様書を更新

### Phase 0 成果物（最初の実行で必ず作る）
Fable が**自分でドキュメントを作成・管理**する。計画・仕様ドキュメントの置き場所は **`docs/` に統一**する:
- 企画概要（何を・なぜ・売り込みメッセージ）
- **ペルソナ具体化**（IT 受託ソフトハウスを土台に、社名像・年商・組織・業務・痛点まで）
- 機能スコープ（本書 §3 を land させ、優先度・見送り判断を明記）
- **会計ドメイン設計**（勘定科目体系・仕訳／伝票のデータモデル・会計期間・貸借の持ち方。**複式簿記の正しさはここで担保する**。実装より先に必ず設計する）
- **実装計画＋進捗台帳**（フェーズ分割・チェックリスト・"次にやること"。毎回ここを起点に再開できる形にする）
- `Designer/Project.md` を埋める（データソース・命名・業務ルール・既存資産）

### ドキュメント運用
- 仕様・設計・意思決定は**都度ドキュメント化**する（上司・顧客への説明資料の下地を兼ねる）。
- 本 CLAUDE.md と `Project.md` は、方針が固まったら Fable 自身が更新してよい（ミッションの芯＝§1 は変えない）。
- ユーザーとのやり取りは**日本語**で行う。

## 5. 検証ループ（必須・自律実行可）

| 対象 | 手段 | 備考 |
|---|---|---|
| デザイン読込妥当性 | `designcheck` CLI | 作成・編集の都度。`findingCount` が 0 になるまで直す。詳細は `Designer/ClaudeCodeForDesigner/CLAUDE.md` |
| DB（DDL・投入・確認） | `sql` CLI | モジュールを作ったらテーブルも用意する。主キーは INTEGER 自動採番が原則 |
| 稼働アプリへの反映（デプロイ） | **`Designer/tools/deploy.ps1` を実行**（`Designer/Design/` 一式を zip 化 → `LocalData\designs\App.zip` に配置） | `FileWatcher` が `*.zip` を検知して hot-reload（GUI「送信」の代替）。zip 内のパス区切りは `\`（デザイナ独自の詰め方・deploy.ps1 が再現） |
| 画面・挙動 | サーバ起動（`http://localhost:5085`）→ ブラウザでスクショ／操作 | `designcheck` で拾えない意味的バグ（合計計算・状態による出し分け等）を実際に見て潰す |

**検証系は設定済み（この環境で確認済み）**:
- **デザイナ exe のパス**は `Designer/LocalEnvironment.md` の `DesignerExePath:` に登録済み（1.3.15 以降の claude-workspace 方式では `Designer/` 直下に置く）。`designcheck`／`sql`／`rename-*`／`ai-refresh` 等を含む自律実行の許可は、**ポータブルな分がルート `.claude/settings.json`（手動管理の恒久リスト）**、**マシン固有の絶対パスを含む分が Git 追跡外の `.claude/settings.local.json`（雛形: `.claude/settings.local.json.sample`）**と `Designer/.claude/settings.local.json`（claude-workspace 生成）にある（不足が出たら追記して育てる方針。絶対パスを含む許可を追跡ファイルに書かないこと＝ADR-0038）。exe を再ビルドしてパスが変わったら LocalEnvironment.md と両 settings.local.json を更新する。
- build（net8.0）・run（`http://localhost:5085`）・deploy（`LocalData\designs\App.zip`）・hot-reload・ブラウザ実機確認は動作確認済み。

## 6. この環境の既知事実

- **ランタイム**: `net8.0`。Server は作業フォルダを直読みせず、`LocalData\designs\App.zip` からデザインを読む。`UseHotReload:true`。
- **DB**: SQLite（`LocalData\db\business-app_v1.db`。実行環境データの構成は `LocalData/README.md`・移設経緯は `docs/decisions/0017`）。**`AllowCliSqlAccess:true` のデータソースだけ** `sql` CLI の対象になる（安全境界）。
- **認証**: Cookie 認証（`AppUser` モジュール、初期ユーザー admin/admin）。承認ワークフローは認証前提。
- **AI 連携**: プロバイダ切替式（`AISettings.Provider`: Mock／Claude／AzureOpenAI。Server の `Services/AI/`。AzureOpenAI パスは Extras.Server 0.4.0 の標準 `AITextAnalyzeService` へ委譲）。実キーは .NET User Secrets（ADR-0024）。Claude API を使う実装では**最新モデル（例: Claude Opus 4.8）**を使い、サーバ経由で実装する。実装前に `claude-api` の情報を確認すること。
- **CLB バージョン**: ランタイム・デザイナとも 1.3.16（2026-07-29 の BusinessApp ソリューション移行で更新・ADR-0037。Extras 0.4.0／ApexCharts 0.25.3。`HorizontalAlignment` は列挙 `Start/Center/End/Stretch`——旧値 `Left/Right` は**エラーにならず既定 Start に化ける**ので使用禁止）。ソリューションは `BusinessApp.slnx`＋`BusinessApp/`（旧 AccountingApp ソリューションは廃止）。

## 7. 参照ドキュメント（索引）

| ドキュメント | 用途 |
|---|---|
| `Designer/CLAUDE.md` | ワークスペース運用ルール（着手前に必読） |
| `Designer/ClaudeCodeForDesigner/CLAUDE.md` | **CLB 仕様リファレンス（通読）** |
| `Designer/ClaudeCodeForDesigner/Docs/` | AppPatterns／各 Guidelines |
| `Designer/ClaudeCodeForDesigner/_specs/` | フレームワーク仕様リファレンス（ModuleDesign／Layouts／Scripts 等） |
| `Designer/ClaudeCodeForDesigner/_field_catalog.md`・`_script_catalog.md` | フィールド型・スクリプトオブジェクトの動的生成カタログ（真実の源） |
| `Designer/ClaudeCodeForDesigner/_samples/` | 複製元の正典（PatternShowcase／PatternShowcaseAuth 等） |
| `Designer/ClaudeCodeForDesigner/_defaults/` | 全デザイン型の既定 JSON |
| `Designer/Project.md` | プロジェクト固有ルール（Fable が育てる） |
| CLB Web マニュアル | 人間向け解説: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Manual |
