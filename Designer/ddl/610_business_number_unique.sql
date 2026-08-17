-- 610_business_number_unique.sql — 業務番号の一意性を DB で保証する（BUG-0069 / BUG-0133・2026-08-17）
--
-- 背景: 伝票番号（journal_entries.journal_no）・請求書番号・検収番号・受注番号・見積番号は
-- すべて「その時点の最大値 +1」をアプリが読んで採番している。読み取りと INSERT の間にロックが
-- 無いため、同時起票・一括生成の最中の手動発行などで**同じ番号が 2 件**でき、しかもエラーが
-- 出ない。実害は識別子の重複だけに留まらず、「番号で引き直して Id を得る」経路
-- （Acceptance の請求書作成→遷移／請求書に売上仕訳を紐づける処理）が**別レコードを掴む**。
--
-- 方針（2026-08-17 ユーザー決定）: **欠番は許す。** 税務上、業務番号の連続は要件ではない。
-- 欠番を埋めようとすると採番に排他が要り、削除・失敗時の巻き戻しも必要になって割に合わない。
-- したがって「連番は努力目標・一意性だけを DB で保証する」に振り切る。
--   - 採番は read-max+1 のまま（正典メソッドに一本化済み。詳細は下記）
--   - 衝突したら UNIQUE 違反で INSERT が落ちる。落ちた側は番号が付かず、押し直せば
--     新しい最大値 +1 が取れて成功する（＝欠番は発生しない。失敗した試行は行を作らないため）
--   - 伝票の確定に失敗した場合の巻き戻しは既存実装のまま（status を draft に戻す）
--
-- 一意の範囲:
--   - journal_no … **年度内一意**（docs/04_会計ドメイン設計.md §3「年度+連番でユニーク」が正）。
--     下書きは journal_no が NULL なので部分インデックスで除外する。
--   - invoice_no / acceptance_no / order_no / quote_no … **全期間一意**。
--     いずれも `INV-{yy}-{seq}` のように**番号自体に西暦下 2 桁を含む**ので、
--     年度スコープを別に持たせる必要が無く、テーブル全体で一意にできる。
--     採番プレフィックスが暦年である点は現行仕様のまま（会計年度ではない）。
--
-- 適用前提（2026-08-17 実測。sql CLI の SELECT で確認済み）:
--   journal_entries 87 件（journal_no が NULL の下書き 1 件）／invoices 16 件／acceptances 10 件
--   ／sales_orders 10 件／quotes 10 件。**重複は 5 種すべて 0 件**なので、
--   データの書き換え無しでインデックスを張れる。
--
-- 注意: vendor_invoices.invoice_no は**仕入先が発行した請求書番号**（自社の採番ではない）なので
-- 対象外。expense_request.asset_no も手入力の資産管理番号なので対象外。

-- 伝票番号: 年度内一意（下書き＝journal_no IS NULL は対象外）
CREATE UNIQUE INDEX IF NOT EXISTS idx_journal_entries_year_no_unique
    ON journal_entries(fiscal_year_id, journal_no) WHERE journal_no IS NOT NULL;

-- 請求書番号 / 検収番号 / 受注番号 / 見積番号: 全期間一意（空文字は将来の取込データ対策で除外）
CREATE UNIQUE INDEX IF NOT EXISTS idx_invoices_no_unique
    ON invoices(invoice_no) WHERE invoice_no IS NOT NULL AND invoice_no <> '';

CREATE UNIQUE INDEX IF NOT EXISTS idx_acceptances_no_unique
    ON acceptances(acceptance_no) WHERE acceptance_no IS NOT NULL AND acceptance_no <> '';

CREATE UNIQUE INDEX IF NOT EXISTS idx_sales_orders_no_unique
    ON sales_orders(order_no) WHERE order_no IS NOT NULL AND order_no <> '';

CREATE UNIQUE INDEX IF NOT EXISTS idx_quotes_no_unique
    ON quotes(quote_no) WHERE quote_no IS NOT NULL AND quote_no <> '';

-- 040_journal.sql:73 の idx_journal_entries_year_no（非ユニーク・同じ列構成）は**残す**。
-- 部分インデックスは journal_no IS NULL の行を含まないため、「年度で絞るだけ」の検索
-- （下書き一覧・年度別集計）は既存インデックスが引き続き受け持つ。落とすと実行計画が変わる。
