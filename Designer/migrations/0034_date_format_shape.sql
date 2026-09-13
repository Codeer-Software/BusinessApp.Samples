-- 0034 日付の守りを、読み出し側が読める書式ちょうどに揃える
--
-- 正典は Designer/ddl/011_date_format.sql（同じ内容をここから配る）。
--
-- 0032・0033 の条件は**読み出し側より広かった**（2026-09-14 の自己レビュー。qa/02 のラウンド 89）。
--   ・末尾の空白・タブ・改行・裸の T が素通りする（date() が読み飛ばすため）
--   ・'…Z'・'…+09:00'・'HH:mm' 止まりを通すが、DbValue.ParseDateTime は TryParseExact なので読めない
--     ——**その 1 行が入ると AccountingMasterLoader が計上のたびに落ち、計上が全面停止する**
--
-- **10 本とも作り直す**（トリガは ALTER できない）。DROP してから CREATE すると
-- その表の中では最後に作られた状態になる——正典でも 011 が最後なので順は食い違わない。
--
-- **適用前に、矛盾する行を数える**（migrations/README。トリガは既にある行を見ないので、
-- 適用は必ず成功し、残った行は黙って残る）。**0032 の SELECT は julianday() を包み忘れており、
-- 実在しない日を 1 件も数えられていなかった**ので、ここでは条件を 3 つそのまま使う。
--
-- --   SELECT '会計年度.開始日' AS 列, COUNT(*) AS 件数 FROM fiscal_years
--    WHERE start_date IS NOT NULL AND (date(julianday(start_date)) IS NOT substr(start_date, 1, 10)
--                  OR (start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
--                      AND start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
--                      AND NOT (start_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
--                               AND substr(start_date, 21) NOT GLOB '*[^0-9]*'
--                               AND LENGTH(start_date) <= 27))
--                  OR LENGTH(CAST(start_date AS BLOB)) <> LENGTH(start_date))
-- --   SELECT '会計年度.終了日' AS 列, COUNT(*) AS 件数 FROM fiscal_years
--    WHERE end_date IS NOT NULL AND (date(julianday(end_date)) IS NOT substr(end_date, 1, 10)
--                  OR (end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
--                      AND end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
--                      AND NOT (end_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
--                               AND substr(end_date, 21) NOT GLOB '*[^0-9]*'
--                               AND LENGTH(end_date) <= 27))
--                  OR LENGTH(CAST(end_date AS BLOB)) <> LENGTH(end_date))
-- --   SELECT '会計年度.優良な電子帳簿の適用開始日' AS 列, COUNT(*) AS 件数 FROM fiscal_years
--    WHERE premium_ledger_from IS NOT NULL AND (date(julianday(premium_ledger_from)) IS NOT substr(premium_ledger_from, 1, 10)
--                  OR (premium_ledger_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
--                      AND premium_ledger_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
--                      AND NOT (premium_ledger_from GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
--                               AND substr(premium_ledger_from, 21) NOT GLOB '*[^0-9]*'
--                               AND LENGTH(premium_ledger_from) <= 27))
--                  OR LENGTH(CAST(premium_ledger_from AS BLOB)) <> LENGTH(premium_ledger_from))
-- --   SELECT '会計期間.開始日' AS 列, COUNT(*) AS 件数 FROM accounting_periods
--    WHERE start_date IS NOT NULL AND (date(julianday(start_date)) IS NOT substr(start_date, 1, 10)
--                  OR (start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
--                      AND start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
--                      AND NOT (start_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
--                               AND substr(start_date, 21) NOT GLOB '*[^0-9]*'
--                               AND LENGTH(start_date) <= 27))
--                  OR LENGTH(CAST(start_date AS BLOB)) <> LENGTH(start_date))
-- --   SELECT '会計期間.終了日' AS 列, COUNT(*) AS 件数 FROM accounting_periods
--    WHERE end_date IS NOT NULL AND (date(julianday(end_date)) IS NOT substr(end_date, 1, 10)
--                  OR (end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
--                      AND end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
--                      AND NOT (end_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
--                               AND substr(end_date, 21) NOT GLOB '*[^0-9]*'
--                               AND LENGTH(end_date) <= 27))
--                  OR LENGTH(CAST(end_date AS BLOB)) <> LENGTH(end_date))
-- --   SELECT '振替伝票.取引日' AS 列, COUNT(*) AS 件数 FROM journal_entries
--    WHERE transaction_date IS NOT NULL AND (date(julianday(transaction_date)) IS NOT substr(transaction_date, 1, 10)
--                  OR (transaction_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
--                      AND transaction_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
--                      AND NOT (transaction_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
--                               AND substr(transaction_date, 21) NOT GLOB '*[^0-9]*'
--                               AND LENGTH(transaction_date) <= 27))
--                  OR LENGTH(CAST(transaction_date AS BLOB)) <> LENGTH(transaction_date))
-- --   SELECT '振替伝票.計上日' AS 列, COUNT(*) AS 件数 FROM journal_entries
--    WHERE posting_date IS NOT NULL AND (date(julianday(posting_date)) IS NOT substr(posting_date, 1, 10)
--                  OR (posting_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
--                      AND posting_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
--                      AND NOT (posting_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
--                               AND substr(posting_date, 21) NOT GLOB '*[^0-9]*'
--                               AND LENGTH(posting_date) <= 27))
--                  OR LENGTH(CAST(posting_date AS BLOB)) <> LENGTH(posting_date))
-- --   SELECT '仕訳明細.課税仕入れの時点' AS 列, COUNT(*) AS 件数 FROM journal_lines
--    WHERE tax_point IS NOT NULL AND (date(julianday(tax_point)) IS NOT substr(tax_point, 1, 10)
--                  OR (tax_point NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
--                      AND tax_point NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
--                      AND NOT (tax_point GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
--                               AND substr(tax_point, 21) NOT GLOB '*[^0-9]*'
--                               AND LENGTH(tax_point) <= 27))
--                  OR LENGTH(CAST(tax_point AS BLOB)) <> LENGTH(tax_point))
-- --   SELECT 'インボイス登録.登録年月日' AS 列, COUNT(*) AS 件数 FROM partner_invoice_registrations
--    WHERE valid_from IS NOT NULL AND (date(julianday(valid_from)) IS NOT substr(valid_from, 1, 10)
--                  OR (valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
--                      AND valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
--                      AND NOT (valid_from GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
--                               AND substr(valid_from, 21) NOT GLOB '*[^0-9]*'
--                               AND LENGTH(valid_from) <= 27))
--                  OR LENGTH(CAST(valid_from AS BLOB)) <> LENGTH(valid_from))
-- --   SELECT 'インボイス登録.取消・失効年月日' AS 列, COUNT(*) AS 件数 FROM partner_invoice_registrations
--    WHERE ended_on IS NOT NULL AND (date(julianday(ended_on)) IS NOT substr(ended_on, 1, 10)
--                  OR (ended_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
--                      AND ended_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
--                      AND NOT (ended_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
--                               AND substr(ended_on, 21) NOT GLOB '*[^0-9]*'
--                               AND LENGTH(ended_on) <= 27))
--                  OR LENGTH(CAST(ended_on AS BLOB)) <> LENGTH(ended_on))
-- --   SELECT 'インボイス登録.最終確認日' AS 列, COUNT(*) AS 件数 FROM partner_invoice_registrations
--    WHERE confirmed_on IS NOT NULL AND (date(julianday(confirmed_on)) IS NOT substr(confirmed_on, 1, 10)
--                  OR (confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
--                      AND confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
--                      AND NOT (confirmed_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
--                               AND substr(confirmed_on, 21) NOT GLOB '*[^0-9]*'
--                               AND LENGTH(confirmed_on) <= 27))
--                  OR LENGTH(CAST(confirmed_on AS BLOB)) <> LENGTH(confirmed_on))
-- --   SELECT 'インボイス登録.公表システムの更新年月日' AS 列, COUNT(*) AS 件数 FROM partner_invoice_registrations
--    WHERE nta_updated_on IS NOT NULL AND (date(julianday(nta_updated_on)) IS NOT substr(nta_updated_on, 1, 10)
--                  OR (nta_updated_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
--                      AND nta_updated_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
--                      AND NOT (nta_updated_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
--                               AND substr(nta_updated_on, 21) NOT GLOB '*[^0-9]*'
--                               AND LENGTH(nta_updated_on) <= 27))
--                  OR LENGTH(CAST(nta_updated_on AS BLOB)) <> LENGTH(nta_updated_on))
--
-- **2026-09-14 に稼働 DB で数えたら 12 列とも 0 件だった**（この差分を当てた回）。

