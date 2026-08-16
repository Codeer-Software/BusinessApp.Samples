-- 何を保証するか: 会計年度ごとに期首残高（opening_balances）の符号付き合計が 0 であること。
--                 balance は「借方残 = 正 / 貸方残 = 負」の符号付きなので、貸借一致 ⇔ 合計 0。
-- 違反時の意味: 期首の時点で貸借が合っていない。以後どれだけ正しく起票しても
--               試算表・BS が永久に合わない。複式の入口が壊れている状態。
-- 出典: docs/04_会計ドメイン設計.md §6 opening_balances（符号付き）／§9「期首残高の貸借一致検証」
--       docs/tests/11_E2Eテストシナリオ/README.md §5 ④（第19期 期首 Σ=0）
SELECT
    fy.id      AS 年度id,
    fy.name    AS 年度,
    COUNT(*)   AS 行数,
    SUM(ob.balance) AS 符号付き合計
FROM opening_balances ob
JOIN fiscal_years fy ON fy.id = ob.fiscal_year_id
GROUP BY fy.id
HAVING SUM(ob.balance) <> 0
ORDER BY fy.start_date
