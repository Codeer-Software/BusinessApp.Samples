-- 何を保証するか: `journal_template_lines.tax_input_mode` が必ず `inclusive` / `exclusive` / `none` のいずれかで
--                 入っていること。あわせて「課税区分なのに税計算しない」明細を列挙する。
-- 違反時の意味: **BUG-0441 そのもの**。「税区分＝課税仕入 10%／税計算＝空」の定型を 1 つ作ると、
--               そこから起票した伝票の仮払消費税が 0 円になる。
--               貸借は合い、警告も出ないまま**毎月 10,000 円の仕入税額控除が静かに消える**。
--               修正はフィールドを必須にしただけで **DB 制約は無い**ので、
--               SQL 直投入・移行・デザインの差し戻しで簡単に元へ戻る。
--               マスタ 1 行の欠落が全起票に波及する型なので、H01（閾値の存在確認）と同じ趣旨の安い保険。
-- 出典: docs/qa/02_バグ台帳.md BUG-0441
-- 備考: 2 本目の「課税区分なのに税計算しない」は**意図的な設定でもありうる**（税抜で別行に税を持つ運用）。
--       赤くなったら、まず意図を確かめること。

SELECT '税計算方式が未設定' AS 違反, l.id AS 明細id, t.code AS 定型コード, t.name AS 定型名,
       l.line_no AS 行番号, a.code AS 科目, tc.code AS 税区分,
       COALESCE(l.tax_input_mode, '(NULL)') AS 税計算
FROM journal_template_lines l
JOIN journal_templates t ON t.id = l.template_id
LEFT JOIN accounts a        ON a.id  = l.account_id
LEFT JOIN tax_categories tc ON tc.id = l.tax_category_id
WHERE l.tax_input_mode IS NULL OR l.tax_input_mode NOT IN ('inclusive', 'exclusive', 'none')

UNION ALL

SELECT '課税区分なのに税計算しない', l.id, t.code, t.name, l.line_no, a.code, tc.code, l.tax_input_mode
FROM journal_template_lines l
JOIN journal_templates t ON t.id = l.template_id
JOIN tax_categories tc   ON tc.id = l.tax_category_id
JOIN tax_rates tr        ON tr.id = tc.tax_rate_id
LEFT JOIN accounts a     ON a.id  = l.account_id
WHERE tc.taxation_type IN ('taxable_sales', 'taxable_purchase') AND tr.rate_percent > 0
  AND l.tax_input_mode = 'none'
