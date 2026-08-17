-- 合計残高試算表
--
-- 【期間が空のとき】日付（自）／（至）を消して検索されたら、当年度（＝入っている方の日付、
--   どちらも空なら今日を含む会計年度）の期首／期末で補う（BUG-0274）。
--   空を「全期間」と解釈すると期首繰越が一切乗らず、貸借の崩れた表が正しい顔で出てしまう。
--   GeneralLedger.Query.sql / ProfitLoss.Query.sql と同じ「SQL 側で当年度へフォールバックする」流儀。
-- 【最下行】合計（貸借検算）行。繰越・期末は借方−貸方の純額なので、貸借が合っていれば 0 になる（BUG-0276）。
WITH fy AS (
  SELECT id, start_date, end_date FROM fiscal_years
  WHERE date(start_date) <= date(COALESCE(@date_from, @date_to, date('now', 'localtime')))
    AND date(end_date)   >= date(COALESCE(@date_from, @date_to, date('now', 'localtime')))
),
rng AS (
  SELECT
    COALESCE(date(@date_from), (SELECT date(start_date) FROM fy), '0001-01-01') AS d_from,
    COALESCE(date(@date_to),   (SELECT date(end_date)   FROM fy), '9999-12-31') AS d_to
),
ob AS (
  SELECT account_id, SUM(balance) AS bal
  FROM opening_balances
  WHERE fiscal_year_id IN (SELECT id FROM fy)
  GROUP BY account_id
),
pre AS (
  SELECT l.account_id, SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS dmc
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  WHERE e.status = 'posted'
    AND date(e.entry_date) >= (SELECT date(start_date) FROM fy)
    AND date(e.entry_date) < (SELECT d_from FROM rng)
  GROUP BY l.account_id
),
sums AS (
  SELECT
    l.account_id,
    SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE 0 END) AS dsum,
    SUM(CASE WHEN l.dc = 'C' THEN l.amount ELSE 0 END) AS csum
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  WHERE e.status = 'posted'
    AND date(e.entry_date) >= (SELECT d_from FROM rng)
    AND date(e.entry_date) <= (SELECT d_to FROM rng)
  GROUP BY l.account_id
),
merged AS (
  SELECT
    a.id,
    a.code,
    a.name,
    a.dc_normal,
    COALESCE(o.bal, 0) + COALESCE(p.dmc, 0) AS open_dmc,
    COALESCE(s.dsum, 0) AS dsum,
    COALESCE(s.csum, 0) AS csum
  FROM accounts a
  LEFT JOIN ob o ON o.account_id = a.id
  LEFT JOIN pre p ON p.account_id = a.id
  LEFT JOIN sums s ON s.account_id = a.id
  WHERE COALESCE(o.bal, 0) <> 0 OR COALESCE(p.dmc, 0) <> 0
     OR COALESCE(s.dsum, 0) <> 0 OR COALESCE(s.csum, 0) <> 0
)
SELECT * FROM (
  SELECT
    m.id AS account_id_raw,   -- 元帳へのドリルダウン用（ADR-0065）。表示せず DrillButton の遷移先解決に使う
    '元帳' AS drill_label,    -- リンク文字。合計行は空にしてリンクを消す（IsVisible はリスト内のアンカーに効かない）
    m.code AS account_code,
    m.name AS account_name,
    CASE WHEN m.dc_normal = 'D' THEN m.open_dmc ELSE -m.open_dmc END AS opening_balance,
    m.dsum AS debit_total,
    m.csum AS credit_total,
    CASE WHEN m.dc_normal = 'D' THEN m.open_dmc + m.dsum - m.csum
         ELSE -m.open_dmc + m.csum - m.dsum END AS balance
  FROM merged m
  UNION ALL
  -- 合計（貸借検算）行。繰越・残高は借方−貸方の純額（＝貸借一致なら 0）、借方合計と貸方合計は一致するのが正。
  SELECT
    NULL AS account_id_raw,
    '' AS drill_label,
    '' AS account_code,
    '合計（貸借検算）' AS account_name,
    SUM(m2.open_dmc) AS opening_balance,
    SUM(m2.dsum) AS debit_total,
    SUM(m2.csum) AS credit_total,
    SUM(m2.open_dmc + m2.dsum - m2.csum) AS balance
  FROM merged m2
)
ORDER BY CASE WHEN account_code = '' THEN 1 ELSE 0 END, account_code
