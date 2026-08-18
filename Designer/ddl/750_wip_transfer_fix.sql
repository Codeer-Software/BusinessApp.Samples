-- 750: 仕掛品ビューの是正（敵対的レビューで出た P1-1 / P2-1 / P3・ADR-0072）
--
-- ① **翌期首の振戻を当期原価に数える。** これが本丸の誤り。
--    洗い替えは「期末時点の**累計**未検収原価」を測り直す方式なのに、720 は `wip_reversal` まで
--    除外していたので **当期発生分しか測れていなかった**。
--    3 期以上またぐ案件で、2 期目に前期の繰延が費用として残り、BS の仕掛品も過小になる。
--    さらに悪い形として、2 期目の新規原価が 0 円（検収待ちだけの期）だと
--    `wip_amount > 0` の条件で候補から丸ごと落ち、**前期の繰延が全額 2 期目の費用**になる。
--
--    振戻（借方 5900）を当期原価に含めれば、当期原価がそのまま「累計未検収原価」になる。
--    冪等性は保たれる——当期の `wip` 仕訳（貸方 5900）だけを除けば再計算値は安定する。
--
-- ② **売上原価（COGS）の科目だけを繰り延べる。** `account_type='expense'` は販管費・営業外費用・
--    特別損失・法人税等まで含む。実データにも案件タグ付きの `6300 減価償却費` などがある。
--    貸方が COGS の控除科目（5900）なので、販管費を売上原価へ付け替えたうえで資産化することになり、
--    売上総利益が歪む。**繰り延べるのは原価科目に集計されたものだけ**とする
--    （案件に紐づく販管費は繰り延べない。これは割り切りであり ADR-0072 に明記する）。
--
-- ③ `v_wip_status` の伝票本数が `status` を見ていなかった（下書きの wip 伝票があると
--    「起票済み 0 円で陳腐化」と誤警告する）。

DROP VIEW IF EXISTS v_project_direct_cost;
CREATE VIEW v_project_direct_cost AS
SELECT e.fiscal_year_id AS fiscal_year_id,
       l.project_id     AS project_id,
       SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS direct_cost
FROM journal_lines l
JOIN journal_entries e ON e.id = l.journal_entry_id
JOIN accounts a ON a.id = l.account_id
JOIN account_categories c ON c.id = a.category_id
WHERE e.status = 'posted'
  AND a.account_type = 'expense'
  AND c.code = 'COGS'                       -- ② 原価科目だけ
  AND l.project_id IS NOT NULL
  AND COALESCE(e.source_type, '') <> 'wip'  -- ① 当期の繰延だけを除く（振戻は数える）
GROUP BY e.fiscal_year_id, l.project_id;

DROP VIEW IF EXISTS v_wip_status;
CREATE VIEW v_wip_status AS
SELECT fy.id   AS fiscal_year_id,
       fy.name AS fiscal_year_name,
       (SELECT COUNT(*) FROM v_wip_candidate c WHERE c.fiscal_year_id = fy.id) AS project_count,
       COALESCE((SELECT SUM(c.wip_amount) FROM v_wip_candidate c
                 WHERE c.fiscal_year_id = fy.id), 0) AS computed_amount,
       (SELECT COUNT(*) FROM journal_entries e
        WHERE e.source_type = 'wip' AND e.source_id = fy.id AND e.status = 'posted') AS posted_entries,
       COALESCE((SELECT SUM(l.amount) FROM journal_lines l
                 JOIN journal_entries e ON e.id = l.journal_entry_id
                 WHERE e.source_type = 'wip' AND e.source_id = fy.id
                   AND e.status = 'posted' AND l.dc = 'D'), 0) AS posted_amount,
       (SELECT COUNT(*) FROM journal_entries e
        WHERE e.source_type = 'wip_reversal' AND e.source_id = fy.id AND e.status = 'posted') AS reversal_entries,
       COALESCE((SELECT SUM(l.amount) FROM journal_lines l
                 JOIN journal_entries e ON e.id = l.journal_entry_id
                 WHERE e.source_type = 'wip_reversal' AND e.source_id = fy.id
                   AND e.status = 'posted' AND l.dc = 'C'), 0) AS reversal_amount
FROM fiscal_years fy;
