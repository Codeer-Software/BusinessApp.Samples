-- 640: 翌期繰越の陳腐化検知ビュー（ADR-0068・BUG-0060）
--
-- 翌期繰越は「実行した瞬間のスナップショット」なので、実行後に前期の伝票を足す／直すと
-- 翌期の期首残高は黙って誤りになる。どの帳票にも現れない（翌期の BS は年度内で閉じているため
-- 貸借は一致したまま）ので、機械が気づくしかない。
--
-- 判定はここ 1 か所に置く（ADR-0068 §帰結・ADR-0060 の二重実装を増やさない）。
--   - 画面: Accounting/CarryOverStatus（Query モジュール）がこのビューを読む
--   - 検査: docs/qa/20_不変条件SQL/B03・B04 が同じ計算を独立に持つ（相互検証のため意図的に別実装）
--
-- 期待値の定義は FiscalYear.CarryOverSql.sql の逆算:
--   翌期首(科目) = 前期首(科目) + 前期の確定仕訳(科目) + (科目が 3100 なら前期の当期純利益)
-- 年度の連結は next_year_id ではなく日付の連続（前期末 + 1 日 = 翌期首）で行う。
-- next_year_id は繰越実行のトリガ用で、通常は NULL に戻される。
DROP VIEW IF EXISTS v_carryover_staleness;
CREATE VIEW v_carryover_staleness AS
WITH yr AS (
  SELECT id, name, date(start_date) AS sd, date(end_date) AS ed FROM fiscal_years
),
pair AS (
  SELECT p.id AS prev_id, p.name AS prev_name, p.sd AS prev_sd, p.ed AS prev_ed,
         n.id AS next_id, n.name AS next_name
  FROM yr p
  JOIN yr n ON n.sd = date(p.ed, '+1 day')
  -- 前期に繰越の元（期首残高または確定仕訳）が無い年度は判定しない。
  -- 導入初年度の期首残高は繰越ではなく手入力で投入するため（docs/04 §6）、
  -- 「前期末＝翌期首」は成立しない。B03 が同じ理由で同じ除外をしている
  WHERE EXISTS (SELECT 1 FROM opening_balances ob WHERE ob.fiscal_year_id = p.id)
     OR EXISTS (SELECT 1 FROM journal_entries e WHERE e.fiscal_year_id = p.id AND e.status = 'posted')
),
diff AS (
  SELECT
    pr.prev_id, pr.next_id, a.id AS account_id,
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              WHERE ob.fiscal_year_id = pr.prev_id AND ob.account_id = a.id), 0)
    + COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
                FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id
                WHERE e.status = 'posted' AND l.account_id = a.id
                  AND date(e.entry_date) >= pr.prev_sd AND date(e.entry_date) <= pr.prev_ed), 0)
    + CASE WHEN a.code = '3100' THEN
        COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
                  FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id
                       JOIN accounts pa ON pa.id = l.account_id
                  WHERE e.status = 'posted' AND pa.account_type IN ('revenue', 'expense')
                    AND date(e.entry_date) >= pr.prev_sd AND date(e.entry_date) <= pr.prev_ed), 0)
      ELSE 0 END AS expected,
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              WHERE ob.fiscal_year_id = pr.next_id AND ob.account_id = a.id), 0) AS stored
  FROM pair pr
  CROSS JOIN accounts a
  WHERE a.account_type IN ('asset', 'liability', 'equity')
)
SELECT
  pr.prev_id   AS fiscal_year_id,
  pr.prev_name AS fiscal_year_name,
  pr.next_id   AS next_year_id,
  pr.next_name AS next_year_name,
  -- not_carried = まだ繰り越していない / stale = 繰越後に前期が動いた / current = 一致
  CASE
    WHEN NOT EXISTS (SELECT 1 FROM opening_balances ob WHERE ob.fiscal_year_id = pr.next_id) THEN 'not_carried'
    WHEN EXISTS (SELECT 1 FROM diff d WHERE d.prev_id = pr.prev_id AND d.next_id = pr.next_id
                   AND d.stored <> d.expected) THEN 'stale'
    ELSE 'current'
  END AS state,
  (SELECT COUNT(*) FROM diff d
    WHERE d.prev_id = pr.prev_id AND d.next_id = pr.next_id AND d.stored <> d.expected) AS diff_accounts,
  (SELECT COALESCE(SUM(ABS(d.stored - d.expected)), 0) FROM diff d
    WHERE d.prev_id = pr.prev_id AND d.next_id = pr.next_id AND d.stored <> d.expected) AS diff_amount
FROM pair pr;
