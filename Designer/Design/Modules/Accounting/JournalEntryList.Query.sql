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
  COALESCE((SELECT pt.name FROM partners pt WHERE pt.id = je.partner_id), '') AS partner_name,
  COALESCE(SUM(CASE WHEN jl.dc = 'D' THEN jl.amount ELSE 0 END), 0) AS debit_total
FROM journal_entries je
LEFT JOIN journal_lines jl ON jl.journal_entry_id = je.id
WHERE (@date_from IS NULL OR date(je.entry_date) >= date(@date_from))
  AND (@date_to IS NULL OR date(je.entry_date) <= date(@date_to))
  AND (@desc IS NULL OR @desc = '' OR je.description LIKE '%' || @desc || '%')
  AND (@status IS NULL OR @status = '' OR je.status = @status)
  -- 電帳法の検索要件（取引年月日・取引金額・取引先）を仕訳一覧で満たす（BUG-0003・ADR-0076）
  AND (@partner IS NULL OR @partner = '' OR EXISTS (
        SELECT 1 FROM partners pt WHERE pt.id = je.partner_id AND pt.name LIKE '%' || @partner || '%'))
GROUP BY je.id
HAVING (@amount_from IS NULL OR COALESCE(SUM(CASE WHEN jl.dc = 'D' THEN jl.amount ELSE 0 END), 0) >= @amount_from)
   AND (@amount_to   IS NULL OR COALESCE(SUM(CASE WHEN jl.dc = 'D' THEN jl.amount ELSE 0 END), 0) <= @amount_to)
