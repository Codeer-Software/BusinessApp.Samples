-- 何を保証するか: 自動起票された仕訳の source_type / source_id が、実在する業務レコードを指すこと。
--                 source_type と source_id は「型のない外部キー」で DB 制約が一切効かないため、
--                 業務レコードを削除すると仕訳だけが残り、逆引きが永久に失敗する。
-- 違反時の意味: 「この売上はどの検収から来たのか」が追えない。取消・再起票のガード
--               （source_type + source_id での二重生成チェック）もすり抜ける。
-- 出典: 各モジュールの起票箇所（Acceptance/Receipt/ExpenseRequest/VendorInvoice/FixedAsset/
--       RecurringRun/SesBilling/BankPosting/JournalTemplate .mod.cs の SourceType.Value 代入）
-- source_type → 参照先テーブルの対応:
--   acceptance                              → acceptances
--   receipt                                 → receipts
--   expense / expense_payment               → expense_request
--   vendor_invoice / vendor_payment         → vendor_invoices
--   depreciation / disposal                 → fixed_assets
--   wip / wip_reversal                      → fiscal_years（どの年度の振替か。振戻は前期の年度 id）
--   recurring / recurring_annual /
--   recurring_defer / ses                   → invoices
--   bank                                    → bank_statement_lines
--   template                                → journal_templates
--   cashbook / import                       → source_id を持たない（対象外）
SELECT je.source_type AS 連動元種別, je.id AS 伝票id, je.source_id AS 参照値,
       je.entry_date AS 日付, je.description AS 摘要
FROM journal_entries je
WHERE je.source_id IS NOT NULL
  AND (
       (je.source_type = 'acceptance'
        AND NOT EXISTS (SELECT 1 FROM acceptances p WHERE p.id = je.source_id))
    OR (je.source_type = 'receipt'
        AND NOT EXISTS (SELECT 1 FROM receipts p WHERE p.id = je.source_id))
    OR (je.source_type IN ('expense', 'expense_payment')
        AND NOT EXISTS (SELECT 1 FROM expense_request p WHERE p.id = je.source_id))
    OR (je.source_type IN ('vendor_invoice', 'vendor_payment')
        AND NOT EXISTS (SELECT 1 FROM vendor_invoices p WHERE p.id = je.source_id))
    OR (je.source_type IN ('depreciation', 'disposal')
        AND NOT EXISTS (SELECT 1 FROM fixed_assets p WHERE p.id = je.source_id))
    OR (je.source_type IN ('wip', 'wip_reversal')
        AND NOT EXISTS (SELECT 1 FROM fiscal_years p WHERE p.id = je.source_id))
    OR (je.source_type IN ('recurring', 'recurring_annual', 'recurring_defer', 'ses')
        AND NOT EXISTS (SELECT 1 FROM invoices p WHERE p.id = je.source_id))
    OR (je.source_type = 'bank'
        AND NOT EXISTS (SELECT 1 FROM bank_statement_lines p WHERE p.id = je.source_id))
    OR (je.source_type = 'template'
        AND NOT EXISTS (SELECT 1 FROM journal_templates p WHERE p.id = je.source_id))
  )
ORDER BY je.source_type, je.id
