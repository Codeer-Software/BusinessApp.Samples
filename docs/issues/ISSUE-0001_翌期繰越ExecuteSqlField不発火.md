# ISSUE-0001: Update タイミングの ExecuteSqlField が Submit 経由で発火しない（翌期繰越ボタン）

> ## ✅ 解決済み（2026-07-08）
>
> **真因: ExecuteSqlField の `Parameters` は、SQL 内の `@プレースホルダ` を「フィールド名」ではなく「**DB 列名（DbColumn）**」で解決する。**
> `@id` が通っていたのはフィールド名 `Id` と列名 `id` が同名だったため。`@NextYearId`（フィールド名）は列名 `next_year_id` と不一致でバインドされず、SQLite が `Must add values for the following parameters: @NextYearId` を返して Submit ごとロールバックしていた。
>
> **修正**: `FiscalYear.CarryOverSql.sql` のプレースホルダを `@next_year_id` に変更＋ `FiscalYear.mod.json` の Parameters Name を `next_year_id` に変更（コミット参照）。
> **実機検証**: 「翌期繰越を実行」→ opening_balances の row_id が 8/9/10→11/12/13 に変わり（DELETE→INSERT 実行）、残高は 478,000／−500,000／+22,000（Σ=0）で正しく再計算。NextYearId を null に戻すリセット Submit も成功。
>
> **経緯メモ**: 本票起票時（7/6）は「エラーなしの偽成功」と記録したが、7/8 の再現ではユーザー・Fable とも一貫して上記パラメータエラーが出た。7/6 時点は NextYearId 非バインド（DataOnly）→実列化の試行が混在しており、検証時に旧デザインが配信されていた可能性が高い（デプロイ/再起動漏れ）。現行の実列バインド構成では「実行される・列名で解決される・修正で完全動作」が確定。

| 項目 | 内容 |
|---|---|
| 状態 | **解決済み**（2026-07-08・上記参照） |
| 発見日 | 2026-07-06（自律総合テスト 第1ラウンド） |
| 影響機能 | 会計年度画面の「翌期繰越を実行」ボタン（decisions/0006 年次繰越） |
| 影響度 | 中（年1回の操作。回避策あり＝sql CLI で繰越 SQL を手動実行すれば完全動作） |
| 環境 | Codeer.LowCode.Blazor **1.2.51.0** / net8.0 / SQLite / Windows 11 |
| 関連ファイル | `Designer/Designer/Modules/FiscalYear.mod.json`（CarryOverSql）・`FiscalYear.CarryOverSql.sql`・`FiscalYear.mod.cs`（CarryOver_OnClick） |

## 概要

FiscalYear モジュールに置いた **Timing: Update の ExecuteSqlField（CarryOverSql）** を、ボタンスクリプトからの `Submit()` で発火させる設計にしたが、**Submit は成功する（true が返り、トーストも成功表示になる）のに、SQL の効果（opening_balances の洗い替え）が現れない**。同じ SQL を sql CLI で手動実行すると完全に正しく動作する。

## 前提となる実装（なぜこの構造か）

- ExecuteSqlField は**スクリプトから直接実行できない**（全メンバー ScriptHide。マニュアル JP/db/execute_sql_field.md で確認済み）。
- そのため「擬似 Standalone 実行」パターンを採用:
  1. パラメータ用フィールド `NextYearId`（NumberField、実列 `fiscal_years.next_year_id` にバインド）を用意
  2. SQL 側は `WHERE @NextYearId IS NOT NULL AND ...` の no-op ガード付き（通常の保存では何もしない）
  3. ボタン（CarryOver_OnClick）が `NextYearId.Value = 翌期のId;` をセットして `Submit()` → Update タイミングの CarryOverSql が発火する想定

### CarryOverSql の設定（FiscalYear.mod.json 抜粋）

```json
{
  "Timing": "Update",
  "WithStandardIO": "Before",
  "ExecuteSqlSetting": {
    "CommandType": "Sql",
    "MethodType": "NonQuery",
    "Parameters": [
      { "IsParameter": true, "Name": "id",         "DbType": "bigint", "DbParameterDirection": "Input" },
      { "IsParameter": true, "Name": "NextYearId", "DbType": "bigint", "DbParameterDirection": "Input" }
    ]
  },
  "Name": "CarryOverSql",
  "TypeFullName": "Codeer.LowCode.Blazor.Repository.Design.ExecuteSqlFieldDesign"
}
```

