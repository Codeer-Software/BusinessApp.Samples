-- 270_expense_department.sql — 経費申請に部門スナップショット列（初見UXテスト U2-8 対応・AccountingSQLite）
-- 「部門の申請一覧」の代わりに、申請一覧へ部門検索を足すための列。
-- 申請時に申請者の所属部門を記録する（人事異動後も「申請時点の部門」で検索できる。
-- 仕訳への部門引継ぎ（B-7）と同じスナップショット思想）。
ALTER TABLE expense_request ADD COLUMN department_id INTEGER REFERENCES departments(id);

-- 既存データのバックフィル: 起案者の現在の所属部門で補完
UPDATE expense_request
SET department_id = (SELECT u.department_id FROM app_users u WHERE u.id = expense_request.creator)
WHERE department_id IS NULL;