DROP TRIGGER trg_fiscal_years_date_format_insert;
DROP TRIGGER trg_fiscal_years_date_format_update;
DROP TRIGGER trg_accounting_periods_date_format_insert;
DROP TRIGGER trg_accounting_periods_date_format_update;
DROP TRIGGER trg_journal_entries_date_format_insert;
DROP TRIGGER trg_journal_entries_date_format_update;
DROP TRIGGER trg_journal_lines_date_format_insert;
DROP TRIGGER trg_journal_lines_date_format_update;
DROP TRIGGER trg_partner_invoice_registrations_date_format_insert;
DROP TRIGGER trg_partner_invoice_registrations_date_format_update;

CREATE TRIGGER trg_fiscal_years_date_format_insert
BEFORE INSERT ON fiscal_years
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.start_date)) IS NOT substr(NEW.start_date, 1, 10)
        OR (NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.start_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.start_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.start_date) <= 27))
        OR LENGTH(CAST(NEW.start_date AS BLOB)) <> LENGTH(NEW.start_date);
    SELECT RAISE(ABORT, '「終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.end_date)) IS NOT substr(NEW.end_date, 1, 10)
        OR (NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.end_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.end_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.end_date) <= 27))
        OR LENGTH(CAST(NEW.end_date AS BLOB)) <> LENGTH(NEW.end_date);
    SELECT RAISE(ABORT, '「優良な電子帳簿の適用開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.premium_ledger_from)) IS NOT substr(NEW.premium_ledger_from, 1, 10)
        OR (NEW.premium_ledger_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.premium_ledger_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.premium_ledger_from GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.premium_ledger_from, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.premium_ledger_from) <= 27))
        OR LENGTH(CAST(NEW.premium_ledger_from AS BLOB)) <> LENGTH(NEW.premium_ledger_from);
