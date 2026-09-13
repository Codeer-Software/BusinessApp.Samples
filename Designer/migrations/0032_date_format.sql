-- 0032 日付の列は年月日として読める値だけを受け取る
--
-- 正典は Designer/ddl/011_date_format.sql（同じ内容をここから配る）。
--
-- date(x) は読めない値に NULL を返し、一意索引は NULL 同士を別物として扱う。
-- そのため ux_partner_invoice_registrations_valid_from は '20260401' のような値では重複を止めず、
-- 期間の重なりを見るトリガも比較が全部 NULL になって鳴らない（2026-09-13 実測）。
-- 文字列のまま比べている CHECK（002 の 2 本・006 の 1 本）も、
-- '2026-06-30' <= '2026-6-1' が辞書順で真になり、終わりが始まりより前の期間を通す。
-- 入口で ISO の年月日だけを受け取れば、どれも直る。
--
-- **2026-09-14 に稼働 DB で数えたら 9 列とも 0 件だった**（この差分を当てた回）。
--
-- **適用前に、矛盾する行を数える**（migrations/README。トリガは既にある行を評価しないので
-- 適用そのものは必ず成功するが、**残っていると上の 3 つの穴が開いたままになる**）。
--
--   SELECT '会計年度.開始日' AS 列, COUNT(*) AS 件数 FROM fiscal_years
--    WHERE start_date IS NOT NULL
--      AND (date(start_date) IS NOT substr(start_date, 1, 10)
--           OR LENGTH(CAST(start_date AS BLOB)) <> LENGTH(start_date))
--   UNION ALL
--   SELECT '会計年度.終了日', COUNT(*) FROM fiscal_years
--    WHERE end_date IS NOT NULL
--      AND (date(end_date) IS NOT substr(end_date, 1, 10)
--           OR LENGTH(CAST(end_date AS BLOB)) <> LENGTH(end_date))
--   UNION ALL
--   SELECT '会計年度.優良な電子帳簿の適用開始日', COUNT(*) FROM fiscal_years
--    WHERE premium_ledger_from IS NOT NULL
--      AND (date(premium_ledger_from) IS NOT substr(premium_ledger_from, 1, 10)
--           OR LENGTH(CAST(premium_ledger_from AS BLOB)) <> LENGTH(premium_ledger_from))
--   UNION ALL
--   SELECT '会計期間.開始日', COUNT(*) FROM accounting_periods
--    WHERE start_date IS NOT NULL
--      AND (date(start_date) IS NOT substr(start_date, 1, 10)
--           OR LENGTH(CAST(start_date AS BLOB)) <> LENGTH(start_date))
--   UNION ALL
--   SELECT '会計期間.終了日', COUNT(*) FROM accounting_periods
--    WHERE end_date IS NOT NULL
--      AND (date(end_date) IS NOT substr(end_date, 1, 10)
--           OR LENGTH(CAST(end_date AS BLOB)) <> LENGTH(end_date))
--   UNION ALL
--   SELECT '振替伝票.取引日', COUNT(*) FROM journal_entries
--    WHERE transaction_date IS NOT NULL
--      AND (date(transaction_date) IS NOT substr(transaction_date, 1, 10)
--           OR LENGTH(CAST(transaction_date AS BLOB)) <> LENGTH(transaction_date))
--   UNION ALL
--   SELECT '振替伝票.計上日', COUNT(*) FROM journal_entries
--    WHERE posting_date IS NOT NULL
--      AND (date(posting_date) IS NOT substr(posting_date, 1, 10)
--           OR LENGTH(CAST(posting_date AS BLOB)) <> LENGTH(posting_date))
--   UNION ALL
--   SELECT 'インボイス登録.登録年月日', COUNT(*) FROM partner_invoice_registrations
--    WHERE valid_from IS NOT NULL
--      AND (date(valid_from) IS NOT substr(valid_from, 1, 10)
--           OR LENGTH(CAST(valid_from AS BLOB)) <> LENGTH(valid_from))
--   UNION ALL
--   SELECT 'インボイス登録.取消・失効年月日', COUNT(*) FROM partner_invoice_registrations
--    WHERE ended_on IS NOT NULL
--      AND (date(ended_on) IS NOT substr(ended_on, 1, 10)
--           OR LENGTH(CAST(ended_on AS BLOB)) <> LENGTH(ended_on));
--
-- **どれも 0 でなければ、先に手で直してから適用する**（DML マイグレーションはフェーズ 3 まで無い）。
-- 当たった行は quote()・typeof()・hex() を添えて見る——**途中に U+0000 のある値は
-- quote() では NUL の手前までしか見えず、BLOB と TEXT も見分けが付かない**。
-- 稼働 DB の実測値は、この配達物を当てた人が控えること。
--
-- **8 本とも、この配達物の中で作られた順が正典と同じである**（表ごとに insert → update）。
-- 正典を 011 という新しい番号に置いたのは、fiscal_years が 008 に既にトリガを持つためで、
-- 002 の末尾に足すと正典と再生で作成順が食い違う（MigrationEquivalenceTests が見る）。

