-- 0033 日付の守りに残りの 3 列を足す
--
-- 正典は Designer/ddl/011_date_format.sql（同じ内容をここから配る）。
--
-- 0032 で 4 表 9 列を塞いだが、**DATE で宣言した列を数え直したら 3 列漏れていた**
-- （2026-09-14。journal_lines.tax_point・partner_invoice_registrations.confirmed_on・
-- 同 .nta_updated_on）。**漏れた列は、0032 が閉じた 3 つの穴をそのまま抱えている。**
--
-- **partner_invoice_registrations の 2 本は作り直す**（トリガは ALTER できない）。
-- DROP してから CREATE すると、その表の中では最後に作られた状態になる——
-- 正典でも 011 が最後なので、作成順は食い違わない（migrations/README）。
--
-- 適用前に矛盾する行が無いことは、0032 と同じ SELECT で確かめた（2026-09-14。0 件）。

DROP TRIGGER trg_partner_invoice_registrations_date_format_insert;
DROP TRIGGER trg_partner_invoice_registrations_date_format_update;

CREATE TRIGGER trg_journal_lines_date_format_insert
BEFORE INSERT ON journal_lines
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「課税仕入れの時点」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.tax_point)) IS NOT substr(NEW.tax_point, 1, 10)
        OR LENGTH(CAST(NEW.tax_point AS BLOB)) <> LENGTH(NEW.tax_point);
END;

CREATE TRIGGER trg_journal_lines_date_format_update
BEFORE UPDATE OF tax_point ON journal_lines
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「課税仕入れの時点」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.tax_point)) IS NOT substr(NEW.tax_point, 1, 10)
        OR LENGTH(CAST(NEW.tax_point AS BLOB)) <> LENGTH(NEW.tax_point);
END;

CREATE TRIGGER trg_partner_invoice_registrations_date_format_insert
BEFORE INSERT ON partner_invoice_registrations
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「登録年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.valid_from)) IS NOT substr(NEW.valid_from, 1, 10)
        OR LENGTH(CAST(NEW.valid_from AS BLOB)) <> LENGTH(NEW.valid_from);
    SELECT RAISE(ABORT, '「取消・失効年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.ended_on)) IS NOT substr(NEW.ended_on, 1, 10)
        OR LENGTH(CAST(NEW.ended_on AS BLOB)) <> LENGTH(NEW.ended_on);
    SELECT RAISE(ABORT, '「最終確認日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.confirmed_on)) IS NOT substr(NEW.confirmed_on, 1, 10)
        OR LENGTH(CAST(NEW.confirmed_on AS BLOB)) <> LENGTH(NEW.confirmed_on);
    SELECT RAISE(ABORT, '「公表システムの更新年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.nta_updated_on)) IS NOT substr(NEW.nta_updated_on, 1, 10)
        OR LENGTH(CAST(NEW.nta_updated_on AS BLOB)) <> LENGTH(NEW.nta_updated_on);
END;

CREATE TRIGGER trg_partner_invoice_registrations_date_format_update
BEFORE UPDATE OF valid_from, ended_on, confirmed_on, nta_updated_on ON partner_invoice_registrations
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「登録年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.valid_from)) IS NOT substr(NEW.valid_from, 1, 10)
        OR LENGTH(CAST(NEW.valid_from AS BLOB)) <> LENGTH(NEW.valid_from);
    SELECT RAISE(ABORT, '「取消・失効年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.ended_on)) IS NOT substr(NEW.ended_on, 1, 10)
        OR LENGTH(CAST(NEW.ended_on AS BLOB)) <> LENGTH(NEW.ended_on);
    SELECT RAISE(ABORT, '「最終確認日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.confirmed_on)) IS NOT substr(NEW.confirmed_on, 1, 10)
        OR LENGTH(CAST(NEW.confirmed_on AS BLOB)) <> LENGTH(NEW.confirmed_on);
    SELECT RAISE(ABORT, '「公表システムの更新年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.nta_updated_on)) IS NOT substr(NEW.nta_updated_on, 1, 10)
        OR LENGTH(CAST(NEW.nta_updated_on AS BLOB)) <> LENGTH(NEW.nta_updated_on);
END;
