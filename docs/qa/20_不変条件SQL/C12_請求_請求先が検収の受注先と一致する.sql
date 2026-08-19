-- 何を保証するか: 検収に紐づく請求書（`invoices.acceptance_id` / `acceptances.billed_invoice_id`）が、
--                 その検収の受注先と同じ取引先を指していること。
-- 違反時の意味: 売上も売掛金も**検収から**立つので、食い違うと
--               **帳簿上の債権は A 社、請求書と入金は B 社**という状態になる。
--               取引先別の売掛が両側で合わず、入金が他社の債権を消し込む。
--               C08 は総額の突合なので、取引先が入れ替わっても差が出ず**素通り**する。
-- 出典: docs/qa/02_バグ台帳.md BUG-0424（検収 → 請求の向き）／ BUG-0452（請求 → 検収の向き）
-- 備考: 実データに 1 件あった（INV-26-010 が アルタイル商事 宛なのに A-26-005 の受注先は グランメゾン印刷）。
--       請求書の検収選択が `Status='confirmed'` でしか絞っておらず、別会社の検収を選べたのが原因。
--       `Designer/ddl/823` で是正済み。

SELECT '請求書の検収が別の取引先' AS 違反, iv.id AS 請求書id, iv.invoice_no AS 請求番号, iv.status AS 状態,
       iv.partner_id AS 請求先id, p1.name AS 請求先, so.partner_id AS 受注先id, p2.name AS 受注先,
       ac.acceptance_no AS 検収番号, iv.gross_amount AS 税込請求額
FROM invoices iv
JOIN acceptances ac  ON ac.id = iv.acceptance_id
JOIN sales_orders so ON so.id = ac.sales_order_id
LEFT JOIN partners p1 ON p1.id = iv.partner_id
LEFT JOIN partners p2 ON p2.id = so.partner_id
WHERE COALESCE(iv.partner_id, -1) <> COALESCE(so.partner_id, -1)

UNION ALL

SELECT '検収の合算先が別の取引先', iv.id, iv.invoice_no, iv.status,
       iv.partner_id, p1.name, so.partner_id, p2.name,
       ac.acceptance_no, iv.gross_amount
FROM acceptances ac
JOIN sales_orders so ON so.id = ac.sales_order_id
JOIN invoices iv     ON iv.id = ac.billed_invoice_id
LEFT JOIN partners p1 ON p1.id = iv.partner_id
LEFT JOIN partners p2 ON p2.id = so.partner_id
WHERE COALESCE(iv.partner_id, -1) <> COALESCE(so.partner_id, -1)
