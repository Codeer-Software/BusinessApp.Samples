-- 540_department_required.sql — 部門の必須化（ADR-0056・2026-08-14）
--
-- 実データを数えると、部門別 P/L はほとんど機能していなかった（費用 38 行中 29 行・
-- 収益 18 行中 15 行が部門なし）。ADR-0052 で税区分について決めたのと同じ形——
-- **「未設定（NULL）」という表現を廃止し、意味のある既定値で埋めて NOT NULL にする**——を部門にも適用する。
--
-- 「損益科目の行だけ NOT NULL」は列制約では書けない（判定に accounts.account_type が要るが、
-- SQLite の CHECK はサブクエリを書けない）。全行 NOT NULL にして、意味を持たない行には
-- 「全社共通」を入れる。税区分で BS 科目にも「対象外」を入れたのと同じ冗長さで、一貫している。
--
-- **実行前にサーバを停止し、DB ファイルをバックアップすること。**
-- ※ ALTER TABLE は冪等でない。このファイルは 1 回だけ流す移行スクリプト。

-- ---- ① 共通費の受け皿を、コードではなくフラグで指せるようにする ----
-- 「全社共通」（code 00）は最初から部門マスタにあったが、使われていなかった。
-- 以後アプリはこのフラグで受け皿を解決する（コード '00' の直書きをしない）。
ALTER TABLE departments ADD COLUMN is_common INTEGER NOT NULL DEFAULT 0;

UPDATE departments SET is_common = 1 WHERE code = '00';

-- ---- ② 既存の部門なし明細を全社共通に寄せる ----
-- 金額は 1 円も動かない。予実対比の「(部門未設定)」行が消えて全社共通に移る。
UPDATE journal_lines
   SET department_id = (SELECT id FROM departments WHERE is_common = 1)
 WHERE department_id IS NULL;

-- ---- ③ NOT NULL 化（SQLite は既存列に NOT NULL を付けられないのでテーブル再構築） ----
-- 500 と同じ手順。journal_lines に紐づくのはインデックス 2 本のみで、
-- トリガー・ビュー・journal_lines を参照する外部キーは無い（500 実施時に確認済み）。

PRAGMA foreign_keys = OFF;

BEGIN TRANSACTION;

CREATE TABLE journal_lines_new (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    journal_entry_id INTEGER NOT NULL REFERENCES journal_entries(id),
    line_no INTEGER NOT NULL,
    dc TEXT NOT NULL,                     -- D(借方) / C(貸方)
    account_id INTEGER NOT NULL REFERENCES accounts(id),
    sub_account_id INTEGER REFERENCES sub_accounts(id),
    department_id INTEGER NOT NULL REFERENCES departments(id),  -- ADR-0056: 未設定は作らない（意味を持たない行は「全社共通」を明示する）
    project_id INTEGER REFERENCES projects(id),
    amount INTEGER NOT NULL,              -- 円（税抜経理の本体額。税は別行）
    tax_category_id INTEGER NOT NULL REFERENCES tax_categories(id),  -- ADR-0052
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

-- ---- ④ 起票経路が部門を持てるようにする列を足す ----
-- 銀行明細（取込した明細ごとに部門を選ぶ）と、その自動割当ルール
ALTER TABLE bank_statement_lines ADD COLUMN department_id INTEGER REFERENCES departments(id);
ALTER TABLE matching_rules ADD COLUMN department_id INTEGER REFERENCES departments(id);
-- 仕入先請求書（伝票ヘッダが部門を持ち、費用行に転記する。販売伝票 ADR-0029 と同じ考え方）
ALTER TABLE vendor_invoices ADD COLUMN department_id INTEGER REFERENCES departments(id);
-- 定型仕訳（明細ごとに部門を持たせる。家賃→全社共通、開発部の経費→開発1部 のように固定できる）
ALTER TABLE journal_template_lines ADD COLUMN department_id INTEGER REFERENCES departments(id);
