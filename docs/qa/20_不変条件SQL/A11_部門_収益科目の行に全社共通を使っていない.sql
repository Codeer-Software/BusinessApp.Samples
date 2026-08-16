-- 何を保証するか: 収益科目（account_type = revenue）の仕訳行に、
--                 共通費の受け皿である「全社共通」部門（departments.is_common = 1）が使われていないこと。
-- 違反時の意味: 部門別 PL でその売上がどの部にも属さない。売上は必ず稼いだ部門があるので、
--               全社共通が入っているのは「起票時に部門が決まらず補完で埋まった」＝入力漏れ。
--               部門別損益・予算実績・案件損益のすべてが実態より小さく見える。
-- 出典: ADR-0056（損益科目の行には実部門が要る／BS 科目は全社共通で補完）
--       docs/04_会計ドメイン設計.md §8
-- 備考: 費用側の全社共通は「共通費を受けて後から配賦する」正当な使い方があるため対象にしない
--       （現状の件数は README の参考値を参照）。収益側にはその言い訳が無いので、こちらだけを不変条件にした。
SELECT
    je.id          AS 伝票id,
    je.journal_no  AS 伝票番号,
    je.entry_date  AS 日付,
    je.source_type AS 連動元,
    a.code || ' ' || a.name AS 科目,
    jl.amount      AS 金額,
    jl.description AS 行摘要
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
JOIN accounts a ON a.id = jl.account_id
JOIN departments d ON d.id = jl.department_id
WHERE je.status = 'posted'
  AND a.account_type = 'revenue'
  AND d.is_common = 1
ORDER BY je.entry_date, je.id, jl.line_no
