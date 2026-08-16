-- 何を保証するか: 繰越利益剰余金(3100) の翌期首残高が
--                 「前期首 + 前期の 3100 への直接仕訳 + 前期の当期純利益」と一致すること。
--                 本アプリは損益振替仕訳を作らず、翌期繰越 SQL が 3100 に当期純利益を織り込む方式
--                 （decisions/0006）なので、この式が「当期純利益 = 繰越利益剰余金の増分」の実体。
-- 違反時の意味: 当期純利益が純資産に正しく積まれていない。BS の貸借が合わない、
--               または株主資本等変動計算書と BS が食い違う。
-- 出典: docs/04_会計ドメイン設計.md §6 年次決算 2.（損益 → 繰越利益剰余金）
--       Modules/Accounting/FiscalYear.CarryOverSql.sql の CASE WHEN a.code = '3100' 分岐
--       Modules/FinancialReports/EquityChange.Query.sql（当期純利益は 3100 の行にのみ差し込む）
-- 符号: balance は 借方残 = 正 / 貸方残 = 負。当期純利益（黒字）は 3100 の貸方残を増やす
--       ＝ 符号付き値としてはマイナス方向に動く。列「当期純利益」は読みやすさのため符号反転して表示。
-- 備考: B03 と同じく、前後どちらかに期首残高が無い年度の組は対象外（導入初年度は手入力投入のため）。
WITH yr AS (
  SELECT id, name, date(start_date) AS sd, date(end_date) AS ed FROM fiscal_years
),
pair AS (
  SELECT p.id AS prev_id, p.name AS prev_name, p.sd AS prev_sd, p.ed AS prev_ed,
         n.id AS next_id, n.name AS next_name
  FROM yr p
  JOIN yr n ON n.sd = date(p.ed, '+1 day')
  WHERE EXISTS (SELECT 1 FROM opening_balances ob WHERE ob.fiscal_year_id = n.id)
    AND EXISTS (SELECT 1 FROM opening_balances ob WHERE ob.fiscal_year_id = p.id)
),
calc AS (
  SELECT
    pr.prev_sd   AS 並び順,
    pr.prev_name AS 前期,
    pr.next_name AS 翌期,
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              JOIN accounts a ON a.id = ob.account_id
              WHERE ob.fiscal_year_id = pr.prev_id AND a.code = '3100'), 0) AS 前期首3100,
    COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
              FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id
              JOIN accounts a ON a.id = l.account_id
              WHERE e.status = 'posted' AND a.code = '3100'
                AND date(e.entry_date) >= pr.prev_sd AND date(e.entry_date) <= pr.prev_ed), 0) AS 前期3100仕訳,
    COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
              FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id
              JOIN accounts a ON a.id = l.account_id
              WHERE e.status = 'posted' AND a.account_type IN ('revenue', 'expense')
                AND date(e.entry_date) >= pr.prev_sd AND date(e.entry_date) <= pr.prev_ed), 0) AS 損益符号付き,
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              JOIN accounts a ON a.id = ob.account_id
              WHERE ob.fiscal_year_id = pr.next_id AND a.code = '3100'), 0) AS 翌期首3100
  FROM pair pr
)
SELECT 前期, 翌期,
       -損益符号付き AS 当期純利益,
       前期首3100, 前期3100仕訳, 翌期首3100,
       翌期首3100 - (前期首3100 + 前期3100仕訳 + 損益符号付き) AS 差額
FROM calc
WHERE 翌期首3100 <> 前期首3100 + 前期3100仕訳 + 損益符号付き
ORDER BY 並び順
