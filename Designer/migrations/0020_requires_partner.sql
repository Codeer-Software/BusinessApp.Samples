-- 勘定科目に「取引先を要する」を足し、取引先の無い明細を計上させない（docs/04 §1 の A-4）。
-- 相手方を欠いた行は「相手方別」のどの帳簿にも載らず、計上済みは直せない
-- （電帳規則 5 ① の括弧書き。docs/40 §4-1。開発者の承認。2026-09-06）。
--
-- **列は末尾に足す**（SQLite の ADD COLUMN がそこに作るので、正典の列順もそれに合わせてある）。
-- **既定は 0**——立てるのは初期データ（Designer/seed/004_accounts.sql）と、
-- 使っている DB では利用者が画面で決める（この列は「意味を決める列」ではないので凍結しない。
-- 理由は docs/12 §2）。
ALTER TABLE accounts ADD COLUMN requires_partner INTEGER NOT NULL DEFAULT 0 CHECK (requires_partner IN (0, 1));

-- 免除の形は補助科目の 2 値（0019）とまったく同じ——取消の、
-- **計上済みの原仕訳を写しただけの明細**だけを外す。理由は正典（ddl/005_journals.sql）の注記。
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
                                              AND o.account_id = l.account_id
                                              AND COALESCE(o.partner_id, oe.partner_id) IS NULL)));
END;
