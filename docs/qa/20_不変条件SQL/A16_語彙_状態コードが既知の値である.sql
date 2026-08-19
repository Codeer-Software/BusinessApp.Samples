-- 何を保証するか: 状態を表す列に、定義済みの語彙以外の値が入っていないこと。
-- 違反時の意味: **表記ゆれが 1 つ入ると、その行が全帳票から静かに消える**。
--               たとえば `journal_entries.status` が `'Posted'`（大文字）になると、
--               試算表・BS・PL・元帳・月次推移・消費税集計表はすべて `status = 'posted'` で絞るので
--               その伝票だけが載らない。**年度内連番だけ消費して帳簿に出ない伝票**ができる。
--               しかも既存の不変条件は全滅する——A01/A02/A05/A13/B05 は `posted` の行だけを集計するので
--               違反にならず、A04 も対象外、A07 は締め済み期間しか見ない。
--               `fiscal_periods.status` はもっと直接的で、締めガードは全 15 経路が
--               `Status.Value == "closed"` の文字列一致 1 本。値が化けると**締め済み期間への起票が通る**。
-- なぜ CHECK でやらないか: SQLite に `ALTER TABLE ADD CONSTRAINT` は無く、CHECK の追加は
--               テーブル再構築になる。伝票テーブルの再構築は割に合わないので、ここで見張る。
-- 出典: 2026-08-19 のスキーマ監査（`Designer/ddl/` 全 114 本と実体の突き合わせ）

SELECT 'journal_entries.status' AS 列, id AS 行id, status AS 値 FROM journal_entries
WHERE status NOT IN ('draft', 'posted')
UNION ALL
SELECT 'journal_entries.entry_type', id, entry_type FROM journal_entries
WHERE entry_type IS NOT NULL
  AND entry_type NOT IN ('transfer', 'receipt', 'payment', 'expense', 'auto', 'adjust')
UNION ALL
SELECT 'journal_lines.dc', id, dc FROM journal_lines WHERE dc NOT IN ('D', 'C')
UNION ALL
SELECT 'fiscal_years.status', id, status FROM fiscal_years
WHERE status IS NOT NULL AND status NOT IN ('open', 'closed')
UNION ALL
SELECT 'fiscal_periods.status', id, status FROM fiscal_periods
WHERE status IS NOT NULL AND status NOT IN ('open', 'closed')
UNION ALL
SELECT 'invoices.status', id, status FROM invoices
WHERE status IS NOT NULL AND status NOT IN ('draft', 'issued', 'partial', 'paid', 'void')
UNION ALL
SELECT 'quotes.status', id, status FROM quotes
WHERE status IS NOT NULL AND status NOT IN ('draft', 'sent', 'accepted', 'rejected')
UNION ALL
SELECT 'sales_orders.status', id, status FROM sales_orders
WHERE status IS NOT NULL AND status NOT IN ('open', 'closed', 'cancelled')
UNION ALL
SELECT 'acceptances.status', id, status FROM acceptances
WHERE status IS NOT NULL AND status NOT IN ('draft', 'confirmed')
UNION ALL
SELECT 'vendor_invoices.status', id, status FROM vendor_invoices
WHERE status IS NOT NULL AND status NOT IN ('received', 'accrued', 'paid')
UNION ALL
SELECT 'expense_request.settlement_status', id, settlement_status FROM expense_request
WHERE settlement_status IS NOT NULL
  AND settlement_status NOT IN ('draft', 'applying', 'approved', 'accounting', 'settled', 'completed')
UNION ALL
SELECT 'bank_statement_lines.status', id, status FROM bank_statement_lines
WHERE status IS NOT NULL AND status NOT IN ('pending', 'journalized', 'excluded')
UNION ALL
SELECT 'approval_flow.status', id, status FROM approval_flow
WHERE status IS NOT NULL AND status NOT IN ('Draft', 'Pending', 'Approved', 'Rejected', 'Cancelled')
UNION ALL
SELECT 'approval_flow_order.status', id, status FROM approval_flow_order
WHERE status IS NOT NULL AND status NOT IN ('Waiting', 'Active', 'Approved', 'Rejected', 'Skipped', 'Cancelled')
UNION ALL
SELECT 'approval_flow_member.status', id, status FROM approval_flow_member
WHERE status IS NOT NULL AND status NOT IN ('Waiting', 'Approved', 'Rejected', 'Skipped', 'Cancelled')
