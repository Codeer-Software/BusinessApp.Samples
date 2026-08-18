-- 仕掛品の対象（年度 × 案件）。ADR-0072・BUG-0016。
-- 判定と金額はビュー v_wip_candidate（Designer/ddl/720）に置く——
-- 画面・仕訳生成・不変条件検査で同じ計算を三重に書かないため（ADR-0060 の教訓）。
-- 対象は「受託案件で、当期の原価があり、当期売上が 0 で、期末までに確定検収が無いもの」。
SELECT
  fiscal_year_id,
  project_id,
  project_code,
  project_name,
  department_id,
  direct_cost,
  labor_cost,
  wip_amount
FROM v_wip_candidate
ORDER BY fiscal_year_id, project_code
