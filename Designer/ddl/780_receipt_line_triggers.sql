-- 780: 入金と消込明細を DB で同期させる（ADR-0071 の移行レイヤ）
--
-- 入金予定は **4 か所**（請求書の発行・定期請求の実行・SES 精算・一部入金の残額）で作られる。
-- そのすべてに「明細も 1 行作る」コードを足すと、**5 か所目を足した人が必ず忘れる**（ADR-0060 の教訓）。
-- 1 対 1 の入金は「ヘッダに請求書がある」ことで完全に決まるので、**同期は DB のトリガに置く**。
--
-- こうしておくと、既存の 4 か所は 1 行も変えずに済み、明細を持つのは
-- 「合算した入金」だけ——つまり**新しく増えた責任はスクリプト側の合算処理だけ**になる。
--
-- 注意: **合算した入金（明細 2 行以上）には触らない**。金額の同期は明細がちょうど 1 行のときだけ。

DROP TRIGGER IF EXISTS trg_receipt_line_ai;
CREATE TRIGGER trg_receipt_line_ai
AFTER INSERT ON receipts
WHEN NEW.invoice_id IS NOT NULL
BEGIN
  INSERT INTO receipt_lines (receipt_id, line_no, invoice_id, amount)
  VALUES (NEW.id, 1, NEW.invoice_id, COALESCE(NEW.amount, 0));
END;

-- ヘッダの入金額を直したら、1 行だけの明細はそれに合わせる（不変条件 C05「合計＝入金額」を保つ）
DROP TRIGGER IF EXISTS trg_receipt_line_au_amount;
CREATE TRIGGER trg_receipt_line_au_amount
AFTER UPDATE OF amount ON receipts
WHEN (SELECT COUNT(*) FROM receipt_lines WHERE receipt_id = NEW.id) = 1
BEGIN
  UPDATE receipt_lines SET amount = COALESCE(NEW.amount, 0) WHERE receipt_id = NEW.id;
END;

-- ヘッダの請求書を差し替えたら 1 行だけの明細も差し替える（入金の請求書欄は読み取り専用だが、
-- 移行データの補正で触ることがある）
DROP TRIGGER IF EXISTS trg_receipt_line_au_invoice;
CREATE TRIGGER trg_receipt_line_au_invoice
AFTER UPDATE OF invoice_id ON receipts
WHEN NEW.invoice_id IS NOT NULL
 AND (SELECT COUNT(*) FROM receipt_lines WHERE receipt_id = NEW.id) = 1
BEGIN
  UPDATE receipt_lines SET invoice_id = NEW.invoice_id WHERE receipt_id = NEW.id;
END;

-- 入金を消したら明細も消える（孤児を残さない）
DROP TRIGGER IF EXISTS trg_receipt_line_ad;
CREATE TRIGGER trg_receipt_line_ad
AFTER DELETE ON receipts
BEGIN
  DELETE FROM receipt_lines WHERE receipt_id = OLD.id;
END;
