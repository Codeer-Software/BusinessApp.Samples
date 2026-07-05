WITH yr AS (
  SELECT id, start_date FROM fiscal_years
  WHERE id = COALESCE(@fiscal_year_id,
    (SELECT id FROM fiscal_years
     WHERE date(start_date) <= date('now') AND date(end_date) >= date('now')))
),
bal AS (
  SELECT
    a.code,
    a.name,
    a.account_type,
    a.dc_normal,
    c.name AS cat_name,
    c.section_order,
    c.statement,
    COALESCE(o.bal, 0) + COALESCE(j.dmc, 0) AS dmc
  FROM accounts a
  JOIN account_categories c ON c.id = a.category_id
  LEFT JOIN (
    SELECT account_id, SUM(balance) AS bal
    FROM opening_balances
    WHERE fiscal_year_id IN (SELECT id FROM yr)
    GROUP BY account_id
  ) o ON o.account_id = a.id
  LEFT JOIN (
    SELECT l.account_id, SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS dmc
    FROM journal_lines l
    JOIN journal_entries e ON e.id = l.journal_entry_id
    WHERE e.status = 'posted'
      AND e.fiscal_year_id IN (SELECT id FROM yr)
    GROUP BY l.account_id
  ) j ON j.account_id = a.id
  WHERE COALESCE(o.bal, 0) <> 0 OR COALESCE(j.dmc, 0) <> 0
),
bs AS (SELECT * FROM bal WHERE statement = 'BS'),
pl AS (SELECT * FROM bal WHERE statement = 'PL'),
ni AS (SELECT COALESCE(-SUM(dmc), 0) AS net_income FROM pl)

-- 科目行
SELECT
  printf('%02d', b.section_order) || '-1-' || b.code AS sort_key,
  b.cat_name AS section,
  b.name AS item,
  CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END AS amount
FROM bs b

UNION ALL
-- 区分小計
SELECT
  printf('%02d', b.section_order) || '-2-ZZZZ',
  b.cat_name,
  b.cat_name || ' 計',
  SUM(CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END)
FROM bs b
GROUP BY b.section_order, b.cat_name

UNION ALL
-- 資産合計
SELECT '29-9-ZZZZ', '資産', '資産合計', COALESCE(SUM(b.dmc), 0)
FROM bs b WHERE b.account_type = 'asset'

UNION ALL
-- 負債合計
SELECT '39-9-ZZZZ', '負債', '負債合計', COALESCE(SUM(-b.dmc), 0)
FROM bs b WHERE b.account_type = 'liability'

UNION ALL
-- 当期純利益
SELECT '48-1-ZZZZ', '純資産', '当期純利益', (SELECT net_income FROM ni)

UNION ALL
-- 純資産合計（当期純利益込み）
SELECT '48-9-ZZZZ', '純資産', '純資産合計',
  COALESCE((SELECT SUM(-b.dmc) FROM bs b WHERE b.account_type = 'equity'), 0) + (SELECT net_income FROM ni)

UNION ALL
-- 負債・純資産合計（＝資産合計と一致すべき検算行）
SELECT '59-9-ZZZZ', '検算', '負債・純資産合計',
  COALESCE((SELECT SUM(-b.dmc) FROM bs b WHERE b.account_type = 'liability'), 0)
  + COALESCE((SELECT SUM(-b.dmc) FROM bs b WHERE b.account_type = 'equity'), 0)
  + (SELECT net_income FROM ni)

ORDER BY sort_key
