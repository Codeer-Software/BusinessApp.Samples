-- 812_budget_salary_unique.sql — 予算明細・人件費コストの重複行を DB で禁止する（BUG-0053）
--
-- 業務キーに一意制約が無かった:
--   budget_lines     … 年度 × 部門 × 科目 × 月
--   monthly_salaries … 年度 × 月 × 社員
-- どちらも `CREATE INDEX`（非ユニーク）だけだった。
--
-- 何が起きるか: `CostAllocation.Query.sql` と `ProjectProfit.Query.sql` は `monthly_salaries` を
-- LEFT JOIN しているので、同一キーの行が 2 本あると**工数行が行数分に増殖**して配賦と案件損益が壊れる。
-- 資金繰り予測とポータルの人件費も二重に積まれる。画面には「重複しています」とは出ないので、
-- 金額が倍になったことにしか気づけない。
--
-- `MonthlySalary` は経営管理フレームから直接登録でき、**スクリプト（.mod.cs）が無い**ので
-- アプリ側に重複チェックを置く場所も無い。DB で担保するのが唯一の確実な手。
-- 先例: `251_department_managers_unique.sql`（同じ理由で後付けの UNIQUE を足している）。
--
-- 注意（運用）: 重複する行を保存しようとするとエラーになる。これは正しい挙動で、
-- 行を 1 本に直して保存し直す。予算の通常入力（`Management/BudgetEntry.mod.cs`）は
-- period_no で突き合わせて上書きする冪等な作りなので、この制約には引っかからない。
--
-- 実行前の実測（2026-08-19）: budget_lines 48 行・重複キー 0 件 ／ monthly_salaries 9 行・重複キー 0 件。
-- NULL を含むキーは SQLite では常に一意扱いになる（重複を止められない）が、
-- 実運用では 4 列・3 列とも必ず入るので実害は無い。

CREATE UNIQUE INDEX IF NOT EXISTS ux_budget_lines_key
    ON budget_lines(fiscal_year_id, department_id, account_id, period_no);

CREATE UNIQUE INDEX IF NOT EXISTS ux_monthly_salaries_key
    ON monthly_salaries(fiscal_year_id, period_no, user_id);
