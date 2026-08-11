-- 471_acceptance_lines.sql — 検収に明細行を持たせる（ADR-0049・2026-08-11）
--
-- 背景: 見積・受注・請求書は明細を持つのに検収だけが合計金額しか持たず、
-- 検収から作った請求書は「金額＝検収額／明細＝受注明細」という別々の根拠を抱えていた。
-- そのため分割検収の請求書を画面で開くと請求額が受注全額に戻る（改善候補 A-1）。
-- 検収に明細を持たせ、請求書がそれをコピーすることで、明細合計＝検収額が構造的に成立する。

CREATE TABLE IF NOT EXISTS acceptance_lines (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    acceptance_id       INTEGER REFERENCES acceptances(id),
    line_no             INTEGER,
    sales_order_line_id INTEGER REFERENCES sales_order_lines(id),  -- 受注明細への紐づき
    description         TEXT,      -- 以下 4 列は受注明細のスナップショット（画面では読み取り専用）
    qty                 INTEGER,
    unit                TEXT,
    unit_price          INTEGER,
    order_amount        INTEGER,   -- 受注明細の金額（残額計算・超過判定の基準）
    amount              INTEGER,   -- 今回検収する金額（唯一の編集対象）
    tax_category_id     INTEGER REFERENCES tax_categories(id)
);

CREATE INDEX IF NOT EXISTS idx_acceptance_lines_acc ON acceptance_lines(acceptance_id);
CREATE INDEX IF NOT EXISTS idx_acceptance_lines_sol ON acceptance_lines(sales_order_line_id);

-- 請求明細 → 検収明細の紐づき（行単位の超過警告の根拠。手動・定期・SES では NULL）
ALTER TABLE invoice_lines ADD COLUMN acceptance_line_id INTEGER REFERENCES acceptance_lines(id);

-- ---- 既存データの移行（受注明細から復元） ----
-- 検収 9 件のうち 8 件は検収額＝受注明細合計、1 件（A-26-009）が分割検収で
-- 検収額 1,200,000 ＜ 受注明細合計 2,000,000。まず受注明細をそのままコピーし、
-- そのあと最終行で差額を調整して合計を検収額に一致させる。
-- 帳簿（売上仕訳）は検収額の合計で計上済みなので、この移行で金額は 1 円も動かない。

INSERT INTO acceptance_lines
    (acceptance_id, line_no, sales_order_line_id, description, qty, unit, unit_price, order_amount, amount, tax_category_id)
SELECT a.id, sol.line_no, sol.id, sol.description, sol.qty, sol.unit, sol.unit_price, sol.amount, sol.amount, sol.tax_category_id
  FROM acceptances a
  JOIN sales_order_lines sol ON sol.sales_order_id = a.sales_order_id
 WHERE NOT EXISTS (SELECT 1 FROM acceptance_lines al WHERE al.acceptance_id = a.id);

-- 最終行で差額を調整（分割検収だった検収の合計を検収額に合わせる）
UPDATE acceptance_lines
   SET amount = amount + (
        SELECT a.amount - (SELECT SUM(al2.amount) FROM acceptance_lines al2 WHERE al2.acceptance_id = a.id)
          FROM acceptances a WHERE a.id = acceptance_lines.acceptance_id)
 WHERE id IN (
        SELECT MAX(al3.id) FROM acceptance_lines al3
         GROUP BY al3.acceptance_id
        HAVING (SELECT a3.amount FROM acceptances a3 WHERE a3.id = al3.acceptance_id)
               <> SUM(al3.amount));

-- 既存の請求明細を検収明細に紐づける（検収由来の請求書のみ・行番号で対応づけ）
UPDATE invoice_lines
   SET acceptance_line_id = (
        SELECT al.id FROM acceptance_lines al
          JOIN invoices i ON i.acceptance_id = al.acceptance_id
         WHERE i.id = invoice_lines.invoice_id
           AND al.line_no = invoice_lines.line_no)
 WHERE EXISTS (SELECT 1 FROM invoices i2
                WHERE i2.id = invoice_lines.invoice_id AND i2.acceptance_id IS NOT NULL);
