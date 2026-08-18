-- 仕掛品（未成業務支出金）の期末振替の状態（ADR-0072・BUG-0016）。会計年度ごとに 1 行。
-- 判定と金額はビュー v_wip_status（Designer/ddl/720）に置く——
-- 画面・仕訳生成・不変条件検査で同じ計算を三重に書かないため（ADR-0060 の教訓）。
--   computed_amount … いま計算し直したら振り替えるべき額
--   posted_amount   … 実際に起票済みの額（期末振替の借方合計）
--   reversal_amount … 翌期首の振戻額（貸方合計）
SELECT
  fiscal_year_id,
  fiscal_year_name,
  project_count,
  computed_amount,
  posted_entries,
  posted_amount,
  reversal_entries,
  reversal_amount,
  missing_salary_count
FROM v_wip_status
