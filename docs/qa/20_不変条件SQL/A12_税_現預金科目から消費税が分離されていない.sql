-- 何を保証するか: 現金・預金の科目（accounts.is_cash_equivalent = 1）から消費税が分離されていないこと。
--
-- 違反時の意味: **BUG-0067 の指紋。** 明細行で課税科目（消耗品費など）を選ぶと税区分「課税仕入 10%」が
--   自動で入り、そのあと同じ行の科目を「普通預金」に変えても税区分が残る。確定すると
--   `RegenerateTaxLines` が働き `普通預金 11,000` が `普通預金 10,000 ＋ 仮払消費税 1,000` に分解される。
--
--   **借方合計と貸方合計は一致したままなので、貸借一致の検査（A01/A02）を素通りする。**
--   実測では消費税集計表の課税仕入 10% が 168,416 → 169,416 に増え、
--   預金振替が架空の仕入税額控除に化けた（伝票 No.94・2026-08-17）。
--
-- なぜ「現預金科目」に絞るのか（誤検出を作らないための注意・README §2 と同じ趣旨）:
--   最初は「科目マスタの既定税区分が課税でない科目」を条件に書いたが、**誤検出 51 件**を出した。
--   理由は 2 つある。
--     (1) `tax_categories.taxation_type` の実値は `taxable_purchase` / `taxable_sales` / `out_of_scope` で、
--         `'taxable'` という値は存在しない（決め打ちが誤りだった）
--     (2) 直しても、**科目マスタの既定が `out_of_scope` でも業務上は課税が正しい科目がある**。
--         実例: 前受収益（2110）は既定 `out_of_scope` だが、年額前受の売上計上では課税売上 10% を
--         載せた仕訳が正当に存在する（伝票 id=3）。「既定と違う＝誤り」は成立しない。
--   現金・預金だけは**業務上どう転んでも消費税を分離しない**ので、ここに絞れば偽陽性が出ない。
--
-- 出典: docs/04_会計ドメイン設計.md §3.2（明示税行方式）／BUG-0067／ADR-0053
SELECT '現預金科目から消費税が分離されている' AS 違反,
       je.id         AS 伝票id,
       je.journal_no AS 伝票番号,
       je.entry_date AS 日付,
       je.status     AS 伝票状態,
       p.line_no     AS 親行no,
       a.code        AS 親行科目コード,
       a.name        AS 親行科目,
       COALESCE(ptc.name, '（未設定）') AS 親行税区分,
       p.amount      AS 親行金額,
       jl.amount     AS 分離された税額
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
JOIN journal_lines p    ON p.journal_entry_id = jl.journal_entry_id
                       AND p.line_no = jl.parent_line_no
                       AND COALESCE(p.is_tax_line, 0) = 0
JOIN accounts a         ON a.id = p.account_id
LEFT JOIN tax_categories ptc ON ptc.id = p.tax_category_id
WHERE jl.is_tax_line = 1
  AND COALESCE(a.is_cash_equivalent, 0) = 1
ORDER BY je.entry_date, je.id, p.line_no;
