-- 取引先の「無い」を、NULL だけでなく**マスタに実在しないこと**で見る（docs/10 §6-2。0021 の続き）。
--
-- **外部キーを切った接続からは、空文字や実在しない識別子が入る**（取込・CLI・手打ちの SQL——
-- このトリガが守る相手そのもの）。`COALESCE(...) IS NULL` だけでは素通りし、
-- **その行は取引先で絞った帳簿にも、取引先が空の検索にも出てこない**まま計上済みで固定される
-- （自己レビューで見つけた。2026-09-08。インメモリで再現した）。
--
-- **免除の側も同じ物差しにする**——原仕訳の行も「実在する取引先を持たない」ことで判定する。
DROP TRIGGER trg_journal_entries_partner_presence_when_posted;

CREATE TRIGGER trg_journal_entries_partner_presence_when_posted
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.status = 'posted' AND OLD.status <> 'posted'
BEGIN
    SELECT RAISE(ABORT, '取引先を要する勘定科目の明細には取引先が要る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN accounts a ON a.id = l.account_id
                    WHERE l.journal_entry_id = NEW.id
                      AND a.requires_partner = 1
                      AND NOT EXISTS (SELECT 1 FROM partners p
                                       WHERE p.id = COALESCE(l.partner_id, NEW.partner_id))
                      AND NOT (NEW.entry_type = 'reversal'
                               AND EXISTS (SELECT 1 FROM journal_lines o
                                             JOIN journal_entries oe ON oe.id = o.journal_entry_id
                                            WHERE o.journal_entry_id = NEW.original_entry_id
                                              AND oe.status = 'posted'
                                              AND o.line_no = l.line_no
                                              AND o.account_id = l.account_id
                                              AND o.amount = l.amount
                                              AND o.debit_credit <> l.debit_credit
                                              AND NOT EXISTS (SELECT 1 FROM partners q
                                                               WHERE q.id = COALESCE(o.partner_id, oe.partner_id)))));
END;
