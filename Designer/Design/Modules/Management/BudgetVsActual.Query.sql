-- 予実対比: 部門×費用科目ごとの 年間予算 / 経過月までの予算 / 実績 / 差異 / 消化率 / ペース / 警告
-- 警告率は system_thresholds.BUDGET_ALERT_RATE を参照（ハードコード禁止）
-- 部門未設定の実績（journal_lines.department_id IS NULL）も「(部門未設定)」行で可視化する
--
-- **警告は「年間予算」ではなく「経過月までの予算」で判定する**（BUG-0433）。
-- 年間予算と期中累計実績を割ると、第2月末に月次予算の 3 倍を使った部門でも消化率は 50%（=6/12ヶ月分）に
-- 届かず警告が出ない。⚠ が鳴るのは実質的に年度末近くだけで、
-- 「早期に気づいて手を打つ」という予実対比の目的が成立しない。
-- budget_lines は月次で持っているので、経過月ぶんを足せば正しい分母が作れる（年額÷12 の按分は要らない）。
-- 年間消化率も**併記する**——「年間ではまだ 50% だが、ペースは 300%」の両方が要る情報だから
WITH elapsed AS (
  -- 経過月数（＝開始日が今日以前の月次期間の数）。予算の消化ペースを測る分母を作るために要る。
  -- **月次期間が 1 つも無い年度は 12 とみなす**——0 にすると「経過月までの予算 0 円」になり、
  -- 警告が**永久に鳴らない**方向に倒れる。期間の作り忘れで警告が消えるのは最悪の壊れ方
  SELECT CASE
    WHEN (SELECT COUNT(*) FROM fiscal_periods WHERE fiscal_year_id = @fiscal_year_id) = 0 THEN 12
    ELSE (SELECT COUNT(*) FROM fiscal_periods
           WHERE fiscal_year_id = @fiscal_year_id AND date(start_date) <= date('now', 'localtime'))
  END AS n
),
b AS (
  SELECT department_id, account_id, SUM(amount) AS budget
  FROM budget_lines
  WHERE fiscal_year_id = @fiscal_year_id
  GROUP BY department_id, account_id
),
btd AS (
  -- 経過月までの予算累計
  SELECT department_id, account_id, SUM(amount) AS budget
  FROM budget_lines
  WHERE fiscal_year_id = @fiscal_year_id AND period_no <= (SELECT n FROM elapsed)
  GROUP BY department_id, account_id
),
act AS (
  SELECT l.department_id, l.account_id,
         SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS actual
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  JOIN accounts a ON a.id = l.account_id
  WHERE e.status = 'posted'
    AND e.fiscal_year_id = @fiscal_year_id
    AND a.account_type = 'expense'
    -- 仕掛品の期末振替・翌期首の振戻は実績に混ぜない（BUG-0371）。
    -- 予実対比が見たいのは「部門がいくら使ったか」で、仕掛品は**決算の付け替え**である。
    -- 混ぜると予算 0 の「仕掛品振替高」に大きなマイナス実績が並び、部門の消化率も歪む
    AND COALESCE(e.source_type, '') NOT IN ('wip', 'wip_reversal')
  GROUP BY l.department_id, l.account_id
),
keys AS (
  SELECT department_id, account_id FROM b
  UNION
  SELECT department_id, account_id FROM act
),
alert_rate AS (
  -- 行が無ければ 80%（ポータルのアラートと同じ既定。欠けたら黙って警告が消える、を作らない）
  SELECT COALESCE((SELECT amount FROM v_system_threshold_current WHERE code = 'BUDGET_ALERT_RATE'), 80) AS rate
)
SELECT
  COALESCE(d.name, '(部門未設定)') AS department_name,
  a.code AS account_code,
  a.name AS account_name,
  COALESCE(b.budget, 0) AS budget_amount,
  COALESCE(btd.budget, 0) AS budget_todate,
  COALESCE(act.actual, 0) AS actual_amount,
  COALESCE(b.budget, 0) - COALESCE(act.actual, 0) AS diff_amount,
  CASE WHEN COALESCE(b.budget, 0) > 0
       THEN COALESCE(act.actual, 0) * 100 / b.budget
       ELSE NULL END AS usage_rate,
  CASE WHEN COALESCE(btd.budget, 0) > 0
       THEN COALESCE(act.actual, 0) * 100 / btd.budget
       ELSE NULL END AS pace_rate,
  CASE WHEN COALESCE(btd.budget, 0) > 0
        AND COALESCE(act.actual, 0) * 100 / btd.budget >= (SELECT rate FROM alert_rate)
       THEN '⚠ 予算警告' ELSE '' END AS alert_mark
FROM keys k
JOIN accounts a ON a.id = k.account_id
LEFT JOIN departments d ON d.id = k.department_id
LEFT JOIN b ON b.department_id IS k.department_id AND b.account_id = k.account_id
LEFT JOIN btd ON btd.department_id IS k.department_id AND btd.account_id = k.account_id
LEFT JOIN act ON act.department_id IS k.department_id AND act.account_id = k.account_id
WHERE (@department_id IS NULL OR k.department_id = @department_id)
ORDER BY COALESCE(d.display_order, 9999), a.code
