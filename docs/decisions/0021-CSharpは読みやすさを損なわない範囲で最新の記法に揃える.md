---
title: ADR-0021 C# は読みやすさを損なわない範囲で最新の記法に揃える
status: current
scope: 会計コア
audience: [開発]
updated: 2026-08-25
supersedes: []
related: [0012-テスト方針とカバレッジのゲート.md, 0008-CLBとCSharpライブラリの責務分担.md]
---
# ADR-0021 C# は読みやすさを損なわない範囲で最新の記法に揃える

## 状況

対象は .NET 8（C# 12）。コードは primary constructor・コレクション式・record など
新しい記法を既に多く使っているが、古い書き方（文字列連結・`Environment.NewLine` の連結）と
読みにくい新記法（`is { }`）が混在している。

**開発者の指示（2026-08-25）**: C# 12 までのできるだけ新しい記法を使う。
見本として `JournalPostingRejectedException.cs` を開発者自身が改稿した。
あわせて **`is { }` は非常に分かりにくいので使わない**。

## 決定

### 1. 目的は読みやすさである。新しさそれ自体ではない

新しい記法でも読みにくくなるもの（`is { }` が代表）は採らない。
迷ったら「初見の読み手が意味を一意に取れるか」で決める。

### 2. 記法の対応表

| 場面 | 使う | 使わない |
|---|---|---|
| null 検査＋値の取り出し | **型パターン** `x is int v` / `x is string s` | `x is { } v`（**禁止**。null 検査に見えない） |
| null 検査のみ | `is null` / `is not null` | `== null` / `!= null`（演算子の多重定義に左右される） |
| 文字列の組み立て | **補間** `$"{...}"` | `+` の連結 |
| 複数行の文字列 | **raw string literal** `$"""..."""` | `Environment.NewLine` の連結 |
| コレクションの生成 | **コレクション式** `[...]`・スプレッド `[.. xs]` | `new List<T> { ... }`・`ToArray()` の羅列 |
| 依存の受け取り | **primary constructor** | フィールド代入だけのコンストラクタ |
| 型が左辺で明らかな生成 | target-typed `new()` | 型名の繰り返し |
| 条件分岐で値を返す | **switch 式**・パターンマッチ | if-else の連鎖で代入 |
| 名前空間 | file-scoped（既に全域） | ブロック形 |

**raw string literal の改行はソースファイルの改行そのもの**であり、
`Environment.NewLine`（Windows では CRLF）ではない。しかも作業コピーの改行は
クローンごとの `core.autocrlf` 設定に依存する（現状の `.gitattributes` は `eol` を固定していない）。
そこで 2 つで守る: ① `.gitattributes` に `*.cs text eol=lf` を敷いて**ソースの改行を LF に固定**する
（一斉見直しタスクで導入）。②**利用者向け文言の改行は raw literal の改行（＝LF）に統一し、
`Environment.NewLine` を混ぜない**。`string.Join` で行を束ねるときも `"\n"` を使う
（表示先はブラウザで、LF で足りる。混ぜると 1 つの文言の中で改行コードが割れる）。

### 3. スコープ

- **対象**: 自作の C#（`AccountingCore` 系・`TestSupport`・`Schema.Tests`・`SchemaVerifyCli`、
  `BusinessApp.Server` と `BusinessApp.Client.Shared` の自作拡張部分）
- **対象外**: **CLB スクリプト（`*.mod.cs`）**。ツリーウォーク型インタプリタが対応する
  範囲しか書けない（[ADR-0008](0008-CLBとCSharpライブラリの責務分担.md)）。新記法を持ち込まない
- **必要最小限に留める**: CLB テンプレート由来のファイル（`Program.cs` など）。
  テンプレート更新との差分を無用に増やさない

### 4. 機械で守る

- `.editorconfig` の `csharp_style_*` / `dotnet_style_*` を warning に上げ、
  `EnforceCodeStyleInBuild` でビルドに載せる。`TreatWarningsAsErrors` と組で強制になるが、
  **これが無いプロジェクト（`Schema.Tests`・`AccountingCore.Tests`・`AccountingCore.Server.Tests`）には
  追加する**。`BusinessApp.Server` はテンプレート由来コードに警告が出るので
  **ビルド強制の対象外**とし、自作部分は規約テストで守る
- ビルドで強制できるのは一部のルールに限る（IDE 診断にはビルドで報告されないものがある）。
  効かないもの・アナライザで表現できない禁止形（`is { }` など）は
  **規約テスト**（`Conventions/`）がソースを正規表現で検査する
- `.gitattributes` に `*.cs text eol=lf` を足し、raw string literal の改行を LF に固定する
- いずれも一斉見直しのタスク（[docs/05 §1](../05_実装計画と現在地.md)）で導入する。
  **導入前でも、新しく書くコードは本 ADR に従う**

### 5. 挙動を変えないことの担保

一斉見直しは「挙動を変えないリファクタリング」として行い、既存の全ゲート
（テスト・カバレッジ 100%・ミューテーション・`designcheck`・`-Verify`）を通し、
主要画面の実機確認で締める（[ADR-0012](0012-テスト方針とカバレッジのゲート.md)）。
記法の置き換えで意味が変わりうる箇所（演算子の多重定義・改行コード・遅延評価）は、
機械的に置換せず 1 か所ずつ読む。

## 理由

- **早いほど安い。** 後回しにするほど直す箇所が増える（開発者の指摘）。
  全ゲートが緑で機能作業が仕掛かっていない今が、挙動を変えないリファクタリングの最安のタイミング
- **規約は書いた時点から効く。** 一斉見直しを先にやれば、以後のすべてのタスクの差分が
  安定した記法の上に乗り、レビューのノイズが減る
- `is { }` の禁止は好みの問題ではなく読み手のコストの問題である。`is int v` は
  「int が入っていたら取り出す」と読めるが、`is { }` は null 検査であることが字面から読めない

## 帰結

- 一斉見直しを [docs/05 §1](../05_実装計画と現在地.md) の着手順 1 番に置く
- 開発者の見本の変更（`JournalPostingRejectedException.cs`）はタスクの最初に取り込む。
  ただし取り込み時に 2 点を規約に揃える:
  ① `is not null and int lineNo` → `is int lineNo`（同義。型パターンが null を通さないため、
  規約の推奨形は短い方にした。**開発者と合意済み。2026-08-25**）
  ② raw string literal の中の `string.Join(Environment.NewLine, ...)` → `string.Join("\n", ...)`
  （見本のままだと 1 つの文言の中で raw literal の改行と CRLF が混ざるため。§2 の規則の帰結）
- 記法の判断で迷ったら本 ADR の表に足す（表に無い場面は「読みやすい方」でよい）
