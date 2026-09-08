-- accounts.requires_sub_account を uses_sub_account に改める（ADR-0038 §3。docs/04 §1 の A-3）。
-- 列の意味が「必須か」から「使うか」に変わったので、名前を意味に合わせる。
--
-- 意味の凍結のトリガは、RENAME COLUMN が本文を書き換えてくれるが、
-- ここでは作り直す——RAISE の文言から列ラベルの写しを外すため（qa/02 R45-19 が
-- 「トリガを作り直す回に直す」と保留していたもの）。
-- **accounts のトリガ 3 本を、正典（ddl/005_journals.sql）と同じ順に作り直す**——
-- 1 本だけ作り直すと、表ごとのトリガの作られた順が正典とずれる
-- （発火順は SQLite の仕様上 undefined なので、順序も同値検査の対象である）。
DROP TRIGGER trg_accounts_meaning_frozen_when_posted;
DROP TRIGGER trg_accounts_no_replace_used_insert;
DROP TRIGGER trg_accounts_no_replace_used_update;

ALTER TABLE accounts RENAME COLUMN requires_sub_account TO uses_sub_account;

CREATE TRIGGER trg_accounts_meaning_frozen_when_posted
BEFORE UPDATE OF code, category, is_contra, uses_sub_account ON accounts
FOR EACH ROW WHEN NEW.code IS NOT OLD.code
              OR NEW.category IS NOT OLD.category
              OR NEW.is_contra IS NOT OLD.is_contra
              OR NEW.uses_sub_account IS NOT OLD.uses_sub_account
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている勘定科目の意味は変更できない。新しい科目を作る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.account_id = OLD.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_accounts_no_replace_used_insert
BEFORE INSERT ON accounts
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている勘定科目を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.account_id = NEW.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_accounts_no_replace_used_update
BEFORE UPDATE OF id ON accounts
FOR EACH ROW WHEN NEW.id IS NOT OLD.id
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている勘定科目を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.account_id IN (OLD.id, NEW.id) AND e.status = 'posted');
END;

-- 補助科目を使わない勘定科目の明細に、補助科目を付けたまま計上させない（ADR-0038 §3）。
-- 広さは計上の関門に揃えてある（取消・訂正では「持てない」側を見ない）。
CREATE TRIGGER trg_journal_entries_sub_account_matches_account_when_posted
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.status = 'posted' AND OLD.status <> 'posted'
BEGIN
    SELECT RAISE(ABORT, '補助科目を使う勘定科目の明細には補助科目が要る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN accounts a ON a.id = l.account_id
                    WHERE l.journal_entry_id = NEW.id
                      AND a.uses_sub_account = 1 AND l.sub_account_id IS NULL);

    SELECT RAISE(ABORT, '補助科目を使わない勘定科目の明細に補助科目は付けられない。')
     WHERE NEW.entry_type NOT IN ('reversal', 'correction')
       AND EXISTS (SELECT 1 FROM journal_lines l
                     JOIN accounts a ON a.id = l.account_id
                    WHERE l.journal_entry_id = NEW.id
                      AND a.uses_sub_account = 0 AND l.sub_account_id IS NOT NULL);
END;
