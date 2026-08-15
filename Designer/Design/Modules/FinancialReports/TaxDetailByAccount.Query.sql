-- 科目別税区分表（ADR-0052 の続き・2026-08-12）
--
-- 消費税集計表が「税区分ごとの合計」を出すのに対し、こちらは **科目 × 税区分** の内訳を出す。
-- 目的は消費税集計表と違い、申告書への転記ではなく **数字の検証**:
--   ・「課税仕入 10% が前期比で多い。内訳は？」に答える
--   ・**税区分の誤設定を見つける**——給与に課税仕入が付いていないか、海外 SaaS の
--     利用料が対象外になって控除漏れになっていないか、など
-- 実務の消費税の誤りは計算ミスより「税区分の付け間違い」が多く、合計表だけでは発見できない。
-- 税区分で「対象外」「不課税」に絞り、そこに損益科目が並んでいないかを見るのが定石の使い方。
--
-- 並びは科目コード順 → 税区分順。**1 つの科目に複数の税区分が縦に並ぶ**ので、
-- 「この科目には本来どの税区分が付くはずか」を目で追える向きにしている。
-- 金額の考え方（符号は accounts.dc_normal 基準の差引・逆側は「戻し」列）は消費税集計表と同じ。
WITH fy AS (
  SELECT COALESCE(@fiscal_year_id,
                  (SELECT id FROM fiscal_years
                    WHERE date(start_date) <= date('now', 'localtime') AND date(end_date) >= date('now', 'localtime'))) AS id
),
lines AS (
  SELECT
    a.id            AS account_id,
    a.code          AS account_code,
    a.name          AS account_name,
    tc.id           AS tc_id,
    tc.display_order AS tc_order,
    tc.name         AS tc_name,
    tc.taxation_type AS taxation_type,
    l.is_tax_line   AS is_tax_line,
    l.amount        AS amount,
    CASE WHEN l.dc = a.dc_normal THEN 1 ELSE -1 END AS sgn
  FROM journal_lines l
  JOIN journal_entries e ON e.id  = l.journal_entry_id
  JOIN tax_categories tc ON tc.id = l.tax_category_id
  JOIN accounts a        ON a.id  = l.account_id
  WHERE e.status = 'posted'
    AND e.fiscal_year_id = (SELECT id FROM fy)
    AND (@date_from       IS NULL OR date(e.entry_date) >= date(@date_from))
    AND (@date_to         IS NULL OR date(e.entry_date) <= date(@date_to))
    AND (@tax_category_id IS NULL OR tc.id = @tax_category_id)
    AND (@account_id      IS NULL OR a.id  = @account_id)
)
SELECT
  CAST(account_code AS INTEGER) * 1000 + tc_order AS sort_key,
  account_code,
  account_name,
  tc_name AS tax_category_name,
  CASE taxation_type
    WHEN 'taxable_sales'    THEN '課税売上'
    WHEN 'taxable_purchase' THEN '課税仕入'
    WHEN 'exempt_sales'     THEN '非課税売上'
    WHEN 'exempt_purchase'  THEN '非課税仕入'
    WHEN 'non_taxable'      THEN '不課税'
    WHEN 'export_exempt'    THEN '免税売上'
    ELSE '対象外'
  END AS taxation_type_name,
  SUM(CASE WHEN is_tax_line = 0 THEN amount * sgn ELSE 0 END)
    + SUM(CASE WHEN is_tax_line = 1 THEN amount * sgn ELSE 0 END) AS gross_amount,
  SUM(CASE WHEN is_tax_line = 0 THEN amount * sgn ELSE 0 END)        AS base_amount,
  SUM(CASE WHEN is_tax_line = 1 THEN amount * sgn ELSE 0 END)        AS tax_amount,
  SUM(CASE WHEN is_tax_line = 0 AND sgn = -1 THEN amount ELSE 0 END) AS base_reverse,
  SUM(CASE WHEN is_tax_line = 1 AND sgn = -1 THEN amount ELSE 0 END) AS tax_reverse
FROM lines
GROUP BY account_id, tc_id
ORDER BY sort_key