END;

CREATE TRIGGER trg_fiscal_years_date_format_update
BEFORE UPDATE OF start_date, end_date, premium_ledger_from ON fiscal_years
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.start_date)) IS NOT substr(NEW.start_date, 1, 10)
        OR (NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.start_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.start_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.start_date) <= 27))
        OR LENGTH(CAST(NEW.start_date AS BLOB)) <> LENGTH(NEW.start_date);
    SELECT RAISE(ABORT, '「終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.end_date)) IS NOT substr(NEW.end_date, 1, 10)
        OR (NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.end_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.end_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.end_date) <= 27))
        OR LENGTH(CAST(NEW.end_date AS BLOB)) <> LENGTH(NEW.end_date);
    SELECT RAISE(ABORT, '「優良な電子帳簿の適用開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.premium_ledger_from)) IS NOT substr(NEW.premium_ledger_from, 1, 10)
        OR (NEW.premium_ledger_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.premium_ledger_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.premium_ledger_from GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.premium_ledger_from, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.premium_ledger_from) <= 27))
        OR LENGTH(CAST(NEW.premium_ledger_from AS BLOB)) <> LENGTH(NEW.premium_ledger_from);
END;

CREATE TRIGGER trg_accounting_periods_date_format_insert
BEFORE INSERT ON accounting_periods
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.start_date)) IS NOT substr(NEW.start_date, 1, 10)
        OR (NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.start_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.start_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.start_date) <= 27))
        OR LENGTH(CAST(NEW.start_date AS BLOB)) <> LENGTH(NEW.start_date);
    SELECT RAISE(ABORT, '「終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.end_date)) IS NOT substr(NEW.end_date, 1, 10)
        OR (NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.end_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.end_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.end_date) <= 27))
        OR LENGTH(CAST(NEW.end_date AS BLOB)) <> LENGTH(NEW.end_date);
END;

CREATE TRIGGER trg_accounting_periods_date_format_update
BEFORE UPDATE OF start_date, end_date ON accounting_periods
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.start_date)) IS NOT substr(NEW.start_date, 1, 10)
        OR (NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.start_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.start_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.start_date) <= 27))
        OR LENGTH(CAST(NEW.start_date AS BLOB)) <> LENGTH(NEW.start_date);
    SELECT RAISE(ABORT, '「終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.end_date)) IS NOT substr(NEW.end_date, 1, 10)
        OR (NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.end_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.end_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.end_date) <= 27))
        OR LENGTH(CAST(NEW.end_date AS BLOB)) <> LENGTH(NEW.end_date);
END;

CREATE TRIGGER trg_journal_entries_date_format_insert
BEFORE INSERT ON journal_entries
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「取引日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.transaction_date)) IS NOT substr(NEW.transaction_date, 1, 10)
        OR (NEW.transaction_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.transaction_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.transaction_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.transaction_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.transaction_date) <= 27))
        OR LENGTH(CAST(NEW.transaction_date AS BLOB)) <> LENGTH(NEW.transaction_date);
    SELECT RAISE(ABORT, '「計上日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.posting_date)) IS NOT substr(NEW.posting_date, 1, 10)
        OR (NEW.posting_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.posting_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.posting_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.posting_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.posting_date) <= 27))
        OR LENGTH(CAST(NEW.posting_date AS BLOB)) <> LENGTH(NEW.posting_date);
END;

