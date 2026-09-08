-- 取消の免除に「原仕訳が計上済みであること」を足す（ADR-0038 §3。0018 の続き）。
-- 0018 は原仕訳の明細に同じ組み合わせがあるかしか見ておらず、**その原仕訳が計上済みである必要が無かった**。
-- journal_entries には「取消の原仕訳は計上済み」という制約が無いので、
-- 違反する組み合わせの明細を持つ**下書き**を 1 件おとりに立てれば、規則を外せた（自己レビューで見つけた）。
DROP TRIGGER trg_journal_entries_sub_account_presence_when_posted;

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
                                              AND o.account_id = l.account_id
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
                                              AND o.account_id = l.account_id
                                              AND o.sub_account_id IS l.sub_account_id)));
END;
