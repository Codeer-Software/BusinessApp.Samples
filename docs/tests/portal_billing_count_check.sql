-- portal_billing_count_check.sql — ポータルの「当月未生成」件数と画面のプランを突き合わせる（ADR-0060）
--
-- 使い方:
--   1. 先に SES 精算・請求（SesBilling）と定期請求の実行（RecurringRun）を当月で開く
--      （プランテーブルは画面を開いたときにしか作られない）
--   2. この SQL を sql CLI で流す
--   3. 3 つの結果の件数が一致していることを確認する
--        A: ポータルが数える件数（PortalQueueData.Query.sql と同条件）
--        B: 定期請求のプランテーブルの「生成予定」
--        C: SES のプランテーブルの「生成予定」
--      A の recurring_pending == B、A の ses_pending == C なら整合。
--
-- 一致しないときは「SQL と BuildPlan の除外条件がズレた」ということ。
-- 直すのは原則 SQL 側（画面の BuildPlan が判定の正典）。
--
-- ※ このチェックは実機検証のチェックリストに入れる。判定が二重実装で残っている以上、
--    自動では守られない（ADR-0060「残る弱点」）。

-- ---- A: ポータルが数える件数 ----
SELECT
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
                        AND date(iv.billing_month) = date('now', 'localtime', 'start of month'))) AS portal_recurring_pending,
  (SELECT count(*) FROM (
     SELECT p.id,
            CAST(COALESCE((SELECT sum(t.minutes) FROM time_entries t
                            WHERE t.project_id = p.id
                              AND date(t.work_date) >= date('now', 'localtime', 'start of month')
                              AND date(t.work_date) <  date('now', 'localtime', 'start of month', '+1 month')), 0) AS INTEGER) AS mins,
            p.ses_monthly_rate AS rate, p.ses_lower_hours AS lo, p.ses_upper_hours AS hi,
            COALESCE(p.ses_deduct_rate, 0) AS deduct, COALESCE(p.ses_excess_rate, 0) AS excess
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
                 THEN ((s.mins / 60) - s.hi) * s.excess ELSE 0 END) > 0) AS portal_ses_pending;

-- ---- B: 定期請求のプラン（画面が作った判定結果） ----
SELECT status, count(*) AS cnt FROM recurring_run_plan GROUP BY status;

-- ---- C: SES のプラン（画面が作った判定結果） ----
SELECT status, count(*) AS cnt FROM ses_run_plan GROUP BY status;

-- ---- 参考: 対象外になった理由の内訳（ズレたときの原因追跡用） ----
SELECT 'recurring' AS kind, status, detail, count(*) AS cnt FROM recurring_run_plan GROUP BY status, detail
UNION ALL
SELECT 'ses', status, detail, count(*) FROM ses_run_plan GROUP BY status, detail;