CREATE TRIGGER trg_journal_entries_date_format_update
BEFORE UPDATE OF transaction_date, posting_date ON journal_entries
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「取引日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.transaction_date)) IS NOT substr(NEW.transaction_date, 1, 10)
        OR (NEW.transaction_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.transaction_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.transaction_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.transaction_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.transaction_date) <= 27))
        OR LENGTH(CAST(NEW.transaction_date AS BLOB)) <> LENGTH(NEW.transaction_date);
    SELECT RAISE(ABORT, '「計上日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.posting_date)) IS NOT substr(NEW.posting_date, 1, 10)
        OR (NEW.posting_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.posting_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.posting_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.posting_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.posting_date) <= 27))
        OR LENGTH(CAST(NEW.posting_date AS BLOB)) <> LENGTH(NEW.posting_date);
END;

CREATE TRIGGER trg_journal_lines_date_format_insert
BEFORE INSERT ON journal_lines
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「課税仕入れの時点」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.tax_point)) IS NOT substr(NEW.tax_point, 1, 10)
        OR (NEW.tax_point NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.tax_point NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.tax_point GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.tax_point, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.tax_point) <= 27))
        OR LENGTH(CAST(NEW.tax_point AS BLOB)) <> LENGTH(NEW.tax_point);
END;

CREATE TRIGGER trg_journal_lines_date_format_update
BEFORE UPDATE OF tax_point ON journal_lines
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「課税仕入れの時点」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.tax_point)) IS NOT substr(NEW.tax_point, 1, 10)
        OR (NEW.tax_point NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.tax_point NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.tax_point GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.tax_point, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.tax_point) <= 27))
        OR LENGTH(CAST(NEW.tax_point AS BLOB)) <> LENGTH(NEW.tax_point);
END;

CREATE TRIGGER trg_partner_invoice_registrations_date_format_insert
BEFORE INSERT ON partner_invoice_registrations
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「登録年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.valid_from)) IS NOT substr(NEW.valid_from, 1, 10)
        OR (NEW.valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.valid_from GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.valid_from, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.valid_from) <= 27))
        OR LENGTH(CAST(NEW.valid_from AS BLOB)) <> LENGTH(NEW.valid_from);
    SELECT RAISE(ABORT, '「取消・失効年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.ended_on)) IS NOT substr(NEW.ended_on, 1, 10)
        OR (NEW.ended_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.ended_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.ended_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.ended_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.ended_on) <= 27))
        OR LENGTH(CAST(NEW.ended_on AS BLOB)) <> LENGTH(NEW.ended_on);
    SELECT RAISE(ABORT, '「最終確認日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.confirmed_on)) IS NOT substr(NEW.confirmed_on, 1, 10)
        OR (NEW.confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.confirmed_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.confirmed_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.confirmed_on) <= 27))
        OR LENGTH(CAST(NEW.confirmed_on AS BLOB)) <> LENGTH(NEW.confirmed_on);
    SELECT RAISE(ABORT, '「公表システムの更新年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.nta_updated_on)) IS NOT substr(NEW.nta_updated_on, 1, 10)
        OR (NEW.nta_updated_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.nta_updated_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.nta_updated_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.nta_updated_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.nta_updated_on) <= 27))
        OR LENGTH(CAST(NEW.nta_updated_on AS BLOB)) <> LENGTH(NEW.nta_updated_on);
END;

CREATE TRIGGER trg_partner_invoice_registrations_date_format_update
BEFORE UPDATE OF valid_from, ended_on, confirmed_on, nta_updated_on ON partner_invoice_registrations
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「登録年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.valid_from)) IS NOT substr(NEW.valid_from, 1, 10)
        OR (NEW.valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.valid_from GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.valid_from, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.valid_from) <= 27))
        OR LENGTH(CAST(NEW.valid_from AS BLOB)) <> LENGTH(NEW.valid_from);
    SELECT RAISE(ABORT, '「取消・失効年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.ended_on)) IS NOT substr(NEW.ended_on, 1, 10)
        OR (NEW.ended_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.ended_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.ended_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.ended_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.ended_on) <= 27))
        OR LENGTH(CAST(NEW.ended_on AS BLOB)) <> LENGTH(NEW.ended_on);
    SELECT RAISE(ABORT, '「最終確認日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.confirmed_on)) IS NOT substr(NEW.confirmed_on, 1, 10)
        OR (NEW.confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.confirmed_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.confirmed_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.confirmed_on) <= 27))
        OR LENGTH(CAST(NEW.confirmed_on AS BLOB)) <> LENGTH(NEW.confirmed_on);
    SELECT RAISE(ABORT, '「公表システムの更新年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.nta_updated_on)) IS NOT substr(NEW.nta_updated_on, 1, 10)
        OR (NEW.nta_updated_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.nta_updated_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.nta_updated_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.nta_updated_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.nta_updated_on) <= 27))
        OR LENGTH(CAST(NEW.nta_updated_on AS BLOB)) <> LENGTH(NEW.nta_updated_on);
END;
