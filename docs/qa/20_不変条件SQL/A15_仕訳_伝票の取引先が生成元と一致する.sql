-- 何を保証するか: `journal_entries.partner_id`（ADR-0076・電帳法の検索要件）が、
--                 その伝票の生成元から導ける取引先と一致していること。
-- 違反時の意味: 電子取引データの「取引先」検索が**嘘を返す**。
--               ① 取引先を後から付け替えた業務レコードに仕訳が追随していない
--               ② **新しい自動起票経路が `partner_id` をセットし忘れている**——
--                  この場合 NULL になるだけでエラーも出ず、誰も気づけない。
--               F01（孤児 FK）はこの新しい列を見ていないので、ここが唯一の検出手段。
-- 出典: docs/decisions/0076-仕訳が取引先を持つ.md ／ Designer/ddl/816・817
-- 備考: 相手が取引先マスタに載らない経路（bank / cashbook / template / import / depreciation / wip / 手入力）は
--       対象外。経費は「支払先が取引先のときだけ」なので、社員への精算は両側 NULL が正しい。

WITH expected AS (
  SELECT je.id AS 伝票id, je.journal_no AS 伝票番号, date(je.entry_date) AS 日付,
         je.source_type AS 連動元, je.source_id AS 連動元id, je.partner_id AS 伝票取引先,
         CASE je.source_type
           WHEN 'acceptance' THEN (SELECT so.partner_id FROM acceptances ac
                                     JOIN sales_orders so ON so.id = ac.sales_order_id
                                    WHERE ac.id = je.source_id)
           WHEN 'receipt'    THEN (SELECT MIN(iv.partner_id) FROM receipt_lines rl
                                     JOIN invoices iv ON iv.id = rl.invoice_id
                                    WHERE rl.receipt_id = je.source_id)
           WHEN 'vendor_invoice'   THEN (SELECT partner_id FROM vendor_invoices WHERE id = je.source_id)
           WHEN 'vendor_payment'   THEN (SELECT partner_id FROM vendor_invoices WHERE id = je.source_id)
           WHEN 'recurring'        THEN (SELECT partner_id FROM invoices WHERE id = je.source_id)
           WHEN 'recurring_annual' THEN (SELECT partner_id FROM invoices WHERE id = je.source_id)
           WHEN 'recurring_defer'  THEN (SELECT partner_id FROM invoices WHERE id = je.source_id)
           WHEN 'ses'              THEN (SELECT partner_id FROM invoices WHERE id = je.source_id)
           WHEN 'recurring_settle' THEN (SELECT partner_id FROM recurring_billings WHERE id = je.source_id)
           WHEN 'expense'          THEN (SELECT payee_partner_id FROM expense_request
                                          WHERE id = je.source_id AND payee_type = 'partner')
           WHEN 'expense_payment'  THEN (SELECT payee_partner_id FROM expense_request
                                          WHERE id = je.source_id AND payee_type = 'partner')
           WHEN 'reversal'         THEN (SELECT partner_id FROM journal_entries s WHERE s.id = je.source_id)
         END AS 生成元取引先
  FROM journal_entries je
  WHERE je.source_type IN ('acceptance', 'receipt', 'vendor_invoice', 'vendor_payment', 'recurring',
                           'recurring_annual', 'recurring_defer', 'ses', 'recurring_settle',
                           'expense', 'expense_payment', 'reversal')
)
SELECT e.伝票id, e.伝票番号, e.日付, e.連動元, e.連動元id,
       e.伝票取引先, p1.name AS 伝票取引先名, e.生成元取引先, p2.name AS 生成元取引先名
FROM expected e
LEFT JOIN partners p1 ON p1.id = e.伝票取引先
LEFT JOIN partners p2 ON p2.id = e.生成元取引先
WHERE COALESCE(e.伝票取引先, -1) <> COALESCE(e.生成元取引先, -1)
ORDER BY e.日付, e.伝票id
