-- 430_fiscal_year_label.sql — 会計年度の表示ラベル（2026-08-06 ユーザー要望・レビュー第9弾）
-- 「第18期」だけでは西暦が分からないため、全画面の年度表示を「第18期（2026年度）」に統一する。
-- 年度 = 開始日の暦年（当社は 4 月開始）。label は name / start_date から導出されるキャッシュ
-- （select_label と同じ経路非依存のトリガー保守。手で更新しない）。
-- 注意: ALTER TABLE ... ADD COLUMN は一度きり（再実行不可）。

ALTER TABLE fiscal_years ADD COLUMN label TEXT;

DROP TRIGGER IF EXISTS trg_fiscal_years_label_ai;
DROP TRIGGER IF EXISTS trg_fiscal_years_label_au;

CREATE TRIGGER trg_fiscal_years_label_ai AFTER INSERT ON fiscal_years
BEGIN
  UPDATE fiscal_years
  SET label = COALESCE(NEW.name, '') || '（' || strftime('%Y', NEW.start_date) || '年度）'
  WHERE id = NEW.id;
END;

CREATE TRIGGER trg_fiscal_years_label_au AFTER UPDATE OF name, start_date ON fiscal_years
BEGIN
  UPDATE fiscal_years
  SET label = COALESCE(NEW.name, '') || '（' || strftime('%Y', NEW.start_date) || '年度）'
  WHERE id = NEW.id;
END;

-- 初期一括計算（以後はトリガーが保守）
UPDATE fiscal_years
SET label = COALESCE(name, '') || '（' || strftime('%Y', start_date) || '年度）';
