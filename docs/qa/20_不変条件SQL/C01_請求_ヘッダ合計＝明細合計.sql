-- 何を保証するか: 請求書ヘッダの税抜金額（invoices.amount）が明細合計（Σ invoice_lines.amount）と一致すること。
--                 明細を 1 行も持たない請求書に金額が入っていないこと。
-- 違反時の意味: 画面の合計と印刷される明細が食い違う。売上計上仕訳はヘッダ額で起票されるため、
--               明細を直したのにヘッダの再計算が走らなかった場合、帳簿と請求書の実体がズレる。
-- 出典: ADR-0049（検収の明細化と請求書はその写し）／Modules/Sales/Invoice.mod.cs RecalcTotal()
-- 備考: void（取消）も対象に含める。取消済みでも「何を請求したか」の記録は正しくあるべきなので。
SELECT
    i.id            AS 請求書id,
    i.invoice_no    AS 請求書番号,
    i.status        AS 状態,
    i.invoice_source AS 発生源,
    i.issue_date    AS 発行日,
    COALESCE(i.amount, 0) AS ヘッダ税抜,
    COALESCE((SELECT SUM(l.amount) FROM invoice_lines l WHERE l.invoice_id = i.id), 0) AS 明細合計,
    (SELECT COUNT(*) FROM invoice_lines l WHERE l.invoice_id = i.id) AS 明細行数,
    COALESCE(i.amount, 0)
      - COALESCE((SELECT SUM(l.amount) FROM invoice_lines l WHERE l.invoice_id = i.id), 0) AS 差額
FROM invoices i
WHERE COALESCE(i.amount, 0)
      <> COALESCE((SELECT SUM(l.amount) FROM invoice_lines l WHERE l.invoice_id = i.id), 0)
ORDER BY i.id
