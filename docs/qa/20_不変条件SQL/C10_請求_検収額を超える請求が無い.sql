-- 何を保証するか: 検収に紐づく請求書の税込請求額が、その検収の税込確定額を超えないこと。
-- 違反時の意味: 売上・売掛金は「検収の確定」でしか計上されないため、検収額を超えて請求すると
--               その超過分は請求書には載るのに帳簿には一切現れない。
--               結果として「総勘定元帳の売掛金 ≠ 売掛残高一覧の残額合計」（C08）になり、
--               入金があっても消し込めない残額が永久に残る。
-- 出典: Modules/Sales/Invoice.mod.cs UpdateOverAcceptanceWarning()
--       ISSUE-0002（増額は変更契約として新しい受注・検収を起こす運用。警告は出すがブロックしない）
-- 備考: 画面の警告は「明細行 vs 検収明細行」で行うが、帳簿への影響は伝票単位なのでここでは総額で見る。
--       違反が出たら、変更契約として検収を追加するのが正しい直し方（請求書を減額するのではなく）。
SELECT
    i.id            AS 請求書id,
    i.invoice_no    AS 請求書番号,
    i.status        AS 状態,
    i.issue_date    AS 発行日,
    ac.acceptance_no AS 検収番号,
    ac.status        AS 検収状態,
    COALESCE(ac.amount, 0) + COALESCE(ac.tax_amount, 0) AS 検収税込,
    COALESCE(i.amount, 0)  + COALESCE(i.tax_amount, 0)  AS 請求税込,
    (COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0))
      - (COALESCE(ac.amount, 0) + COALESCE(ac.tax_amount, 0)) AS 超過額
FROM invoices i
JOIN acceptances ac ON ac.id = i.acceptance_id
WHERE i.status <> 'void'
  AND (COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0))
      > (COALESCE(ac.amount, 0) + COALESCE(ac.tax_amount, 0))
ORDER BY i.id
