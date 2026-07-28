SELECT
  je.id AS entry_id,
  je.journal_no,
  je.entry_date,
  CASE je.entry_type
    WHEN 'transfer' THEN '振替'
    WHEN 'receipt' THEN '入金'
    WHEN 'payment' THEN '出金'
    WHEN 'expense' THEN '経費'
    WHEN 'auto' THEN '自動'
    WHEN 'adjust' THEN '決算整理'
    ELSE COALESCE(je.entry_type, '')
  END AS entry_type_label,
  COALESCE(je.description, '') AS description,
  CASE je.status WHEN 'posted' THEN '確定' WHEN 'draft' THEN '下書き' ELSE COALESCE(je.status, '') END AS status_label,
  COALESCE(SUM(CASE WHEN jl.dc = 'D' THEN jl.amount ELSE 0 END), 0) AS debit_total
FROM journal_entries je
LEFT JOIN journal_lines jl ON jl.journal_entry_id = je.id
WHERE (@date_from IS NULL OR date(je.entry_date) >= date(@date_from))
  AND (@date_to IS NULL OR date(je.entry_date) <= date(@date_to))
  AND (@desc IS NULL OR @desc = '' OR je.description LIKE '%' || @desc || '%')
  AND (@status IS NULL OR @status = '' OR je.status = @status)
GROUP BY je.id
