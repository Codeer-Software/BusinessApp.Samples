-- 資金繰り予測（当月含む今後4ヶ月）: 期首資金 / 入金予定 / 出金予定 / 期末資金 / 警告
-- 「今日」は既存帳票（売掛残高・元帳の既定値）に合わせ date('now', 'localtime') を使用。
-- 入金: 未回収請求書（期日月、期日超過は当月）＋ 定期請求の未生成将来分（対象月の翌月末入金）
--       未回収額の控除は消込済みの入金だけ。発行時に自動作成される未確定の入金予定（ADR-0032）を
--       引くと全請求書が残額 0 になり、入金予定が構造的に 0 円になる（改善候補 A-2）
-- 出金: 未払金残高（当月）＋ 承認済み未仕訳の経費（当月）＋ 月次人件費（各月）
--       ＋ 仕入先請求書の未払い分（D-6 連動。支払期限月・期限超過/期限なしは当月。
--         received/accrued を請求書ベースで拾うため買掛金 GL 残高は加算しない=二重計上回避）
WITH RECURSIVE months(idx, month_first) AS (
  SELECT 0, date('now', 'localtime', 'start of month')
  UNION ALL
  SELECT idx + 1, date(month_first, '+1 month') FROM months WHERE idx < 3
),
-- 【前月分の入金を落とさない・BUG-0329】定期請求の入金は「対象月の翌月末」に立てる。
--   対象月のリストを当月から始めると、**前月分の請求に対する当月の入金が生成されない**——
--   当月（idx=0）の入金見込みから定期請求が構造的に消える。
--   対象月だけ 1 ヶ月前から回し、着地月が窓の外に出た分は `flows` の突き合わせで自然に落ちる
rec_months(idx, month_first) AS (
  SELECT -1, date('now', 'localtime', 'start of month', '-1 month')
  UNION ALL
  SELECT idx + 1, date(month_first, '+1 month') FROM rec_months WHERE idx < 3
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
  -- **支払期限が無い請求も入金見込みに数える**（BUG-0139。BUG-0254 の売掛版）。
  -- 旧実装は `AND i.due_date IS NOT NULL` で落としており、期限を空にした請求書の債権が
  -- 資金繰り予測の入金から**丸ごと消えていた**（売掛残には残るのに入金は立たない＝予測が過小）。
  -- 同じファイルの買掛側 `vend_out` は BUG-0254 で `COALESCE` 済みで、**売掛側だけが取り残されていた**。
  -- 期限なしは買掛側と同じく**当月扱い**にする（外側の max(..., 当月) が下限を当月に丸めるので、
  -- 過去日の期限も当月に寄る＝「まだ入っていない金は当月以降に入る」という予測の約束と揃う）
  SELECT max(COALESCE(date(i.due_date, 'start of month'),
                      (SELECT month_first FROM months WHERE idx = 0)),
             (SELECT month_first FROM months WHERE idx = 0)) AS m,
         COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0) - COALESCE(rc.received, 0) AS amt
  FROM invoices i
  LEFT JOIN v_invoice_received rc
    ON rc.invoice_id = i.id
  WHERE i.status IN ('issued', 'partial')
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
  FROM rec_months mm
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
  -- **科目は `accounts.account_role` で引く**（BUG-0434）。`a.code = '2020'` の直書きだと、
  -- 導入先が別の科目コード体系を使った瞬間に**無言で 0 円**になり、当月の出金予定が消える。
  -- スクリプト側（ExpenseRequestAccounting 等）は「未払金の科目がありません」とトーストで落ちるのに、
  -- SQL は黙って 0 を返すぶん危ない。同じファイルの cash_now は既に is_cash_equivalent に是正済みで、
  -- ここだけ作法が割れていた
  SELECT
    COALESCE((SELECT SUM(-ob.balance)
              FROM opening_balances ob JOIN accounts a ON a.id = ob.account_id
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
final AS (
  SELECT idx, strftime('%Y-%m', month_first) AS month_label, cash_in, cash_out,
    (SELECT cash FROM cash_now)
      + SUM(cash_in - cash_out) OVER (ORDER BY idx ROWS UNBOUNDED PRECEDING) AS ending
  FROM flows
),
-- 危険水域の閾値はマスタから引く（BUG-0249）。行が無い／0 なら従来どおり「マイナスのときだけ」になる
alert_limit AS (
  SELECT COALESCE((SELECT amount FROM v_system_threshold_current WHERE code = 'CASH_ALERT_BALANCE'), 0) AS v
)
SELECT
  idx AS sort_no,
  month_label,
  ending - (cash_in - cash_out) AS opening_cash,
  cash_in,
  cash_out,
  ending AS ending_cash,
  -- 2 段階にする。ショートしてから鳴る警告は「手を打つ」ために遅すぎる
  CASE
    WHEN ending < 0 THEN '⚠ 資金ショート'
    WHEN (SELECT v FROM alert_limit) > 0 AND ending < (SELECT v FROM alert_limit) THEN '△ 資金が危険水域'
    ELSE ''
  END AS alert_mark
FROM final
ORDER BY idx
