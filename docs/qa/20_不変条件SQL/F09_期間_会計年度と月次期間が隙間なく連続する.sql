-- 何を保証するか: (a) 会計年度どうしが重ならない (b) 年度の間に隙間が無い（前期末 +1 日 = 翌期首）
--                 (c) 月次期間が年度内で連続する (d) 第 1 期の開始・第 12 期の終了が年度の端と一致する。
-- 違反時の意味: **B03/B04 が前提にしている「日付の連続で年度を繋ぐ」判定が黙って成立しなくなる**——
--               年度が 1 日でもずれると JOIN が外れ、**赤にならずに検査そのものが消える**（一番たちが悪い）。
--               加えて隙間の日付の伝票は A06 で年度に紐づけられず、
--               BUG-0435 / 0444 / 0448 / 0246 が扱った「今日がどの年度にも入らない日」を構造的に作り出す。
--               F05 は「12 本あるか・範囲内か」までで、**隙間と重なりは見ていない**。
-- 出典: docs/qa/02_バグ台帳.md BUG-0246 / 0435 / 0444 / 0448

SELECT '会計年度が重なっている' AS 違反, y1.id AS 年度id, y1.name AS 年度,
       date(y1.start_date) AS 開始, date(y1.end_date) AS 終了,
       y2.name AS 相手, date(y2.start_date) AS 相手開始, date(y2.end_date) AS 相手終了
FROM fiscal_years y1
JOIN fiscal_years y2 ON y2.id > y1.id
WHERE date(y1.start_date) <= date(y2.end_date) AND date(y2.start_date) <= date(y1.end_date)

UNION ALL

SELECT '年度の間に隙間がある', y1.id, y1.name, date(y1.start_date), date(y1.end_date),
       y2.name, date(y2.start_date), date(y2.end_date)
FROM fiscal_years y1
JOIN fiscal_years y2 ON date(y2.start_date) > date(y1.end_date)
WHERE date(y2.start_date) <> date(y1.end_date, '+1 day')
  AND NOT EXISTS (SELECT 1 FROM fiscal_years y3
                   WHERE date(y3.start_date) > date(y1.end_date)
                     AND date(y3.start_date) < date(y2.start_date))

UNION ALL

SELECT '月次期間が連続していない', p2.fiscal_year_id, y.name,
       date(p1.start_date), date(p1.end_date),
       '第' || p2.period_no || '期', date(p2.start_date), date(p2.end_date)
FROM fiscal_periods p1
JOIN fiscal_periods p2 ON p2.fiscal_year_id = p1.fiscal_year_id AND p2.period_no = p1.period_no + 1
JOIN fiscal_years y ON y.id = p1.fiscal_year_id
WHERE date(p2.start_date) <> date(p1.end_date, '+1 day')

UNION ALL

SELECT '年度の端と月次期間の端が揃っていない', y.id, y.name,
       date(y.start_date), date(y.end_date), '第1期開始/第12期終了',
       (SELECT date(start_date) FROM fiscal_periods WHERE fiscal_year_id = y.id AND period_no = 1),
       (SELECT date(end_date)   FROM fiscal_periods WHERE fiscal_year_id = y.id AND period_no = 12)
FROM fiscal_years y
WHERE (SELECT date(start_date) FROM fiscal_periods WHERE fiscal_year_id = y.id AND period_no = 1) <> date(y.start_date)
   OR (SELECT date(end_date)   FROM fiscal_periods WHERE fiscal_year_id = y.id AND period_no = 12) <> date(y.end_date)
