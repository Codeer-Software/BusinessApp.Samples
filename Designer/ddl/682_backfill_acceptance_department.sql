-- 682_backfill_acceptance_department.sql — 検収由来の売上に受注の部門を当てる（BUG-0062 の残り・2026-08-18）
--
-- 【背景】不変条件 `A11_部門_売上高の行に全社共通を使っていない` の残り 5 件は、すべて
-- 2026-07 の**部門必須化（ADR-0029/0031）より前**に作られた検収由来の売上である。
--
-- 【導出元は受注（sales_orders.department_id）】当初は「確かな導出元が無い」と判断したが、
-- **受注には部門が入っていた**。しかも現行コードの `Acceptance` は既に
-- 受注の部門を仕訳行と請求書に写している（`Acceptance.mod.cs:650, 731`）ので、
-- **この backfill は現行コードと同じ規則を過去に当てるだけ**である。推測ではない。
--   No.1 A-26-001 → 総務部 ／ No.55 A-26-003 → 営業部 ／ No.60 A-26-004 → 総務部 ／
--   No.67 A-26-005 → 開発2部 ／ No.69 A-26-007 → 開発2部
--
-- 【安全性】部門は金額を 1 円も動かさない。確定伝票の部門編集は ADR-0056 が正式に用意している。
UPDATE journal_lines
SET department_id = (
  SELECT so.department_id
  FROM journal_entries je
  JOIN acceptances a ON a.id = je.source_id
  JOIN sales_orders so ON so.id = a.sales_order_id
  WHERE je.id = journal_lines.journal_entry_id
)
WHERE department_id = (SELECT id FROM departments WHERE is_common = 1)
  AND EXISTS (
    SELECT 1
    FROM journal_entries je
    JOIN acceptances a ON a.id = je.source_id
    JOIN sales_orders so ON so.id = a.sales_order_id
    WHERE je.id = journal_lines.journal_entry_id
      AND je.source_type = 'acceptance'
      AND so.department_id IS NOT NULL
  );

-- 検収由来の請求書ヘッダも同じ規則でそろえる
UPDATE invoices
SET department_id = (
  SELECT so.department_id
  FROM acceptances a JOIN sales_orders so ON so.id = a.sales_order_id
  WHERE a.id = invoices.acceptance_id
)
WHERE acceptance_id IS NOT NULL
  AND EXISTS (
    SELECT 1 FROM acceptances a JOIN sales_orders so ON so.id = a.sales_order_id
    WHERE a.id = invoices.acceptance_id
      AND so.department_id IS NOT NULL
      AND so.department_id <> COALESCE(invoices.department_id, -1)
  );
