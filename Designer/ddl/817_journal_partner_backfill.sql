-- 817_journal_partner_backfill.sql — 既存仕訳の取引先を生成元から埋め戻す（BUG-0003・ADR-0076）
--
-- 816 で journal_entries.partner_id を足したが、既に存在する仕訳は空のまま。
-- 電帳法の検索要件（取引年月日・取引金額・取引先）は「過去分も探せること」なので、
-- 生成元が取引先を持っている経路はここで遡って埋める。
-- 何度流しても同じ結果になる（partner_id IS NULL の行だけ触る）。
--
-- 取引先を持たない経路（bank / cashbook / template / import / depreciation / wip / 手入力）は
-- 対象外。これらは「相手が取引先マスタに無い」ので空のままが正しい。

-- 検収 → 受注 → 取引先
UPDATE journal_entries SET partner_id = (
  SELECT so.partner_id FROM acceptances ac
    JOIN sales_orders so ON so.id = ac.sales_order_id
   WHERE ac.id = journal_entries.source_id)
 WHERE partner_id IS NULL AND source_type = 'acceptance';

-- 入金 → 消込明細 → 請求書 → 取引先（合算入金も全明細が同一取引先なので MIN で足りる）
UPDATE journal_entries SET partner_id = (
  SELECT MIN(iv.partner_id) FROM receipt_lines rl
    JOIN invoices iv ON iv.id = rl.invoice_id
   WHERE rl.receipt_id = journal_entries.source_id)
 WHERE partner_id IS NULL AND source_type = 'receipt';

-- 仕入先請求・その支払 → 仕入先
UPDATE journal_entries SET partner_id = (
  SELECT vi.partner_id FROM vendor_invoices vi WHERE vi.id = journal_entries.source_id)
 WHERE partner_id IS NULL AND source_type IN ('vendor_invoice', 'vendor_payment');

-- 定期請求（SaaS 月額・年額前受・按分振替）と SES 請求 → source_id は請求書 ID
UPDATE journal_entries SET partner_id = (
  SELECT iv.partner_id FROM invoices iv WHERE iv.id = journal_entries.source_id)
 WHERE partner_id IS NULL
   AND source_type IN ('recurring', 'recurring_annual', 'recurring_defer', 'ses');

-- 前受収益の打ち切り → 定期請求契約 → 取引先
UPDATE journal_entries SET partner_id = (
  SELECT rb.partner_id FROM recurring_billings rb WHERE rb.id = journal_entries.source_id)
 WHERE partner_id IS NULL AND source_type = 'recurring_settle';

-- 経費（計上・支払）→ 支払先が取引先のときだけ。社員への精算は取引先ではない
UPDATE journal_entries SET partner_id = (
  SELECT er.payee_partner_id FROM expense_request er
   WHERE er.id = journal_entries.source_id AND er.payee_type = 'partner')
 WHERE partner_id IS NULL AND source_type IN ('expense', 'expense_payment');

-- 赤伝 → 元伝票から引き継ぐ（最後に流す。上の更新結果を拾えるように）
UPDATE journal_entries SET partner_id = (
  SELECT src.partner_id FROM journal_entries src WHERE src.id = journal_entries.source_id)
 WHERE partner_id IS NULL AND source_type = 'reversal';
