-- 671_backfill_ses_department.sql — SES 由来の売上に案件の担当部門を当てる（BUG-0061 の過去分・開発者判断 2026-08-18）
--
-- ddl/670 で案件に担当部門を持たせ、`SesBilling` が起票時にそれを使うようにした。
-- ここでは**それ以前に起票済みの分**を同じ規則で埋める。放置すると不変条件
-- `A11_部門_売上高の行に全社共通を使っていない` が赤のまま残り、検査として機能しなくなる。
--
-- 【安全性】部門は**金額を 1 円も動かさない**（部門別 P/L の内訳が変わるだけ）。
-- 確定伝票の部門・案件を直す操作は ADR-0056 が正式に用意している（`JournalLineDepartment`）ので、
-- 裏技ではなく想定された更新である。
--
-- 【対象の絞り方】`source_type='ses'` の伝票のうち、**行に案件が入っていて**、
-- その案件に担当部門があり、いま全社共通が入っている行だけ。1 行でも条件を外れたら触らない。
UPDATE journal_lines
SET department_id = (SELECT p.department_id FROM projects p WHERE p.id = journal_lines.project_id)
WHERE journal_lines.project_id IS NOT NULL
  AND department_id = (SELECT id FROM departments WHERE is_common = 1)
  AND EXISTS (SELECT 1 FROM projects p
              WHERE p.id = journal_lines.project_id AND p.project_type = 'ses' AND p.department_id IS NOT NULL)
  AND EXISTS (SELECT 1 FROM journal_entries je
              WHERE je.id = journal_lines.journal_entry_id AND je.source_type = 'ses');

-- 請求書のヘッダ部門も同じ規則でそろえる（伝票と請求書で部門が食い違うのを避ける）
UPDATE invoices
SET department_id = (SELECT p.department_id FROM projects p WHERE p.id = invoices.project_id)
WHERE invoices.project_id IS NOT NULL
  AND department_id = (SELECT id FROM departments WHERE is_common = 1)
  AND invoice_source = 'ses'
  AND EXISTS (SELECT 1 FROM projects p
              WHERE p.id = invoices.project_id AND p.project_type = 'ses' AND p.department_id IS NOT NULL);
