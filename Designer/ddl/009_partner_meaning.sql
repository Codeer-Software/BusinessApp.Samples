-- 009 使用中の取引先はコードを変えられない（ADR-0047 の決定 9・ADR-0038）
--
-- 会計コアの 4 マスタ（005 の trg_*_meaning_frozen_when_posted）と同じ規則を、取引先にも当てる。
-- **揃える理由は、利用者から見て「科目コードは変えられないのに取引先コードは変えられる」を
-- 説明できないからである**（帳簿には取引先の名称と登録番号を焼き込んでいるので、
-- コードを変えても過去の記載は動かない。ADR-0018）。
--
-- **取引先だけは「使用中」の数え方が違う。** 明細（journal_lines.partner_id）だけでなく、
-- **伝票（journal_entries.partner_id）**も見る——明細が空なら伝票の取引先が実効値になるからである
-- （005 の COALESCE(l.partner_id, e.partner_id) と同じ規則）。
-- 明細だけを見ると、伝票にだけ取引先を入れた計上済みの伝票を取りこぼす。
--
-- **008 より後に置く。** 表ごとのトリガの作られた順は、正典と再生（baseline + migrations）で
-- 一致していないといけない（MigrationEquivalenceTests）。

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
