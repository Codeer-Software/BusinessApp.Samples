-- 0030 journal_entries のトリガの作られた順を正典に揃える
--
-- 稼働 DB では trg_journal_entries_posted_no_delete が trg_journal_entries_entry_type_immutable より先に作られていた
-- （正典と再生した DB は逆。2026-09-10 に migrate.ps1 -Verify へ足した順の検査で見つかった）。
-- 定義は 1 字も変えない。trg_journal_entries_entry_type_immutable 以降の 9 本を落とし、正典の順で作り直す。

DROP TRIGGER trg_journal_entries_entry_type_immutable;
DROP TRIGGER trg_journal_entries_original_only_for_amendment;
DROP TRIGGER trg_journal_entries_original_only_for_amendment_update;
DROP TRIGGER trg_journal_entries_posted_no_delete;
DROP TRIGGER trg_journal_entries_no_replace_posted_insert;
DROP TRIGGER trg_journal_entries_no_replace_posted_update;
DROP TRIGGER trg_journal_entries_description_required_when_posted;
DROP TRIGGER trg_journal_entries_sub_account_presence_when_posted;
DROP TRIGGER trg_journal_entries_partner_presence_when_posted;

CREATE TRIGGER trg_journal_entries_entry_type_immutable
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.entry_type IS NOT OLD.entry_type
BEGIN
    SELECT RAISE(ABORT, '仕訳の種別は変更できない。種別を変えるなら下書きを作り直す。');
END;

CREATE TRIGGER trg_journal_entries_original_only_for_amendment
BEFORE INSERT ON journal_entries
FOR EACH ROW WHEN NEW.original_entry_id IS NOT NULL
                  AND NEW.entry_type NOT IN ('correction', 'reversal')
BEGIN
    SELECT RAISE(ABORT, '原仕訳を指定できるのは訂正・取消だけ。');
END;

CREATE TRIGGER trg_journal_entries_original_only_for_amendment_update
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.original_entry_id IS NOT NULL
                  AND NEW.entry_type NOT IN ('correction', 'reversal')
BEGIN
    SELECT RAISE(ABORT, '原仕訳を指定できるのは訂正・取消だけ。');
END;

CREATE TRIGGER trg_journal_entries_posted_no_delete
BEFORE DELETE ON journal_entries
FOR EACH ROW WHEN OLD.status = 'posted'
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳は削除できない。訂正・取消は反対仕訳で行う。');
END;

CREATE TRIGGER trg_journal_entries_no_replace_posted_insert
BEFORE INSERT ON journal_entries
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳を上書きできない。訂正・取消は反対仕訳で行う。')
     WHERE EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.status = 'posted'
                      AND (e.id = NEW.id
                           OR (NEW.idempotency_key IS NOT NULL
                               AND e.idempotency_key = NEW.idempotency_key)));
END;

CREATE TRIGGER trg_journal_entries_no_replace_posted_update
BEFORE UPDATE ON journal_entries
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳を上書きできない。訂正・取消は反対仕訳で行う。')
     WHERE EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.status = 'posted' AND e.id <> OLD.id
                      AND (e.id = NEW.id
                           OR (NEW.idempotency_key IS NOT NULL
                               AND e.idempotency_key = NEW.idempotency_key)));
END;

CREATE TRIGGER trg_journal_entries_description_required_when_posted
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.status = 'posted' AND OLD.status <> 'posted'
             AND trim(coalesce(NEW.description, ''),
                      char(9, 10, 11, 12, 13, 32, 133, 160, 5760,
                       8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202,
                       8232, 8233, 8239, 8287, 12288)) = ''
BEGIN
    SELECT RAISE(ABORT, '摘要のない仕訳は計上できない。帳簿の記載事項「内容」を欠くため。');
END;

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
