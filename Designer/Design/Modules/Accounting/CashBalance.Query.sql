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
-- 【今日がどの年度にも入らない日の縮退・BUG-0097】
--   旧実装は「今日を含む年度」だけを見ていたので、期初に翌期をまだ作っていない日や年度の隙間に開くと
--   `yr` が 0 件になり、`COALESCE(...,0)` が効いて**全科目 0 円**が並んだ。エラーも警告も出ないので
--   「本当に残高がゼロ」と区別できない——**期初は経理がいちばん画面を開く時期**なので必ず踏む。
--   今日を含む年度が無いときは**直近の年度**へ縮退する（0 円よりは直近の実残高の方が 判断を誤らせない）。
WITH yr AS (
  -- 優先順: ①今日を含む年度 → ②**直前に終わった年度**（期初に翌期を作り忘れた日はここ）
  --         → ③これから始まる年度（過去の年度が 1 つも無いとき）
  SELECT id FROM (
    SELECT id, 0 AS pri, 0 AS ord FROM fiscal_years
     WHERE date(start_date) <= date('now', 'localtime')
       AND date(end_date)   >= date('now', 'localtime')
    UNION ALL
    SELECT id, 1 AS pri, -julianday(date(end_date)) AS ord FROM fiscal_years
     WHERE date(end_date) < date('now', 'localtime')
    UNION ALL
    SELECT id, 2 AS pri,  julianday(date(start_date)) AS ord FROM fiscal_years
     WHERE date(start_date) > date('now', 'localtime')
  )
  ORDER BY pri, ord
  LIMIT 1
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
              -- **今日までの仕訳だけ**（BUG-0414）。画面は「いま帳簿上いくらあるか」を名乗るので、
              -- 先日付の支払仕訳を引いた額を出すと、実際より少ない残高で起票の判断をさせてしまう
              AND date(e.entry_date) <= date('now', 'localtime')
              AND e.fiscal_year_id IN (SELECT id FROM yr)), 0) AS book_balance
FROM accounts a
WHERE a.is_cash_equivalent = 1
  AND a.is_active = 1
ORDER BY a.display_order, a.code
