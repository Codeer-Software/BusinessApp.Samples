-- 総勘定元帳（＋補助元帳: 補助科目・部門・案件の任意絞り込み）
-- @sub_account_id / @department_id / @project_id はいずれも NULL=絞り込みなし（従来の総勘定元帳）。
--
-- 【1 行目 = 繰越行】必ず 1 行出す（BUG-0275）。残高列は累計なので、起点が印字されていないと
--   元帳単体で検算できず、税理士・監査法人への提出物として成立しない（市販ソフトはいずれも印字する）。
--   繰越行の残高＝明細行の累計の起点（下の base.dmc）と同じ値なので、両者は必ず一致する。
--   借方残の科目は借方欄に、貸方残の科目は貸方欄に出す（accounts.dc_normal）。
--
-- 【残高の意味】期首残高＋期中累計。繰越行の摘要は「前期繰越」
--              （@date_from が期首より後なら期中の途中からの繰越なので「繰越」）。
--              補助科目・部門で絞ったときも期首残高を含める——翌期繰越が (科目 × 補助科目 × 部門) の
--              粒度で期首残高を作るようになったため（BUG-0092 の修正）。
--              ただし**案件（@project_id）で絞ったときだけは期首残高を含めない**。
--              opening_balances に案件の次元が無く、期首を案件へ割り当てる術がないためである。
--              このとき繰越行の摘要を「繰越（期首残高を含まず）」とし、0 起算であることを帳簿上で明示する
--              （行を消すと起点が消えて元の不具合に戻り、0 と書くと期首が 0 だという嘘になる）。
--              なお導入初年度の期首残高は手入力で投入するため補助科目・部門を持たない。
--              その年度を補助科目・部門で絞ると期首は 0 起算になる（嘘ではなく「その次元の期首が無い」）。
--
-- 【期間が空のとき】日付（自）／（至）を消して検索されたら、当年度（＝入っている方の日付、
--   どちらも空なら今日を含む会計年度）の期首／期末で補う（BUG-0285）。
--   （至）を「無期限」と解釈すると翌期の仕訳まで累計残高に載り、同じ操作で帳票ごとに違う期間が出る。
--   TrialBalance.Query.sql / CashBook.Query.sql と同じ fy / rng の流儀。
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
  -- 元帳は単一科目が前提（@account_id 必須）なので、表示符号はここで 1 回だけ決める
  SELECT CASE WHEN dc_normal = 'D' THEN 1 ELSE -1 END AS dc_sign,
         CASE WHEN account_type IN ('expense', 'revenue') THEN 1 ELSE 0 END AS is_pl
  FROM accounts WHERE id = @account_id
),
base AS (
  SELECT
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              WHERE ob.fiscal_year_id IN (SELECT id FROM fy) AND ob.account_id = @account_id
                AND (@sub_account_id IS NULL OR ob.sub_account_id = @sub_account_id)
                AND (@department_id IS NULL OR ob.department_id = @department_id)), 0)
    * (CASE WHEN @project_id IS NULL THEN 1 ELSE 0 END)
    +
    COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
              FROM journal_lines l
              JOIN journal_entries e ON e.id = l.journal_entry_id
              WHERE e.status = 'posted'
                AND l.account_id = @account_id
                AND (@sub_account_id IS NULL OR l.sub_account_id = @sub_account_id)
                AND (@department_id IS NULL OR l.department_id = @department_id)
                AND (@project_id IS NULL OR l.project_id = @project_id)
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
  NULL AS entry_id,
  '' AS open_label,      -- 伝票が無い行なので「開く」リンクを出さない（AnchorTag は TitleVariable が空だと消える）
  c.carry_date AS entry_date,
  NULL AS journal_no,
  NULL AS line_no,
  '' AS counter_account_name,
  CASE
    WHEN @project_id IS NOT NULL THEN '繰越（期首残高を含まず）'
    WHEN date(c.carry_date) <= (SELECT date(start_date) FROM fy) THEN '前期繰越'
    ELSE '繰越'
  END AS line_description,
  CASE WHEN (SELECT dc_sign FROM acct) = 1 THEN c.carry_balance END AS debit_amount,
  CASE WHEN (SELECT dc_sign FROM acct) = -1 THEN c.carry_balance END AS credit_amount,
  c.carry_balance AS balance
FROM carry c
WHERE @account_id IS NOT NULL
  AND c.carry_date IS NOT NULL
  -- 損益科目は期末に締め切られるので「前期繰越」という概念が無い（弥生・奉行の元帳も出さない）。
  -- 期首から見ているときは値も必ず 0 なので、行ごと落とす。
  -- ただし**期中から見ているときの「繰越」はその期の期首からの累計**＝意味があるので残す
  AND NOT ((SELECT is_pl FROM acct) = 1
           AND date(c.carry_date) <= (SELECT date(start_date) FROM fy))

UNION ALL

-- 明細行
SELECT
  1 AS sort_seq,
  e.id AS entry_id,   -- 伝票へのドリルダウン用（ADR-0065）。表示はせず OpenAnchor の IdVariable が読む
  '開く' AS open_label,
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
  CASE WHEN l.dc = 'D' THEN l.amount END AS debit_amount,
  CASE WHEN l.dc = 'C' THEN l.amount END AS credit_amount,
  (SELECT dc_sign FROM acct)
  * ((SELECT dmc FROM base)
     + SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
         OVER (ORDER BY date(e.entry_date), e.journal_no, l.line_no
               ROWS UNBOUNDED PRECEDING)) AS balance
FROM journal_lines l
JOIN journal_entries e ON e.id = l.journal_entry_id
WHERE e.status = 'posted'
  AND l.account_id = @account_id
  AND (@sub_account_id IS NULL OR l.sub_account_id = @sub_account_id)
  AND (@department_id IS NULL OR l.department_id = @department_id)
  AND (@project_id IS NULL OR l.project_id = @project_id)
  AND date(e.entry_date) >= (SELECT d_from FROM rng)
  AND date(e.entry_date) <= (SELECT d_to FROM rng)

-- 複合 SELECT の ORDER BY は先頭 SELECT の出力列名で指定する（式は使えない。ProfitLoss.Query.sql と同じ流儀）
ORDER BY sort_seq, entry_date, journal_no, line_no
