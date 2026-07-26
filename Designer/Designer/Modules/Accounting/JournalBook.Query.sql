SELECT
  e.entry_date,
  e.journal_no,
  l.line_no,
  a.code AS account_code,
  a.name AS account_name,
  COALESCE(s.name, '') AS sub_account_name,
  CASE WHEN l.dc = 'D' THEN l.amount END AS debit_amount,
  CASE WHEN l.dc = 'C' THEN l.amount END AS credit_amount,
  COALESCE(l.description, e.description, '') AS line_description
FROM journal_lines l
JOIN journal_entries e ON e.id = l.journal_entry_id
JOIN accounts a ON a.id = l.account_id
LEFT JOIN sub_accounts s ON s.id = l.sub_account_id
WHERE e.status = 'posted'
  AND (@date_from IS NULL OR date(e.entry_date) >= date(@date_from))
  AND (@date_to IS NULL OR date(e.entry_date) <= date(@date_to))
