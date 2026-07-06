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

-- seed: 年額プラン（割り切れる 1,200,000/年 = 月 100,000 で検証しやすく）
INSERT INTO recurring_billings (partner_id, project_id, title, monthly_amount, annual_amount, billing_cycle, start_month, end_month, is_active)
SELECT
    (SELECT id FROM partners WHERE code = 'C001'),
    (SELECT id FROM projects WHERE project_type = 'saas' AND is_active = 1 ORDER BY id LIMIT 1),
    'クラウド勤怠 SaaS 年額プラン',
    NULL,
    1200000,
    'yearly',
    '2026-07-01',
    NULL,
    1
WHERE NOT EXISTS (SELECT 1 FROM recurring_billings WHERE title = 'クラウド勤怠 SaaS 年額プラン');
