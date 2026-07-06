-- 200_expense_project.sql — 経費申請への案件選択（磨きバックログ）
-- 経費申請に任意の案件（プロジェクト）を紐付け、仕訳生成時に journal_lines.project_id へ
-- 引き継ぐことで、案件別損益（ProjectProfit）に経費が直課費用として乗るようにする。
-- 注意: SQLite の ALTER TABLE ADD COLUMN は IF NOT EXISTS が使えないため冪等ではない。
--       2回目以降の実行は「duplicate column name」エラーになるが、既に列がある＝適用済みなので害はない。

ALTER TABLE expense_request ADD COLUMN project_id INTEGER REFERENCES projects(id);
