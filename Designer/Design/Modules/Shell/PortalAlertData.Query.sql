-- ポータル「アラート」件数（ADR-0045・docs/13 §3 #7-#10 の契約。1 行）
-- 支払期限 = 支払予定表（PaymentSchedule）と同一条件。「まもなく」日数は system_thresholds.PAY_DUE_SOON_DAYS
-- 期限超過の売掛 = 売掛残高一覧（ReceivableBalance）の state='期限超過' と同一条件
-- 資金ショート = 資金繰り予測（CashFlowForecastData）の alert_mark と同一モデル（当月+3ヶ月・期末資金<0 の月数）
-- 予算警告 = 予実対比（BudgetVsActual）の alert_mark と同一条件の部門数（当年度・BUDGET_ALERT_RATE）
-- 入金の集計は 3 帳票とも「消込済み（消込仕訳がある）入金」だけを数える。発行時に自動作成される
-- 未確定の入金予定（ADR-0032）を含めると期限超過が 0 件・入金予定が 0 円になる（改善候補 A-2）
WITH RECURSIVE months(idx, month_first) AS (
  SELECT 0, date('now', 'localtime', 'start of month')
  UNION ALL
  SELECT idx + 1, date(month_first, '+1 month') FROM months WHERE idx < 3
),
threshold AS (
  SELECT COALESCE((SELECT amount FROM v_system_threshold_current WHERE code = 'PAY_DUE_SOON_DAYS'), 7) AS days
),
pay AS (
  SELECT CAST(julianday(date(v.due_date)) - julianday(date('now', 'localtime')) AS INTEGER) AS days_left
  FROM vendor_invoices v
  WHERE v.status IN ('received', 'accrued')
),
recv AS (
  SELECT count(*) AS c
  FROM invoices i
  LEFT JOIN v_invoice_received rc
    ON rc.invoice_id = i.id
  WHERE i.status <> 'void' AND i.status <> 'draft' AND i.status <> 'paid'
    AND COALESCE(rc.received, 0) < COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0)
    AND i.due_date IS NOT NULL AND date(i.due_date) < date('now', 'localtime')
),
cur_yr AS (
  SELECT id FROM fiscal_years
  WHERE date(start_date) <= date('now', 'localtime') AND date(end_date) >= date('now', 'localtime')
),
cash_now AS (
  -- 「いまの現預金」の 2 つの約束（2026-08-19）:
  --   1. **対象科目は `accounts.is_cash_equivalent`**（BUG-0327）。科目コードを直書きしていた頃は
  --      定期預金(1030) が予測から落ち、C/F 計算書（1000〜1099）と食い違っていた。
  --      定期預金への振替が**予測では純減・C/F では増減 0**に見える
  --   2. **今日までの仕訳だけを足す**（BUG-0328）。決算整理（3/31 付）や先日付の支払仕訳が
  --      「いまの残高」に混ざると、**実在しない額**から予測が始まる
  SELECT
    COALESCE((SELECT SUM(ob.balance)
              FROM opening_balances ob JOIN accounts a ON a.id = ob.account_id
              WHERE a.is_cash_equivalent = 1
                AND ob.fiscal_year_id IN (SELECT id FROM cur_yr)), 0)
    +
    COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
              FROM journal_lines l
              JOIN journal_entries e ON e.id = l.journal_entry_id
              JOIN accounts a ON a.id = l.account_id
              WHERE e.status = 'posted' AND a.is_cash_equivalent = 1
                AND date(e.entry_date) <= date('now', 'localtime')
                AND e.fiscal_year_id IN (SELECT id FROM cur_yr)), 0) AS cash
),
-- 売上の既定税区分（税制マスタで設定: tax_categories.default_for='sales'）に紐づく税率
sales_rate AS (
  -- 既定用途='売上' の税区分が無い／無効のとき、0% にフォールバックしてはいけない。
  -- 入金見込みが税抜のまま（約 10% 過小）になり、警告も出ないので気づけない。
  -- 税率は直書きせず（CLAUDE.md §3）、**有効な課税売上区分の最高税率**を代わりに使う
  SELECT COALESCE(
           (SELECT tr.rate_percent
            FROM tax_categories tc JOIN tax_rates tr ON tr.id = tc.tax_rate_id
            WHERE tc.default_for = 'sales' AND tc.is_active = 1),
           (SELECT MAX(tr2.rate_percent)
            FROM tax_categories tc2 JOIN tax_rates tr2 ON tr2.id = tc2.tax_rate_id
            WHERE tc2.taxation_type = 'taxable_sales' AND tc2.is_active = 1),
           0) AS pct
),
inv_in AS (
  SELECT max(date(i.due_date, 'start of month'), (SELECT month_first FROM months WHERE idx = 0)) AS m,
         COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0) - COALESCE(rc.received, 0) AS amt
  FROM invoices i
  LEFT JOIN v_invoice_received rc
    ON rc.invoice_id = i.id
  WHERE i.status IN ('issued', 'partial') AND i.due_date IS NOT NULL
),
rec_in AS (
  -- 【周期で金額と月が変わる・BUG-0412】月額契約は毎月 monthly_amount、
  --   年額契約は**起点月の応当月に 1 回だけ** annual_amount を請求する（`RecurringRun` と同じ規則:
  --   起点月からの経過月数を 12 で割った余りが 0 の月が周期起点）。
  --   旧実装は billing_cycle を見ずに monthly_amount だけを毎月足していたため、
  --   ①年額契約の入金見込みが**丸ごと落ちる**（monthly_amount が空なので NULL＝加算されない）
  --   ②年額契約に古い monthly_amount が残っていると**毎月その額が乗る**、という二重の誤りがあった。
  SELECT date(mm.month_first, '+1 month') AS m,
         CASE WHEN rb.billing_cycle = 'yearly' THEN COALESCE(rb.annual_amount, 0)
              ELSE COALESCE(rb.monthly_amount, 0) END
           * (100 + (SELECT pct FROM sales_rate)) / 100 AS amt
  FROM months mm
  -- 確定済のみ（ADR-0057）。下書き・終了は「定期請求の実行」の対象外なので入金見込みにも載せない
  JOIN recurring_billings rb ON rb.is_active = 1 AND rb.status = 'confirmed'
    AND date(rb.start_month) <= mm.month_first
    AND (rb.end_month IS NULL OR date(rb.end_month) >= mm.month_first)
    AND (rb.billing_cycle <> 'yearly'
         OR ((CAST(strftime('%Y', mm.month_first) AS INTEGER) - CAST(strftime('%Y', rb.start_month) AS INTEGER)) * 12
             + (CAST(strftime('%m', mm.month_first) AS INTEGER) - CAST(strftime('%m', rb.start_month) AS INTEGER))) % 12 = 0)
  WHERE NOT EXISTS (SELECT 1 FROM invoices iv
                    WHERE iv.recurring_billing_id = rb.id
                      AND date(iv.billing_month) = mm.month_first)
),
ap_now AS (
  -- **未払金は今日で切らない**（BUG-0414 の見直し）。負債は「すでに確定した債務」なので、
  -- 月末付の未払計上も当月の出金予定に含める。ここで切ると**確定済みの支払いが予測から消え**、
  -- 資金ショートの警告が鈍る（保守主義の原則。現預金＝資産の側だけ「今日まで」で切る）
  SELECT
    COALESCE((SELECT SUM(-ob.balance)
              FROM opening_balances ob JOIN accounts a ON a.id = ob.account_id
              -- 科目は account_role で引く（BUG-0434）。コード直書きは導入先の体系が違うと
              -- **無言で 0 円**になり、資金ショート警告が鳴らなくなる方向に倒れる
              WHERE a.account_role = 'accounts_payable'
                AND ob.fiscal_year_id IN (SELECT id FROM cur_yr)), 0)
    +
    COALESCE((SELECT SUM(CASE WHEN l.dc = 'C' THEN l.amount ELSE -l.amount END)
              FROM journal_lines l
              JOIN journal_entries e ON e.id = l.journal_entry_id
              JOIN accounts a ON a.id = l.account_id
              WHERE e.status = 'posted' AND a.account_role = 'accounts_payable'
                AND e.fiscal_year_id IN (SELECT id FROM cur_yr)), 0) AS ap
),
exp_now AS (
  -- **会計期間で絞る**（BUG-0248 ①）。旧実装は `settlement_status = 'approved'` だけで
  -- **日付条件も会計年度条件も無かった**ので、3 年前に承認されたまま精算されずに残っている申請も
  -- 今月の出金として満額計上されていた。放置された古い申請ほど当月の資金を食う、という嘘になる。
  -- 対象は**当年度に計上される経費**（`expense_date` が当年度内）に限る。
  --
  -- **残り（BUG-0248 ②）**: 未払金・承認済み経費を `idx = 0` で当月に一括計上している点は未対応。
  -- 期日の情報を持っているのに使っていないので当月に山ができ、
  -- 「今月は苦しいが来月から楽になる」という実在しない形が毎月出る。
  -- これは `docs/tasks/04`（資金繰り M-2）の「確定債務層」で作り直す前提なので、ここでは触らない
  SELECT COALESCE(SUM(er.amount), 0) AS exp
  FROM expense_request er
  WHERE er.settlement_status = 'approved'
    AND EXISTS (SELECT 1 FROM fiscal_years fy
                 WHERE fy.id IN (SELECT id FROM cur_yr)
                   AND date(er.expense_date) >= date(fy.start_date)
                   AND date(er.expense_date) <= date(fy.end_date))
),
vend_out AS (
  SELECT max(COALESCE(date(v.due_date, 'start of month'),
                      (SELECT month_first FROM months WHERE idx = 0)),
             (SELECT month_first FROM months WHERE idx = 0)) AS m,
         v.amount AS amt
  FROM vendor_invoices v
  WHERE v.status IN ('received', 'accrued')
),
sal_out AS (
  -- **もう払った月の給与は積まない**（BUG-0429）。
  -- 出金源のうち ap_now は残高・vend_out は未払ステータス・exp_now は未仕訳分と、
  -- いずれも「残っている債務」だけを見ている。sal_out だけが monthly_salaries を無条件に積んでいたため、
  -- 給与の定型仕訳（T02: D 給料手当 / C 普通預金・毎月25日）を切った瞬間から
  -- **同じ給与が現預金残高でも減り、当月の出金予定でも減る**——月末まで期末資金が丸 1 ヶ月分過少に出て、
  -- 「⚠ 資金ショート」が誤発報する。
  -- 判定は「その月に、今日までの日付で、給与科目を借方に立てた確定仕訳があるか」。
  -- **今日までで切る**のが要点——cash_now が今日までしか足さないので、
  -- 先日付で起票済みの給与仕訳（例: 今日が10日で25日付）はまだ現預金に反映されていない。
  -- そこを落とすと逆に出金が消える
  SELECT mm.month_first AS m, SUM(ms.cost) AS amt
  FROM months mm
  JOIN fiscal_periods fp ON date(fp.start_date) = mm.month_first
  JOIN monthly_salaries ms ON ms.fiscal_year_id = fp.fiscal_year_id AND ms.period_no = fp.period_no
  WHERE NOT EXISTS (
    SELECT 1 FROM journal_lines l
    JOIN journal_entries e ON e.id = l.journal_entry_id
    JOIN accounts a ON a.id = l.account_id
    WHERE e.status = 'posted' AND l.dc = 'D' AND a.account_role = 'salary_expense'
      AND date(e.entry_date) >= mm.month_first
      AND date(e.entry_date) < date(mm.month_first, '+1 month')
      AND date(e.entry_date) <= date('now', 'localtime'))
  GROUP BY mm.month_first
),
flows AS (
  SELECT mm.idx, mm.month_first,
    COALESCE((SELECT SUM(amt) FROM inv_in WHERE inv_in.m = mm.month_first AND amt > 0), 0)
    + COALESCE((SELECT SUM(amt) FROM rec_in WHERE rec_in.m = mm.month_first), 0) AS cash_in,
    (CASE WHEN mm.idx = 0 THEN (SELECT ap FROM ap_now) + (SELECT exp FROM exp_now) ELSE 0 END)
    + COALESCE((SELECT SUM(amt) FROM vend_out WHERE vend_out.m = mm.month_first), 0)
    + COALESCE((SELECT amt FROM sal_out WHERE sal_out.m = mm.month_first), 0) AS cash_out
  FROM months mm
),
cash_final AS (
  SELECT idx, cash_in, cash_out,
    (SELECT cash FROM cash_now)
      + SUM(cash_in - cash_out) OVER (ORDER BY idx ROWS UNBOUNDED PRECEDING) AS ending
  FROM flows
),
alert_rate AS (
  -- 既定値のフォールバックが要る。マスタの行が消えると rate が NULL になり、
  -- 下の比較が NULL → budget_alert が 0 行 → ポータルの予算警告が行ごと消える。
  -- 「警告が無い＝健全」に見えるので気づけない。同ファイルの PAY_DUE_SOON_DAYS は
  -- 既に COALESCE を持っており、作法が割れていた（既定 80% は投入値と同じ）
  SELECT COALESCE((SELECT amount FROM v_system_threshold_current WHERE code = 'BUDGET_ALERT_RATE'), 80) AS rate
),
budget_elapsed AS (
  -- 経過月数。**予実対比（BudgetVsActual）と同一の定義に保つ**（BUG-0433・ADR-0060）。
  -- 月次期間が 1 つも無い年度は 12 とみなす（0 だと警告が永久に鳴らない）
  SELECT CASE
    WHEN (SELECT COUNT(*) FROM fiscal_periods WHERE fiscal_year_id IN (SELECT id FROM cur_yr)) = 0 THEN 12
    ELSE (SELECT COUNT(*) FROM fiscal_periods
           WHERE fiscal_year_id IN (SELECT id FROM cur_yr)
             AND date(start_date) <= date('now', 'localtime'))
  END AS n
),
budget_alert AS (
  -- 判定の分母は**経過月までの予算**（年間予算ではない）。年間で割ると年度末近くまで ⚠ が鳴らない
  SELECT b.department_id AS department_id
  FROM (SELECT department_id, account_id, SUM(amount) AS budget
        FROM budget_lines
        WHERE fiscal_year_id IN (SELECT id FROM cur_yr)
          AND period_no <= (SELECT n FROM budget_elapsed)
        GROUP BY department_id, account_id) b
  LEFT JOIN (SELECT l.department_id, l.account_id,
                    SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS actual
             FROM journal_lines l
             JOIN journal_entries e ON e.id = l.journal_entry_id
             JOIN accounts a ON a.id = l.account_id
             WHERE e.status = 'posted'
               AND e.fiscal_year_id IN (SELECT id FROM cur_yr)
               AND a.account_type = 'expense'
               -- 予実対比（BudgetVsActual）と**同一条件**に保つ（BUG-0371）。
               -- 片方だけ直すとポータルの件数と画面の警告行数が黙ってずれる（ADR-0060）
               AND COALESCE(e.source_type, '') NOT IN ('wip', 'wip_reversal')
             GROUP BY l.department_id, l.account_id) act
    ON act.department_id IS b.department_id AND act.account_id = b.account_id
  WHERE b.budget > 0
    AND COALESCE(act.actual, 0) * 100 / b.budget >= (SELECT rate FROM alert_rate)
  GROUP BY b.department_id
)
SELECT
  (SELECT count(*) FROM pay WHERE days_left < 0) AS pay_overdue,
  (SELECT count(*) FROM pay
    WHERE days_left >= 0 AND days_left <= (SELECT days FROM threshold)) AS pay_soon,
  (SELECT c FROM recv) AS receivable_overdue,
  -- 資金ショート（期末資金がマイナス）の月数。**危険水域とは混ぜない**——
  -- 混ぜるとポータルが黒字の月まで「ショート」と表示し、予測画面の「△ 危険水域」と重大度が食い違う
  (SELECT count(*) FROM cash_final WHERE ending < 0) AS cash_alert_months,
  -- 危険水域（マイナスではないが閾値を下回る）の月数（BUG-0249）。
  -- **CashFlowForecastData.Query.sql の alert_mark と同じ条件にすること**（この 2 本は複製・BUG-0257）。
  -- 閾値が 0／未設定なら 0 件になり、従来どおり「マイナスのときだけ」の挙動に戻る
  (SELECT count(*) FROM cash_final
    WHERE ending >= 0
      AND COALESCE((SELECT amount FROM v_system_threshold_current WHERE code = 'CASH_ALERT_BALANCE'), 0) > 0
      AND ending < (SELECT amount FROM v_system_threshold_current WHERE code = 'CASH_ALERT_BALANCE')
  ) AS cash_warn_months,
  (SELECT count(*) FROM budget_alert) AS budget_alert_depts,
  -- 警告が出ている部門の ID リスト（カンマ区切り。非経理ユーザーの「自部門のみ表示」判定用・2026-08-06）
  (SELECT COALESCE(group_concat(department_id), '') FROM budget_alert) AS budget_alert_dept_ids,
  (SELECT days FROM threshold) AS due_soon_days,
  -- 資金繰り予測の**起点が作れているか**（BUG-0246）。0 なら期首資金は信用できない。
  --   ① 今日を含む会計年度が無い → cur_yr が空 → 期首資金 0 円・未払金 0 円
  --   ② 当年度の期首残高がまだ無い（前期の繰越を走らせていない。期首から 2〜3 ヶ月ふつうに続く）
  --      → 前期末の現預金がまるごと欠落する
  -- どちらも `ending < 0` が全月で立ち、「⚠ 資金ショート予測: 4 ヶ月」と叫ぶ。
  -- **一度でも空振りすると、本当にショートする月が来てもアラートが信用されない。**
  -- 前期そのものが無い（初年度）なら期首残高が無いのは正常なので、そこは 1 とみなす
  CASE
    WHEN NOT EXISTS (SELECT 1 FROM cur_yr) THEN 0
    WHEN EXISTS (SELECT 1 FROM opening_balances ob
                  WHERE ob.fiscal_year_id IN (SELECT id FROM cur_yr)) THEN 1
    WHEN NOT EXISTS (SELECT 1 FROM fiscal_years fy
                      WHERE date(fy.end_date) < (SELECT date(start_date) FROM fiscal_years
                                                  WHERE id IN (SELECT id FROM cur_yr))) THEN 1
    ELSE 0
  END AS cash_base_ok
