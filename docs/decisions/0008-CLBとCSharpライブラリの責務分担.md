---
title: ADR-0008 CLB と C# ライブラリの責務分担
status: current
scope: 会計コア
audience: [開発]
updated: 2026-08-26
supersedes: []
related: [0002-部品の定義と分割の判断基準.md, 0004-優良な電子帳簿への準拠と仕訳の不変性.md, 0009-専門家レビューの代替手段.md, 0022-明細を並べる帳簿はクエリモジュールで作る.md]
---
# ADR-0008 CLB と C# ライブラリの責務分担

> **状態**: 有効。ただし「実行場所の決め方」の表のうち**「帳票・集計」の行の『元帳』だけ**は
> [ADR-0022](0022-明細を並べる帳簿はクエリモジュールで作る.md) が上書きした（2026-08-26）。
> **明細を並べる帳簿（仕訳帳・総勘定元帳・補助元帳）はクエリモジュールで作る。**
> 畳んで初めて意味が出る集計帳票（試算表・B/S・P/L・税集計）がサーバ完結であることは変わらない。

## 状況

前回プロジェクトは業務ロジックを CLB スクリプト（`*.mod.cs`）に置いた。CLB スクリプトは
**ツリーウォーク型インタプリタ**で、静的検査が事実上効かない。実測で次の「静かな失敗」を踏んでいる。

- `AddRows()` の多重定義解決が外れて実行時に落ちる（`designcheck` は緑）
- 整数どうしの割り算が小数になり、請求額が過大になった
- ユーザー定義関数の `ref` / `out` が呼び出し元に伝わらず、消費税額が常に 0 になった
- `OrderBy` を 2 回呼ぶと 1 本目のキーが黙って捨てられる

会計ロジックの不具合は製品として致命的である。**テスト可能な場所へロジックを移す**必要がある。

## 決定

### プロジェクト構成

| プロジェクト | 役割 | 依存 |
|---|---|---|
| `BusinessApp.AccountingCore` | **純粋ドメイン**。仕訳・税額計算・集計・検証・制度ルールの解決。副作用なし | なし（.NET 標準のみ） |
| `BusinessApp.AccountingCore.Server` | サーバ側。DB アクセス、帳票 API の Controller 実装、保存時の関門 | `AccountingCore` |
| `BusinessApp.AccountingCore.Client` | CLB スクリプトから呼ぶサービスの登録。軽い計算はローカル、重い集計はサーバ API を叩く | `AccountingCore` |
| `BusinessApp.AccountingCore.Tests` | ユニット・ゴールデン・性質テスト | `AccountingCore`（＋必要なら Server） |

`AccountingCore` を**外部依存ゼロ**に保つことが要である。DB も HTTP も CLB も知らない純粋な計算だけを置く。

### CLB 側の責務

CLB は**薄いプレゼンテーション層**に徹する。

- 画面（レイアウト・一覧・検索・入力フォーム）
- マスタの CRUD（勘定科目・取引先・部門など、ロジックの薄いもの）
- 仕訳の入力 UI とデータの保存
- `*.mod.cs` に書いてよいのは、**画面上の値の出し入れと、C# サービスの呼び出しだけ**。
  金額計算・税額計算・状態遷移の判断をスクリプトに書かない

### CLB から C# を呼ぶ経路（Extras の実装に基づく）

`ScriptRuntimeTypeManager` にサービス・型を登録すると、CLB スクリプトから型名で直接呼べる。
サービスの実体は**クライアント（WASM）で動く**。サーバの処理が必要なときは、
アプリ側が定義したエンドポイント URL へ POST する（Extras の `MailService.SendMailEndPoint` /
`BulkFileTransferService.BulkSubmitEndPoint` などが実例）。

```csharp
// BusinessApp.Client.Shared/Services/ServiceInitializer.cs での登録イメージ
var manager = app.GetScriptRuntimeTypeManager();
manager.AddService(new AccountingService(http));   // スクリプトから AccountingService.XXX() で呼べる
```

### 実行場所の決め方

| 処理 | 実行場所 | 理由 |
|---|---|---|
| 1 伝票の検証・税額計算（入力中の即時フィードバック） | **クライアント**（`AccountingCore` を WASM に載せる） | 往復なしで応答する。同じライブラリなのでサーバと結果が一致する |
| 保存時の不変条件の強制 | **サーバ**（`ModuleDataIO` の override で `AccountingCore` を呼ぶ） | クライアントの制御は迂回できる。**最終的な関門は必ずサーバに置く** |
| 帳票・集計（試算表・B/S・P/L・税集計。**元帳は ADR-0022 が上書き**） | **サーバ完結**（Controller が SQL で明細を取り、`AccountingCore` で集計して結果を返す） | クライアントへ明細を全件降ろしてから再送する二重往復を避ける |
| 単純な一覧・検索 | CLB 標準（`ListField` / `SearchField` / `QueryField`） | CLB が最も得意とする領域。自前で作り直さない |

**帳票のクライアント側は「条件を送り、集計済みの結果を受け取って描画する」だけ**にする。
描画は CLB の一覧で足りなければ `ProCodeField`（カスタム Blazor コンポーネント）を使う。

## 理由

- **同じ検証ロジックをクライアントとサーバの両方で走らせられる**のが、この構成の最大の利点である。
  実装が 1 つなので食い違わず、UX（即時フィードバック）と安全性（サーバでの強制）を両立できる
- 集計をサーバ完結にするのは往復回数の問題である。CLB のデータ取得はクライアント（WASM）向けなので、
  「クライアントへ全件降ろす → サーバへ送り直して集計する」形にすると件数に比例して重くなる
- `AccountingCore` を依存ゼロに保つのはテストのためである。DB も HTTP も要らないので、
  **境界値のゴールデンテストを数千件書いても一瞬で回る**

## 帰結

- **CLB スクリプトの行数が少ないほど良い**という基準を持つ。スクリプトに条件分岐と計算が増え始めたら、
  それは C# へ移すべきロジックが漏れているサインである
- `AccountingCore` は WASM にも載るので、**サイズと起動時間に配慮**する（大きな依存を入れない）
- サーバ側の関門（`ModuleDataIO` の override）は、CSV 取込・API・スクリプトのどの経路からも
  必ず通る位置に置く（[ADR-0004](0004-優良な電子帳簿への準拠と仕訳の不変性.md) の「迂回経路を作らない」）
- 帳票 API のエンドポイント URL は**アプリ側（`ServiceInitializer`）が一元定義**する。
  ライブラリに URL を埋め込まない（Extras の作法に合わせる）
- 実際の呼び出し方・型の登録方法は Extras の
  `Source/Codeer.LowCode.Blazor.Extras/ExtrasClientInitializer.cs` を正典として参照する
