-- 何を保証するか: 仕入先請求書に紐づく未払計上仕訳・支払仕訳の借方合計が、税込請求額と一致すること。
--                 未払計上は「費用本体 + 仮払消費税 / 買掛金（税込）」なので借方合計 = 税込額。
--                 支払は「買掛金（税込）/ 預金」なので借方合計 = 税込額。
-- 違反時の意味: 請求額を後から直したのに仕訳が追随していない。買掛金が残る／消えすぎる。
-- 出典: Modules/Purchasing/VendorInvoice.mod.cs（未払計上・支払の起票）
--       docs/04_会計ドメイン設計.md §3.2（税抜経理・明示税行方式）
-- 備考: 免税事業者からの仕入（インボイス経過措置）でも、控除できない分は費用本体に上乗せされるため
--       借方合計は税込額のまま変わらない。
SELECT '未払計上仕訳の金額不一致' AS 違反,
       vi.id AS 仕入先請求書id, vi.invoice_no AS 請求書番号, vi.status AS 状態,
       vi.amount AS 税込請求額, je.id AS 伝票id,
       (SELECT SUM(l.amount) FROM journal_lines l WHERE l.journal_entry_id = je.id AND l.dc = 'D') AS 仕訳借方計
FROM vendor_invoices vi
JOIN journal_entries je ON je.id = vi.accrual_entry_id
WHERE COALESCE(vi.amount, 0)
      <> COALESCE((SELECT SUM(l.amount) FROM journal_lines l
                   WHERE l.journal_entry_id = je.id AND l.dc = 'D'), 0)

UNION ALL
SELECT '支払仕訳の金額不一致',
       vi.id, vi.invoice_no, vi.status, vi.amount, je.id,
       (SELECT SUM(l.amount) FROM journal_lines l WHERE l.journal_entry_id = je.id AND l.dc = 'D')
FROM vendor_invoices vi
JOIN journal_entries je ON je.id = vi.payment_entry_id
WHERE COALESCE(vi.amount, 0)
      <> COALESCE((SELECT SUM(l.amount) FROM journal_lines l
                   WHERE l.journal_entry_id = je.id AND l.dc = 'D'), 0)
