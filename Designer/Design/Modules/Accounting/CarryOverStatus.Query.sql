-- 翌期繰越の状態（ADR-0068・BUG-0060）。年度の連結ごとに 1 行。
-- 判定そのものはビュー v_carryover_staleness（Designer/ddl/640）に置く——
-- 画面と不変条件で同じ計算を二重に書かないため（ADR-0060 の教訓）。
--   not_carried … まだ繰り越していない（翌期に期首残高が 1 行も無い）
--   stale       … 繰越済みだが、その後に前期が動いて陳腐化した（再繰越が要る）
--   current     … 翌期首＝前期末で一致している
SELECT
  fiscal_year_id,
  fiscal_year_name,
  next_year_id,
  next_year_name,
  state,
  diff_accounts,
  diff_amount
FROM v_carryover_staleness
