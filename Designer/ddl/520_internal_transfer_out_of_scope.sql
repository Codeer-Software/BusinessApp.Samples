-- 520_internal_transfer_out_of_scope.sql — 内部振替の行を「対象外」に直す（ADR-0052・2026-08-12）
--
-- 490 は税区分の無い行を「勘定科目マスタの既定」で埋めたが、これは内部振替の行には誤りだった。
-- 科目の既定は「その科目の典型的な取引」に対する既定であって、その科目に触れるすべての仕訳に
-- 当てはまるわけではない。実測で見つかった 2 つの誤り:
--
--   ・減価償却の貸方（工具器具備品）に、取得時の既定である課税仕入 10% が付いた。
--     消費税集計表の符号付き集計では「課税仕入の戻し」として引かれ、課税仕入を 114,375 円過少にした。
--   ・前受収益の按分振替の貸方（SaaS売上高）に課税売上 10% が付いた。
--     年額請求の時点で課税売上を計上済みなので、月次の振替で二重計上していた（月 100,000 円）。
--
-- 減価償却も按分振替も前払振替も、消費税は元の取引の時点で確定している。内部振替は対象外が正しい。
-- アプリ側は JournalEntry.MarkRemainingLinesOutOfScope()（明示されなかった行は対象外）と
-- CashEntry の相手科目の明示セットで恒久対応済み。本ファイルは既存データの是正。
--
-- 対象は「内部振替の仕訳」×「申告に出てくる税区分が付いている行」に限る。
-- 不課税・対象外が付いている行は触らない（すでに正しく、法人税等の不課税などを壊さないため）。

UPDATE journal_lines
SET tax_category_id = (SELECT id FROM tax_categories WHERE taxation_type = 'out_of_scope')
WHERE is_tax_line = 0
  AND journal_entry_id IN (
        SELECT id FROM journal_entries
         WHERE source_type IN ('depreciation', 'recurring_defer')   -- 減価償却・前受収益の按分振替
            OR entry_type = 'adjust'                                -- 決算整理（前払振替など）
      )
  AND tax_category_id IN (
        SELECT id FROM tax_categories
         WHERE taxation_type IN ('taxable_sales', 'taxable_purchase',
                                 'exempt_sales', 'exempt_purchase', 'export_exempt')
      );

-- 検証: 内部振替の仕訳に申告対象の税区分が残っていないこと（0 件になる）
SELECT '内部振替に残った申告対象の税区分' AS check_name, COUNT(*) AS remaining
  FROM journal_lines l
  JOIN journal_entries e  ON e.id  = l.journal_entry_id
  JOIN tax_categories tc  ON tc.id = l.tax_category_id
 WHERE l.is_tax_line = 0
   AND (e.source_type IN ('depreciation', 'recurring_defer') OR e.entry_type = 'adjust')
   AND tc.taxation_type IN ('taxable_sales', 'taxable_purchase',
                            'exempt_sales', 'exempt_purchase', 'export_exempt');
