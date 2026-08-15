-- 550_invoice_gross_amount.sql — 請求書に「請求額（税込）」列を足す（ADR-0061・BusinessAppSQLite）
--
-- 請求書一覧に金額列が無く、実務で必要な「入金と突き合わせる金額＝税込」が一覧から読めなかった。
-- amount（税抜）と tax_amount は別々に保存されており、税込の保存列は無い。
--
-- 【方式の検討ログ】
--   ① 一覧に行スクリプトでラベル列を作る … 保存列を増やさない代わりにソート・検索ができない
--   ② SQLite の生成列（GENERATED ALWAYS AS ... VIRTUAL）… 定義上ズレようがなく理想的に見えたが、
--      **CLB のスキーマ照合から見えない**（designcheck が「カラム 'gross_amount' が存在しません」。
--      PRAGMA table_info は生成列を返さず table_xinfo が要るため）。実測で不採用。
--   ③ 実列＋DB トリガー保守 … 採用。select_label（ddl/280）と同じ理由——請求書は
--      手動作成 / 検収から作成 / SES 一括生成 / 定期請求実行 の4経路で INSERT されるので、
--      アプリ層のフックだと経路ごとの保守が要り必ず漏れる。トリガーなら経路を問わず常に整合する。
--
-- モジュール側は IsUpdateProtected:true + IgnoreModification:true の NumberField で読むだけにする
-- （select_label と同じ「DB が保守する列」の扱い）。

-- ② の検証で追加した生成列を戻す（同名で作り直すため。存在しない環境ではこの行を飛ばしてよい）
ALTER TABLE invoices DROP COLUMN gross_amount;

ALTER TABLE invoices ADD COLUMN gross_amount INTEGER;

UPDATE invoices SET gross_amount = COALESCE(amount, 0) + COALESCE(tax_amount, 0);

CREATE TRIGGER IF NOT EXISTS trg_invoices_gross_ins AFTER INSERT ON invoices
BEGIN
  UPDATE invoices SET gross_amount = COALESCE(new.amount, 0) + COALESCE(new.tax_amount, 0)
  WHERE id = new.id;
END;

-- UPDATE OF で監視列を限定しているため、トリガー自身の UPDATE（gross_amount のみ）では再発火しない
CREATE TRIGGER IF NOT EXISTS trg_invoices_gross_upd AFTER UPDATE OF amount, tax_amount ON invoices
BEGIN
  UPDATE invoices SET gross_amount = COALESCE(new.amount, 0) + COALESCE(new.tax_amount, 0)
  WHERE id = new.id;
END;
