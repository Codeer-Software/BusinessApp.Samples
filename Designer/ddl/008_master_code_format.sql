-- 008 マスタのコードの書式（docs/12 §2-1・ADR-0047）
--
-- コードを持つ 6 つの表すべてに、同じ規則を当てる。
-- 半角英数字とハイフン・アンダーバーだけ。記号は先頭・末尾に置けず、連続もできない。長さは 1〜20。
--
-- **CHECK ではなくトリガで書く。** CHECK を後から足すには表を作り直すしかなく、
-- accounts は journal_lines から参照され、既にトリガも持っているので、作り直しは危険に見合わない。
-- トリガなら追加で配れる（005 のマスタの守りと同じ形）。
--
-- **この 1 ファイルにまとめる理由は、トリガの作られる順である。** 004 の末尾に足すと、
-- 既存 DB へ配る側（migrations）では 005 のトリガより後に作られ、正典と順が食い違う。
-- 同値テストは表ごとのトリガの作られた順まで見る（MigrationEquivalenceTests）。
--
-- **前後の空白を落とすのは関門（C# の MasterCode）の仕事**である。ここへ来る値は落とした後の姿で、
-- 空白が残っていれば「使えない字」として断る——字種の GLOB がすべての空白を拾う。
--
-- 大小を無視した重複は、末尾の一意索引が止める。COLLATE NOCASE が畳むのは ASCII の英字だけだが、
-- 字種を半角英数に絞ってあるので過不足なく噛み合う。

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
