-- 残高照合（D-2 / ADR-0012）: 銀行明細の残高 vs 帳簿（総勘定元帳）の残高を日次で突合
-- @bank_account_id: 対象口座（NULL=全口座）
-- 行 = 口座×明細日付（明細が存在する日のみ）。
--   stmt_balance  = その日の最終明細（同日内は id 最大）の残高列
--   ledger_balance = その日が属する会計年度の期首残高 ＋ 年度期首からその日までの posted 仕訳Σ(D-C)
--   pending_count = その日以前の未起票（pending）明細の累計件数（＝差異の原因候補）
WITH tgt AS (
  SELECT id, name, ledger_account_id
  FROM bank_accounts
  WHERE (@bank_account_id IS NULL OR id = @bank_account_id)
),
days AS (
  SELECT l.bank_account_id AS ba_id, date(l.line_date) AS d
  FROM bank_statement_lines l
  JOIN tgt t ON t.id = l.bank_account_id
  WHERE l.status <> 'preview'  -- プレビュー（未登録）の明細は照合対象にしない
  GROUP BY l.bank_account_id, date(l.line_date)
),
raw AS (
  SELECT
    t.name AS acct_name,
    dy.d AS line_date,
    (SELECT b.balance FROM bank_statement_lines b
      WHERE b.bank_account_id = dy.ba_id AND date(b.line_date) = dy.d
        AND b.status <> 'preview'
      ORDER BY b.id DESC LIMIT 1) AS stmt_balance,
    COALESCE((SELECT SUM(o.balance) FROM opening_balances o
      WHERE o.account_id = t.ledger_account_id
        AND o.fiscal_year_id = (SELECT y.id FROM fiscal_years y
                                WHERE date(y.start_date) <= dy.d AND date(y.end_date) >= dy.d)), 0)
    + COALESCE((SELECT SUM(CASE WHEN jl.dc = 'D' THEN jl.amount ELSE -jl.amount END)
      FROM journal_lines jl
      JOIN journal_entries je ON je.id = jl.journal_entry_id
      WHERE jl.account_id = t.ledger_account_id
        AND je.status = 'posted'
        AND date(je.entry_date) >= (SELECT date(y.start_date) FROM fiscal_years y
                                    WHERE date(y.start_date) <= dy.d AND date(y.end_date) >= dy.d)
        AND date(je.entry_date) <= dy.d), 0) AS ledger_balance,
    (SELECT COUNT(*) FROM bank_statement_lines p
      WHERE p.bank_account_id = dy.ba_id AND p.status = 'pending'
        AND date(p.line_date) <= dy.d) AS pending_count
  FROM days dy
  JOIN tgt t ON t.id = dy.ba_id
)
SELECT
  acct_name,
  line_date,
  stmt_balance,
  ledger_balance,
  CASE WHEN stmt_balance IS NULL THEN NULL ELSE stmt_balance - ledger_balance END AS diff,
  pending_count,
  CASE WHEN stmt_balance IS NULL THEN '－ 明細残高なし'
       WHEN stmt_balance = ledger_balance THEN '○ 一致'
       ELSE '⚠ 差異' END AS verdict
FROM raw
ORDER BY acct_name, line_date
