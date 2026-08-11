-- 490_tax_category_required.sql — 税区分未設定（NULL）を廃止する（ADR-0052・2026-08-12）
--
-- 背景: 「税区分が無い」を NULL で表す一方、税区分マスタには OUT_OF_SCOPE（対象外）が存在し、
-- 同じ意味を 2 通りで表現していた。集計を書くたびに両方を意識する必要があり、実際に
-- TaxSummary.Query.sql が `tax_category_id IS NOT NULL` で NULL 行を切り捨てて
-- 改善候補 B-5（インポートした税行が消費税集計表から消える）を生んでいる。
--
-- 方針: 貸借対照表科目の税区分は「対象外」とする（市販会計ソフトの流儀。2026-08-12 リサーチで確認）。
-- NULL は使わない。以後、勘定科目マスタの既定税区分が全科目に入っているので、
-- 仕訳入力時は科目を選べば税区分が自動で入り、利用者の手間は増えない。
--
-- 実行順に意味がある: ② を ③ より先に流すこと。③ で BS 科目に「対象外」が入ると、
-- ② の「本体行の税区分が 1 種類だけ」という継承条件が壊れる（対象外が 2 種類目として数えられる）。

-- ---- ① 勘定科目マスタ: 既定税区分が無い科目を「対象外」にする ----
-- 対象は BS 科目（現金・預金・売掛金・買掛金・仮払/仮受消費税 等）と、
-- 内部振替のため課税取引ではない 5900 仕掛品振替高。
UPDATE accounts
SET default_tax_category_id = (SELECT id FROM tax_categories WHERE code = 'OUT_OF_SCOPE')
WHERE default_tax_category_id IS NULL;

-- ---- ② 仕訳: 税行（is_tax_line=1）の税区分を同一伝票の本体行から継承する ----
-- 税行に「対象外」を入れてはいけない（その税額が消費税集計表から消える＝B-5 の再発）。
-- 税行は本体行に対する消費税なので、本体行と同じ税区分を持つのが正しい。
-- 本体行の税区分が 1 種類に定まる伝票だけを対象にし、定まらないものは NULL のまま残して
-- 末尾の検証クエリで表面化させる（推測で埋めない）。
UPDATE journal_lines
SET tax_category_id = (
        SELECT MIN(p.tax_category_id)
        FROM journal_lines p
        WHERE p.journal_entry_id = journal_lines.journal_entry_id
          AND p.is_tax_line = 0
          AND p.tax_category_id IS NOT NULL
    )
WHERE is_tax_line = 1
  AND tax_category_id IS NULL
  AND (
        SELECT COUNT(DISTINCT p.tax_category_id)
        FROM journal_lines p
        WHERE p.journal_entry_id = journal_lines.journal_entry_id
          AND p.is_tax_line = 0
          AND p.tax_category_id IS NOT NULL
      ) = 1;

-- ---- ③ 仕訳: 残る本体行の税区分を勘定科目マスタの既定で埋める ----
UPDATE journal_lines
SET tax_category_id = (
        SELECT a.default_tax_category_id FROM accounts a WHERE a.id = journal_lines.account_id
    )
WHERE tax_category_id IS NULL
  AND is_tax_line = 0;

-- ---- ④ 定型仕訳: 同じ考え方で埋める（定型仕訳は journal_lines を生む経路のひとつ） ----
UPDATE journal_template_lines
SET tax_category_id = (
        SELECT a.default_tax_category_id FROM accounts a WHERE a.id = journal_template_lines.account_id
    )
WHERE tax_category_id IS NULL;

-- ---- ⑤ 検証: すべて 0 件になること ----
-- 0 件でない行が残ったら、税区分を推定できなかった行なので個別に判断すること。
SELECT '勘定科目に既定税区分が無い' AS check_name, COUNT(*) AS remaining FROM accounts WHERE default_tax_category_id IS NULL
UNION ALL
SELECT '仕訳明細に税区分が無い', COUNT(*) FROM journal_lines WHERE tax_category_id IS NULL
UNION ALL
SELECT '定型仕訳明細に税区分が無い', COUNT(*) FROM journal_template_lines WHERE tax_category_id IS NULL
UNION ALL
SELECT '税行なのに対象外/不課税が付いた仕訳明細', COUNT(*)
  FROM journal_lines l JOIN tax_categories tc ON tc.id = l.tax_category_id
 WHERE l.is_tax_line = 1 AND tc.taxation_type IN ('out_of_scope', 'non_taxable');
