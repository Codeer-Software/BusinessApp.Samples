-- 470_default_tax_category.sql — 既定の税区分をマスタで持つ（ADR-0050・2026-08-11）
--
-- 背景: 明細の税区分を必須にするにあたり、新しい行に入れる既定値が必要になった。
-- 当初スクリプトに 'SALES_10' を直書きしたが、「ふつうは 10%」は 2026 年時点の話でしかなく、
-- CLAUDE.md §3 の「税率・税区分・閾値・勘定科目はマスタ化してハードコードしない」に反する。
--
-- 置き場所として company_profile（振込の委託者情報専用）や system_thresholds（金額の閾値・
-- amount INTEGER なので税区分 ID を入れると意味が歪む）も検討したが、
-- 「どの税区分が既定か」は税区分マスタ自身の属性なので、tax_categories に持たせる。
-- 税制改正で新しい税区分が増えたときも、税制マスタの画面で既定を移すだけで全画面が追随する。
-- 前例: accounts.default_tax_category_id（勘定科目の既定税区分）。今回は科目に紐づかない
-- 売上伝票（見積・受注・請求・検収）のための会社既定にあたる。

ALTER TABLE tax_categories ADD COLUMN default_for TEXT;   -- NULL / 'sales' / 'purchase'

-- 売上側の既定＝課税売上 10%、仕入側の既定＝課税仕入 10%（2026-08 時点の運用）
UPDATE tax_categories SET default_for = 'sales'    WHERE code = 'SALES_10';
UPDATE tax_categories SET default_for = 'purchase' WHERE code = 'PUR_10';

-- 用途ごとに既定は 1 行だけ（部分ユニークインデックス。NULL は何行でも可）
CREATE UNIQUE INDEX IF NOT EXISTS idx_tax_categories_default
    ON tax_categories(default_for) WHERE default_for IS NOT NULL;
