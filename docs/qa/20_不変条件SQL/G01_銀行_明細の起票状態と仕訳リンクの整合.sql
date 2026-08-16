-- 何を保証するか: 銀行明細（bank_statement_lines）の状態とデータが整合していること。
--   (a) status='journalized' なら仕訳リンク（journal_entry_id）がある
--   (b) 仕訳リンクがあるのに status が journalized でない、ということが無い
--   (c) 重複取込防止キー（dedup_key）が口座内で一意
--   (d) 入金・出金のどちらか一方だけが正（両方 0／両方正が無い）
--   (e) 起票済み明細の金額と、起票された仕訳の借方合計が一致する
-- 違反時の意味: 預金残高が実際の通帳と合わなくなる。重複取込は預金と費用の二重計上。
--               「両方 0」の明細は取込 CSV のパース失敗の痕跡。
-- 出典: ADR-0012（銀行明細 → 仕訳の起票リンク）／Modules/Bank/BankPosting.mod.cs
--       Memory: 銀行明細 v3（取込 / 一括起票 / 明細一覧の 3 分割・プレビューは別テーブル）
SELECT '起票済みなのに仕訳リンクが無い' AS 違反, bl.id AS 明細id, bl.line_date AS 日付,
       bl.description AS 摘要, bl.status AS 状態, NULL AS 値
FROM bank_statement_lines bl
WHERE bl.status = 'journalized' AND bl.journal_entry_id IS NULL

UNION ALL
SELECT '仕訳リンクがあるのに未起票状態', bl.id, bl.line_date, bl.description, bl.status, NULL
FROM bank_statement_lines bl
WHERE bl.journal_entry_id IS NOT NULL AND bl.status <> 'journalized'

UNION ALL
SELECT '重複取込キーが重複', MIN(bl.id), MIN(bl.line_date), bl.dedup_key, NULL, CAST(COUNT(*) AS TEXT)
FROM bank_statement_lines bl
GROUP BY bl.bank_account_id, bl.dedup_key
HAVING COUNT(*) > 1

UNION ALL
SELECT '入出金が片側でない', bl.id, bl.line_date, bl.description, bl.status,
       '出金' || bl.amount_out || ' / 入金' || bl.amount_in
FROM bank_statement_lines bl
WHERE (bl.amount_out > 0 AND bl.amount_in > 0)
   OR (COALESCE(bl.amount_out, 0) = 0 AND COALESCE(bl.amount_in, 0) = 0)
   OR bl.amount_out < 0 OR bl.amount_in < 0

UNION ALL
SELECT '起票した仕訳の金額が明細と不一致', bl.id, bl.line_date, bl.description, bl.status,
       '明細' || (bl.amount_out + bl.amount_in) || ' / 仕訳' ||
       COALESCE((SELECT SUM(l.amount) FROM journal_lines l
                 WHERE l.journal_entry_id = bl.journal_entry_id AND l.dc = 'D'), 0)
FROM bank_statement_lines bl
JOIN journal_entries je ON je.id = bl.journal_entry_id
WHERE (COALESCE(bl.amount_out, 0) + COALESCE(bl.amount_in, 0))
      <> COALESCE((SELECT SUM(l.amount) FROM journal_lines l
                   WHERE l.journal_entry_id = bl.journal_entry_id AND l.dc = 'D'), 0)
