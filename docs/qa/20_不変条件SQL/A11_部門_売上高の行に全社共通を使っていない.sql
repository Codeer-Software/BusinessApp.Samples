-- 何を保証するか: 売上高（account_categories.code = 'REV'）の仕訳行に、
--                 共通費の受け皿である「全社共通」部門（departments.is_common = 1）が使われていないこと。
-- 違反時の意味: 部門別 PL でその売上がどの部にも属さない。売上は必ず稼いだ部門があるので、
--               全社共通が入っているのは「起票時に部門が決まらず補完で埋まった」＝入力漏れ。
--               部門別損益・予算実績・案件損益のすべてが実態より小さく見える。
-- 出典: ADR-0056（損益科目の行には実部門が要る／BS 科目は全社共通で補完）
--       docs/04_会計ドメイン設計.md §8・§2（account_categories が BS/PL の組み上げ定義）
--
-- 対象範囲をなぜ「売上高区分」に限るか（2026-08-17 に修正・重要）:
--   当初は account_type = 'revenue' で書いていたが、この型には
--   **営業外収益（受取利息 7000・雑収入 7010）と特別利益（固定資産売却益 8000）も含まれる**。
--   預金利息のような営業外収益は稼いだ部門が存在せず、全社共通が正しい姿なので、
--   これを違反として数えるのは誤り（実際に受取利息 2 行・620 円を過検出していた）。
--   区分は科目コードのベタ書き（'4%'）ではなく account_categories.code='REV' で解決する。
--   売上科目を増やしても区分に載せれば自動で対象に入り、コード体系を変えても壊れない。
--   ※費用側の全社共通も「共通費を受けて後から配賦する」正当な使い方があるため対象にしない
--     （現状の件数は README の参考値を参照）。
--
-- 符号: 売上値引・返品は収益科目の借方行として立つため、純額列は貸方を正とした符号付きで出す。
SELECT
    je.id          AS 伝票id,
    je.journal_no  AS 伝票番号,
    je.entry_date  AS 日付,
    je.source_type AS 連動元,
    a.code || ' ' || a.name AS 科目,
    jl.dc          AS 貸借,
    jl.amount      AS 金額,
    CASE WHEN jl.dc = 'C' THEN jl.amount ELSE -jl.amount END AS 純額,
    jl.description AS 行摘要
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
JOIN accounts a ON a.id = jl.account_id
JOIN account_categories c ON c.id = a.category_id
JOIN departments d ON d.id = jl.department_id
WHERE COALESCE(je.status, '') = 'posted'
  AND c.code = 'REV'
  AND d.is_common = 1
ORDER BY je.entry_date, je.id, jl.line_no
