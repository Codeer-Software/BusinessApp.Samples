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
クローンごとの `core.autocrlf` 設定に依存する。
そこで 2 つで守る: ① `.gitattributes` に `*.cs text eol=lf` を敷いて**ソースの改行を LF に固定**する
（導入済み。§4-3）。②**利用者向け文言の改行は raw literal の改行（＝LF）に統一し、
`Environment.NewLine` を混ぜない**。`string.Join` で行を束ねるときも `"\n"` を使う
（表示先はブラウザで、LF で足りる。混ぜると 1 つの文言の中で改行コードが割れる）。

### 3. スコープ

- **対象**: 自作の C#（`AccountingCore` 系・`TestSupport`・`Schema.Tests`・`SchemaVerifyCli`）。
  ここにはビルドの関門（§4-1）を敷く
- **対象外**: **CLB スクリプト（`*.mod.cs`）**。ツリーウォーク型インタプリタが対応する
  範囲しか書けない（[ADR-0008](0008-CLBとCSharpライブラリの責務分担.md)）。新記法を持ち込まない
- **必要最小限に留める**: CLB テンプレート由来のプロジェクト（`BusinessApp.Server`・
  `BusinessApp.Client`・`BusinessApp.Client.Shared`・`BusinessApp.Designer`・
  `BusinessApp.LicenseRegisterCli`）。**ビルドの関門は敷かない**——テンプレート更新との差分を
  無用に増やさないため。ここに置いた自作コードは §4-2 の規約テストが守る。
  なお `BusinessApp.Server` の自作部分は `JournalsController` だけであり、
  その中身は[実装計画](../05_実装計画と現在地.md)の次のタスクで `AccountingCore.Server` へ移す

### 4. 機械で守る

守り方は 2 つある。**どちらでも守れないものは、規約に書いても守られない。**

#### 4-1. ビルドの関門（`.editorconfig` ＋ `EnforceCodeStyleInBuild`）

`TreatWarningsAsErrors` と組で強制になる。**これが無いプロジェクト
（`Schema.Tests`・`AccountingCore.Tests`・`AccountingCore.Server.Tests`）には追加した。**

**`option = value:warning` の severity はビルドでは効かない**（2026-08-25 実測）。
たとえば `csharp_style_namespace_declarations = file_scoped:warning` だけを書くと、
エディタには波線が出るのに**ビルドは素通りする**。止めたい規則は
`dotnet_diagnostic.IDEnnnn.severity = warning` を**規則 ID ごとに明示的に**書かなければならない。

これは**「機械で守っているつもりで、実は何も見ていない」**という壊れ方をする。
気づいたのは、規約に反する形をわざと書いて**警報が鳴るかを確かめた**ときである。
以後、機械の関門を足したら**必ず一度わざと壊して鳴ることを確かめる**。

そこで `.editorconfig` は 2 段構成にしてある。

| 段 | 何を書くか | 効き目 |
|---|---|---|
| `[*.cs]` | **好みの表明**（どちらの書き方が正か） | エディタの提案。ビルドは見ない |
| `[BusinessApp/BusinessApp.{対象}/**.cs]` | **規則 ID ごとの severity** | ビルドの関門 |

対象外（CLB スクリプトと CLB テンプレート由来のプロジェクト）は、
**下段のパスに含めない**ことで外してある。除外の指定を書かない分だけ壊れにくい。

**強制しないと決めた規則**（理由つきで `.editorconfig` に残す）

- **IDE0305**（LINQ の終端の `.ToList()` / `.ToArray()` を `[.. 式]` にする）。
  複数行の連鎖の頭に `[..` が来ると開き括弧と閉じ括弧が遠く離れ、
  「ここで実体化する」という意図も読み取りにくくなる。§1 の基準で落とした（対象は 19 か所）。
  **配列リテラルの `new[] { ... }` → `[...]`（IDE0300）は強制する。** 別の規則である
- IDE0045 / IDE0046（三項演算子への寄せ）。条件が長いと一行が読めなくなる
- `var` の使い分け（本 ADR の表に無い）・式形式メンバの強制

#### 4-2. 規約テスト（`CSharpStyleConvention`）

ビルドで表現できない禁止形を、ソースを読んで検査する。
判定は `BusinessApp.TestSupport/CSharpStyleConvention.cs`、表明は
`BusinessApp.AccountingCore.Tests/Conventions/CSharpStyleTests.cs`。

| 見るもの | 範囲 |
|---|---|
| `is { }` / `is not { }` | **リポジトリ内のすべての C#**（CLB スクリプトを含む） |
| `== null` / `!= null` | ビルドの関門を敷いたプロジェクト |
| `Environment.NewLine` | `AccountingCore` と `AccountingCore.Server`（利用者向け文言を組み立てる層） |
| 対象プロジェクトが `TreatWarningsAsErrors` と `EnforceCodeStyleInBuild` を宣言しているか | 表に載る全プロジェクト |
| **どちらの表にも載っていないプロジェクトが無いか** | `BusinessApp/` 配下の全プロジェクト |

- **検査はコメントと文字列リテラルを取り除いてから当てる。** そうしないと、
  規約そのものを説明する doc コメント（「`is { }` は使わない」）が違反として報告され、
  規約を書けなくなる。取り除いた分は同じ長さの空白に置き換えて**行番号を保つ**
- **「1 本も読めていない」を検査する。** 場所の解決を間違えると、
  何も読まないまま「違反 0 件」で緑になる。**違反が無いことと何も見ていないことは、
  結果の見た目が同じ**である

#### 4-3. 改行コード

`.gitattributes` に `*.cs text eol=lf` を足し、raw string literal の改行を LF に固定した。
リポジトリ側は既に LF なので、この変更で差分は出ない（変わるのは作業コピーだけ）。

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

**一斉見直しは実施済み（2026-08-25）。** 全ゲート緑（テスト 635 件・カバレッジ 100%）。

- 開発者の見本（`JournalPostingRejectedException.cs`）は 2 点を規約に揃えて取り込んだ:
  ① `is not null and int lineNo` → `is int lineNo`（同義。型パターンが null を通さないため、
  規約の推奨形は短い方にした。**開発者と合意済み**）
  ② raw string literal の中の `string.Join(Environment.NewLine, ...)` → `string.Join("\n", ...)`
  （見本のままだと 1 つの文言の中で raw literal の改行と CRLF が混ざるため。§2 の規則の帰結）
- `is { }` は 30 か所を型パターンに置き換えた。**うち 2 か所で `using` が足りずビルドが落ちた**——
  型名が字面に出ることの効果がそのまま出た形である
- 利用者に見せる文言の `+` 連結 4 か所は 1 つの補間文字列に、
  テストの SQL の `+` 連結 4 か所は raw string literal にした
- **記法の判断で迷ったら本 ADR の表に足す**（表に無い場面は「読みやすい方」でよい）
