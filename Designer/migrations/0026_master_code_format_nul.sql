-- 0026 コードの書式のトリガに、NUL を弾く条件を足す
--
-- 正典は Designer/ddl/008_master_code_format.sql。
--
-- **SQLite の LENGTH と GLOB は、文字列の途中の U+0000 で止まる**（2026-09-09 に実測。3.53.1）。
--   select length('A' || char(0) || 'B')                  -> 1
--   select 'A' || char(0) || 'B' glob '*[^0-9A-Za-z_-]*'  -> 0（＝使える字だと答える）
--   select hex('A' || char(0) || 'B')                     -> 410042（値は 3 文字ぶん入っている）
-- そのため、NUL を混ぜたコードは 008 の 6 条件を全部すり抜けて保存できた。
-- **関門（C# の MasterCode）は U+0000 を「目に見えない文字」として断る**ので、
-- 通ってしまうのは取込・CLI・SQL の直打ち——**トリガが最後の守りである持ち場そのもの**である。
--
-- バイト数と文字数を突き合わせる条件を足して塞ぐ。半角英数だけのコードでは必ず等しく、
-- NUL でも非 ASCII でも食い違う（自己レビュー。2026-09-09）。
--
-- **009 の 3 本も作り直す。** 表ごとのトリガの作られた順は、正典と再生（baseline + migrations）で
-- 一致していないといけない（MigrationEquivalenceTests）。partners の書式のトリガを作り直すと、
-- そのままでは意味の凍結の 3 本より後ろに回ってしまう。
--
-- **既存の行は評価しない**（トリガは新しく書き込まれる値しか見ない。migrations/README）。

DROP TRIGGER trg_fiscal_years_code_format_insert;
DROP TRIGGER trg_fiscal_years_code_format_update;
DROP TRIGGER trg_tax_categories_code_format_insert;
DROP TRIGGER trg_tax_categories_code_format_update;
DROP TRIGGER trg_accounts_code_format_insert;
DROP TRIGGER trg_accounts_code_format_update;
DROP TRIGGER trg_sub_accounts_code_format_insert;
DROP TRIGGER trg_sub_accounts_code_format_update;
DROP TRIGGER trg_departments_code_format_insert;
DROP TRIGGER trg_departments_code_format_update;
DROP TRIGGER trg_partners_code_format_insert;
DROP TRIGGER trg_partners_code_format_update;
DROP TRIGGER trg_partners_meaning_frozen_when_posted;
DROP TRIGGER trg_partners_no_replace_used_insert;
DROP TRIGGER trg_partners_no_replace_used_update;

CREATE TRIGGER trg_fiscal_years_code_format_insert
BEFORE INSERT ON fiscal_years
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
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
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
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
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
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
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
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
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
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
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
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
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
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
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
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
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
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
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
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
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
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
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
BEGIN
    SELECT RAISE(ABORT, '取引先のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
END;

CREATE TRIGGER trg_partners_meaning_frozen_when_posted
BEFORE UPDATE OF code ON partners
FOR EACH ROW WHEN NEW.code IS NOT OLD.code
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳が使っている取引先のコードは変更できない。新しい取引先を作る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.partner_id = OLD.id AND e.status = 'posted')
        OR EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.partner_id = OLD.id AND e.status = 'posted');
END;

-- REPLACE で id を乗っ取る経路（qa/03 L-26 と同じ型）。
-- 使用中の id へ別の行を流し込むと、UPDATE のトリガを通らずに意味が変わる。
CREATE TRIGGER trg_partners_no_replace_used_insert
BEFORE INSERT ON partners
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳が使っている取引先は置き換えられない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.partner_id = NEW.id AND e.status = 'posted')
        OR EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.partner_id = NEW.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_partners_no_replace_used_update
BEFORE UPDATE OF id ON partners
FOR EACH ROW WHEN NEW.id IS NOT OLD.id
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳が使っている取引先は置き換えられない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.partner_id IN (OLD.id, NEW.id) AND e.status = 'posted')
        OR EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.partner_id IN (OLD.id, NEW.id) AND e.status = 'posted');
END;
