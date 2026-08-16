-- 何を保証するか: 経費申請の計上仕訳・支払仕訳の借方合計が、申請額（税込）と一致すること。
-- 違反時の意味: 承認後に金額を書き換えたのに仕訳が追随していない。費用と未払金がズレる。
-- 出典: Modules/Expense/ExpenseRequest.mod.cs（計上・支払の起票）
--       docs/04_会計ドメイン設計.md §3.2（税抜経理: 費用本体 + 仮払消費税 = 税込額）
-- 注意: 「経費申請の明細行化」改修で expense_request.amount の意味（ヘッダ合計）が変わるため、
--       改修後はこのチェックを「ヘッダ = 明細合計 = 仕訳借方合計」の 3 点照合に拡張すること。
SELECT '計上仕訳の金額不一致' AS 違反,
       er.id AS 申請id, er.title AS 件名, er.settlement_status AS 精算状態,
       er.amount AS 申請額, je.id AS 伝票id,
       (SELECT SUM(l.amount) FROM journal_lines l WHERE l.journal_entry_id = je.id AND l.dc = 'D') AS 仕訳借方計
FROM expense_request er
JOIN journal_entries je ON je.source_type = 'expense' AND je.source_id = er.id
WHERE COALESCE(er.amount, 0)
      <> COALESCE((SELECT SUM(l.amount) FROM journal_lines l
                   WHERE l.journal_entry_id = je.id AND l.dc = 'D'), 0)

UNION ALL
SELECT '支払仕訳の金額不一致',
       er.id, er.title, er.settlement_status, er.amount, je.id,
       (SELECT SUM(l.amount) FROM journal_lines l WHERE l.journal_entry_id = je.id AND l.dc = 'D')
FROM expense_request er
JOIN journal_entries je ON je.source_type = 'expense_payment' AND je.source_id = er.id
WHERE COALESCE(er.amount, 0)
      <> COALESCE((SELECT SUM(l.amount) FROM journal_lines l
                   WHERE l.journal_entry_id = je.id AND l.dc = 'D'), 0)
