-- 何を保証するか: 消込仕訳が入金レコードの実体と一致すること。
--   (a) 1 件の入金に消込仕訳が 2 本以上作られていない（＝預金の二重計上が無い）
--   (b) 振込・現金入金は、仕訳の現預金科目（accounts.is_cash_equivalent = 1）の借方合計が入金額と一致する
--   (c) 相殺入金（method='offset'）で現預金が動いていない（相殺なのに現金が増えている、が無い）
-- 違反時の意味: 通帳残高と帳簿の預金残高が合わなくなる。
-- 出典: ADR-0051（消込済み = 消込仕訳の存在）／ADR-0035（相殺は買掛側連動）
--       Modules/Sales/Receipt.mod.cs（入金仕訳の起票）
-- 実装メモ: 仕訳の借方合計そのものと比べてはいけない。振込手数料を当社が負担する入金は
--           「預金 109,560 / 支払手数料 440 ／ 売掛金 110,000」となり、借方合計 = 請求額 ≠ 入金額 になる。
--           入金額と一対一で対応するのは「現預金科目に入った額」。
SELECT '消込仕訳が複数ある' AS 違反, r.id AS 入金id, r.receipt_date AS 入金日,
       r.method AS 方法, r.amount AS 入金額, NULL AS 仕訳側の額
FROM receipts r
WHERE (SELECT COUNT(*) FROM journal_entries je
       WHERE je.source_type = 'receipt' AND je.source_id = r.id) > 1

UNION ALL
SELECT '現預金の増加額が入金額と不一致', r.id, r.receipt_date, r.method, r.amount,
       COALESCE((SELECT SUM(jl.amount) FROM journal_lines jl
                 JOIN accounts a ON a.id = jl.account_id
                 WHERE jl.journal_entry_id = je.id AND jl.dc = 'D' AND a.is_cash_equivalent = 1), 0)
FROM receipts r
JOIN journal_entries je ON je.source_type = 'receipt' AND je.source_id = r.id
WHERE COALESCE(r.method, '') <> 'offset'
  AND COALESCE(r.amount, 0)
      <> COALESCE((SELECT SUM(jl.amount) FROM journal_lines jl
                   JOIN accounts a ON a.id = jl.account_id
                   WHERE jl.journal_entry_id = je.id AND jl.dc = 'D' AND a.is_cash_equivalent = 1), 0)

UNION ALL
SELECT '相殺入金なのに現預金が動いている', r.id, r.receipt_date, r.method, r.amount,
       (SELECT SUM(jl.amount) FROM journal_lines jl
        JOIN accounts a ON a.id = jl.account_id
        WHERE jl.journal_entry_id = je.id AND a.is_cash_equivalent = 1)
FROM receipts r
JOIN journal_entries je ON je.source_type = 'receipt' AND je.source_id = r.id
WHERE r.method = 'offset'
  AND EXISTS (SELECT 1 FROM journal_lines jl
              JOIN accounts a ON a.id = jl.account_id
              WHERE jl.journal_entry_id = je.id AND a.is_cash_equivalent = 1)
