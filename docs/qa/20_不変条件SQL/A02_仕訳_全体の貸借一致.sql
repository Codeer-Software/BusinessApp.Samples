-- 何を保証するか: 確定済み仕訳全体で Σ借方 = Σ貸方 であること。
-- 違反時の意味: 帳簿全体が壊れている。A01（伝票ごと）が合格でここが違反することは
--               理屈上ありえないので、その場合は集計の前提（status/JOIN）を疑う。
-- 出典: docs/tests/11_E2Eテストシナリオ/README.md §5 ①（総合検算・期待値 11,852,675）
SELECT
    SUM(CASE WHEN jl.dc = 'D' THEN jl.amount ELSE 0 END)  AS 借方合計,
    SUM(CASE WHEN jl.dc = 'C' THEN jl.amount ELSE 0 END)  AS 貸方合計,
    SUM(CASE WHEN jl.dc = 'D' THEN jl.amount ELSE -jl.amount END) AS 差額
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
WHERE je.status = 'posted'
HAVING SUM(CASE WHEN jl.dc = 'D' THEN jl.amount ELSE -jl.amount END) <> 0
