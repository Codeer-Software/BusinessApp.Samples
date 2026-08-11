-- 510_full_deduction_thresholds.sql — 仕入税額控除の「全額控除」判定閾値をマスタ化（ADR-0052・2026-08-12）
--
-- 消費税集計表に課税売上割合と控除方式の判定を出すにあたり、判定に使う 2 つの制度値
-- （課税売上割合 95% 以上・課税売上高 5 億円以下）が必要になった。
-- CLAUDE.md §3「税率・税区分・閾値・勘定科目はマスタ化してハードコードしない」に従い
-- system_thresholds に置く。根拠: 消費税法第30条第2項（平成24年4月1日以後開始課税期間）。
--
-- system_thresholds.amount は「円」の閾値を入れる列だったため、割合(%)も扱えるように
-- unit 列を足す（既存 4 レコードは 'yen'）。前例: tax_categories.default_for（ADR-0050）と同じく
-- 「その概念を持つべきマスタ自身に列を足す」方針。

ALTER TABLE system_thresholds ADD COLUMN unit TEXT NOT NULL DEFAULT 'yen';   -- yen / percent

INSERT OR IGNORE INTO system_thresholds (id, code, name, amount, valid_from, valid_to, unit) VALUES
    (6, 'FULL_DEDUCT_RATIO_MIN', '全額控除できる課税売上割合の下限(%)', 95,        '2012-04-01', NULL, 'percent'),
    (7, 'FULL_DEDUCT_SALES_CAP', '全額控除できる課税売上高の上限(円)', 500000000, '2012-04-01', NULL, 'yen');
