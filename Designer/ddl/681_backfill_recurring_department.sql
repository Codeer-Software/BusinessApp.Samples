-- 681_backfill_recurring_department.sql — 定期請求由来の売上に契約の部門を当てる（BUG-0062 の一部・開発者判断 2026-08-18）
--
-- 【背景】不変条件 `A11_部門_売上高の行に全社共通を使っていない` に残っていた 9 件のうち、
-- **定期請求由来の 4 件は契約側に部門がある**（クラウド勤怠 SaaS 利用料＝営業部／年額プラン＝総務部）。
-- 現行コード（`RecurringRun`）は契約の部門を使っているので、これは**古いデータだけの問題**である。
--
-- 【たどり方】`journal_entries.source_id` は定期請求では**請求書 id**（実測で確認）。
--   仕訳 → 請求書 → `recurring_billings.department_id` と辿る。
-- 【安全性】部門は金額を 1 円も動かさない（ADR-0056 が確定伝票の部門編集を正式に用意している）。

-- 売上・前受振替の仕訳行
UPDATE journal_lines
SET department_id = (
  SELECT rb.department_id
  FROM journal_entries je
  JOIN invoices i ON i.id = je.source_id
  JOIN recurring_billings rb ON rb.id = i.recurring_billing_id
  WHERE je.id = journal_lines.journal_entry_id
)
WHERE department_id = (SELECT id FROM departments WHERE is_common = 1)
  AND EXISTS (
    SELECT 1
    FROM journal_entries je
    JOIN invoices i ON i.id = je.source_id
    JOIN recurring_billings rb ON rb.id = i.recurring_billing_id
    WHERE je.id = journal_lines.journal_entry_id
      AND je.source_type IN ('recurring', 'recurring_defer', 'recurring_annual')
      AND rb.department_id IS NOT NULL
  );

-- 請求書ヘッダの部門も契約にそろえる（作成者の所属部が入ってしまっていた）
UPDATE invoices
SET department_id = (SELECT rb.department_id FROM recurring_billings rb WHERE rb.id = invoices.recurring_billing_id)
WHERE recurring_billing_id IS NOT NULL
  AND EXISTS (SELECT 1 FROM recurring_billings rb
              WHERE rb.id = invoices.recurring_billing_id
                AND rb.department_id IS NOT NULL
                AND rb.department_id <> COALESCE(invoices.department_id, -1));
