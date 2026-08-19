-- 何を保証するか: 税計算方式を持つ本体行について、入力額・税率・**仕訳日で解決した経過措置の控除割合**から
--                 再計算した税額と本体額が、実際の税行・本体行と 1 円単位で一致すること。
-- 違反時の意味: ① インボイス経過措置の適用漏れ（BUG-0183/0417/0419）
--               ② 控除割合を**起票した日**で引いてしまう誤り（BUG-0438）。区切りは年度でも暦年でもなく
--                  **9/30〜10/1** なので、10 月に 9 月分の銀行明細をまとめて取り込むと 80% が 70% になる
--               ③ 内税分解の小数化（BUG-0421/0437）や端数処理の取り違え
--               いずれも貸借は合うので A01/A02 では出ない。A09 は税行の**親子関係と科目**しか見ておらず、
--               **税額そのものを検算している検査はここだけ**。
-- 出典: docs/qa/02_バグ台帳.md BUG-0417 / BUG-0419 / BUG-0438 ／ ADR-0049・ADR-0050
-- 備考: 対象は posted・本体行・`inclusive`/`exclusive`・課税区分・税率 > 0 のみ。
--       `none`（税計算しない）と不課税・非課税は対象外。

WITH src AS (
  SELECT jl.id AS 行id, je.id AS 伝票id, je.journal_no AS 伝票番号, date(je.entry_date) AS 日付,
         COALESCE(je.source_type, '(手入力)') AS 連動元, jl.line_no AS 行番号,
         jl.amount AS 本体額, jl.input_amount AS 入力額, jl.tax_input_mode AS 税計算,
         tc.code AS 税区分, tc.uses_transition_deduction AS 経過措置, tr.rate_percent AS 税率,
         (SELECT itr.rate_percent FROM invoice_transition_rates itr
           WHERE date(itr.valid_from) <= date(je.entry_date, 'start of month')
             AND date(itr.valid_to)   >= date(je.entry_date, 'start of month') LIMIT 1) AS 控除割合,
         (SELECT COALESCE(SUM(t.amount), 0) FROM journal_lines t
           WHERE t.journal_entry_id = je.id AND t.is_tax_line = 1
             AND t.parent_line_no = jl.line_no) AS 税行額
  FROM journal_lines jl
  JOIN journal_entries je ON je.id = jl.journal_entry_id
  JOIN tax_categories tc  ON tc.id = jl.tax_category_id
  JOIN tax_rates tr       ON tr.id = tc.tax_rate_id
  WHERE je.status = 'posted' AND jl.is_tax_line = 0
    AND jl.tax_input_mode IN ('inclusive', 'exclusive')
    AND tc.taxation_type IN ('taxable_sales', 'taxable_purchase') AND tr.rate_percent > 0
),
calc AS (
  SELECT src.*,
         CASE WHEN 税計算 = 'inclusive' THEN CAST(入力額 * 税率 / (100 + 税率) AS INTEGER)
              ELSE CAST(入力額 * 税率 / 100 AS INTEGER) END AS 満額税
  FROM src
),
calc2 AS (
  SELECT calc.*,
         CASE WHEN 経過措置 = 1 THEN CAST(満額税 * COALESCE(控除割合, 0) / 100 AS INTEGER)
              ELSE 満額税 END AS 期待税額
  FROM calc
)
SELECT 伝票id, 伝票番号, 日付, 連動元, 行番号, 税区分, 税計算, 税率, 経過措置, 控除割合,
       入力額, 本体額, 税行額, 期待税額,
       CASE WHEN 税計算 = 'inclusive' THEN 入力額 - 期待税額
            ELSE 入力額 + 満額税 - 期待税額 END AS 期待本体額
FROM calc2
WHERE 税行額 <> 期待税額
   OR 本体額 <> CASE WHEN 税計算 = 'inclusive' THEN 入力額 - 期待税額
                     ELSE 入力額 + 満額税 - 期待税額 END
ORDER BY 日付, 伝票id, 行番号
