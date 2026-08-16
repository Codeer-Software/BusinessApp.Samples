-- 何を保証するか: 検収ヘッダの税抜金額・消費税額が、検収明細（acceptance_lines）から導いた値と一致すること。
--                 税額は請求書と同じく「税率ごとに合計してから 1 回切り捨て」。
-- 違反時の意味: 売上計上仕訳の金額が実際の検収内容と食い違う。
--               検収は売上の発生源（確定で売上仕訳が立つ）なので、ここのズレは PL に直結する。
-- 出典: ADR-0049（検収が明細行を持ち請求書はその写し）／ADR-0050（税率ごとに 1 回端数処理）
--       Memory: 「A-1 の真因は OnDataChanged の無条件な数量×単価」— この経路の再発検知を兼ねる。
WITH std AS (
  SELECT MAX(rate_percent) AS pct FROM tax_rates
  WHERE is_active = 1
    AND date(valid_from) <= date('now', 'localtime')
    AND (valid_to IS NULL OR date(valid_to) >= date('now', 'localtime'))
),
ln AS (
  SELECT
    al.acceptance_id AS acc,
    CASE WHEN al.tax_category_id IS NULL THEN (SELECT pct FROM std)
         WHEN tc.taxation_type = 'taxable_sales' THEN COALESCE(tr.rate_percent, 0)
         ELSE 0 END AS pct,
    COALESCE(al.amount, 0) AS amt
  FROM acceptance_lines al
  LEFT JOIN tax_categories tc ON tc.id = al.tax_category_id
  LEFT JOIN tax_rates tr ON tr.id = tc.tax_rate_id
),
base AS (
  SELECT acc, SUM(amt) AS total FROM ln GROUP BY acc
),
per_rate AS (
  SELECT acc, pct, SUM(amt) AS b FROM ln WHERE pct > 0 GROUP BY acc, pct
),
expected AS (
  SELECT acc, SUM(CAST(b * pct / 100 AS INTEGER)) AS tax FROM per_rate GROUP BY acc
)
SELECT
    a.id              AS 検収id,
    a.acceptance_no   AS 検収番号,
    a.status          AS 状態,
    a.acceptance_date AS 検収日,
    COALESCE(a.amount, 0)     AS ヘッダ税抜,
    COALESCE(b.total, 0)      AS 明細合計,
    COALESCE(a.tax_amount, 0) AS 記録税額,
    COALESCE(e.tax, 0)        AS 期待税額,
    COALESCE(a.amount, 0) - COALESCE(b.total, 0)      AS 本体差額,
    COALESCE(a.tax_amount, 0) - COALESCE(e.tax, 0)    AS 税額差額
FROM acceptances a
LEFT JOIN base b ON b.acc = a.id
LEFT JOIN expected e ON e.acc = a.id
WHERE COALESCE(a.amount, 0) <> COALESCE(b.total, 0)
   OR COALESCE(a.tax_amount, 0) <> COALESCE(e.tax, 0)
ORDER BY a.id
