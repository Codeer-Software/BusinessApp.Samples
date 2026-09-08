-- 0024 使用中の取引先はコードを変えられない（ADR-0047 の決定 9）
--
-- 正典は Designer/ddl/009_partner_meaning.sql（同じ内容をここから配る）。
-- 会計コアの 4 マスタと揃える。取引先だけは伝票（journal_entries.partner_id）も見る——
-- 明細が空なら伝票の取引先が実効値になるからである。

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
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳が使っている取引先は置き換えられない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.partner_id IN (OLD.id, NEW.id) AND e.status = 'posted')
        OR EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.partner_id IN (OLD.id, NEW.id) AND e.status = 'posted');
END;
