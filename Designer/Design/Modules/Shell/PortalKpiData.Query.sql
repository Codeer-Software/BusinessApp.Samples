-- ホーム KPI（経理向け・1行）: 現預金 / 売掛金 / 買掛金 / 当月売上高 / 当月費用 / 当月利益
-- Home.mod.cs から ModuleSearcher で読み取り、ラベルに整形表示する（画面への直接埋め込みはしない）。
-- 「当月」= date('now', 'localtime') を含む月次期間（既存帳票と同じ規約。date() 同士の比較なので境界日の罠は無い）
WITH cur AS (
  SELECT id FROM fiscal_years
  WHERE date(start_date) <= date('now', 'localtime') AND date(end_date) >= date('now', 'localtime')
),
per AS (
  SELECT start_date, end_date FROM fiscal_periods
  WHERE fiscal_year_id IN (SELECT id FROM cur)
    AND date(start_date) <= date('now', 'localtime') AND date(end_date) >= date('now', 'localtime')
),
bs AS (
  SELECT a.code, a.is_cash_equivalent, a.account_role,
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              WHERE ob.account_id = a.id AND ob.fiscal_year_id IN (SELECT id FROM cur)), 0)
    + COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
                FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id
                WHERE e.status = 'posted' AND l.account_id = a.id
                  -- **今日で切るのは現預金だけ**（BUG-0414）。
                  --   資産（現預金）＝「いま実際に残っている額」なので、先日付の支払仕訳を引いてはいけない
                  --   売掛金・買掛金＝「すでに確定した債権債務」なので、月末付の計上も含める。
                  --     定期請求の売上は月末付で起票するのが通常運用で、ここで切ると
                  --     **売掛残高一覧と食い違う**（保守主義の原則: 資産は控えめに、負債は漏らさず）
                  AND (a.is_cash_equivalent <> 1
                       OR date(e.entry_date) <= date('now', 'localtime'))
                  AND e.fiscal_year_id IN (SELECT id FROM cur)), 0) AS bal
  FROM accounts a
),
pl AS (
  SELECT a.account_type,
         SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS dmc
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  JOIN accounts a ON a.id = l.account_id
  JOIN account_categories c ON c.id = a.category_id
  WHERE e.status = 'posted'
    AND c.statement = 'PL'
    AND date(e.entry_date) >= (SELECT date(start_date) FROM per)
    AND date(e.entry_date) <= (SELECT date(end_date) FROM per)
  GROUP BY a.account_type
)
SELECT
  -- **科目はフラグ／役割で引く。コードで引かない**（BUG-0439）。
  -- 同じファイルの :25 は既に is_cash_equivalent で「今日で切る対象」を決めているのに、
  -- ここだけコード範囲の直書きだった＝「今日で切る集合」と「現預金として足す集合」が別定義。
  -- is_cash_equivalent の科目を 1100 以降（例: 別口座 1150）に作ると、
  -- **今日で切る対象にはなるのに合計には入らない**——BUG-0414 で 3 画面を揃えた整合が
  -- 科目を 1 本足しただけで静かに崩れる。
  -- 売掛金・買掛金も同様に account_role（ddl/819）で引く
  COALESCE((SELECT SUM(bal) FROM bs WHERE is_cash_equivalent = 1), 0) AS cash_balance,
  COALESCE((SELECT bal FROM bs WHERE account_role = 'accounts_receivable'), 0) AS ar_balance,
  COALESCE((SELECT -bal FROM bs WHERE account_role = 'trade_payable'), 0) AS ap_balance,
  COALESCE((SELECT -dmc FROM pl WHERE account_type = 'revenue'), 0) AS month_sales,
  COALESCE((SELECT SUM(dmc) FROM pl WHERE account_type <> 'revenue'), 0) AS month_expense,
  COALESCE((SELECT -dmc FROM pl WHERE account_type = 'revenue'), 0)
    - COALESCE((SELECT SUM(dmc) FROM pl WHERE account_type <> 'revenue'), 0) AS month_profit
