-- ポータル「経理の作業キュー」件数（ADR-0045・docs/13 §3 #4-#6 の契約。1 行）
-- 精算処理待ち = ExpenseSettlementQueue と同一条件（approved/accounting/settled）
-- 定期請求の当月未生成 = 確定済み・有効・当月が契約期間内で、当月分の請求書が無い契約
--   （BuildPlan の冪等判定 recurring_billing_id×billing_month と同条件。月額は毎月、年額は周期起点月のみ対象）
-- SES の当月未生成 = 有効な SES 案件（契約条件設定済み）で当月の SES 請求書が無いもの
--   （BuildPlan の冪等判定 invoice_source='ses'×project_id×billing_month と同条件）
SELECT
  (SELECT count(*) FROM expense_request
    WHERE settlement_status IN ('approved', 'accounting', 'settled')) AS settlement_queue,
  (SELECT count(*) FROM bank_statement_lines WHERE status = 'pending') AS bank_pending,
  (SELECT count(*) FROM journal_entries WHERE status = 'draft') AS journal_drafts,
  (SELECT count(*) FROM recurring_billings rb
    WHERE rb.is_active = 1 AND rb.status = 'confirmed'
      AND date(rb.start_month) <= date('now', 'start of month')
      AND (rb.end_month IS NULL OR date(rb.end_month) >= date('now', 'start of month'))
      AND (rb.billing_cycle <> 'annual'
           OR ((CAST(strftime('%Y', 'now') AS INTEGER) * 12 + CAST(strftime('%m', 'now') AS INTEGER))
               - (CAST(strftime('%Y', rb.start_month) AS INTEGER) * 12 + CAST(strftime('%m', rb.start_month) AS INTEGER))) % 12 = 0)
      AND NOT EXISTS (SELECT 1 FROM invoices iv
                      WHERE iv.recurring_billing_id = rb.id
                        AND date(iv.billing_month) = date('now', 'start of month'))) AS recurring_pending,
  (SELECT count(*) FROM projects p
    WHERE p.project_type = 'ses' AND p.status = 'active'
      AND p.ses_monthly_rate IS NOT NULL AND p.ses_monthly_rate > 0
      AND NOT EXISTS (SELECT 1 FROM invoices iv
                      WHERE iv.invoice_source = 'ses' AND iv.project_id = p.id
                        AND date(iv.billing_month) = date('now', 'start of month'))) AS ses_pending
