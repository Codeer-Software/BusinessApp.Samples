WITH yr AS (
  SELECT id, start_date FROM fiscal_years
  WHERE @date_from IS NOT NULL
    AND date(start_date) <= date(@date_from)
    AND date(end_date) >= date(@date_from)
),
ob AS (
  SELECT account_id, SUM(balance) AS bal
  FROM opening_balances
  WHERE fiscal_year_id IN (SELECT id FROM yr)
  GROUP BY account_id
),
pre AS (
  SELECT l.account_id, SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS dmc
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  WHERE e.status = 'posted'
    AND @date_from IS NOT NULL
    AND date(e.entry_date) >= (SELECT date(start_date) FROM yr)
    AND date(e.entry_date) < date(@date_from)
  GROUP BY l.account_id
),
sums AS (
  SELECT
    l.account_id,
    SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE 0 END) AS dsum,
    SUM(CASE WHEN l.dc = 'C' THEN l.amount ELSE 0 END) AS csum
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  WHERE e.status = 'posted'
    AND (@date_from IS NULL OR date(e.entry_date) >= date(@date_from))
    AND (@date_to IS NULL OR date(e.entry_date) <= date(@date_to))
  GROUP BY l.account_id
),
merged AS (
  SELECT
    a.id,
    a.code,
    a.name,
    a.dc_normal,
    COALESCE(o.bal, 0) + COALESCE(p.dmc, 0) AS open_dmc,
    COALESCE(s.dsum, 0) AS dsum,
    COALESCE(s.csum, 0) AS csum
  FROM accounts a
  LEFT JOIN ob o ON o.account_id = a.id
  LEFT JOIN pre p ON p.account_id = a.id
  LEFT JOIN sums s ON s.account_id = a.id
  WHERE COALESCE(o.bal, 0) <> 0 OR COALESCE(p.dmc, 0) <> 0
     OR COALESCE(s.dsum, 0) <> 0 OR COALESCE(s.csum, 0) <> 0
)
SELECT * FROM (
  SELECT
    m.code AS account_code,
    m.name AS account_name,
    CASE WHEN m.dc_normal = 'D' THEN m.open_dmc ELSE -m.open_dmc END AS opening_balance,
    m.dsum AS debit_total,
    m.csum AS credit_total,
    CASE WHEN m.dc_normal = 'D' THEN m.open_dmc + m.dsum - m.csum
         ELSE -m.open_dmc + m.csum - m.dsum END AS balance
  FROM merged m
  UNION ALL
  SELECT
    '' AS account_code,
    '合計（貸借検算）' AS account_name,
    NULL AS opening_balance,
    SUM(m2.dsum) AS debit_total,
    SUM(m2.csum) AS credit_total,
    NULL AS balance
  FROM merged m2
)
ORDER BY CASE WHEN account_code = '' THEN 1 ELSE 0 END, account_code
