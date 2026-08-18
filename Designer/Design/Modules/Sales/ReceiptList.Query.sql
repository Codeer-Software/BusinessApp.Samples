-- 入金は 1 件で複数の請求書に消し込める（ADR-0071）ので、請求書欄は**明細から組み立てる**。
-- 1 件なら請求書のラベルそのまま、複数なら「INV-26-001 ほか 2 件」と畳む。
SELECT
  r.id AS receipt_id,
  r.receipt_date,
  COALESCE((SELECT CASE WHEN COUNT(*) = 0 THEN ''
                        WHEN COUNT(*) = 1 THEN MIN(COALESCE(li.select_label, li.invoice_no))
                        ELSE MIN(COALESCE(li.invoice_no, '')) || ' ほか ' || (COUNT(*) - 1) || ' 件'
                   END
            FROM receipt_lines rl
            JOIN invoices li ON li.id = rl.invoice_id
            WHERE rl.receipt_id = r.id), '') AS invoice_label,
  r.amount,
  CASE r.method WHEN 'bank' THEN '銀行振込' WHEN 'cash' THEN '現金' WHEN 'offset' THEN '相殺' ELSE COALESCE(r.method, '') END AS method_label,
  CASE WHEN je.id IS NULL THEN '未確定' ELSE '消込済' END AS settle_status,
  je.journal_no AS journal_no
FROM receipts r
LEFT JOIN journal_entries je ON je.source_type = 'receipt' AND je.source_id = r.id
WHERE (@date_from IS NULL OR date(r.receipt_date) >= date(@date_from))
  AND (@date_to IS NULL OR date(r.receipt_date) <= date(@date_to))
  AND (@settle IS NULL OR @settle = '' OR (CASE WHEN je.id IS NULL THEN 'pending' ELSE 'settled' END) = @settle)