-- A-4 の自己レビューで見つけた 2 つの穴を塞ぐ（docs/10 §6-2。0020 の続き）。
--
-- ① **「取引先を要する」を、使用中の科目でオフにできた。**
--    画面でオフにして計上し、また戻せば、計上の関門も 0020 のトリガも素通りする——
--    **二層の守りが同じ 1 列に乗っている**ので、まとめて外せた（qa/03 L-08 の型）。
--    **オンにするのは今までどおり通す**（規則を後から採り入れられなくなってはいけない）。
-- ② **免除が「科目が同じ明細が原仕訳に 1 行でもあること」しか見ていなかった。**
--    規則より前に計上された伝票を 1 本指すだけで、**取引先の無い明細を何行でも・任意の金額で**
--    新しく計上できた。**同じ行を貸借だけ入れ替えて写したこと**まで見る
--    （行番号・勘定科目・金額が同じで貸借が逆。JournalReversal が作る形）。
--    **補助科目の 2 値（0019）にも同じ穴があるので、2 本まとめて直す。**
--
-- **journal_entries のトリガは正典と同じ順に作り直す**（表ごとの作られた順が同値検査の対象。
-- 補助科目 → 取引先の順。理由は Designer/migrations/README.md）。
DROP TRIGGER trg_journal_entries_sub_account_presence_when_posted;
DROP TRIGGER trg_journal_entries_partner_presence_when_posted;

CREATE TRIGGER trg_journal_entries_sub_account_presence_when_posted
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.status = 'posted' AND OLD.status <> 'posted'
BEGIN
    SELECT RAISE(ABORT, '補助科目を使う勘定科目の明細には補助科目が要る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN accounts a ON a.id = l.account_id
                    WHERE l.journal_entry_id = NEW.id
                      AND a.uses_sub_account = 1 AND l.sub_account_id IS NULL
                      AND NOT (NEW.entry_type = 'reversal'
                               AND EXISTS (SELECT 1 FROM journal_lines o
                                             JOIN journal_entries oe ON oe.id = o.journal_entry_id
                                            WHERE o.journal_entry_id = NEW.original_entry_id
                                              AND oe.status = 'posted'
                                              AND o.line_no = l.line_no
                                              AND o.account_id = l.account_id
                                              AND o.amount = l.amount
                                              AND o.debit_credit <> l.debit_credit
                                              AND o.sub_account_id IS l.sub_account_id)));

    SELECT RAISE(ABORT, '補助科目を使わない勘定科目の明細に補助科目は付けられない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN accounts a ON a.id = l.account_id
                    WHERE l.journal_entry_id = NEW.id
                      AND a.uses_sub_account = 0 AND l.sub_account_id IS NOT NULL
                      AND NOT (NEW.entry_type = 'reversal'
                               AND EXISTS (SELECT 1 FROM journal_lines o
                                             JOIN journal_entries oe ON oe.id = o.journal_entry_id
                                            WHERE o.journal_entry_id = NEW.original_entry_id
                                              AND oe.status = 'posted'
                                              AND o.line_no = l.line_no
                                              AND o.account_id = l.account_id
                                              AND o.amount = l.amount
                                              AND o.debit_credit <> l.debit_credit
                                              AND o.sub_account_id IS l.sub_account_id)));
END;

CREATE TRIGGER trg_journal_entries_partner_presence_when_posted
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.status = 'posted' AND OLD.status <> 'posted'
BEGIN
    SELECT RAISE(ABORT, '取引先を要する勘定科目の明細には取引先が要る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN accounts a ON a.id = l.account_id
                    WHERE l.journal_entry_id = NEW.id
                      AND a.requires_partner = 1
                      AND COALESCE(l.partner_id, NEW.partner_id) IS NULL
                      AND NOT (NEW.entry_type = 'reversal'
                               AND EXISTS (SELECT 1 FROM journal_lines o
                                             JOIN journal_entries oe ON oe.id = o.journal_entry_id
                                            WHERE o.journal_entry_id = NEW.original_entry_id
                                              AND oe.status = 'posted'
                                              AND o.line_no = l.line_no
                                              AND o.account_id = l.account_id
                                              AND o.amount = l.amount
                                              AND o.debit_credit <> l.debit_credit
                                              AND COALESCE(o.partner_id, oe.partner_id) IS NULL)));
END;

CREATE TRIGGER trg_accounts_requires_partner_not_loosened_when_posted
BEFORE UPDATE OF requires_partner ON accounts
FOR EACH ROW WHEN OLD.requires_partner = 1 AND NEW.requires_partner = 0
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている勘定科目で、取引先を必須から外すことはできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.account_id = OLD.id AND e.status = 'posted');
END;
