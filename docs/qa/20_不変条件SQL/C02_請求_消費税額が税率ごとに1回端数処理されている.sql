-- 何を保証するか: 請求書の消費税額（invoices.tax_amount）が
--                 「明細を税率ごとに合計してから 1 回だけ切り捨て」た額と一致すること。
-- 違反時の意味: インボイス制度違反。適格請求書は「一の請求書につき税率ごとに 1 回の端数処理」と
--               定められており、行ごとに切ってから足すと 1 円単位でズレる。
--               取引先の仕入税額控除額と食い違い、再発行を求められる。
-- 出典: ADR-0050（税は明細の税区分ベースで税率ごとに 1 回端数処理）
--       Modules/Sales/Invoice.mod.cs CalcTaxByLine()（本チェックはこの実装の SQL 版）
--       docs/04_会計ドメイン設計.md §3.2（外税は税抜額 × 率/100 を切り捨て）
-- 実装メモ:
--   ・税区分が課税売上（taxable_sales）の行だけが課税対象。非課税・免税・不課税・対象外は税額 0。
--   ・税区分が未設定の行はスクリプトが「標準税率の課税売上」とみなす（黙って非課税にすると過少計上になるため）。
--     ここでも同じ扱いにする。標準税率はマスタから解決する（ハードコードしない）。
WITH std AS (
  SELECT MAX(rate_percent) AS pct FROM tax_rates
  WHERE is_active = 1
    AND date(valid_from) <= date('now', 'localtime')
    AND (valid_to IS NULL OR date(valid_to) >= date('now', 'localtime'))
),
ln AS (
  SELECT
    il.invoice_id AS inv,
    CASE WHEN il.tax_category_id IS NULL THEN (SELECT pct FROM std)
         WHEN tc.taxation_type = 'taxable_sales' THEN COALESCE(tr.rate_percent, 0)
         ELSE 0 END AS pct,
    COALESCE(il.amount, 0) AS amt
  FROM invoice_lines il
  LEFT JOIN tax_categories tc ON tc.id = il.tax_category_id
  LEFT JOIN tax_rates tr ON tr.id = tc.tax_rate_id
),
per_rate AS (
  SELECT inv, pct, SUM(amt) AS base FROM ln WHERE pct > 0 GROUP BY inv, pct
),
expected AS (
  SELECT inv, SUM(CAST(base * pct / 100 AS INTEGER)) AS tax FROM per_rate GROUP BY inv
)
SELECT
    i.id         AS 請求書id,
    i.invoice_no AS 請求書番号,
    i.status     AS 状態,
    i.issue_date AS 発行日,
    COALESCE(i.amount, 0)     AS 税抜,
    COALESCE(i.tax_amount, 0) AS 記録税額,
    COALESCE(e.tax, 0)        AS 期待税額,
    COALESCE(i.tax_amount, 0) - COALESCE(e.tax, 0) AS 差額
FROM invoices i
LEFT JOIN expected e ON e.inv = i.id
WHERE EXISTS (SELECT 1 FROM invoice_lines l WHERE l.invoice_id = i.id)
  AND COALESCE(i.tax_amount, 0) <> COALESCE(e.tax, 0)
ORDER BY i.id
