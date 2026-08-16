-- 何を保証するか: 経費申請のヘッダが持つ合計金額（税込）・うち消費税が、明細の合計と一致すること。
--   ヘッダの amount / tax_amount は明細からの導出値で、画面では読み取り専用（ADR-0066・ADR-0062）。
--   あわせて「明細を 1 行も持たない申請が無い」「行の税区分が埋まっている」ことも見る。
-- 違反時の意味:
--   ・合計がズレる → 承認ルートの判定額・精算処理待ちの一覧・資金繰り予測・支払仕訳が
--     すべてヘッダの amount を見ているため、実際の明細と違う金額で業務が回る
--   ・明細ゼロ → 申請の中身が無い（申請時のガードをすり抜けた、または移行漏れ）
--   ・税区分ゼロ → 仕訳生成が税区分を解決できず止まる（ADR-0052 の NOT NULL 方針と同じ精神）
-- 出典: docs/decisions/0066-経費申請の明細行化.md（決定 3・決定 4）
--       Modules/Expense/ExpenseRequest.mod.cs の RecalcFromLines / CalcLineTax
-- 注意: 税額は「行ごとに確定して単純合計」する（レシート記載額が正）。
--       税率ごとに 1 回だけ端数処理する ADR-0050 は自社が発行する請求書側の規約で、ここには当てはまらない。

SELECT '合計金額がヘッダと明細で違う' AS 違反,
       e.id AS 申請id, e.title AS 件名, e.settlement_status AS 精算状態,
       e.amount AS ヘッダ合計,
       (SELECT SUM(l.amount) FROM expense_request_lines l WHERE l.expense_request_id = e.id) AS 明細合計,
       e.amount - COALESCE((SELECT SUM(l.amount) FROM expense_request_lines l WHERE l.expense_request_id = e.id), 0) AS 差額
FROM expense_request e
WHERE EXISTS (SELECT 1 FROM expense_request_lines l WHERE l.expense_request_id = e.id)
  AND COALESCE(e.amount, 0)
      <> COALESCE((SELECT SUM(l.amount) FROM expense_request_lines l WHERE l.expense_request_id = e.id), 0)

UNION ALL
-- うち消費税: 行ごとに「手入力があればその値、無ければ課税仕入のときだけ内税計算（切り捨て）」
SELECT 'うち消費税がヘッダと明細で違う',
       e.id, e.title, e.settlement_status,
       e.tax_amount,
       (SELECT SUM(CASE
                     WHEN COALESCE(l.tax_amount, 0) > 0 THEN l.tax_amount
                     WHEN tc.taxation_type = 'taxable_purchase' AND COALESCE(tr.rate_percent, 0) > 0
                          THEN CAST(l.amount * tr.rate_percent / (100 + tr.rate_percent) AS INTEGER)
                     ELSE 0
                  END)
          FROM expense_request_lines l
          LEFT JOIN tax_categories tc ON tc.id = l.tax_category_id
          LEFT JOIN tax_rates tr ON tr.id = tc.tax_rate_id
         WHERE l.expense_request_id = e.id),
       COALESCE(e.tax_amount, 0)
       - COALESCE((SELECT SUM(CASE
                                 WHEN COALESCE(l.tax_amount, 0) > 0 THEN l.tax_amount
                                 WHEN tc.taxation_type = 'taxable_purchase' AND COALESCE(tr.rate_percent, 0) > 0
                                      THEN CAST(l.amount * tr.rate_percent / (100 + tr.rate_percent) AS INTEGER)
                                 ELSE 0
                              END)
                   FROM expense_request_lines l
                   LEFT JOIN tax_categories tc ON tc.id = l.tax_category_id
                   LEFT JOIN tax_rates tr ON tr.id = tc.tax_rate_id
                  WHERE l.expense_request_id = e.id), 0)
FROM expense_request e
WHERE EXISTS (SELECT 1 FROM expense_request_lines l WHERE l.expense_request_id = e.id)
  AND COALESCE(e.tax_amount, 0)
      <> COALESCE((SELECT SUM(CASE
                                 WHEN COALESCE(l.tax_amount, 0) > 0 THEN l.tax_amount
                                 WHEN tc.taxation_type = 'taxable_purchase' AND COALESCE(tr.rate_percent, 0) > 0
                                      THEN CAST(l.amount * tr.rate_percent / (100 + tr.rate_percent) AS INTEGER)
                                 ELSE 0
                              END)
                   FROM expense_request_lines l
                   LEFT JOIN tax_categories tc ON tc.id = l.tax_category_id
                   LEFT JOIN tax_rates tr ON tr.id = tc.tax_rate_id
                  WHERE l.expense_request_id = e.id), 0)

UNION ALL
-- 明細を 1 行も持たない申請（下書きは作成途中なので除外する）
SELECT '明細が 1 行も無い',
       e.id, e.title, e.settlement_status, e.amount, 0, e.amount
FROM expense_request e
WHERE COALESCE(e.settlement_status, '') <> 'draft'
  AND NOT EXISTS (SELECT 1 FROM expense_request_lines l WHERE l.expense_request_id = e.id)

UNION ALL
-- 行の必須項目が埋まっていない（費目・税区分・金額）
SELECT '明細の必須項目が空',
       e.id, e.title, e.settlement_status, l.amount, l.line_no, NULL
FROM expense_request_lines l
JOIN expense_request e ON e.id = l.expense_request_id
WHERE COALESCE(e.settlement_status, '') <> 'draft'
  AND (l.expense_category_id IS NULL OR l.tax_category_id IS NULL
       OR l.amount IS NULL OR l.amount <= 0)
