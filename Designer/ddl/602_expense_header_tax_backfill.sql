-- 602_expense_header_tax_backfill.sql — 移行済み申請の「うち消費税（合計）」を明細から埋め直す（ADR-0066）
--
-- 経緯: ddl/600 の移行は expense_request.tax_amount をそのまま明細へ写した。
--   これは「レシート記載の税額を手入力した申請」では正しいが、手入力が無い申請では
--   ヘッダが NULL のまま残る。明細行化後のヘッダ tax_amount は
--   「明細の税額合計（手入力が無い行は税区分から内税計算）」という導出値に意味が変わったので、
--   NULL のままだと画面の「うち消費税（合計）」が空欄で、明細に一度触っただけで値が現れる。
--   帳簿は行ごとに計算するため影響しないが、見えている数字が状態によって変わるのは避ける。
--
--   不変条件 SQL `docs/qa/20_不変条件SQL/D05_経費_ヘッダ合計が明細合計と一致する.sql` が
--   この不整合を 11 件検出したのが発端（2026-08-17）。
--
-- 計算式は ExpenseRequest.mod.cs の CalcLineTax と同じ:
--   行に手入力があればその値 / 無ければ課税仕入のときだけ 税込 × 率 ÷ (100+率) の切り捨て / それ以外は 0

UPDATE expense_request
   SET tax_amount = (
        SELECT SUM(CASE
                      WHEN COALESCE(l.tax_amount, 0) > 0 THEN l.tax_amount
                      WHEN tc.taxation_type = 'taxable_purchase' AND COALESCE(tr.rate_percent, 0) > 0
                           THEN CAST(l.amount * tr.rate_percent / (100 + tr.rate_percent) AS INTEGER)
                      ELSE 0
                   END)
          FROM expense_request_lines l
          LEFT JOIN tax_categories tc ON tc.id = l.tax_category_id
          LEFT JOIN tax_rates tr ON tr.id = tc.tax_rate_id
         WHERE l.expense_request_id = expense_request.id)
 WHERE EXISTS (SELECT 1 FROM expense_request_lines l WHERE l.expense_request_id = expense_request.id);
