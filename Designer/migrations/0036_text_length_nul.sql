--------------------------------------------------------------------------------
-- 0036 文字の欄の上限を作り直す（docs/12 §2-2）
--
-- **0035 が作った 14 本を落として、16 本を作り直す。** 直すのは 3 点である。
--
--   1. **NUL を挟むと上限をすり抜けられた**（自己レビューのラウンド 94。2 人が独立に指摘）。
--      `LENGTH(CAST(x AS BLOB)) > 4 * LENGTH(x)` は 1 符号点あたり 3 バイトの余白を残すので、
--      `'A'×30 || NUL || 'B'×60` が `accounts.name`（上限 30）に入った。
--      **`instr(CAST(x AS BLOB), x'00') > 0` に変える。**
--   2. **会計年度の「年度名」を足す**（旧 Q-20 の問いは会計年度を「マスタの名前」に含めていた）。
--   3. **`partners.name_kana` の呼び名を「取引先のカナ」にする**——
--      この文言が届くのは画面を通らない経路の人で、「カナ」では表が決まらない。
--
-- **0035 を書き換えないのは、既に適用したからである**（ADR-0020。`migrate.ps1 -Apply` が
-- SHA-256 で照合する）。**落としてから作り直すので、表ごとのトリガの作られる順は
-- 正典（`ddl/012`）と同じまま**である——012 は最後のファイルで、この 16 本はどの表でも末尾に並ぶ。
--
-- **既にある行は見ない**（トリガは追加と更新のときだけ鳴る）ので、適用は必ず成功する。
-- **数え直しは `LENGTH()` だけに頼らない**——0035 のヘッダに書いた数え方は、
-- **まさに捕まえたい BLOB と NUL 入りを数え落とす**（同じラウンドの指摘）。
--
--   SELECT '<表>.<列>' AS 欄, COUNT(*) FROM <表>
--    WHERE typeof(<列>) = 'blob'
--       OR instr(CAST(<列> AS BLOB), x'00') > 0
--       OR LENGTH(CAST(<列> AS BLOB)) > 4 * LENGTH(<列>)
--       OR LENGTH(<列>) > <上限>;
--
-- **2026-09-14 に稼働 DB で 8 欄とも 0 件。**
--------------------------------------------------------------------------------

DROP TRIGGER trg_accounts_name_length_insert;
DROP TRIGGER trg_accounts_name_length_update;
DROP TRIGGER trg_sub_accounts_name_length_insert;
DROP TRIGGER trg_sub_accounts_name_length_update;
DROP TRIGGER trg_departments_name_length_insert;
DROP TRIGGER trg_departments_name_length_update;
DROP TRIGGER trg_tax_categories_name_length_insert;
DROP TRIGGER trg_tax_categories_name_length_update;
DROP TRIGGER trg_partners_name_length_insert;
DROP TRIGGER trg_partners_name_length_update;
DROP TRIGGER trg_partners_name_kana_length_insert;
DROP TRIGGER trg_partners_name_kana_length_update;
DROP TRIGGER trg_partners_address_length_insert;
DROP TRIGGER trg_partners_address_length_update;

CREATE TRIGGER trg_accounts_name_length_insert
BEFORE INSERT ON accounts
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「科目名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「科目名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_accounts_name_length_update
BEFORE UPDATE OF name ON accounts
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「科目名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「科目名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_sub_accounts_name_length_insert
BEFORE INSERT ON sub_accounts
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「補助科目名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「補助科目名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_sub_accounts_name_length_update
BEFORE UPDATE OF name ON sub_accounts
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「補助科目名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「補助科目名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_departments_name_length_insert
BEFORE INSERT ON departments
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「部門名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「部門名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_departments_name_length_update
BEFORE UPDATE OF name ON departments
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「部門名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「部門名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_tax_categories_name_length_insert
BEFORE INSERT ON tax_categories
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「税区分名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「税区分名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_tax_categories_name_length_update
BEFORE UPDATE OF name ON tax_categories
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「税区分名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「税区分名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_fiscal_years_label_length_insert
BEFORE INSERT ON fiscal_years
FOR EACH ROW
WHEN NEW.label IS NOT NULL
     AND (LENGTH(NEW.label) > 30
          OR typeof(NEW.label) = 'blob'
          OR instr(CAST(NEW.label AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.label AS BLOB)) > 4 * LENGTH(NEW.label))
