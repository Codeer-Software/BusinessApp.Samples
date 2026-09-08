-- 補助科目の 2 値のトリガを作り直す（ADR-0038 §3。0016 の続き）。
-- 変えたのは 2 つ。
--   ① 外すのは取消だけにした（0016 は訂正も外していた）。
--      再計上の中身は利用者が決めるので、補助科目を空にすれば通る——止めても行き止まりにならない。
--      外したままだと、訂正を経由して規則より後の違反を新しく帳簿へ入れられる。
--   ② 名前を presence に改めた。見ているのは補助科目の有無だけで、
--      「その補助科目が明細の勘定科目に属しているか」は計上の関門だけが見ている。
DROP TRIGGER trg_journal_entries_sub_account_matches_account_when_posted;

CREATE TRIGGER trg_journal_entries_sub_account_presence_when_posted
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.status = 'posted' AND OLD.status <> 'posted' AND NEW.entry_type <> 'reversal'
BEGIN
    SELECT RAISE(ABORT, '補助科目を使う勘定科目の明細には補助科目が要る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN accounts a ON a.id = l.account_id
                    WHERE l.journal_entry_id = NEW.id
                      AND a.uses_sub_account = 1 AND l.sub_account_id IS NULL);

    SELECT RAISE(ABORT, '補助科目を使わない勘定科目の明細に補助科目は付けられない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN accounts a ON a.id = l.account_id
                    WHERE l.journal_entry_id = NEW.id
                      AND a.uses_sub_account = 0 AND l.sub_account_id IS NOT NULL);
END;
