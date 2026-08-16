-- 590_vendor_invoice_project.sql — 仕入先請求書に案件（プロジェクト）を足す（ADR-0064・BusinessAppSQLite）
--
-- 【問題】案件別損益（ProjectProfit）の直課費用は
--   「確定仕訳の費用科目で project_id IS NOT NULL の行」を集計している。
--   ところが仕入先請求書（vendor_invoices）に案件の列が無いため、
--   未払計上ボタンが作る仕訳の明細は project_id が必ず NULL になる。
--   結果、**ペルソナ最大のコストである外注費・SES 仕入が案件別損益の原価に一切乗らない**。
--
--   2026-08-16 の実測（比較レビュー docs/14 §7.2）:
--     確定仕訳の費用明細 36 行のうち案件が付いているのは 3 行だけ
--     （旅費交通費 1・新聞図書費 1・減価償却費 1 = 経費精算と固定資産の経路のみ）。
--     外注費 500,000 円（基幹システム改修 6 月分・仕訳 No.6）は project_id が NULL。
--     基幹システム改修は 売上 1,250,000 に対し直課費用 8,000（旅費のみ）で、粗利が 50 万円過大。
--
-- 【設計】先例 ddl/200_expense_project.sql（経費申請への案件選択）と同型。
--   ヘッダに 1 本持たせ、未払計上・支払の両仕訳の全明細へ伝搬する。
--   1 枚の請求書に複数案件が載るケース（外注先からの月次請求）は明細行化が要るため別途
--   （docs/issues/2026-08-16_市販ソフト比較レビューの改善候補.md P-1 の残件）。
--
-- 注意: SQLite の ALTER TABLE ADD COLUMN は IF NOT EXISTS が使えないため冪等ではない。
--       2 回目以降は「duplicate column name」エラーになるが、既に列がある＝適用済みなので害はない。

ALTER TABLE vendor_invoices ADD COLUMN project_id INTEGER REFERENCES projects(id);

-- ---- 既存データの是正 -------------------------------------------------------
-- デモ DB の仕入先請求書 5 件のうち、案件性があるのは VG-2026-071（外注費・基幹システム改修）だけ。
-- 残り 4 件は 消耗品費・広告宣伝費・雑費 の社内経費で、案件が無いのが正しい。
UPDATE vendor_invoices
SET project_id = (SELECT id FROM projects WHERE code = 'PRJ-001')
WHERE invoice_no = 'VG-2026-071';

-- 既に生成済みの仕訳にも遡って案件を入れる（未払計上 No.6 / 支払 No.7 の全明細）。
-- 新しいスクリプトは生成時に全明細へ伝搬するので、既存分だけここで揃える。
UPDATE journal_lines
SET project_id = (SELECT id FROM projects WHERE code = 'PRJ-001')
WHERE journal_entry_id IN (
  SELECT e.id FROM journal_entries e
  JOIN vendor_invoices v ON v.id = e.source_id
  WHERE e.source_type IN ('vendor_invoice', 'vendor_payment')
    AND v.invoice_no = 'VG-2026-071'
);

-- ---- 確認 -------------------------------------------------------------------
-- 期待: VG-2026-071 だけ project_id が入る
SELECT invoice_no, project_id FROM vendor_invoices ORDER BY id;

-- 期待: 基幹システム改修(PRJ-001) の直課費用が 8,000 → 508,000 になる
SELECT p.code, p.name,
       COALESCE(SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END), 0) AS direct_cost
FROM projects p
LEFT JOIN journal_lines l ON l.project_id = p.id
LEFT JOIN journal_entries e ON e.id = l.journal_entry_id AND e.status = 'posted'
LEFT JOIN accounts a ON a.id = l.account_id AND a.account_type = 'expense'
WHERE a.id IS NOT NULL
GROUP BY p.code, p.name
ORDER BY p.code;