BEGIN
    SELECT RAISE(ABORT, '「年度名」に使えない字が入っている。')
     WHERE typeof(NEW.label) = 'blob'
        OR instr(CAST(NEW.label AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.label AS BLOB)) > 4 * LENGTH(NEW.label);
    SELECT RAISE(ABORT, '「年度名」は 30 文字以内。')
     WHERE LENGTH(NEW.label) > 30;
END;

CREATE TRIGGER trg_fiscal_years_label_length_update
BEFORE UPDATE OF label ON fiscal_years
FOR EACH ROW
WHEN NEW.label IS NOT NULL
     AND (LENGTH(NEW.label) > 30
          OR typeof(NEW.label) = 'blob'
          OR instr(CAST(NEW.label AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.label AS BLOB)) > 4 * LENGTH(NEW.label))
BEGIN
    SELECT RAISE(ABORT, '「年度名」に使えない字が入っている。')
     WHERE typeof(NEW.label) = 'blob'
        OR instr(CAST(NEW.label AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.label AS BLOB)) > 4 * LENGTH(NEW.label);
    SELECT RAISE(ABORT, '「年度名」は 30 文字以内。')
     WHERE LENGTH(NEW.label) > 30;
END;

CREATE TRIGGER trg_partners_name_length_insert
BEFORE INSERT ON partners
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 100
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「取引先名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「取引先名」は 100 文字以内。')
     WHERE LENGTH(NEW.name) > 100;
END;

CREATE TRIGGER trg_partners_name_length_update
BEFORE UPDATE OF name ON partners
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 100
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「取引先名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「取引先名」は 100 文字以内。')
     WHERE LENGTH(NEW.name) > 100;
END;

CREATE TRIGGER trg_partners_name_kana_length_insert
BEFORE INSERT ON partners
FOR EACH ROW
WHEN NEW.name_kana IS NOT NULL
     AND (LENGTH(NEW.name_kana) > 100
          OR typeof(NEW.name_kana) = 'blob'
          OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「取引先のカナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「取引先のカナ」は 100 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 100;
END;

CREATE TRIGGER trg_partners_name_kana_length_update
BEFORE UPDATE OF name_kana ON partners
FOR EACH ROW
WHEN NEW.name_kana IS NOT NULL
     AND (LENGTH(NEW.name_kana) > 100
          OR typeof(NEW.name_kana) = 'blob'
          OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「取引先のカナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「取引先のカナ」は 100 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 100;
END;

CREATE TRIGGER trg_partners_address_length_insert
BEFORE INSERT ON partners
FOR EACH ROW
WHEN NEW.address IS NOT NULL
     AND (LENGTH(NEW.address) > 200
          OR typeof(NEW.address) = 'blob'
          OR instr(CAST(NEW.address AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address))
BEGIN
    SELECT RAISE(ABORT, '「所在地」に使えない字が入っている。')
     WHERE typeof(NEW.address) = 'blob'
        OR instr(CAST(NEW.address AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address);
    SELECT RAISE(ABORT, '「所在地」は 200 文字以内。')
     WHERE LENGTH(NEW.address) > 200;
END;

CREATE TRIGGER trg_partners_address_length_update
BEFORE UPDATE OF address ON partners
FOR EACH ROW
WHEN NEW.address IS NOT NULL
     AND (LENGTH(NEW.address) > 200
          OR typeof(NEW.address) = 'blob'
          OR instr(CAST(NEW.address AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address))
BEGIN
    SELECT RAISE(ABORT, '「所在地」に使えない字が入っている。')
     WHERE typeof(NEW.address) = 'blob'
        OR instr(CAST(NEW.address AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address);
    SELECT RAISE(ABORT, '「所在地」は 200 文字以内。')
     WHERE LENGTH(NEW.address) > 200;
END;
