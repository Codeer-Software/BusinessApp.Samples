-- 売掛残高一覧: 請求書ごとの税込請求額・入金累計・残額・状態
-- void は除外。日付比較は date() で正規化 (Project.md 知見)。支払期限昇順。
WITH rc AS (
  SELECT invoice_id, SUM(amount) AS received
  FROM receipts
  GROUP BY invoice_id
)
SELECT
  i.invoice_no AS invoice_no,
  p.name AS partner_name,
  i.title AS title,
  i.issue_date AS issue_date,
  i.due_date AS due_date,
  COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0) AS total_amount,
  COALESCE(rc.received, 0) AS received_amount,
  COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0) - COALESCE(rc.received, 0) AS balance,
  CASE
    WHEN COALESCE(rc.received, 0) >= COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0) THEN '入金済'
    WHEN i.due_date IS NOT NULL AND date(i.due_date) < date('now') THEN '期限超過'
    WHEN COALESCE(rc.received, 0) > 0 THEN '一部入金'
    ELSE '未入金'
  END AS state
FROM invoices i
LEFT JOIN partners p ON p.id = i.partner_id
LEFT JOIN rc ON rc.invoice_id = i.id
WHERE i.status <> 'void'
ORDER BY date(i.due_date), i.invoice_no
