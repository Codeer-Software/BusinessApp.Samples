# ADR-0037: BusinessApp ソリューションへの移行と CLB 1.3.16 対応

- 日付: 2026-07-29
- 状態: 採用
- 関連: ADR-0017（LocalData 移設）・ADR-0024（User Secrets）・ADR-0025（環境設定の Git 除外）

## 背景（問題）

ランタイム・デザイナのソリューションを `AccountingApp`（CLB 1.3.4／デザイナ 1.3.6）から、
ユーザーが新規作成した `BusinessApp` ソリューション（CLB 1.3.16）へ丸ごと入れ替えることになった。
パッチ番号の差以上にエコシステムが変わっている:

- **サーバテンプレートの再編**: `*.Server.Shared` プロジェクトが廃止され、AI/Excel/Mail/
  FileManagement/FileWatcher(HotReload) が NuGet パッケージ **`Codeer.LowCode.Blazor.Extras.Server` 0.4.0** に移動
  （旧 Extras 0.0.17 → 0.4.0、ApexCharts 0.23 → 0.25.3）。
- **AITextAnalyzer の型移動**: フィールド型 `AITextAnalyzerFieldDesign` が旧 Client.Shared のサンプル実装
  （`Design.Samples.AIDocumentAnalyzer`）から **`Codeer.LowCode.Blazor.Extras.Designs`（Extras 本体）** へ移動。
  旧 TypeFullName を含む `ExpenseRequest.mod.json` は 1.3.16 で**読込不能**（デザイナのビルド失敗の主因）。
- **Claude Code ワークスペースの世代交代（デザイナ 1.3.15 以降）**: `ClaudeCodeForDesigner/` は
  `ai-refresh` が丸ごと再生成する生成物（`_defaults/`・`_samples/`・`_specs/`・`_field_catalog.md`・
  `_script_catalog.md`）となり Git 追跡外に。`LocalEnvironment.md` はワークスペース直下
  （`Designer/` 直下）へ移動。CLI に `rename-*`/`rename-batch`/`ai-refresh`/`field-catalog`/
  `script-catalog`/`defaults`/`template-*`/`claude-workspace` が追加。
- **designcheck の検証強化**: PageFrame 内 SearchCondition の `SortFieldVariable` 等の変数参照が
  検証されるようになり、旧版では素通りしていた潜在不備（存在しないフィールドへのソート指定＝実行時 no-op）が
  顕在化した。

## 決定

1. **デザインフォルダを `Designer/Designer/` → `Designer/Design/` に改名**（新ワークスペースの
   claude-workspace 生成物＝フック・許可リストが `Design` 前提のため。以後この名前が正）。
2. **デザインの 1.3.16 対応**:
   - `ExpenseRequest.mod.json` の AITextAnalyzer TypeFullName を `Codeer.LowCode.Blazor.Extras.Designs.` へ変更。
   - PageFrame の無効 SortFieldVariable を整理（ReceiptBoard/JournalEntryBoard=クリア、
     承認ルート判定=自モジュール Priority 昇順に正して説明文どおりの表示に）。
   - `app.clprj` の `Versions[]` を 1.3.16/0.4.0/BusinessApp.Client.Shared/0.25.3 に更新。designcheck 0 件。
3. **サーバ側カスタムの再統合方針**: Mock/Claude パスは in-project 実装を維持しつつ、
   **AzureOpenAI パスは Extras.Server 標準 `AITextAnalyzeService` へ委譲**（カスタム C# 最小化の原則）。
   `AISettings` は Extras 基底を拡張した `AppAISettings`（Provider/ClaudeApiKey/ClaudeModel 追加）。
   BankAi/InvoiceCheck 両コントローラと NtaInvoiceSettings は従来どおり移植。
4. **UserSecretsId は旧プロジェクトと同一値を引き継ぐ**（API キー再登録不要。ADR-0024 の運用継続）。
5. **旧資産（`AccountingApp.slnx`・`AccountingApp/`・`Designer/*.old`）は削除**。履歴は Git に残る。

## 結果

- designcheck 0 件・`dotnet build` 0 警告 0 エラー・deploy.ps1（zip 形式そのまま）＋
  FileWatcher(SignalR 化) の hot-reload 実機確認済み。
- E2E スモーク（admin/経理の 2 ロール）: 仕訳 CRUD・試算表/BS（貸借一致）/PL/月次推移・
  資金繰りチャート（ApexCharts）・総勘定元帳・請求/入金（相殺表示）/仕入請求/支払予定・
  SES 精算/定期請求プラン・銀行明細取込プレビュー・経費申請＋ **AI 領収書読み取り（実キー・
  委譲パスで抽出成功）**・インボイス照合（モック）・ユーザー管理・承認ルート判定・ログアウトを確認。
- 未検証: Excel/PDF ダウンロード出力（ブラウザ自動操作のダウンロード許可の都合。次回手動確認）。

## 備考（将来の罠）

- `ClaudeCodeForDesigner/` は手で編集しない（ai-refresh で全消し再生成される）。恒久メモは
  `Designer/Project.md` か `Designer/LocalEnvironment.md` へ。
- 過去 ADR（0017/0024/0025 等）内の `AccountingApp/...` パス表記は歴史的記録としてそのまま。
  現行パスは本 ADR とルート CLAUDE.md が正。
