-- 貸借対照表（勘定式・左右対照）
-- 左列=資産の部／右列=負債・純資産の部 を行番号でペアリングし、
-- 最終行に「資産合計」と「負債・純資産合計」を同じ行で対置する（一致が一目で検算できる）。
-- 一覧の Excel ダウンロードもこの4列がそのまま出力される。
WITH yr AS (
  SELECT id, start_date FROM fiscal_years
  WHERE id = COALESCE(@fiscal_year_id,
    (SELECT id FROM fiscal_years
     WHERE date(start_date) <= date('now', 'localtime') AND date(end_date) >= date('now', 'localtime')))
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
ni AS (SELECT COALESCE(-SUM(dmc), 0) AS net_income FROM pl),

-- 左側: 資産の部（区分見出し・科目・区分計。資産合計は最終行に固定するため含めない）
lsrc AS (
  SELECT printf('%02d', b.section_order) || '-0-0000' AS sort_key,
         '【' || b.cat_name || '】' AS item, NULL AS amount
  FROM bs b WHERE b.account_type = 'asset'
  GROUP BY b.section_order, b.cat_name
  UNION ALL
  SELECT printf('%02d', b.section_order) || '-1-' || b.code, '　' || b.name, b.dmc
  FROM bs b WHERE b.account_type = 'asset'
  UNION ALL
  SELECT printf('%02d', b.section_order) || '-2-ZZZZ', b.cat_name || ' 計', SUM(b.dmc)
  FROM bs b WHERE b.account_type = 'asset'
  GROUP BY b.section_order, b.cat_name
),
-- 右側: 負債・純資産の部（負債合計・当期純利益・純資産合計を含む）
rsrc AS (
  SELECT printf('%02d', b.section_order) || '-0-0000' AS sort_key,
         '【' || b.cat_name || '】' AS item, NULL AS amount
  FROM bs b WHERE b.account_type IN ('liability', 'equity')
  GROUP BY b.section_order, b.cat_name
  UNION ALL
  SELECT printf('%02d', b.section_order) || '-1-' || b.code, '　' || b.name, -b.dmc
  FROM bs b WHERE b.account_type IN ('liability', 'equity')
  UNION ALL
  SELECT printf('%02d', b.section_order) || '-2-ZZZZ', b.cat_name || ' 計', SUM(-b.dmc)
  FROM bs b WHERE b.account_type IN ('liability', 'equity')
  GROUP BY b.section_order, b.cat_name
  UNION ALL
  SELECT '39-8-ZZZZ', '負債合計', COALESCE(SUM(-b.dmc), 0)
  FROM bs b WHERE b.account_type = 'liability'
  UNION ALL
  SELECT '48-1-ZZZZ', '　当期純利益', (SELECT net_income FROM ni)
  UNION ALL
  SELECT '48-9-ZZZZ', '純資産合計',
    COALESCE((SELECT SUM(-b.dmc) FROM bs b WHERE b.account_type = 'equity'), 0)
    + (SELECT net_income FROM ni)
),
lrows AS (SELECT ROW_NUMBER() OVER (ORDER BY sort_key) AS rn, item, amount FROM lsrc),
rrows AS (SELECT ROW_NUMBER() OVER (ORDER BY sort_key) AS rn, item, amount FROM rsrc),
-- 総合計行は左右の長い方の次の行に揃えて配置する
tot AS (SELECT MAX((SELECT COUNT(*) FROM lrows), (SELECT COUNT(*) FROM rrows)) + 1 AS rn),
lall AS (
  SELECT rn, item, amount FROM lrows
  UNION ALL
  SELECT (SELECT rn FROM tot), '資産合計',
    COALESCE((SELECT SUM(b.dmc) FROM bs b WHERE b.account_type = 'asset'), 0)
),
rall AS (
  SELECT rn, item, amount FROM rrows
  UNION ALL
  SELECT (SELECT rn FROM tot), '負債・純資産合計',
    COALESCE((SELECT SUM(-b.dmc) FROM bs b WHERE b.account_type IN ('liability', 'equity')), 0)
    + (SELECT net_income FROM ni)
),
seq AS (SELECT DISTINCT rn FROM (SELECT rn FROM lall UNION ALL SELECT rn FROM rall))

SELECT
  s.rn AS row_no,
  COALESCE(l.item, '') AS asset_item,
  l.amount AS asset_amount,
  COALESCE(r.item, '') AS liab_item,
  r.amount AS liab_amount
FROM seq s
LEFT JOIN lall l ON l.rn = s.rn
LEFT JOIN rall r ON r.rn = s.rn
ORDER BY s.rn
