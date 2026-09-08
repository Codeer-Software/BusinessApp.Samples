-- 0028 自然キーになる列に BLOB を入れさせない
--
-- 正典は Designer/ddl/010_natural_key_text.sql（同じ内容をここから配る）。
--
-- **適用前に、矛盾する行を数える**（migrations/README。トリガは既存行を評価しないので
-- 適用そのものは必ず成功するが、**残っていると見た目が同じ 2 行が生き続ける**）。
--
--   SELECT 'partners.corporate_number' AS col, COUNT(*) AS n FROM partners
--    WHERE corporate_number IS NOT NULL AND typeof(corporate_number) <> 'text'
--   UNION ALL
--   SELECT 'app_users.user_name', COUNT(*) FROM app_users WHERE typeof(user_name) <> 'text';
--
-- **どちらも 0 でなければ、先に手で直してから適用する**（DML マイグレーションはフェーズ 3 まで無い）。
-- 稼働 DB は 2026-09-09 に実測して 0 だった。

CREATE TRIGGER trg_partners_corporate_number_text_insert
BEFORE INSERT ON partners
FOR EACH ROW
WHEN typeof(NEW.corporate_number) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '法人番号は 13 桁の数字で入れる。');
END;

CREATE TRIGGER trg_partners_corporate_number_text_update
BEFORE UPDATE OF corporate_number ON partners
FOR EACH ROW
WHEN typeof(NEW.corporate_number) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '法人番号は 13 桁の数字で入れる。');
END;

CREATE TRIGGER trg_app_users_user_name_text_insert
BEFORE INSERT ON app_users
FOR EACH ROW
WHEN typeof(NEW.user_name) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '識別名は文字列で入れる。');
END;

CREATE TRIGGER trg_app_users_user_name_text_update
BEFORE UPDATE OF user_name ON app_users
FOR EACH ROW
WHEN typeof(NEW.user_name) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '識別名は文字列で入れる。');
END;
