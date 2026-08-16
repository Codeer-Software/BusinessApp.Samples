-- 何を保証するか: 仕入先請求書の status と、未払計上仕訳・支払仕訳のリンクが矛盾しないこと。
--   ・accrued / paid  → 未払計上仕訳（accrual_entry_id）がある
--   ・paid            → 支払仕訳（payment_entry_id）と支払日がある
--   ・received        → まだ仕訳が立っていない
--   ・リンク先の伝票が実在する
-- 違反時の意味: 買掛金残高と支払管理の一覧が食い違う。二重支払・支払漏れの温床。
-- 出典: Designer/ddl の vendor_invoices 定義（status: received / accrued / paid）
--       Modules/Purchasing/VendorInvoice.mod.cs（source_type + source_id で自動仕訳を取得）
SELECT '未払計上仕訳が無い' AS 違反, vi.id AS 仕入先請求書id, vi.invoice_no AS 請求書番号,
       vi.status AS 状態, vi.amount AS 税込額, vi.invoice_date AS 請求日
FROM vendor_invoices vi
WHERE vi.status IN ('accrued', 'paid') AND vi.accrual_entry_id IS NULL

UNION ALL
SELECT '支払仕訳が無い', vi.id, vi.invoice_no, vi.status, vi.amount, vi.invoice_date
FROM vendor_invoices vi
WHERE vi.status = 'paid' AND vi.payment_entry_id IS NULL

UNION ALL
SELECT '支払済みなのに支払日が無い', vi.id, vi.invoice_no, vi.status, vi.amount, vi.invoice_date
FROM vendor_invoices vi
WHERE vi.status = 'paid' AND vi.paid_date IS NULL

UNION ALL
SELECT '未計上なのに仕訳リンクがある', vi.id, vi.invoice_no, vi.status, vi.amount, vi.invoice_date
FROM vendor_invoices vi
WHERE vi.status = 'received'
  AND (vi.accrual_entry_id IS NOT NULL OR vi.payment_entry_id IS NOT NULL)

UNION ALL
SELECT '未払計上仕訳が実在しない', vi.id, vi.invoice_no, vi.status, vi.amount, vi.invoice_date
FROM vendor_invoices vi
WHERE vi.accrual_entry_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM journal_entries je WHERE je.id = vi.accrual_entry_id)

UNION ALL
SELECT '支払仕訳が実在しない', vi.id, vi.invoice_no, vi.status, vi.amount, vi.invoice_date
FROM vendor_invoices vi
WHERE vi.payment_entry_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM journal_entries je WHERE je.id = vi.payment_entry_id)
