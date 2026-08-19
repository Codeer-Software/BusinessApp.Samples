-- 何を保証するか: 償却が要る固定資産に、償却に必要な情報が揃っていること。
--                 (a) 定額法・200%定率法なら耐用年数が入っている
--                 (b) 取得年度を過ぎているのに償却仕訳が 1 本も無い資産がない
-- 違反時の意味: 耐用年数が空だと `CalcDepreciationForYear()` が `if (life <= 0) return 0;` で 0 を返し、
--               **その資産は永久に償却されない**。減価償却費が計上されないので**利益が過大**になり、
--               固定資産台帳では「取得価額あり・簿価そのまま」の**正常に見える行**として残る。
--               E01/E02/E03 は**すべて「存在する償却仕訳」しか見ない**ので、「1 本も無い」は検出できない。
--               しかも SQL 直投入ではなく**画面から普通に到達できる**
--               （`UsefulLife` は `IsRequired: false`、警告は Toaster.Warn だけで保存を止めない）。
-- 出典: docs/qa/02_バグ台帳.md BUG-0465 ／ Designer/ddl/060_fixed_assets.sql
-- 備考: (b) は「取得年度の翌年度以降で、処分もされていない資産」に限る。
--       即時償却（immediate）・一括償却（lump_sum_3yr）も償却仕訳は立つので対象に含める。

SELECT '償却方法に必要な耐用年数が無い' AS 違反, fa.id AS 資産id, fa.name AS 資産名,
       fa.depreciation_method AS 償却方法, fa.useful_life AS 耐用年数,
       date(fa.acquisition_date) AS 取得日, fa.acquisition_cost AS 取得価額,
       NULL AS 償却仕訳数
FROM fixed_assets fa
WHERE fa.depreciation_method IN ('straight_line', 'declining_200')
  AND (fa.useful_life IS NULL OR fa.useful_life <= 0)

UNION ALL

SELECT '取得年度を過ぎているのに償却仕訳が 1 本も無い', fa.id, fa.name,
       fa.depreciation_method, fa.useful_life,
       date(fa.acquisition_date), fa.acquisition_cost, 0
FROM fixed_assets fa
WHERE COALESCE(fa.status, '') NOT IN ('retired', 'sold')
  AND fa.acquisition_date IS NOT NULL
  -- 取得年度そのものは、まだ償却仕訳を切っていなくても正常（年度末に切る）
  AND EXISTS (SELECT 1 FROM fiscal_years fy
               WHERE date(fy.end_date) < date('now', 'localtime')
                 AND date(fy.start_date) > date(fa.acquisition_date))
  AND NOT EXISTS (SELECT 1 FROM journal_entries je
                   WHERE je.source_type = 'depreciation' AND je.source_id = fa.id)
