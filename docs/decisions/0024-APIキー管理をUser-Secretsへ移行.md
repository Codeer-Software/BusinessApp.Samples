# ADR-0024: APIキー管理を .NET User Secrets へ移行（skip-worktree 運用の廃止）

- 日付: 2026-07-18
- 状態: 採用・実装済み（実機検証済み）
- 関連: ADR-0017（実行環境データの LocalData 移設——本 ADR で参照パス変更がようやくコミットされた）

## 背景

Azure OpenAI / Document Intelligence の実キーは `appsettings.Development.json` の作業ツリー版にのみ書き、`git update-index --skip-worktree` で誤コミットを防ぐ運用だった（2026-07-06 決定、docs/05 記録）。Git 履歴にキーが入ったことは一度もない。しかしこの方式には実害が確認された:

1. **状態が見えない**: `git status` が clean に見えるため、レビュー時に「作業ツリーの内容＝コミット済み」と誤認しやすい（2026-07-18 に実際に誤認が発生し、ユーザーへ誤った指摘をした）
2. **非秘密の変更まで隠れる**: ADR-0017 の LocalData パス移設が本ファイルに未コミットのまま残っていた（コミット版は旧 `C:\Codeer.LowCode.Blazor.Local\` パスのままだった）
3. **Git 操作に脆い**: stash / 履歴を跨ぐ checkout で衝突・変更消失の原因になり、clone ごとに再適用が必要
4. **作業ツリーに平文キーが残る**: フォルダごと zip で配布するとキーが漏れる

## 決定

**「ファイルは Git に残し、秘密だけを .NET User Secrets に出す」**。`appsettings.Development.json` の Git 除外はしない（このファイルは接続文字列・LocalData パス・HotReload 等、新環境でアプリを動かすための必須設定の雛形であり、履歴で追跡する価値がある。コミット自体は .NET の標準慣行で、アンチパターンは「秘密を書くこと」の方）。

- `AccountingApp.Server.csproj` に `UserSecretsId`（dfae2e03-01ce-4d9a-a011-556c5eed75d0）を追加
- User Secrets に移した設定（秘密＋環境固有のプロバイダ選択）:
  - `AISettings:Provider = AzureOpenAI`（コミット版の既定は Mock のまま＝clone 直後はキー不要で動く）
  - `AISettings:OpenAIEndPoint` / `OpenAIKey` / `ChatModel`
  - `AISettings:DocumentAnalysisEndPoint` / `DocumentAnalysisKey`
  - （将来: `ClaudeApiKey`・`NtaInvoiceSettings:ApplicationId`・`MailSettings:Password` も同様にここへ）
- `appsettings.Development.json` はサニタイズ版（Provider=Mock・キー/エンドポイント空）に戻し、LocalData パス（ADR-0017 分）を正式にコミット
- skip-worktree フラグを解除

コード変更は不要: `Program.cs` は標準の `builder.Configuration` で読んでおり、ASP.NET Core の既定パイプライン（appsettings → appsettings.{Env} → **User Secrets** → 環境変数）がそのまま効く。

## 検証（2026-07-18 実機）

- サーバー起動 → admin ログイン → `POST /api/bank_ai/suggest`（Lines 空）が `isMock: false` を返却＝ファイルは Mock でも User Secrets の Provider=AzureOpenAI が優先されることを確認
- 実明細 2 行で Azure OpenAI 実コール成功（振込手数料→6130 支払手数料／振込入金→3110 売掛金）＝キーが User Secrets 経由で正しく渡ることを確認

## 新環境セットアップへの影響

clone 直後は Provider=Mock で全機能がキー無しで動く（従来どおり）。実 AI を使う場合のみ:

```
dotnet user-secrets set "AISettings:Provider" "AzureOpenAI" --project AccountingApp/AccountingApp.Server
dotnet user-secrets set "AISettings:OpenAIEndPoint" "<endpoint>" --project AccountingApp/AccountingApp.Server
dotnet user-secrets set "AISettings:OpenAIKey" "<key>" --project AccountingApp/AccountingApp.Server
dotnet user-secrets set "AISettings:ChatModel" "<deployment>" --project AccountingApp/AccountingApp.Server
dotnet user-secrets set "AISettings:DocumentAnalysisEndPoint" "<endpoint>" --project AccountingApp/AccountingApp.Server
dotnet user-secrets set "AISettings:DocumentAnalysisKey" "<key>" --project AccountingApp/AccountingApp.Server
```

## 残課題・注意

- `appsettings.Development.json` の LocalData パスは絶対パスのまま（環境依存）。相対パス化はサーバーの作業ディレクトリ前提の検証が必要なため本 ADR ではスコープ外とした
- 配布時は従来どおり `git archive` 推奨（作業ツリーには User Secrets は含まれないため、zip 配布のキー漏洩リスクは本移行で解消済み）