SQL 本文は `FiscalYear.CarryOverSql.sql`（DELETE→INSERT の2文。`@NextYearId IS NOT NULL` ガード付き）。

### ボタンスクリプト（FiscalYear.mod.cs 抜粋）

```csharp
NextYearId.Value = typedNext.Id.Value;
var ret = this.Submit();          // ← true が返る（保存自体は成功）
NextYearId.Value = null;
if (ret == false) { ... }
this.Submit();                    // NextYearId を null に戻す保存
```

## 再現手順

1. サーバ起動（`http://localhost:5085`）、admin でログイン
2. 17期・18期の会計年度が存在し、17期に期首残高または posted 仕訳がある状態にする
3. 事前に翌期の期首残高の行 id を控える:
   `SELECT id, account_id, balance FROM opening_balances WHERE fiscal_year_id = <18期のid>;`
4. 設定 > 会計年度 > 17期 を開き「翌期繰越を実行」→ 確認ダイアログで「実行」
5. トーストは「〜への繰越が完了しました」（Submit が true）
6. 手順3と同じ SELECT を再実行

## 現象（実測）

- **opening_balances が一切変化しない**（行 id・件数・balance 全て同一。DELETE→INSERT が走れば行 id が変わるはずなので、SQL が実行されていないか、`@NextYearId` が NULL で no-op ガードに落ちている）
- Submit 自体はエラーなく成功する（`fiscal_years` の通常カラムの保存は正常）
- 同じ SQL を sql CLI で `@id`/`@NextYearId` を実値に置換して実行すると、**完全に正しい繰越結果**になる（17期→18期で 期首 478,000 / -500,000 / +22,000、Σ=0、18期BS 1,992,999 一致を実証済み）

## 期待する動作

`Timing: Update` の ExecuteSqlField が、当該モジュールの Update Submit のタイミングで実行され、`Parameters` に宣言したフィールド値（`id`, `NextYearId`）が SQL の `@id`, `@NextYearId` に渡されること。

## 試したこと（すべて効果なし・3回ルールで打ち切り）

1. **NextYearId を非バインド（DataOnlyFields）→ 実列バインドに変更**（`fiscal_years.next_year_id` を ddl/155 で追加）。「差分ゼロの Submit だと Update 自体がスキップされる」仮説への対処。実列に差分が出る状態でも SQL の効果なし
2. **WithStandardIO: Before / Timing: Update の組合せ確認**（マニュアルどおり）
3. **Parameters 宣言の見直し**（`id`・`NextYearId` を Input で宣言。名前はフィールド名と一致）
4. designcheck は findingCount 0（設定としては妥当）

## 切り分けの残論点（未実施）

- 「SQL が実行されていない」のか「実行されたがパラメータが NULL」なのかの直接判別。
  判別案: no-op ガードを外した `INSERT INTO <マーカーテーブル> VALUES (...)` の1文だけの ExecuteSqlField を Update タイミングで置き、Submit で行が増えるかを見る（増えれば発火はしており、パラメータ解決の問題に絞れる）
- `FailedCondition` は空設定。ここが評価に影響している可能性は未検証
- CLB コア（ModuleDataIO）側の発火条件のソース確認（ベンダー照会事項）

## 回避策（現行運用）

`Designer/temporary/` の繰越 SQL を sql CLI で手動実行する（`@id`/`@NextYearId` を実値に置換）。この経路は総合テストで完全動作を実証済み。ボタンには空年度ガード・確認ダイアログ等の安全装置は実装済みのため、CLB 側の発火問題が解決すればそのまま活きる。

## 補足

- A-10 実装時に「動作確認 OK」とした記録があるが、当時は繰越元が空（期首も仕訳も無し）で **0件 INSERT の no-op が「成功」に見えていた**可能性が高い
- ベンダー（Codeer）へ問い合わせる場合は、本票の「再現手順」＋ FiscalYear.mod.json / CarryOverSql.sql の2ファイルで再現可能
