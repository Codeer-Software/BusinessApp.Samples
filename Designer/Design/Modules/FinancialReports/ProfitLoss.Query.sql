WITH bal AS (
  SELECT
    a.code,
    a.name,
    a.account_type,
    c.code AS cat_code,
    c.name AS cat_name,
    c.section_order,
    SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS dmc
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  JOIN accounts a ON a.id = l.account_id
  JOIN account_categories c ON c.id = a.category_id
  WHERE e.status = 'posted'
    AND c.statement = 'PL'
    -- 年度未選択（初期表示）は現在日付を含む年度に自動解決（BS/EquityChange と同方式）
    AND e.fiscal_year_id = COALESCE(@fiscal_year_id,
      (SELECT id FROM fiscal_years
       WHERE date(start_date) <= date('now', 'localtime') AND date(end_date) >= date('now', 'localtime')))
  GROUP BY a.id
),
amt AS (
  SELECT code, name, cat_code, cat_name, section_order,
    CASE WHEN account_type = 'revenue' THEN -dmc ELSE dmc END AS amount
  FROM bal
),
sec AS (
  SELECT cat_code, SUM(amount) AS total FROM amt GROUP BY cat_code
),
v AS (
  SELECT
    COALESCE((SELECT total FROM sec WHERE cat_code = 'REV'), 0) AS rev,
    COALESCE((SELECT total FROM sec WHERE cat_code = 'COGS'), 0) AS cogs,
    COALESCE((SELECT total FROM sec WHERE cat_code = 'SGA'), 0) AS sga,
    COALESCE((SELECT total FROM sec WHERE cat_code = 'NOI'), 0) AS noi,
    COALESCE((SELECT total FROM sec WHERE cat_code = 'NOE'), 0) AS noe,
    COALESCE((SELECT total FROM sec WHERE cat_code = 'EI'), 0) AS ei,
    COALESCE((SELECT total FROM sec WHERE cat_code = 'EL'), 0) AS el,
    COALESCE((SELECT total FROM sec WHERE cat_code = 'TAX'), 0) AS tax
)

-- 科目行
SELECT
  printf('%02d', a.section_order) || '-1-' || a.code AS sort_key,
  a.cat_name AS section,
  a.name AS item,
  a.amount
FROM amt a

UNION ALL
-- 区分小計
SELECT printf('%02d', a.section_order) || '-2-ZZZZ', a.cat_name, a.cat_name || ' 計', SUM(a.amount)
FROM amt a
GROUP BY a.section_order, a.cat_name

UNION ALL
SELECT '51-9-ZZZZ', '段階利益', '売上総利益', (SELECT rev - cogs FROM v)
UNION ALL
SELECT '52-9-ZZZZ', '段階利益', '営業利益', (SELECT rev - cogs - sga FROM v)
UNION ALL
SELECT '54-9-ZZZZ', '段階利益', '経常利益', (SELECT rev - cogs - sga + noi - noe FROM v)
UNION ALL
SELECT '56-9-ZZZZ', '段階利益', '税引前当期純利益', (SELECT rev - cogs - sga + noi - noe + ei - el FROM v)
UNION ALL
SELECT '57-9-ZZZZ', '段階利益', '当期純利益', (SELECT rev - cogs - sga + noi - noe + ei - el - tax FROM v)

ORDER BY sort_key
