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
  -- 取引先・請求書番号でも探せるようにする（BUG-0019）。
  -- 入金は 1 件で複数の請求書に消し込める（ADR-0071）ので、**消込明細の側で存在判定する**——
  -- ヘッダの `invoice_id` で引くと合算入金が 1 件目でしか当たらない
  AND (@partner IS NULL OR @partner = '' OR EXISTS (
        SELECT 1 FROM receipt_lines rl2
        JOIN invoices iv2 ON iv2.id = rl2.invoice_id
        JOIN partners pt2 ON pt2.id = iv2.partner_id
        WHERE rl2.receipt_id = r.id AND pt2.name LIKE '%' || @partner || '%'))
  AND (@invoice_no IS NULL OR @invoice_no = '' OR EXISTS (
        SELECT 1 FROM receipt_lines rl3
        JOIN invoices iv3 ON iv3.id = rl3.invoice_id
        WHERE rl3.receipt_id = r.id AND iv3.invoice_no LIKE '%' || @invoice_no || '%'))