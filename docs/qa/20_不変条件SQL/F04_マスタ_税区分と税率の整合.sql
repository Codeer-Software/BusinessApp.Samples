-- 何を保証するか: 税区分マスタと税率マスタが矛盾していないこと。
--   (a) 課税区分（taxable_sales / taxable_purchase）には税率が紐づく
--       ※ export_exempt（免税売上）は税率を持たない設計（税額 0 で計算される）ため対象外
--   (b) 不課税・対象外（non_taxable / out_of_scope）には税率を紐づけない
--   (c) taxation_type が定義済みの値のいずれか
--   (d) 税率の適用期間が逆転していない（valid_from > valid_to）
--   (e) 経過措置フラグ（uses_transition_deduction）が立つのは課税仕入だけ
-- 違反時の意味: 消費税額の計算根拠が壊れる。区分だけあって率が無い＝税額 0 で静かに過少計上、
--               逆に対象外に率がある＝不課税取引に税がつく。
-- 出典: docs/04_会計ドメイン設計.md §4（tax_rates / tax_categories / invoice_transition_rates）
--       ADR-0052（税区分 NULL の廃止）
SELECT '課税区分なのに税率が無い' AS 違反, tc.code AS 税区分コード, tc.name AS 税区分名,
       tc.taxation_type AS 課税種別, NULL AS 補足
FROM tax_categories tc
WHERE tc.taxation_type IN ('taxable_sales', 'taxable_purchase')
  AND tc.tax_rate_id IS NULL

UNION ALL
SELECT '不課税・対象外なのに税率がある', tc.code, tc.name, tc.taxation_type, CAST(tc.tax_rate_id AS TEXT)
FROM tax_categories tc
WHERE tc.taxation_type IN ('non_taxable', 'out_of_scope')
  AND tc.tax_rate_id IS NOT NULL

UNION ALL
SELECT '課税種別が未定義の値', tc.code, tc.name, tc.taxation_type, NULL
FROM tax_categories tc
WHERE tc.taxation_type NOT IN ('taxable_sales', 'taxable_purchase', 'exempt_sales',
                               'exempt_purchase', 'non_taxable', 'export_exempt', 'out_of_scope')

UNION ALL
SELECT '経過措置フラグが課税仕入以外に立っている', tc.code, tc.name, tc.taxation_type, NULL
FROM tax_categories tc
WHERE tc.uses_transition_deduction = 1
  AND tc.taxation_type <> 'taxable_purchase'

UNION ALL
SELECT '税率の適用期間が逆転', tr.code, tr.name, CAST(tr.rate_percent AS TEXT),
       tr.valid_from || ' 〜 ' || COALESCE(tr.valid_to, '(無期限)')
FROM tax_rates tr
WHERE tr.valid_to IS NOT NULL AND date(tr.valid_from) > date(tr.valid_to)

UNION ALL
SELECT '経過措置控除割合の期間が逆転', CAST(itr.id AS TEXT), NULL, CAST(itr.rate_percent AS TEXT),
       itr.valid_from || ' 〜 ' || itr.valid_to
FROM invoice_transition_rates itr
WHERE date(itr.valid_from) > date(itr.valid_to)
