-- 何を保証するか: 確定済み（posted）の伝票は 1 枚ごとに Σ借方 = Σ貸方 であること。
-- 違反時の意味: 複式簿記が壊れている。試算表・BS/PL の貸借検算が合わなくなる。
--               全体合計だけ合っていても伝票単位でズレていれば「相殺されて見えないバグ」なので、
--               このチェックが不整合検出の最も強い一本。
-- 出典: docs/04_会計ドメイン設計.md §0-2 / §3.1（確定時に Σ(D)=Σ(C) を強制）
-- 備考: 下書き（draft）は貸借不一致を許すため対象外（同 §3.1）。
SELECT
    je.id                AS 伝票id,
    je.fiscal_year_id    AS 年度id,
    je.journal_no        AS 伝票番号,
    je.entry_date        AS 日付,
    je.entry_type        AS 種別,
    je.source_type       AS 連動元,
    je.description       AS 摘要,
    SUM(CASE WHEN jl.dc = 'D' THEN jl.amount ELSE 0 END) AS 借方計,
    SUM(CASE WHEN jl.dc = 'C' THEN jl.amount ELSE 0 END) AS 貸方計,
    SUM(CASE WHEN jl.dc = 'D' THEN jl.amount ELSE -jl.amount END) AS 差額
FROM journal_entries je
JOIN journal_lines jl ON jl.journal_entry_id = je.id
WHERE je.status = 'posted'
GROUP BY je.id
HAVING SUM(CASE WHEN jl.dc = 'D' THEN jl.amount ELSE -jl.amount END) <> 0
ORDER BY je.entry_date, je.id
