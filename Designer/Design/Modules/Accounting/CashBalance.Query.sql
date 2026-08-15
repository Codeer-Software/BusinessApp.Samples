-- 現預金科目ごとの帳簿残高（ADR-0055）
-- 入出金起票の画面に「いま帳簿上いくらあるか」を出すための最小クエリ。
-- 対象は accounts.is_cash_equivalent = 1（DDL 530。旧実装のコード直書き 1000/1010/1020 を置き換えた）。
--
-- 残高 = 当年度の期首残高 + 当年度の確定仕訳の増減。出納帳（CashBook.Query.sql）の
-- 期首＋累計と同じ考え方で、現預金は借方増なので D - C で積む（現預金科目は全て dc_normal='D'）。
-- 会計年度は「今日」で解決する（画面は常に当年度の残高を見せる）。
--
-- 見込み残高（未起票の下書きを足した額）は SQL では出さない。下書きは入力者ごとに絞って
-- 見せるものなので、画面側が自分の下書き行から計算して book_balance に足す（CashEntry.mod.cs）。
WITH yr AS (
  SELECT id FROM fiscal_years
  WHERE date(start_date) <= date('now', 'localtime') AND date(end_date) >= date('now', 'localtime')
)
SELECT
  a.id   AS account_id,
  a.name AS account_name,
  COALESCE((SELECT SUM(o.balance) FROM opening_balances o
             WHERE o.account_id = a.id
               AND o.fiscal_year_id IN (SELECT id FROM yr)), 0)
  +
  COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
             FROM journal_lines l
             JOIN journal_entries e ON e.id = l.journal_entry_id
            WHERE l.account_id = a.id
              AND e.status = 'posted'
              AND e.fiscal_year_id IN (SELECT id FROM yr)), 0) AS book_balance
FROM accounts a
WHERE a.is_cash_equivalent = 1
  AND a.is_active = 1
ORDER BY a.display_order, a.code
