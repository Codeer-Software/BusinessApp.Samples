-- 0023 マスタのコードの書式（docs/12 §2-1・ADR-0047）
--
-- 正典は Designer/ddl/008_master_code_format.sql（同じ内容をここから配る）。
--
-- **先に既存の行を 2 つ始末する。** どちらも探索的テスト（2026-09-04〜05）で作られたもので、
-- 2026-09-09 に稼働 DB を実測して数えた（この 2 行だけである）。
--   - accounts の `1100 `（末尾に空白）——**TRIM では直せない**。seed の現金（`1100`）と UNIQUE で衝突する。
--     計上済みの明細からの参照 0 行・ぶら下がる補助科目 0 件・無効化済みなので、行ごと消す
--   - partners.name_kana の空文字——A-5（空文字・空白だけは NULL に寄せる）の移行分
--
-- **これはデータ行の配布ではなく、既存行の移行である。** migrations の README が
-- 「データ行の同値の網が入るまで DML を書かない」と定めているのは配布のほうで、
-- 移行はそこに当たらない（消した行・NULL にした値は、再生した DB には最初から存在しない）。

DELETE FROM accounts
WHERE code = '1100 '
  AND name = '末尾に空白のあるコードの検証'
  AND NOT EXISTS (SELECT 1 FROM journal_lines WHERE journal_lines.account_id = accounts.id)
  AND NOT EXISTS (SELECT 1 FROM sub_accounts WHERE sub_accounts.account_id = accounts.id);

UPDATE partners
SET name_kana = NULL
WHERE name_kana IS NOT NULL
  AND TRIM(name_kana, ' ' || char(9) || char(10) || char(13) || char(12288)) = '';

CREATE TRIGGER trg_fiscal_years_code_format_insert
BEFORE INSERT ON fiscal_years
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
BEGIN
    SELECT RAISE(ABORT, '会計年度のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
END;

CREATE TRIGGER trg_fiscal_years_code_format_update
BEFORE UPDATE OF code ON fiscal_years
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
BEGIN
    SELECT RAISE(ABORT, '会計年度のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
END;

CREATE TRIGGER trg_tax_categories_code_format_insert
BEFORE INSERT ON tax_categories
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
BEGIN
    SELECT RAISE(ABORT, '税区分のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
END;

CREATE TRIGGER trg_tax_categories_code_format_update
BEFORE UPDATE OF code ON tax_categories
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
BEGIN
    SELECT RAISE(ABORT, '税区分のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
END;

CREATE TRIGGER trg_accounts_code_format_insert
BEFORE INSERT ON accounts
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
BEGIN
    SELECT RAISE(ABORT, '勘定科目のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
END;

CREATE TRIGGER trg_accounts_code_format_update
BEFORE UPDATE OF code ON accounts
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
BEGIN
    SELECT RAISE(ABORT, '勘定科目のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
END;

CREATE TRIGGER trg_sub_accounts_code_format_insert
BEFORE INSERT ON sub_accounts
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
BEGIN
    SELECT RAISE(ABORT, '補助科目のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
END;

CREATE TRIGGER trg_sub_accounts_code_format_update
BEFORE UPDATE OF code ON sub_accounts
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
BEGIN
    SELECT RAISE(ABORT, '補助科目のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
END;

CREATE TRIGGER trg_departments_code_format_insert
BEFORE INSERT ON departments
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
BEGIN
    SELECT RAISE(ABORT, '部門のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
END;

CREATE TRIGGER trg_departments_code_format_update
BEFORE UPDATE OF code ON departments
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
BEGIN
    SELECT RAISE(ABORT, '部門のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
END;

CREATE TRIGGER trg_partners_code_format_insert
BEFORE INSERT ON partners
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
BEGIN
    SELECT RAISE(ABORT, '取引先のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
END;

CREATE TRIGGER trg_partners_code_format_update
BEFORE UPDATE OF code ON partners
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
BEGIN
    SELECT RAISE(ABORT, '取引先のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
END;

-- 大小を無視した重複を止める。**保存される字は入力のまま**で、畳むのは判定だけである（ADR-0047 の決定 7）。
-- 列の UNIQUE（バイト列で見る）は残す——外すには表の作り直しが要り、得るのは重複した制約 1 本の削除だけ。
CREATE UNIQUE INDEX ux_fiscal_years_code_nocase ON fiscal_years (code COLLATE NOCASE);
CREATE UNIQUE INDEX ux_tax_categories_code_nocase ON tax_categories (code COLLATE NOCASE);
CREATE UNIQUE INDEX ux_accounts_code_nocase ON accounts (code COLLATE NOCASE);
CREATE UNIQUE INDEX ux_sub_accounts_code_nocase ON sub_accounts (account_id, code COLLATE NOCASE);
CREATE UNIQUE INDEX ux_departments_code_nocase ON departments (code COLLATE NOCASE);
CREATE UNIQUE INDEX ux_partners_code_nocase ON partners (code COLLATE NOCASE);
