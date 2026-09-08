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
-- **最後の 2 つは、GLOB が読めない値のためにある。**
--
-- **BLOB は TEXT の列にそのまま入る**（STRICT ではないので affinity が効かない。
-- 数値は text へ変換されるが、BLOB だけは BLOB のまま残る。2026-09-09 に実測）。
-- **BLOB の `x'4142'` と TEXT の `'AB'` は別の値**なので、COLLATE NOCASE の一意索引でも
-- ぶつからず、**見た目が同じ 2 行が並ぶ**——[qa/03 L-32](../../docs/qa/03_テストで漏らした実例.md) を
-- 別の入口から開け直すことになる。しかも GLOB は BLOB に対して「使える字」と答える。
-- **`typeof` で断る**（NULL は列の NOT NULL が「入れてください」の側で断るので、ここでは見ない）。
--
-- **NUL の条件は、値が TEXT であることを前提にしている。** SQLite の LENGTH と GLOB は文字列の途中の U+0000 で止まるので、
-- 'A' || char(0) || 'B' のような値は上の 6 条件を全部すり抜ける（2026-09-09 に実測。3.53.1）。
-- **値が TEXT なら**バイト数（BLOB へ写した長さ）と文字数が食い違えば ASCII でない——半角英数だけを通す規則と同じことを、
-- GLOB が読めない領域まで含めて言い直している。**関門は U+0000 を「目に見えない文字」として断る**ので、
-- ここが効くのは取込・CLI・SQL の直打ちだけである（それがこのトリガの持ち場でもある）。
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
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
     OR typeof(NEW.code) = 'blob'
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
     OR typeof(NEW.code) = 'blob'
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
     OR typeof(NEW.code) = 'blob'
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
     OR typeof(NEW.code) = 'blob'
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
     OR typeof(NEW.code) = 'blob'
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
     OR typeof(NEW.code) = 'blob'
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
     OR typeof(NEW.code) = 'blob'
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
     OR typeof(NEW.code) = 'blob'
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
     OR typeof(NEW.code) = 'blob'
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
     OR typeof(NEW.code) = 'blob'
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
     OR typeof(NEW.code) = 'blob'
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
     OR typeof(NEW.code) = 'blob'
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