CREATE TRIGGER trg_fiscal_years_date_format_insert
BEFORE INSERT ON fiscal_years
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.start_date)) IS NOT substr(NEW.start_date, 1, 10)
        OR LENGTH(CAST(NEW.start_date AS BLOB)) <> LENGTH(NEW.start_date);
    SELECT RAISE(ABORT, '「終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.end_date)) IS NOT substr(NEW.end_date, 1, 10)
        OR LENGTH(CAST(NEW.end_date AS BLOB)) <> LENGTH(NEW.end_date);
    SELECT RAISE(ABORT, '「優良な電子帳簿の適用開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.premium_ledger_from)) IS NOT substr(NEW.premium_ledger_from, 1, 10)
        OR LENGTH(CAST(NEW.premium_ledger_from AS BLOB)) <> LENGTH(NEW.premium_ledger_from);
END;

CREATE TRIGGER trg_fiscal_years_date_format_update
BEFORE UPDATE OF start_date, end_date, premium_ledger_from ON fiscal_years
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.start_date)) IS NOT substr(NEW.start_date, 1, 10)
        OR LENGTH(CAST(NEW.start_date AS BLOB)) <> LENGTH(NEW.start_date);
    SELECT RAISE(ABORT, '「終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.end_date)) IS NOT substr(NEW.end_date, 1, 10)
        OR LENGTH(CAST(NEW.end_date AS BLOB)) <> LENGTH(NEW.end_date);
    SELECT RAISE(ABORT, '「優良な電子帳簿の適用開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.premium_ledger_from)) IS NOT substr(NEW.premium_ledger_from, 1, 10)
        OR LENGTH(CAST(NEW.premium_ledger_from AS BLOB)) <> LENGTH(NEW.premium_ledger_from);
END;

CREATE TRIGGER trg_accounting_periods_date_format_insert
BEFORE INSERT ON accounting_periods
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.start_date)) IS NOT substr(NEW.start_date, 1, 10)
        OR LENGTH(CAST(NEW.start_date AS BLOB)) <> LENGTH(NEW.start_date);
    SELECT RAISE(ABORT, '「終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.end_date)) IS NOT substr(NEW.end_date, 1, 10)
        OR LENGTH(CAST(NEW.end_date AS BLOB)) <> LENGTH(NEW.end_date);
END;

CREATE TRIGGER trg_accounting_periods_date_format_update
BEFORE UPDATE OF start_date, end_date ON accounting_periods
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.start_date)) IS NOT substr(NEW.start_date, 1, 10)
        OR LENGTH(CAST(NEW.start_date AS BLOB)) <> LENGTH(NEW.start_date);
    SELECT RAISE(ABORT, '「終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.end_date)) IS NOT substr(NEW.end_date, 1, 10)
        OR LENGTH(CAST(NEW.end_date AS BLOB)) <> LENGTH(NEW.end_date);
END;

CREATE TRIGGER trg_journal_entries_date_format_insert
BEFORE INSERT ON journal_entries
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「取引日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.transaction_date)) IS NOT substr(NEW.transaction_date, 1, 10)
        OR LENGTH(CAST(NEW.transaction_date AS BLOB)) <> LENGTH(NEW.transaction_date);
    SELECT RAISE(ABORT, '「計上日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.posting_date)) IS NOT substr(NEW.posting_date, 1, 10)
        OR LENGTH(CAST(NEW.posting_date AS BLOB)) <> LENGTH(NEW.posting_date);
END;

CREATE TRIGGER trg_journal_entries_date_format_update
BEFORE UPDATE OF transaction_date, posting_date ON journal_entries
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「取引日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.transaction_date)) IS NOT substr(NEW.transaction_date, 1, 10)
        OR LENGTH(CAST(NEW.transaction_date AS BLOB)) <> LENGTH(NEW.transaction_date);
    SELECT RAISE(ABORT, '「計上日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.posting_date)) IS NOT substr(NEW.posting_date, 1, 10)
        OR LENGTH(CAST(NEW.posting_date AS BLOB)) <> LENGTH(NEW.posting_date);
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
END;

CREATE TRIGGER trg_partner_invoice_registrations_date_format_update
BEFORE UPDATE OF valid_from, ended_on ON partner_invoice_registrations
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「登録年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.valid_from)) IS NOT substr(NEW.valid_from, 1, 10)
        OR LENGTH(CAST(NEW.valid_from AS BLOB)) <> LENGTH(NEW.valid_from);
    SELECT RAISE(ABORT, '「取消・失効年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.ended_on)) IS NOT substr(NEW.ended_on, 1, 10)
        OR LENGTH(CAST(NEW.ended_on AS BLOB)) <> LENGTH(NEW.ended_on);
END;
