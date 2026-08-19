-- 何を保証するか: 主要テーブルの外部キー列が、実在する親レコードを指していること。
-- 違反時の意味: 参照先が消えている。画面では空欄・「(不明)」として現れ、
--               集計 SQL では JOIN で行ごと落ちて「金額が静かに消える」。会計アプリでは致命的。
-- 【2026-08-19 訂正】ここには「SQLite は既定で外部キー制約を強制しない」と書いてあったが、
--               **この環境では強制されている**（`PRAGMA foreign_keys` = 1、`foreign_key_check` 違反 0。
--               CLB ランタイム 1.3.18 も `sql` CLI も ON）。`ddl/810` のトリガのコメント
--               「FK に引っかかる（実測）」のほうが正しい。
--               したがって **`REFERENCES` を書いた列は DB が守っている**——
--               この検査が拾うのは「FK 宣言が無い列（多態参照）」と「移行データ」だけ。
--               多態参照（`approval_flow.parent_id` は TEXT）は構造的にここでは見られないので **F10** が見る。
-- 出典: docs/04_会計ドメイン設計.md（各テーブル定義）／Designer/ddl/*.sql の REFERENCES 宣言
-- 備考: creator / updater（CLB 予約のシステム列）は運用上 0 や不定値が入りうるため対象外。
--       NULL は「未設定」として正常なので対象外（必須性は各テーブル個別のチェックで見る）。

-- 仕訳
SELECT 'journal_entries.fiscal_year_id' AS 参照元, t.id AS 行id, t.fiscal_year_id AS 参照値
FROM journal_entries t WHERE t.fiscal_year_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM fiscal_years p WHERE p.id = t.fiscal_year_id)
UNION ALL SELECT 'journal_lines.journal_entry_id', t.id, t.journal_entry_id FROM journal_lines t WHERE t.journal_entry_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM journal_entries p WHERE p.id = t.journal_entry_id)
UNION ALL SELECT 'journal_lines.account_id', t.id, t.account_id FROM journal_lines t WHERE t.account_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM accounts p WHERE p.id = t.account_id)
UNION ALL SELECT 'journal_lines.sub_account_id', t.id, t.sub_account_id FROM journal_lines t WHERE t.sub_account_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sub_accounts p WHERE p.id = t.sub_account_id)
UNION ALL SELECT 'journal_lines.department_id', t.id, t.department_id FROM journal_lines t WHERE t.department_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM departments p WHERE p.id = t.department_id)
UNION ALL SELECT 'journal_lines.project_id', t.id, t.project_id FROM journal_lines t WHERE t.project_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM projects p WHERE p.id = t.project_id)
UNION ALL SELECT 'journal_lines.tax_category_id', t.id, t.tax_category_id FROM journal_lines t WHERE t.tax_category_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tax_categories p WHERE p.id = t.tax_category_id)

-- 期首残高・予算
UNION ALL SELECT 'opening_balances.fiscal_year_id', t.id, t.fiscal_year_id FROM opening_balances t WHERE t.fiscal_year_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM fiscal_years p WHERE p.id = t.fiscal_year_id)
UNION ALL SELECT 'opening_balances.account_id', t.id, t.account_id FROM opening_balances t WHERE t.account_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM accounts p WHERE p.id = t.account_id)
UNION ALL SELECT 'opening_balances.department_id', t.id, t.department_id FROM opening_balances t WHERE t.department_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM departments p WHERE p.id = t.department_id)
UNION ALL SELECT 'budget_lines.fiscal_year_id', t.id, t.fiscal_year_id FROM budget_lines t WHERE t.fiscal_year_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM fiscal_years p WHERE p.id = t.fiscal_year_id)
UNION ALL SELECT 'budget_lines.account_id', t.id, t.account_id FROM budget_lines t WHERE t.account_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM accounts p WHERE p.id = t.account_id)
UNION ALL SELECT 'budget_lines.department_id', t.id, t.department_id FROM budget_lines t WHERE t.department_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM departments p WHERE p.id = t.department_id)
UNION ALL SELECT 'fiscal_periods.fiscal_year_id', t.id, t.fiscal_year_id FROM fiscal_periods t WHERE t.fiscal_year_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM fiscal_years p WHERE p.id = t.fiscal_year_id)

-- マスタ
UNION ALL SELECT 'accounts.category_id', t.id, t.category_id FROM accounts t WHERE t.category_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM account_categories p WHERE p.id = t.category_id)
UNION ALL SELECT 'accounts.default_tax_category_id', t.id, t.default_tax_category_id FROM accounts t WHERE t.default_tax_category_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tax_categories p WHERE p.id = t.default_tax_category_id)
UNION ALL SELECT 'sub_accounts.account_id', t.id, t.account_id FROM sub_accounts t WHERE t.account_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM accounts p WHERE p.id = t.account_id)
UNION ALL SELECT 'tax_categories.tax_rate_id', t.id, t.tax_rate_id FROM tax_categories t WHERE t.tax_rate_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tax_rates p WHERE p.id = t.tax_rate_id)
UNION ALL SELECT 'expense_categories.default_account_id', t.id, t.default_account_id FROM expense_categories t WHERE t.default_account_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM accounts p WHERE p.id = t.default_account_id)
UNION ALL SELECT 'expense_categories.default_tax_category_id', t.id, t.default_tax_category_id FROM expense_categories t WHERE t.default_tax_category_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tax_categories p WHERE p.id = t.default_tax_category_id)
UNION ALL SELECT 'bank_accounts.ledger_account_id', t.id, t.ledger_account_id FROM bank_accounts t WHERE t.ledger_account_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM accounts p WHERE p.id = t.ledger_account_id)
UNION ALL SELECT 'projects.partner_id', t.id, t.partner_id FROM projects t WHERE t.partner_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM partners p WHERE p.id = t.partner_id)
UNION ALL SELECT 'departments.parent_id', t.id, t.parent_id FROM departments t WHERE t.parent_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM departments p WHERE p.id = t.parent_id)
UNION ALL SELECT 'app_users.department_id', t.id, t.department_id FROM app_users t WHERE t.department_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM departments p WHERE p.id = t.department_id)
UNION ALL SELECT 'app_users.business_department_id', t.id, t.business_department_id FROM app_users t WHERE t.business_department_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM departments p WHERE p.id = t.business_department_id)
UNION ALL SELECT 'department_members.department_id', t.id, t.department_id FROM department_members t WHERE t.department_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM departments p WHERE p.id = t.department_id)
UNION ALL SELECT 'department_members.user_id', t.id, t.user_id FROM department_members t WHERE t.user_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM app_users p WHERE p.id = t.user_id)

-- 営業（見積・受注・検収・請求・入金）
UNION ALL SELECT 'quotes.partner_id', t.id, t.partner_id FROM quotes t WHERE t.partner_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM partners p WHERE p.id = t.partner_id)
UNION ALL SELECT 'quotes.project_id', t.id, t.project_id FROM quotes t WHERE t.project_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM projects p WHERE p.id = t.project_id)
UNION ALL SELECT 'quote_lines.quote_id', t.id, t.quote_id FROM quote_lines t WHERE t.quote_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM quotes p WHERE p.id = t.quote_id)
UNION ALL SELECT 'sales_orders.quote_id', t.id, t.quote_id FROM sales_orders t WHERE t.quote_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM quotes p WHERE p.id = t.quote_id)
UNION ALL SELECT 'sales_orders.partner_id', t.id, t.partner_id FROM sales_orders t WHERE t.partner_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM partners p WHERE p.id = t.partner_id)
UNION ALL SELECT 'sales_order_lines.sales_order_id', t.id, t.sales_order_id FROM sales_order_lines t WHERE t.sales_order_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sales_orders p WHERE p.id = t.sales_order_id)
UNION ALL SELECT 'acceptances.sales_order_id', t.id, t.sales_order_id FROM acceptances t WHERE t.sales_order_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sales_orders p WHERE p.id = t.sales_order_id)
UNION ALL SELECT 'acceptances.billed_invoice_id', t.id, t.billed_invoice_id FROM acceptances t WHERE t.billed_invoice_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM invoices p WHERE p.id = t.billed_invoice_id)
UNION ALL SELECT 'acceptance_lines.acceptance_id', t.id, t.acceptance_id FROM acceptance_lines t WHERE t.acceptance_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM acceptances p WHERE p.id = t.acceptance_id)
UNION ALL SELECT 'acceptance_lines.sales_order_line_id', t.id, t.sales_order_line_id FROM acceptance_lines t WHERE t.sales_order_line_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sales_order_lines p WHERE p.id = t.sales_order_line_id)
UNION ALL SELECT 'invoices.partner_id', t.id, t.partner_id FROM invoices t WHERE t.partner_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM partners p WHERE p.id = t.partner_id)
UNION ALL SELECT 'invoices.project_id', t.id, t.project_id FROM invoices t WHERE t.project_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM projects p WHERE p.id = t.project_id)
UNION ALL SELECT 'invoices.acceptance_id', t.id, t.acceptance_id FROM invoices t WHERE t.acceptance_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM acceptances p WHERE p.id = t.acceptance_id)
UNION ALL SELECT 'invoices.recurring_billing_id', t.id, t.recurring_billing_id FROM invoices t WHERE t.recurring_billing_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM recurring_billings p WHERE p.id = t.recurring_billing_id)
UNION ALL SELECT 'invoices.department_id', t.id, t.department_id FROM invoices t WHERE t.department_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM departments p WHERE p.id = t.department_id)
UNION ALL SELECT 'invoice_lines.invoice_id', t.id, t.invoice_id FROM invoice_lines t WHERE t.invoice_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM invoices p WHERE p.id = t.invoice_id)
UNION ALL SELECT 'invoice_lines.acceptance_line_id', t.id, t.acceptance_line_id FROM invoice_lines t WHERE t.acceptance_line_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM acceptance_lines p WHERE p.id = t.acceptance_line_id)
UNION ALL SELECT 'receipts.invoice_id', t.id, t.invoice_id FROM receipts t WHERE t.invoice_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM invoices p WHERE p.id = t.invoice_id)
UNION ALL SELECT 'receipt_lines.receipt_id', t.id, t.receipt_id FROM receipt_lines t WHERE NOT EXISTS (SELECT 1 FROM receipts p WHERE p.id = t.receipt_id)
UNION ALL SELECT 'receipt_lines.invoice_id', t.id, t.invoice_id FROM receipt_lines t WHERE NOT EXISTS (SELECT 1 FROM invoices p WHERE p.id = t.invoice_id)
UNION ALL SELECT 'receipts.offset_vendor_invoice_id', t.id, t.offset_vendor_invoice_id FROM receipts t WHERE t.offset_vendor_invoice_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM vendor_invoices p WHERE p.id = t.offset_vendor_invoice_id)
UNION ALL SELECT 'recurring_billings.partner_id', t.id, t.partner_id FROM recurring_billings t WHERE t.partner_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM partners p WHERE p.id = t.partner_id)
UNION ALL SELECT 'recurring_billings.project_id', t.id, t.project_id FROM recurring_billings t WHERE t.project_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM projects p WHERE p.id = t.project_id)
UNION ALL SELECT 'recurring_billings.quote_id', t.id, t.quote_id FROM recurring_billings t WHERE t.quote_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM quotes p WHERE p.id = t.quote_id)

-- 購買・経費・固定資産
UNION ALL SELECT 'vendor_invoices.partner_id', t.id, t.partner_id FROM vendor_invoices t WHERE t.partner_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM partners p WHERE p.id = t.partner_id)
UNION ALL SELECT 'vendor_invoices.expense_account_id', t.id, t.expense_account_id FROM vendor_invoices t WHERE t.expense_account_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM accounts p WHERE p.id = t.expense_account_id)
UNION ALL SELECT 'vendor_invoices.tax_category_id', t.id, t.tax_category_id FROM vendor_invoices t WHERE t.tax_category_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tax_categories p WHERE p.id = t.tax_category_id)
UNION ALL SELECT 'vendor_invoices.bank_account_id', t.id, t.bank_account_id FROM vendor_invoices t WHERE t.bank_account_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM bank_accounts p WHERE p.id = t.bank_account_id)
UNION ALL SELECT 'vendor_invoices.department_id', t.id, t.department_id FROM vendor_invoices t WHERE t.department_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM departments p WHERE p.id = t.department_id)
UNION ALL SELECT 'expense_request.expense_category_id', t.id, t.expense_category_id FROM expense_request t WHERE t.expense_category_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM expense_categories p WHERE p.id = t.expense_category_id)
UNION ALL SELECT 'expense_request.payee_user', t.id, t.payee_user FROM expense_request t WHERE t.payee_user IS NOT NULL AND NOT EXISTS (SELECT 1 FROM app_users p WHERE p.id = t.payee_user)
UNION ALL SELECT 'expense_request.payee_partner_id', t.id, t.payee_partner_id FROM expense_request t WHERE t.payee_partner_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM partners p WHERE p.id = t.payee_partner_id)
UNION ALL SELECT 'expense_request.project_id', t.id, t.project_id FROM expense_request t WHERE t.project_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM projects p WHERE p.id = t.project_id)
UNION ALL SELECT 'expense_request.department_id', t.id, t.department_id FROM expense_request t WHERE t.department_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM departments p WHERE p.id = t.department_id)
UNION ALL SELECT 'expense_request.approval_flow_id', t.id, t.approval_flow_id FROM expense_request t WHERE t.approval_flow_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM approval_flow p WHERE p.id = t.approval_flow_id)
UNION ALL SELECT 'fixed_assets.asset_account_id', t.id, t.asset_account_id FROM fixed_assets t WHERE t.asset_account_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM accounts p WHERE p.id = t.asset_account_id)
UNION ALL SELECT 'fixed_assets.department_id', t.id, t.department_id FROM fixed_assets t WHERE t.department_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM departments p WHERE p.id = t.department_id)

-- 銀行・定型仕訳・工数
UNION ALL SELECT 'bank_statement_lines.bank_account_id', t.id, t.bank_account_id FROM bank_statement_lines t WHERE t.bank_account_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM bank_accounts p WHERE p.id = t.bank_account_id)
UNION ALL SELECT 'bank_statement_lines.journal_entry_id', t.id, t.journal_entry_id FROM bank_statement_lines t WHERE t.journal_entry_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM journal_entries p WHERE p.id = t.journal_entry_id)
UNION ALL SELECT 'bank_statement_lines.suggested_account_id', t.id, t.suggested_account_id FROM bank_statement_lines t WHERE t.suggested_account_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM accounts p WHERE p.id = t.suggested_account_id)
UNION ALL SELECT 'journal_template_lines.template_id', t.id, t.template_id FROM journal_template_lines t WHERE t.template_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM journal_templates p WHERE p.id = t.template_id)
UNION ALL SELECT 'journal_template_lines.account_id', t.id, t.account_id FROM journal_template_lines t WHERE t.account_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM accounts p WHERE p.id = t.account_id)
UNION ALL SELECT 'journal_template_lines.tax_category_id', t.id, t.tax_category_id FROM journal_template_lines t WHERE t.tax_category_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tax_categories p WHERE p.id = t.tax_category_id)
UNION ALL SELECT 'time_entries.user_id', t.id, t.user_id FROM time_entries t WHERE t.user_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM app_users p WHERE p.id = t.user_id)
UNION ALL SELECT 'time_entries.project_id', t.id, t.project_id FROM time_entries t WHERE t.project_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM projects p WHERE p.id = t.project_id)
UNION ALL SELECT 'monthly_salaries.user_id', t.id, t.user_id FROM monthly_salaries t WHERE t.user_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM app_users p WHERE p.id = t.user_id)
UNION ALL SELECT 'monthly_salaries.fiscal_year_id', t.id, t.fiscal_year_id FROM monthly_salaries t WHERE t.fiscal_year_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM fiscal_years p WHERE p.id = t.fiscal_year_id)
