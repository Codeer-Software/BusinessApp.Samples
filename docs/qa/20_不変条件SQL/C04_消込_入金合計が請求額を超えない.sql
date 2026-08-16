-- 何を保証するか: 消込済み（消込仕訳がある）入金の合計が、請求書の税込請求額を超えないこと。
--                 ＝ 売掛残高が負にならないこと。
-- 違反時の意味: 過入金を消し込んでいる。売掛金勘定が貸方残になり、BS の売掛金がマイナス表示になる。
--               本来は「前受金」または「返金」で処理すべきものが売掛の消込に紛れている。
-- 出典: ADR-0051（入金の集計は消込仕訳がある行だけ）
--       Modules/Sales/ReceivableBalance.Query.sql（消込済み = journal_entries(source_type='receipt', source_id) の存在）
-- 備考: 請求書発行時に税込全額の「入金予定」が自動作成される（ADR-0032）ため、
--       消込仕訳の存在で絞らないと全請求書が過入金に見える。この絞りは本チェックの前提。
WITH settled AS (
  SELECT r.invoice_id AS inv, SUM(r.amount) AS received
  FROM receipts r
  WHERE EXISTS (SELECT 1 FROM journal_entries je
                WHERE je.source_type = 'receipt' AND je.source_id = r.id)
  GROUP BY r.invoice_id
)
SELECT
    i.id         AS 請求書id,
    i.invoice_no AS 請求書番号,
    i.status     AS 状態,
    p.name       AS 取引先,
    COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0) AS 税込請求額,
    s.received   AS 消込済入金,
    s.received - (COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0)) AS 超過額
FROM invoices i
JOIN settled s ON s.inv = i.id
LEFT JOIN partners p ON p.id = i.partner_id
WHERE s.received > COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0)
ORDER BY i.id
