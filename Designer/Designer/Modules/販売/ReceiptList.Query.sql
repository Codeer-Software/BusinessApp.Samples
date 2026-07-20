SELECT
  r.id AS receipt_id,
  r.receipt_date,
  COALESCE(i.select_label, '') AS invoice_label,
  r.amount,
  CASE r.method WHEN 'bank' THEN '銀行振込' WHEN 'cash' THEN '現金' WHEN 'offset' THEN '相殺' ELSE COALESCE(r.method, '') END AS method_label,
  CASE WHEN je.id IS NULL THEN '未確定' ELSE '消込済' END AS settle_status,
  je.journal_no AS journal_no
FROM receipts r
LEFT JOIN invoices i ON i.id = r.invoice_id
LEFT JOIN journal_entries je ON je.source_type = 'receipt' AND je.source_id = r.id
WHERE (@date_from IS NULL OR date(r.receipt_date) >= date(@date_from))
  AND (@date_to IS NULL OR date(r.receipt_date) <= date(@date_to))
  AND (@settle IS NULL OR @settle = '' OR (CASE WHEN je.id IS NULL THEN 'pending' ELSE 'settled' END) = @settle)
