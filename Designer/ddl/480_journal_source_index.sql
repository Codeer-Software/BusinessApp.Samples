-- 480: journal_entries の連動元（source_type, source_id）にインデックスを張る
--
-- 「入金が確定（消込）済みかどうか」は receipts の列ではなく
-- journal_entries(source_type='receipt', source_id=receipt.id) の存在で表す（ADR-0032/0033）。
-- 改善候補 A-2 の修正で、売掛残高・資金繰り予測・ポータルアラートの 3 帳票が
-- この存在判定（EXISTS）を行うようになったため、走査を避けるインデックスを追加する。
-- 経費・請求・償却など他の連動元（source_type）の逆引き（Receipt.mod.cs / Acceptance.mod.cs の
-- ModuleSearcher による「この伝票から生成された仕訳」検索）にも同じインデックスが効く。
CREATE INDEX IF NOT EXISTS idx_journal_entries_source
    ON journal_entries(source_type, source_id);
