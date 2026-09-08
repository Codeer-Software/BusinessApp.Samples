-- 010 自然キーになる列に BLOB を入れさせない（ADR-0047 の決定 1 の考え方を、コード以外へ広げる）
--
-- **BLOB は TEXT の列にそのまま入る**（STRICT ではないので affinity が効かない。
-- 数値は text へ変換されるが、BLOB だけは BLOB のまま残る。2026-09-09 に実測）。
-- **`x'61646D696E'` と `'admin'` は別の値**なので UNIQUE でもぶつからず、GLOB の CHECK も素通りする。
--
-- **008 でコードの 6 表を塞いだが、同じ穴が「自然キーとして使う列」に残っていた**
-- （2026-09-09 の自己レビュー）。塞ぐのは 2 つ。
--
--   partners.corporate_number  名寄せの自然キー（ADR-0028）。二重になると誤束ねが黙って起こる
--   app_users.user_name        ログインの照合キー。同じ名前の利用者が 2 人並ぶ
--
-- **CHECK ではなくトリガで書く。** CHECK を足すには表の作り直しが要り、
-- どちらの表も参照されている（008 と同じ判断）。`app_users` は
-- **本体の列を変えない**という約束もある（007 のヘッダ）ので、トリガ以外に手が無い。

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
