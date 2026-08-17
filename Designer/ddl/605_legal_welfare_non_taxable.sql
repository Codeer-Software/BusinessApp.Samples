-- 605: 法定福利費（6030）の既定税区分を「非課税仕入」から「不課税」へ揃える（BUG-0056）
--
-- 同じ「法定福利費」に対し、定型仕訳 T03「社会保険料の納付」の明細は不課税（NON_TAXABLE）、
-- 勘定科目マスタの既定は非課税仕入（PUR_EXEMPT）で、起票経路によって税区分が変わっていた。
--
-- **不課税に揃える**（ユーザー決定 2026-08-17）。社会保険料の事業主負担・納付は
-- 法令に基づく公的負担であって、資産の譲渡等の「対価」ではない＝消費税の課税対象外（不課税）。
-- 消費税法別表第二の非課税取引の列挙にも含まれない。同じ理由で既に不課税にしてある
-- 役員報酬(6000)・給料手当(6010)・賞与(6020)・労務費(5100) と足並みが揃う。
--
-- 税額への影響は無い: 消費税集計表（TaxSummary.Query.sql）は控除対象税額を
-- taxation_type = 'taxable_purchase' の行にしか出さず、課税売上割合の分母も売上系の
-- 税区分だけで作る。exempt_purchase も non_taxable も、どちらも税額計算に入らない。
-- 変わるのは帳票上の分類行（「非課税仕入」→「不課税」）だけ。
--
-- 既存の仕訳明細は書き換えない: 適用時点で account_id が 6030 の journal_lines は 0 件
-- （実測 2026-08-17）。将来ズレた行が出た場合も、確定済みの仕訳は帳簿の事実として残す。

UPDATE accounts
   SET default_tax_category_id = (SELECT id FROM tax_categories WHERE code = 'NON_TAXABLE')
 WHERE code = '6030'
   AND default_tax_category_id = (SELECT id FROM tax_categories WHERE code = 'PUR_EXEMPT');

-- 確認: 0 件が期待値（食い違いが残っていないこと）
SELECT '法定福利費と定型仕訳の税区分が食い違う' AS check_name, COUNT(*) AS remaining
  FROM journal_template_lines l
  JOIN accounts a ON a.id = l.account_id
 WHERE a.code = '6030'
   AND l.tax_category_id <> a.default_tax_category_id;
