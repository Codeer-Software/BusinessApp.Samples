-- 210_annual_billing.sql — 定期請求の年払い対応（磨きバックログ「年払い前受の繰延」）
-- 会計設計:
--   年額請求書の生成月: D 売掛金(税込) / C 前受収益2110(年額) + C 仮受消費税(税)   … source_type='recurring_annual'
--   毎月の按分振替:     D 前受収益(年額/12・端数は周期最終月で調整) / C SaaS売上高4020 … source_type='recurring_defer'
--   （消費税は請求時に全額計上＝資産の譲渡等の対価を前受けした場合の原則的な扱い。振替仕訳は税対象外）
-- 注意: ALTER TABLE ADD COLUMN は冪等でない（2回目の実行は duplicate column エラーになるが、
--       既に適用済みという意味なので害はない）。billing_cycle の DEFAULT 'monthly' は
--       SQLite では既存行にも既定値として効く＝既存の月額契約は無変更で従来動作（後方互換）。

ALTER TABLE recurring_billings ADD COLUMN billing_cycle TEXT DEFAULT 'monthly';  -- monthly / yearly
ALTER TABLE recurring_billings ADD COLUMN annual_amount INTEGER;                 -- 年額（税抜）。yearly のとき使用

-- 年額プランの seed は置かない（2026-07-10 削除）:
-- 業務データは E2E シナリオ（docs/tests/11 の 02_販売 ステップ 2-9）が画面から登録する方針。
-- 検証用の値はシナリオ側に記載（年額 1,200,000 = 月 100,000 で割り切れる設定）。
