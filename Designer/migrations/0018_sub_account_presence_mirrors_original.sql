-- 補助科目の 2 値のトリガを、種別だけに頼らない形へ作り直す（ADR-0038 §3。0017 の続き）。
-- 0017 は `entry_type <> 'reversal'` で外していたが、**entry_type は取込・CLI・手打ちの SQL が
-- 自由に書ける列**なので、「取消だ」と名乗るだけで規則を外せた（自己レビューで見つけた）。
-- **原仕訳に同じ組み合わせ（勘定科目・補助科目）の明細があること**まで見て、
-- 「原仕訳を写しただけの明細」だけを外す。
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
                                            WHERE o.journal_entry_id = NEW.original_entry_id
                                              AND o.account_id = l.account_id
                                              AND o.sub_account_id IS l.sub_account_id)));

    SELECT RAISE(ABORT, '補助科目を使わない勘定科目の明細に補助科目は付けられない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN accounts a ON a.id = l.account_id
                    WHERE l.journal_entry_id = NEW.id
                      AND a.uses_sub_account = 0 AND l.sub_account_id IS NOT NULL
                      AND NOT (NEW.entry_type = 'reversal'
                               AND EXISTS (SELECT 1 FROM journal_lines o
                                            WHERE o.journal_entry_id = NEW.original_entry_id
                                              AND o.account_id = l.account_id
                                              AND o.sub_account_id IS l.sub_account_id)));
END;
