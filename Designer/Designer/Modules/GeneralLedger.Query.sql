SELECT * FROM (
  SELECT
    e.entry_date,
    e.journal_no,
    l.line_no,
    CASE
      WHEN (SELECT COUNT(*) FROM journal_lines x WHERE x.journal_entry_id = e.id AND x.id <> l.id) = 1
        THEN (SELECT a2.name FROM journal_lines x JOIN accounts a2 ON a2.id = x.account_id
              WHERE x.journal_entry_id = e.id AND x.id <> l.id)
      ELSE '諸口'
    END AS counter_account_name,
    COALESCE(l.description, e.description, '') AS line_description,
    CASE WHEN l.dc = 'D' THEN l.amount END AS debit_amount,
    CASE WHEN l.dc = 'C' THEN l.amount END AS credit_amount,
    SUM((CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
        * (CASE WHEN a.dc_normal = 'D' THEN 1 ELSE -1 END))
      OVER (ORDER BY date(e.entry_date), e.journal_no, l.line_no
            ROWS UNBOUNDED PRECEDING) AS balance
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  JOIN accounts a ON a.id = l.account_id
  WHERE e.status = 'posted'
    AND l.account_id = @account_id
    AND (@date_to IS NULL OR date(e.entry_date) <= date(@date_to))
) t
WHERE (@date_from IS NULL OR date(t.entry_date) >= date(@date_from))
ORDER BY date(t.entry_date), t.journal_no, t.line_no
