-- 824_idempotency_unique.sql — 二重生成を DB で止める（冪等性をアプリ任せにしない）
--
-- 定期請求・SES の請求書生成と、そこから立つ売上仕訳は、いま**アプリの read-then-write だけ**で
-- 冪等性を保っている（「同契約×同月の請求書を探して、無ければ作る」）。
-- 検査と INSERT の間にロックが無いので、2 タブ同時実行・二度押し・プラン再構築中の実行で
-- **同じ月の請求書が 2 枚**立ちうる。
--
-- **不変条件では捕まらない**のがこの穴の本質: C01/C02（ヘッダ＝明細）・C08（元帳＝売掛一覧）・
-- C13（入金予定＝残額）はどれも請求書ごとに自己完結して整合するので、
-- **2 枚とも「正しい請求書」として通る**。気づけるのは取引先からのクレームだけ。
--
-- `740_journal_source_unique.sql` が仕訳側に同じ理屈（BUG-0353）で入れた対処の、請求書側の抜け。
-- 既存データに違反が無いことを確認済みなので、そのまま張れる。

-- 1) 定期請求: 契約 × 対象月 × 生成元 で 1 枚
CREATE UNIQUE INDEX IF NOT EXISTS ux_invoices_recurring_month
  ON invoices(recurring_billing_id, billing_month, invoice_source)
  WHERE recurring_billing_id IS NOT NULL AND billing_month IS NOT NULL;

-- 2) SES: 案件 × 対象月 で 1 枚
CREATE UNIQUE INDEX IF NOT EXISTS ux_invoices_ses_month
  ON invoices(project_id, billing_month)
  WHERE invoice_source = 'ses' AND project_id IS NOT NULL AND billing_month IS NOT NULL;

-- 3) 期首残高: 年度 × 科目 × 補助 × 部門 で 1 行
--
--    導入時の期首残高 CSV／SQL を 2 回流すと全科目が倍になり、BS・試算表・元帳の期首・
--    C/F 計算書の期首・資金繰り予測の初期現預金がすべて倍になる。
--    **B01（符号付き合計 = 0）は倍になっても 0 のままなので通る**のがこの穴の本質。
--    B03（翌期首＝前期末）は前期・翌期の両方に期首残高がある年度しか見ないので、
--    最新年度と導入初年度は永久に検査対象外。
--
--    `812_budget_salary_unique.sql` が budget_lines / monthly_salaries に入れたのと同じ趣旨で、
--    `opening_balances` だけ取り残されていた（この画面にも `.mod.cs` が無く、重複チェックを置く場所が無い）。
--
--    **素の UNIQUE では止まらない**——SQLite は NULL を常に相異なると扱うので、
--    `sub_account_id` / `department_id` が NULL の 40 行が素通りする。**式インデックスにする**。
CREATE UNIQUE INDEX IF NOT EXISTS ux_opening_balances_key
  ON opening_balances(fiscal_year_id, account_id,
                      COALESCE(sub_account_id, 0), COALESCE(department_id, 0));

-- 4) 740 の「1 元 1 本」に recurring / recurring_annual / ses を足す
--
--    740 は「確証が無いので今回は対象外にする（保守的に）」として 3 種を外していた。
--    その後 F02 の対応表で `source_id` が**請求書 id**であることが確定し、
--    「請求書 1 枚につき売上仕訳 1 本」で間違いないと確かめられた（実データも重複 0 件）。
--    多対 1 が正しいもの（depreciation / recurring_defer / template）は引き続き対象外。
DROP INDEX IF EXISTS ux_journal_entries_source_single;
CREATE UNIQUE INDEX ux_journal_entries_source_single
  ON journal_entries(source_type, source_id)
  WHERE source_id IS NOT NULL
    AND source_type IN ('acceptance', 'bank', 'disposal', 'expense', 'expense_payment',
                        'receipt', 'vendor_invoice', 'vendor_payment', 'wip', 'wip_reversal',
                        'recurring', 'recurring_annual', 'ses');

-- 5) 人件費コストの「登録済み」判定に中身を見させる
--
--    `v_missing_salary` は行の存在しか見ていないので、`cost` が NULL や 0 の行を入れると
--    「登録済み」と判定して警告を出さない。一方 `v_project_labor_alloc` は `COALESCE(ms.cost, 0)` で
--    0 円配賦するので、**労務費が静かに欠落**する。
--    B06（仕掛品の期末振替と翌期首の振戻）は両方が同じ誤った労務費で計算されるため**一致して合格する**。
DROP VIEW IF EXISTS v_missing_salary;
CREATE VIEW v_missing_salary AS
SELECT fp.fiscal_year_id AS fiscal_year_id,
       COUNT(*)          AS missing_count
FROM (SELECT DISTINCT fp2.fiscal_year_id AS fy, fp2.period_no AS pno, t.user_id AS uid
      FROM time_entries t
      JOIN fiscal_periods fp2
        ON date(t.work_date) >= date(fp2.start_date)
       AND date(t.work_date) <= date(fp2.end_date)) x
JOIN fiscal_periods fp ON fp.fiscal_year_id = x.fy AND fp.period_no = x.pno
WHERE NOT EXISTS (SELECT 1 FROM monthly_salaries ms
                  WHERE ms.fiscal_year_id = x.fy AND ms.period_no = x.pno AND ms.user_id = x.uid
                    AND ms.cost IS NOT NULL AND ms.cost > 0)
GROUP BY fp.fiscal_year_id;

SELECT name FROM sqlite_master
WHERE name IN ('ux_invoices_recurring_month', 'ux_invoices_ses_month',
               'ux_opening_balances_key', 'ux_journal_entries_source_single', 'v_missing_salary')
ORDER BY name;
