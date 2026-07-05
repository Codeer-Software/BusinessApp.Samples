SELECT
  tc.display_order AS sort_key,
  tc.name AS tax_category_name,
  CASE tc.taxation_type
    WHEN 'taxable_sales' THEN '課税売上'
    WHEN 'taxable_purchase' THEN '課税仕入'
    WHEN 'exempt_sales' THEN '非課税売上'
    WHEN 'exempt_purchase' THEN '非課税仕入'
    WHEN 'non_taxable' THEN '不課税'
    WHEN 'export_exempt' THEN '免税売上'
    ELSE '対象外'
  END AS taxation_type_name,
  COALESCE(SUM(CASE WHEN l.is_tax_line = 0 THEN l.amount ELSE 0 END), 0) AS base_amount,
  COALESCE(SUM(CASE WHEN l.is_tax_line = 1 THEN l.amount ELSE 0 END), 0) AS tax_amount
FROM journal_lines l
JOIN journal_entries e ON e.id = l.journal_entry_id
JOIN tax_categories tc ON tc.id = l.tax_category_id
WHERE e.status = 'posted'
  AND l.tax_category_id IS NOT NULL
  AND (@date_from IS NULL OR date(e.entry_date) >= date(@date_from))
  AND (@date_to IS NULL OR date(e.entry_date) <= date(@date_to))
GROUP BY tc.id
ORDER BY tc.display_order
