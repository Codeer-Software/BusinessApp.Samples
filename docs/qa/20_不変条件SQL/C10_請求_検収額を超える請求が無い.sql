-- 何を保証するか: 検収に紐づく請求書の税込請求額が、その検収の税込確定額を超えないこと。
-- 違反時の意味: 売上・売掛金は「検収の確定」でしか計上されないため、検収額を超えて請求すると
--               その超過分は請求書には載るのに帳簿には一切現れない。
--               結果として「総勘定元帳の売掛金 ≠ 売掛残高一覧の残額合計」（C08）になり、
--               入金があっても消し込めない残額が永久に残る。
-- 出典: ADR-0067（請求明細は検収明細の写しとして読み取り専用にする。2026-08-17）
--       Modules/Sales/Invoice.mod.cs UpdateOverAcceptanceWarning()
--       ISSUE-0002（増額は変更契約として新しい受注・検収を起こす運用）
-- 方針（ADR-0067）: この検査は **error のまま残す**。警告レベルには落とさない。
--       超過は「警告するかブロックするか」ではなく **そもそも入力させない**（検収に紐づく請求書の
--       明細金額を読み取り専用にする）ことで構造的に防ぐ。実装後、本検査は恒久的に緑を維持できる。
--       赤が出たら「本当に何かが壊れている」を意味する。
-- 備考: 画面の警告は「明細行 vs 検収明細行」で行うが、帳簿への影響は伝票単位なのでここでは総額で見る。
--       違反が出たら、変更契約として検収を追加するのが正しい直し方（請求書を減額するのではなく）。
--       検収に紐づかない手動の合算請求書（acceptance_id が NULL）は JOIN で自然に対象外。
--       そちらには別の穴がある（仕訳を作らないので売掛金が帳簿に載らない）→ ISSUE-0005
SELECT
    i.id            AS 請求書id,
    i.invoice_no    AS 請求書番号,
    i.status        AS 状態,
    i.issue_date    AS 発行日,
    ac.acceptance_no AS 検収番号,
    ac.status        AS 検収状態,
    COALESCE(ac.amount, 0) + COALESCE(ac.tax_amount, 0) AS 検収税込,
    COALESCE(i.amount, 0)  + COALESCE(i.tax_amount, 0)  AS 請求税込,
    (COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0))
      - (COALESCE(ac.amount, 0) + COALESCE(ac.tax_amount, 0)) AS 超過額
FROM invoices i
JOIN acceptances ac ON ac.id = i.acceptance_id
WHERE COALESCE(i.status, '') <> 'void'   -- status は NOT NULL 制約が無いので COALESCE で拾う
  AND (COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0))
      > (COALESCE(ac.amount, 0) + COALESCE(ac.tax_amount, 0))
ORDER BY i.id
