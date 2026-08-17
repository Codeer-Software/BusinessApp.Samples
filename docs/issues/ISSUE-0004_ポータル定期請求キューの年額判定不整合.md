---
title: ISSUE-0004: ポータル「定期請求・SES の当月未生成」が年額契約を毎月カウントする
status: historical
scope: 営業
audience: [開発]
updated: 2026-08-11
supersedes: []
related: []
---
# ISSUE-0004: ポータル「定期請求・SES の当月未生成」が年額契約を毎月カウントする

- 起票: 2026-08-09（体験シナリオ執筆中に発見）
- 状態: **解決済み（2026-08-09）** — `'annual'` → `'yearly'` の 1 語修正を適用。designcheck 0 件。
  修正前後を同じ DB で比較し、当月のキュー件数が **1 → 0**（誤カウントが消えた）ことを実測で確認
- 影響範囲: 業務ポータル（Main/PortalHome）の作業キュー表示のみ。**帳簿・請求書・仕訳には影響しない**
- 関係: ADR-0045（ポータル契約 SQL）・docs/13 §3 #6

## 症状

業務ポータルの「経理の作業キュー」に出る

> ▶ 定期請求・SES の当月未生成: N 件

の N が、**年額（yearly）契約を周期起点月以外の月でも数えてしまう**。
経理が「まだ生成していない請求がある」と誤認し、「定期請求の実行」を開いても
プランは「対象外」または「生成済み」しか出ない、という食い違いが起きる。

## 原因

`Designer/Design/Modules/Shell/PortalQueueData.Query.sql` 16 行目:

```sql
AND (rb.billing_cycle <> 'annual'
     OR ((...当月と開始月の月差...) % 12 = 0))
```

**課金サイクルの実際の値は `monthly` / `yearly`** であり、`annual` という値は存在しない
（`Designer/Design/Modules/Sales/RecurringBilling.mod.json` の候補定義は `月額,monthly` / `年額,yearly`。
DB の `recurring_billings.billing_cycle` も同じ 2 値）。

したがって年額契約でも `billing_cycle <> 'annual'` が真になり、
**OR の左辺で短絡して「周期起点月かどうか」の判定が一度も評価されない**。

同じ判定は `RecurringRun` の `BuildPlan()` 側では正しく実装されており（プラン一覧では
「周期起点月（{yyyy年M月}）が未実行のため対象外」と正しく除外される）、
**ポータルの契約 SQL だけが実装とズレている**状態。docs/13 §3 の
「元帳票の条件を変えるときは必ず両方を直す」が守られなかったケース。

## 再現・実測（2026-08-09 / 当月 = 2026-08）

`sql` CLI で判定式を分解した結果:

| 契約 | サイクル | 開始月 | 周期位置 | 現在の SQL を通過 | 修正後 | 当月の請求書 |
|---|---|---|---|---|---|---|
| クラウド勤怠 SaaS 利用料 | monthly | 2026-04 | — | ○ | ○ | あり |
| **クラウド勤怠 SaaS 年額プラン** | **yearly** | 2026-07 | **1（起点月でない）** | **○（誤り）** | ✗ | なし |
| 請求クラウドサービス | monthly | 2026-08 | — | ○ | ○ | あり |
| 請求クラウドサービス年額バージョン | yearly | 2026-08 | 0（起点月） | ○ | ✗ | あり |

→ 「クラウド勤怠 SaaS 年額プラン」が**当月未生成として 1 件カウントされている**が、
実際には 2026-07 に生成済みで、次に生成すべきは 2027-07。誤カウント。

## 修正案

```sql
AND (rb.billing_cycle <> 'yearly'
     OR ((CAST(strftime('%Y', 'now') AS INTEGER) * 12 + CAST(strftime('%m', 'now') AS INTEGER))
         - (CAST(strftime('%Y', rb.start_month) AS INTEGER) * 12 + CAST(strftime('%m', rb.start_month) AS INTEGER))) % 12 = 0)
```

`'annual'` → `'yearly'` の 1 語のみ。

### 修正後に確認すること

- 当月（起点月でない年額契約がある月）でポータルのキュー件数が 1 件減ること
- 「定期請求の実行」のプラン一覧の planned 件数と、ポータルの件数が一致すること
- 年額契約の**周期起点月**にはポータルにも 1 件出ること

## 再発防止

`billing_cycle` のような列挙値を SQL に直書きする箇所は、
**モジュール JSON の候補定義（`RecurringBilling.mod.json`）を正として突き合わせる**。
ポータルの契約 SQL 4 本（`Portal*Data.Query.sql`）は各部品のテーブル語彙に依存するため、
docs/13 §3 の対応表と合わせて定期的に点検する。

## 未修正の理由

発見時点（2026-08-09）はユーザーが `Designer/Design/` 配下のレイアウト調整作業中で、
同フォルダへの書き込みを避ける取り決めがあったため、票の起票のみとした。
