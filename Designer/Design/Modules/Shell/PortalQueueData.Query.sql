-- ポータル「経理の作業キュー」件数（ADR-0045・docs/13 §3 #4-#6 の契約。1 行）
--
-- 【この SQL と BuildPlan は「対で保守する」契約】（ADR-0060）
--   定期請求・SES の「当月未生成」は、画面側（RecurringRun.BuildPlan / SesBilling.BuildPlan）が
--   唯一の判定ロジックで、この SQL はその判定を SQL で再現したもの。片方だけ直すと
--   「ホームの件数と画面の件数が合わない」不具合になる（実際に 3 件 vs 2 件のズレを起こした）。
--   突合の検証 SQL: docs/tests/portal_billing_count_check.sql（実機検証のチェックリストに入れる）
--
-- 【日付は必ず localtime】
--   SQLite の date('now') は UTC。JST とは 9 時間ずれるため、毎月 1 日の 0:00〜9:00 は
--   「前月」を数えてしまい、画面（DateTime.Today＝ローカル）と 1 ヶ月ずれる（実測）。
--
-- 精算処理待ち = ExpenseSettlementQueue と同一条件（approved/accounting/settled）
-- 定期請求の当月未生成 = RecurringRun.BuildPlan が status='planned' にする契約と同条件
--   （確定済み・有効・当月が契約期間内・金額が 1 円以上・当月分の請求書が無い。
--    月額は毎月、年額は周期起点月のみ対象）
-- SES の当月未生成 = SesBilling.BuildPlan が status='planned' にする案件と同条件
--   （有効・進行中・月額精算額が設定済み・当月の工数実績あり・精算後の請求額が 1 円以上・
--    当月の SES 請求書が無い）
SELECT
  (SELECT count(*) FROM expense_request
    WHERE settlement_status IN ('approved', 'accounting', 'settled')) AS settlement_queue,
  (SELECT count(*) FROM bank_statement_lines WHERE status = 'pending') AS bank_pending,
  (SELECT count(*) FROM journal_entries WHERE status = 'draft') AS journal_drafts,
  (SELECT count(*) FROM recurring_billings rb
    WHERE rb.is_active = 1 AND rb.status = 'confirmed'
      AND date(rb.start_month) <= date('now', 'localtime', 'start of month')
      AND (rb.end_month IS NULL OR date(rb.end_month) >= date('now', 'localtime', 'start of month'))
      AND (CASE WHEN rb.billing_cycle = 'yearly'
                THEN COALESCE(rb.annual_amount, 0) ELSE COALESCE(rb.monthly_amount, 0) END) > 0
      AND (rb.billing_cycle <> 'yearly'
           OR ((CAST(strftime('%Y', 'now', 'localtime') AS INTEGER) * 12 + CAST(strftime('%m', 'now', 'localtime') AS INTEGER))
               - (CAST(strftime('%Y', rb.start_month) AS INTEGER) * 12 + CAST(strftime('%m', rb.start_month) AS INTEGER))) % 12 = 0)
      AND NOT EXISTS (SELECT 1 FROM invoices iv
                      WHERE iv.recurring_billing_id = rb.id
                        AND date(iv.billing_month) = date('now', 'localtime', 'start of month'))) AS recurring_pending,
  (SELECT count(*) FROM (
     SELECT p.id,
            CAST(COALESCE((SELECT sum(t.minutes) FROM time_entries t
                            WHERE t.project_id = p.id
                              AND date(t.work_date) >= date('now', 'localtime', 'start of month')
                              AND date(t.work_date) <  date('now', 'localtime', 'start of month', '+1 month')), 0) AS INTEGER) AS mins,
            p.ses_monthly_rate AS rate,
            p.ses_lower_hours  AS lo,
            p.ses_upper_hours  AS hi,
            COALESCE(p.ses_deduct_rate, 0) AS deduct,
            COALESCE(p.ses_excess_rate, 0) AS excess
       FROM projects p
      WHERE p.project_type = 'ses' AND p.status = 'active' AND p.is_active = 1
        AND p.ses_monthly_rate IS NOT NULL AND p.ses_monthly_rate > 0
        AND NOT EXISTS (SELECT 1 FROM invoices iv
                        WHERE iv.invoice_source = 'ses' AND iv.project_id = p.id
                          AND date(iv.billing_month) = date('now', 'localtime', 'start of month'))
   ) s
   WHERE s.mins > 0
     AND (s.rate
          - CASE WHEN s.lo IS NOT NULL AND s.hi IS NOT NULL AND (s.mins / 60) < s.lo
                 THEN (s.lo - (s.mins / 60)) * s.deduct ELSE 0 END
          + CASE WHEN s.lo IS NOT NULL AND s.hi IS NOT NULL AND (s.mins / 60) > s.hi
                 THEN ((s.mins / 60) - s.hi) * s.excess ELSE 0 END) > 0) AS ses_pending
