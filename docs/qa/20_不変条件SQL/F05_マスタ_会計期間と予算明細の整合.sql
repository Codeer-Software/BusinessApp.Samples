-- 何を保証するか: 会計期間・予算明細の構造が壊れていないこと。
--   (a) 各会計年度に月次期間がちょうど 12 本ある
--   (b) 月次期間が年度の日付範囲に収まっている／period_no が 1〜12
--   (c) 会計年度の期間が逆転していない（start_date > end_date）
--   (d) 予算明細の period_no が 1〜12
--   (e) 予算明細が（年度・部門・科目・期）で重複していない
-- 違反時の意味: 月次締め・予算実績が「どの月か」を決められない。
--               予算の重複行は予算実績対比で予算だけ二重計上され、差異が実態と合わなくなる。
-- 出典: docs/04_会計ドメイン設計.md §1（fiscal_years / fiscal_periods）・§6（月次締め）
-- 備考: 予算実績の「実績値」は仕訳から都度集計され、実績を保存する列が無い（budget_lines は amount のみ）。
--       したがって「実績値 = 仕訳集計」は定義上恒真であり、突合できるのは予算側の構造だけ。
SELECT '月次期間が12本でない' AS 違反, fy.name AS 年度, NULL AS 期, CAST(COUNT(fp.id) AS TEXT) AS 値
FROM fiscal_years fy
LEFT JOIN fiscal_periods fp ON fp.fiscal_year_id = fy.id
GROUP BY fy.id
HAVING COUNT(fp.id) <> 12

UNION ALL
SELECT '期番号が1〜12の範囲外', fy.name, CAST(fp.period_no AS TEXT), NULL
FROM fiscal_periods fp JOIN fiscal_years fy ON fy.id = fp.fiscal_year_id
WHERE fp.period_no IS NULL OR fp.period_no < 1 OR fp.period_no > 12

UNION ALL
SELECT '月次期間が年度の範囲外', fy.name, CAST(fp.period_no AS TEXT),
       fp.start_date || ' 〜 ' || fp.end_date
FROM fiscal_periods fp JOIN fiscal_years fy ON fy.id = fp.fiscal_year_id
WHERE date(fp.start_date) < date(fy.start_date) OR date(fp.end_date) > date(fy.end_date)

UNION ALL
SELECT '年度の期間が逆転', fy.name, NULL, fy.start_date || ' 〜 ' || fy.end_date
FROM fiscal_years fy
WHERE date(fy.start_date) > date(fy.end_date)

UNION ALL
SELECT '予算明細の期番号が範囲外', COALESCE(fy.name, '(年度未設定)'), CAST(bl.period_no AS TEXT),
       'budget_lines.id=' || bl.id
FROM budget_lines bl LEFT JOIN fiscal_years fy ON fy.id = bl.fiscal_year_id
WHERE bl.period_no IS NULL OR bl.period_no < 1 OR bl.period_no > 12

UNION ALL
SELECT '予算明細が重複', COALESCE(fy.name, '(年度未設定)'), CAST(bl.period_no AS TEXT),
       '部門' || COALESCE(CAST(bl.department_id AS TEXT), '-') ||
       ' 科目' || COALESCE(CAST(bl.account_id AS TEXT), '-') ||
       ' ×' || CAST(COUNT(*) AS TEXT)
FROM budget_lines bl LEFT JOIN fiscal_years fy ON fy.id = bl.fiscal_year_id
GROUP BY bl.fiscal_year_id, bl.department_id, bl.account_id, bl.period_no
HAVING COUNT(*) > 1
