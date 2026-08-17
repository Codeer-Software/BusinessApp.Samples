-- 出納帳: 対象の現預金科目 (@account_code) の入出金明細と累計残高
-- 正典: GeneralLedger.Query.sql（繰越行・期首残高・window 関数の累計・諸口判定・期間クランプ）
-- 現預金は借方増: 入金=D 行 / 出金=C 行
--
-- 【1 行目 = 繰越行】必ず 1 行出す（BUG-0286）。残高列は累計なので、起点が印字されていないと
--   金庫・通帳の実残と突き合わせられない（現預金の出納帳こそ繰越行が必須の帳票）。
--   繰越行の残高＝明細行の累計の起点（下の base.dmc）と同じ値なので、両者は必ず一致する。
--   摘要は @date_from が期首（またはそれ以前）なら「前期繰越」、期中の途中からなら「繰越」。
--   出納帳は補助科目・部門で絞らないので、元帳のような「期首残高を含まず」の分岐は無い。
-- 【期間が空のとき】日付（自）／（至）を消して検索されたら、当年度（＝入っている方の日付、
--   どちらも空なら今日を含む会計年度）の期首／期末で補う（BUG-0285）。
--   （至）を「無期限」と解釈すると翌期の仕訳まで累計残高に載り、同じ操作で帳票ごとに違う期間が出る。
--   TrialBalance.Query.sql / GeneralLedger.Query.sql と同じ fy / rng の流儀。
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
acct AS (
  -- 出納帳は単一科目が前提（@account_code 必須）なので、表示符号はここで 1 回だけ決める
  SELECT id, CASE WHEN dc_normal = 'D' THEN 1 ELSE -1 END AS dc_sign
  FROM accounts WHERE code = @account_code
),
base AS (
  SELECT
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              WHERE ob.fiscal_year_id IN (SELECT id FROM fy)
                AND ob.account_id IN (SELECT id FROM acct)), 0)
    +
    COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
              FROM journal_lines l
              JOIN journal_entries e ON e.id = l.journal_entry_id
              WHERE e.status = 'posted'
                AND l.account_id IN (SELECT id FROM acct)
                AND date(e.entry_date) >= (SELECT date(start_date) FROM fy)
                AND date(e.entry_date) <  (SELECT d_from FROM rng)), 0) AS dmc
),
carry AS (
  SELECT
    -- 明細行の entry_date と同じ形（yyyy-MM-dd HH:mm:ss）に揃える。並び順も自然に先頭になる
    datetime(COALESCE(@date_from, (SELECT start_date FROM fy))) AS carry_date,
    (SELECT dc_sign FROM acct) * (SELECT dmc FROM base) AS carry_balance
)

-- 繰越行（必ず 1 行目）
SELECT
  0 AS sort_seq,
  c.carry_date AS entry_date,
  NULL AS journal_no,
  NULL AS line_no,
  '' AS counter_account_name,
  CASE WHEN date(c.carry_date) <= (SELECT date(start_date) FROM fy) THEN '前期繰越'
       ELSE '繰越' END AS line_description,
  CASE WHEN (SELECT dc_sign FROM acct) =  1 THEN c.carry_balance END AS deposit_amount,
  CASE WHEN (SELECT dc_sign FROM acct) = -1 THEN c.carry_balance END AS withdrawal_amount,
  c.carry_balance AS balance
FROM carry c
WHERE EXISTS (SELECT 1 FROM acct)
  AND c.carry_date IS NOT NULL

UNION ALL

-- 明細行
SELECT
  1 AS sort_seq,
  e.entry_date,
  e.journal_no,
  l.line_no,
  CASE
    WHEN (SELECT COUNT(*) FROM journal_lines x WHERE x.journal_entry_id = e.id AND x.id <> l.id) = 1
      THEN (SELECT a2.name FROM journal_lines x JOIN accounts a2 ON a2.id = x.account_id
            WHERE x.journal_entry_id = e.id AND x.id <> l.id)
    ELSE '諸口'
  END AS counter_account_name,
  COALESCE(l.description, e.description, '') AS line_description,
  CASE WHEN l.dc = 'D' THEN l.amount END AS deposit_amount,
  CASE WHEN l.dc = 'C' THEN l.amount END AS withdrawal_amount,
  (SELECT dc_sign FROM acct)
  * ((SELECT dmc FROM base)
     + SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
         OVER (ORDER BY date(e.entry_date), e.journal_no, l.line_no
               ROWS UNBOUNDED PRECEDING)) AS balance
FROM journal_lines l
JOIN journal_entries e ON e.id = l.journal_entry_id
WHERE e.status = 'posted'
  AND l.account_id IN (SELECT id FROM acct)
  AND date(e.entry_date) >= (SELECT d_from FROM rng)
  AND date(e.entry_date) <= (SELECT d_to FROM rng)

-- 複合 SELECT の ORDER BY は先頭 SELECT の出力列名で指定する（式は使えない。GeneralLedger と同じ流儀）
ORDER BY sort_seq, entry_date, journal_no, line_no
