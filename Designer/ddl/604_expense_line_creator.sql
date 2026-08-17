-- 604: 経費明細行に creator 列を足す（ADR-0069・BUG-0213）
--
-- 経費明細行の行レベル認可を CLB の DataReadCondition で書くには、明細行そのものに
-- 「誰のものか」を示す列が要る。expense_request_lines の 19 列には人を指す列が無く、
-- 親 FK の ExpenseRequestId は IdFieldDesign なのでリンク越しの条件も書けない
-- （CLB の DataReadCondition は自モジュールの列しか見られない）。
--
-- creator は CLB の予約名で、保存時に AppUser.id が自動セットされる（SystemFieldNames.Creator）。
-- 既存行は親申請の creator を引き継がせる。

ALTER TABLE expense_request_lines ADD COLUMN creator INTEGER REFERENCES app_users(id);

-- 既存行のバックフィル: 明細の持ち主は親申請の申請者と同じ
UPDATE expense_request_lines
SET creator = (
    SELECT er.creator
    FROM expense_request er
    WHERE er.id = expense_request_lines.expense_request_id
)
WHERE creator IS NULL;

-- 行レベル認可の絞り込みに使うので索引を張る
CREATE INDEX IF NOT EXISTS idx_expense_request_lines_creator
    ON expense_request_lines (creator);
