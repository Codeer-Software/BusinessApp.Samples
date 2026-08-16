-- 何を保証するか: 経費申請の「明細」と「入力欄の行」がはっきり分かれていること（ADR-0066 の UI 改訂）。
--   画面のブロック 2（明細 1 件分の入力フォーム）は、明細テーブルの行そのものを
--   ModuleField で埋め込んで編集している。入力中の行は expense_request_id も line_no も持たず、
--   親からは editing_line_id だけで指される。「この内容で明細に追加」で両方が入り、明細になる。
--   したがって次が常に成り立つ:
--     ・親を持つ行は必ず line_no を持つ（中途半端な行が明細に混ざらない）
--     ・1 申請の line_no は 1..N の連番で重複しない（削除時は詰める）
--     ・editing_line_id が指す行は実在し、他の申請の明細ではない
-- 違反時の意味:
--   ・親あり line_no なし → 合計・仕訳・検証（GetLinesFromDb）から漏れる幽霊行。金額が合わなくなる
--   ・line_no の重複・欠番 → 「N 件目」の案内と実際がずれ、仕訳摘要の行番号も食い違う
--   ・editing_line_id が宙吊り／他申請の行 → 入力欄が他人の明細を開く・保存で他の申請を壊す
-- 出典: docs/decisions/0066-経費申請の明細行化.md（UI 改訂の節）
--       Modules/Expense/ExpenseRequest.mod.cs の CommitEntry / RenumberLines / PointEditingLineTo
-- 注意: 「親も line_no も持たない行」は入力欄そのものなので違反ではない（正常な状態）。

SELECT '親に紐づくのに行番号が無い' AS 違反,
       l.expense_request_id AS 申請id, e.title AS 件名, l.id AS 明細id,
       l.line_no AS 行番号, l.amount AS 金額
FROM expense_request_lines l
JOIN expense_request e ON e.id = l.expense_request_id
WHERE l.line_no IS NULL

UNION ALL
-- 行番号の重複
SELECT '行番号が重複している',
       l.expense_request_id, e.title, MIN(l.id), l.line_no, COUNT(*)
FROM expense_request_lines l
JOIN expense_request e ON e.id = l.expense_request_id
WHERE l.line_no IS NOT NULL
GROUP BY l.expense_request_id, e.title, l.line_no
HAVING COUNT(*) > 1

UNION ALL
-- 行番号が 1..N の連番になっていない（最大値が件数と違う／1 から始まっていない）
SELECT '行番号が 1..N の連番でない',
       l.expense_request_id, e.title, NULL, MAX(l.line_no), COUNT(*)
FROM expense_request_lines l
JOIN expense_request e ON e.id = l.expense_request_id
WHERE l.line_no IS NOT NULL
GROUP BY l.expense_request_id, e.title
HAVING MAX(l.line_no) <> COUNT(*) OR MIN(l.line_no) <> 1

UNION ALL
-- 入力欄が指す行が実在しない（宙吊りの参照）
SELECT '入力欄の行が実在しない',
       e.id, e.title, e.editing_line_id, NULL, NULL
FROM expense_request e
WHERE e.editing_line_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM expense_request_lines l WHERE l.id = e.editing_line_id)

UNION ALL
-- 入力欄が「他の申請の明細」を指している
SELECT '入力欄が他の申請の明細を指している',
       e.id, e.title, l.id, l.line_no, l.expense_request_id
FROM expense_request e
JOIN expense_request_lines l ON l.id = e.editing_line_id
WHERE l.expense_request_id IS NOT NULL
  AND l.expense_request_id <> e.id
