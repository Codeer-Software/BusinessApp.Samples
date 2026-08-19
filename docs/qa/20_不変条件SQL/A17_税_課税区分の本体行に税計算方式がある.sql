-- 何を保証するか: 仕訳明細の `tax_input_mode` が定義済みの語彙で入っていること。
--                 とくに**課税区分の本体行には必ず税計算方式がある**こと。
-- 違反時の意味: BUG-0441 の伝票版。税区分が「課税仕入 10%」なのに `tax_input_mode` が
--               NULL や空文字だと、**仮払消費税の行が生成されない**。
--               そして `A14`（税額の再計算）は `tax_input_mode IN ('inclusive','exclusive')` で絞るので、
--               **その行は検査対象からまるごと外れる**（A09 は既存の税行しか見ない）。
--               消費税集計表は実際の税行を合計する作りなので、
--               「課税仕入 ○○円・税額 0 円」という**一見それらしい行**として載り、
--               控除が漏れて**納税額が過大**になる。
--               F11 は同じ検査を定型仕訳（`journal_template_lines`）にだけ掛けていて、伝票側が空いていた。
-- 出典: docs/qa/02_バグ台帳.md BUG-0441 ／ 2026-08-19 のスキーマ監査
-- 備考: 対象外（`OUT_OF_SCOPE`）・非課税・不課税の行が NULL なのは正しい。
--       ただし表現が NULL と '' に割れているのは望ましくないので、語彙の検査で拾う。

SELECT '税計算方式が未定義の値' AS 違反, jl.id AS 明細id, je.journal_no AS 伝票番号,
       date(je.entry_date) AS 日付, jl.line_no AS 行番号,
       COALESCE(jl.tax_input_mode, '(NULL)') AS 税計算, tc.code AS 税区分
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
LEFT JOIN tax_categories tc ON tc.id = jl.tax_category_id
WHERE jl.tax_input_mode IS NOT NULL
  AND jl.tax_input_mode NOT IN ('inclusive', 'exclusive', 'none')

UNION ALL

SELECT '課税区分の本体行に税計算方式が無い', jl.id, je.journal_no,
       date(je.entry_date), jl.line_no,
       COALESCE(jl.tax_input_mode, '(NULL)'), tc.code
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
JOIN tax_categories tc  ON tc.id = jl.tax_category_id
JOIN tax_rates tr       ON tr.id = tc.tax_rate_id
WHERE je.status = 'posted' AND jl.is_tax_line = 0
  AND tc.taxation_type IN ('taxable_sales', 'taxable_purchase') AND tr.rate_percent > 0
  AND (jl.tax_input_mode IS NULL OR jl.tax_input_mode NOT IN ('inclusive', 'exclusive'))
  -- **すでに税行が付いている本体行は正常**。自動起票（売上計上・入金消込・仕入計上など）は
  -- 税行を明示的に組み立てるので、本体行の税計算方式は `none`（＝この行から税を導出し直さない）でよい。
  -- 拾いたいのは「課税区分なのに税行が 1 本も無い」行だけ
  AND NOT EXISTS (SELECT 1 FROM journal_lines t
                   WHERE t.journal_entry_id = jl.journal_entry_id
                     AND t.is_tax_line = 1 AND t.parent_line_no = jl.line_no)
