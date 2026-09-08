-- 0025 取引先の置き換えの守りに、id が動いたときだけ鳴る条件を足す
--
-- 正典は Designer/ddl/009_partner_meaning.sql。
--
-- **写し落としの修理である。** 会計コアの 4 マスタ（005 の trg_*_no_replace_used_update）は
-- すべて `WHEN NEW.id IS NOT OLD.id` を持つが、009 で取引先へ写したときに落ちていた。
-- **UPDATE OF id は、SET 句に id が並べば値が同じでも発火する**ので、そのままだと
-- 計上済みの伝票が使っている取引先は名称も住所も一切更新できず、
-- 「置き換えられない」という説明のつかない断りが出る（2026-09-09 の自己レビュー）。

DROP TRIGGER trg_partners_no_replace_used_update;

CREATE TRIGGER trg_partners_no_replace_used_update
BEFORE UPDATE OF id ON partners
FOR EACH ROW WHEN NEW.id IS NOT OLD.id
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳が使っている取引先は置き換えられない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.partner_id IN (OLD.id, NEW.id) AND e.status = 'posted')
        OR EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.partner_id IN (OLD.id, NEW.id) AND e.status = 'posted');
END;
