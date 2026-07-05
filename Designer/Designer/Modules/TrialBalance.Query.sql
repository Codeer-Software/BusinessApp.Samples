WITH sums AS (
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
)
SELECT
  a.code AS account_code,
  a.name AS account_name,
  s.dsum AS debit_total,
  s.csum AS credit_total,
  CASE WHEN a.dc_normal = 'D' THEN s.dsum - s.csum ELSE s.csum - s.dsum END AS balance
FROM sums s
JOIN accounts a ON a.id = s.account_id
UNION ALL
SELECT
  'ZZZZ' AS account_code,
  '合計（貸借検算）' AS account_name,
  SUM(s2.dsum) AS debit_total,
  SUM(s2.csum) AS credit_total,
  NULL AS balance
FROM sums s2
ORDER BY account_code
