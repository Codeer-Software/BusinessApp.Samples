-- 翌期繰越（decisions/0006・ADR-0068）。年度の Update Submit のたびに実行されるが、
-- next_year_id が NULL のときは何もしない no-op ガード付き。
-- 「翌期繰越を実行」ボタンが NextYearId をセットして Submit することで発火する。
-- ★パラメータ名はフィールド名ではなく DB 列名（@next_year_id）で解決される（ISSUE-0001 の真因・2026-07-08 実測）
--
-- 【冪等】対象年度の期首残高を毎回 DELETE してから入れ直す。何回打ち直してもよい
-- （ADR-0068 の「暫定繰越＋陳腐化検知」はこの冪等性の上に成り立っている）。
--
-- 【補助科目・部門を保持する（BUG-0092）】以前は補助科目・部門を捨てて科目単位の 1 行にしていたため、
-- 補助元帳（取引先別の売掛金など）と部門別 BS の期首が翌期で消えていた。総額は合うので試算表では
-- 気づけない。いまは (科目 × 補助科目 × 部門) の粒度で繰り越す。
-- 当期純利益の振替先である繰越利益剰余金(3100) だけは全社の利益なので次元を持たせない。
DELETE FROM opening_balances
WHERE @next_year_id IS NOT NULL AND fiscal_year_id = @next_year_id;

INSERT INTO opening_balances (fiscal_year_id, account_id, sub_account_id, department_id, balance)
SELECT @next_year_id, t.account_id, t.sub_account_id, t.department_id, SUM(t.bal)
FROM (
  -- ① 前期の期首残高（次元ごとにそのまま持ち越す）
  -- 部門が空の行は「全社共通」に寄せる（ADR-0056 と同じ作法）。
  -- 空のまま繰り越すと、同じ科目が「部門なしの行」と「全社共通の行」に割れて、
  -- 部門で絞った元帳が期中の増減しか拾わない（例: 現金 全社共通 が −32,700 のマイナス残高に見える）
  SELECT ob.account_id AS account_id, ob.sub_account_id AS sub_account_id,
         COALESCE(ob.department_id, (SELECT id FROM departments WHERE is_common = 1)) AS department_id,
         ob.balance AS bal
  FROM opening_balances ob
  JOIN accounts a ON a.id = ob.account_id
  WHERE ob.fiscal_year_id = @id
    AND a.account_type IN ('asset', 'liability', 'equity')

  UNION ALL

  -- ② 当期の確定仕訳（BS 科目のみ・次元ごと）
  SELECT l.account_id, l.sub_account_id, l.department_id,
         CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  JOIN accounts a ON a.id = l.account_id
  WHERE e.status = 'posted'
    AND a.account_type IN ('asset', 'liability', 'equity')
    AND date(e.entry_date) >= (SELECT date(start_date) FROM fiscal_years WHERE id = @id)
    AND date(e.entry_date) <= (SELECT date(end_date) FROM fiscal_years WHERE id = @id)

  UNION ALL

  -- ③ 当期純利益を繰越利益剰余金(3100) へ（損益振替仕訳は作らない方式・decisions/0006）
  -- 補助科目は持たせない（全社の利益なので内訳が無い）が、部門は ① と同じく「全社共通」を入れる。
  -- ここだけ NULL にすると 3100 が「部門なしの行」と「全社共通の行」に割れる
  SELECT (SELECT id FROM accounts WHERE code = '3100'), NULL,
         (SELECT id FROM departments WHERE is_common = 1),
         COALESCE(SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END), 0)
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  JOIN accounts pa ON pa.id = l.account_id
  WHERE e.status = 'posted'
    AND pa.account_type IN ('revenue', 'expense')
    AND date(e.entry_date) >= (SELECT date(start_date) FROM fiscal_years WHERE id = @id)
    AND date(e.entry_date) <= (SELECT date(end_date) FROM fiscal_years WHERE id = @id)
    AND EXISTS (SELECT 1 FROM accounts WHERE code = '3100')
) t
WHERE @next_year_id IS NOT NULL
GROUP BY t.account_id, t.sub_account_id, t.department_id
HAVING SUM(t.bal) <> 0;
