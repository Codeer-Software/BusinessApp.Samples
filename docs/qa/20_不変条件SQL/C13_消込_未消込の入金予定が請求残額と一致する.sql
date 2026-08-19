-- 何を保証するか: 発行済み（void/draft 以外）の請求書ごとに、**消込仕訳を持たない入金＝入金予定**の合計が
--                 「税込請求額 − 消込済み額」に等しいこと。
-- 違反時の意味: 「未回収の発行済み請求書には常に残額分の予定が 1 件ある」という約束（ADR-0032・ADR-0033）が
--               崩れている。資金繰り予測とポータルの入金予定はすべてここを源にしているので、
--               ズレると**キャッシュ予測が静かに嘘をつく**。
--               具体的には ①予定の作り忘れ ②二重予定（BUG-0380）③一部入金後の残額不一致（BUG-0381）
--               ④取消でできる幽霊予定（BUG-0422）。
--               C04/C06 は**消込済み側**しか見ておらず、予定側は無検査だった。
-- 出典: docs/qa/02_バグ台帳.md BUG-0380 / BUG-0381 / BUG-0422 ／ ADR-0032・ADR-0033

WITH plan AS (
  SELECT rl.invoice_id, SUM(rl.amount) AS 予定額, COUNT(*) AS 予定件数,
         GROUP_CONCAT(r.id) AS 入金id
  FROM receipt_lines rl
  JOIN receipts r ON r.id = rl.receipt_id
  WHERE NOT EXISTS (SELECT 1 FROM journal_entries je
                     WHERE je.source_type = 'receipt' AND je.source_id = r.id)
  GROUP BY rl.invoice_id
),
gross AS (
  SELECT id, COALESCE(gross_amount, amount + COALESCE(tax_amount, 0)) AS 税込 FROM invoices
)
SELECT iv.id AS 請求書id, iv.invoice_no AS 請求番号, iv.status AS 状態, g.税込 AS 税込請求額,
       COALESCE(v.received, 0) AS 消込済み, g.税込 - COALESCE(v.received, 0) AS 残額,
       COALESCE(p.予定額, 0) AS 予定額, COALESCE(p.予定件数, 0) AS 予定件数, p.入金id,
       COALESCE(p.予定額, 0) - (g.税込 - COALESCE(v.received, 0)) AS 差
FROM invoices iv
JOIN gross g ON g.id = iv.id
LEFT JOIN plan p ON p.invoice_id = iv.id
LEFT JOIN v_invoice_received v ON v.invoice_id = iv.id
WHERE COALESCE(iv.status, '') NOT IN ('void', 'draft')
  AND COALESCE(p.予定額, 0) <> g.税込 - COALESCE(v.received, 0)
ORDER BY iv.id
