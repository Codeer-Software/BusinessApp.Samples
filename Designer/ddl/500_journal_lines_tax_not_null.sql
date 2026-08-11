-- 500_journal_lines_tax_not_null.sql — journal_lines.tax_category_id を NOT NULL にする（ADR-0052・2026-08-12）
--
-- 490 で既存の NULL を一掃し、アプリ側でも塞いだ（JournalLine.TaxCategory の IsRequired と
-- JournalEntry.FillMissingTaxCategories()）。仕上げに DB の制約で保証する。
-- これで「税区分の無い仕訳明細」は物理的に作れなくなり、集計 SQL は NULL を考えなくてよくなる。
--
-- SQLite は ALTER TABLE で既存列に NOT NULL を付けられないため、公式手順のテーブル再構築で行う。
-- **実行前にサーバを停止し、DB ファイルをバックアップすること**（新旧突合で戻せるようにする）。
--
-- journal_lines に紐づくのはインデックス 2 本のみ。トリガー・ビュー・journal_lines を参照する
-- 外部キーが無いことは sqlite_master で確認済み（2026-08-12）。再構築後に同じ 2 本を作り直す。

PRAGMA foreign_keys = OFF;

BEGIN TRANSACTION;

CREATE TABLE journal_lines_new (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    journal_entry_id INTEGER NOT NULL REFERENCES journal_entries(id),
    line_no INTEGER NOT NULL,
    dc TEXT NOT NULL,                     -- D(借方) / C(貸方)
    account_id INTEGER NOT NULL REFERENCES accounts(id),
    sub_account_id INTEGER REFERENCES sub_accounts(id),
    department_id INTEGER REFERENCES departments(id),
    project_id INTEGER REFERENCES projects(id),
    amount INTEGER NOT NULL,              -- 円（税抜経理の本体額。税は別行）
    tax_category_id INTEGER NOT NULL REFERENCES tax_categories(id),  -- ADR-0052: 未設定は作らない（税と無関係な行は「対象外」を明示する）
    tax_input_mode TEXT,                  -- inclusive(内税) / exclusive(外税) / none
    input_amount INTEGER,                 -- ユーザー入力額（内税なら税込）。監査・再計算用
    is_tax_line INTEGER NOT NULL DEFAULT 0,  -- 1=システム生成の消費税行
    parent_line_no INTEGER,               -- 税行が紐づく元行の line_no
    description TEXT
);

INSERT INTO journal_lines_new
    (id, journal_entry_id, line_no, dc, account_id, sub_account_id, department_id, project_id,
     amount, tax_category_id, tax_input_mode, input_amount, is_tax_line, parent_line_no, description)
SELECT
     id, journal_entry_id, line_no, dc, account_id, sub_account_id, department_id, project_id,
     amount, tax_category_id, tax_input_mode, input_amount, is_tax_line, parent_line_no, description
FROM journal_lines;

DROP TABLE journal_lines;

ALTER TABLE journal_lines_new RENAME TO journal_lines;

CREATE INDEX IF NOT EXISTS idx_journal_lines_entry ON journal_lines(journal_entry_id);
CREATE INDEX IF NOT EXISTS idx_journal_lines_account ON journal_lines(account_id);

COMMIT;

PRAGMA foreign_keys = ON;
