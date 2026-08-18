-- 何を保証するか: 資金繰りの危険水域の閾値（system_thresholds.CASH_ALERT_BALANCE）が設定されていること。
-- 違反時の意味: 警告が「期末資金 < 0」だけに退化する＝**ショートしてから鳴る**（BUG-0249）。
--               閾値の行を消した／0 にしたときに、警告が静かに効かなくなるのを検出する。
--               BUDGET_ALERT_RATE が無いと予算警告が永久に出なくなる（BUG-0250）のと同じ型の穴。
-- 出典: docs/qa/02_バグ台帳.md BUG-0249 ／ Designer/ddl/690
-- 備考: **2 本の SQL（CashFlowForecastData / PortalAlertData）が同じ条件を使っているか**の突合は
--       SQL では書けないので、`Designer/tools/lint_design.py` の CLB-038（複製の一致検査）が担当する。
SELECT
  '資金繰りの危険水域（CASH_ALERT_BALANCE）が未設定または 0' AS 違反,
  COALESCE((SELECT amount FROM system_thresholds WHERE code = 'CASH_ALERT_BALANCE' LIMIT 1), 0) AS 現在値
WHERE COALESCE((SELECT amount FROM system_thresholds WHERE code = 'CASH_ALERT_BALANCE' LIMIT 1), 0) <= 0
