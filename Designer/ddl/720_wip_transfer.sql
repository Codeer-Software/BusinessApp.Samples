-- 720: 仕掛品（未成業務支出金）の期末振替（ADR-0072 / BUG-0016）
--
-- 判定と金額の計算は**すべてビューに置く**。画面・仕訳生成・不変条件検査の 3 者が
-- 同じビューを読むことで、同じ計算を 3 か所に書く事故（ADR-0060）を避ける。
--
-- 用語:
--   仕掛品 1200        … 資産。期末に繰り延べた原価の置き場
--   仕掛品振替高 5900  … 売上原価の控除科目（貸方が正常残）。期末に原価から差し引く相手科目

-- 科目は役割で引く（コード直書きをしない・ddl/630 と同じ作法）
UPDATE accounts SET account_role = 'wip_asset'    WHERE code = '1200' AND COALESCE(account_role, '') = '';
UPDATE accounts SET account_role = 'wip_transfer' WHERE code = '5900' AND COALESCE(account_role, '') = '';

-- ---------------------------------------------------------------------------
-- 案件別の配賦人件費（年度 × 月次 × 案件）
--   ProjectProfit.Query.sql の alloc CTE と同じ計算をビューに切り出したもの。
--   配賦は管理会計レイヤで仕訳を作らない（decisions/0009）ため、帳簿からは引けない。
--   端数は円未満切り捨て（SQLite の整数除算）。CostAllocation 画面と同じ挙動。
-- ---------------------------------------------------------------------------
DROP VIEW IF EXISTS v_project_labor_alloc;
CREATE VIEW v_project_labor_alloc AS
WITH te AS (
  SELECT fp.fiscal_year_id AS fiscal_year_id,
         fp.period_no      AS period_no,
         t.user_id         AS user_id,
         t.project_id      AS project_id,
         SUM(t.minutes)    AS mins
  FROM time_entries t
  JOIN fiscal_periods fp
    ON date(t.work_date) >= date(fp.start_date)
   AND date(t.work_date) <= date(fp.end_date)
  GROUP BY fp.fiscal_year_id, fp.period_no, t.user_id, t.project_id
),
tot AS (
  SELECT fiscal_year_id, period_no, user_id, SUM(mins) AS total_mins
  FROM te GROUP BY fiscal_year_id, period_no, user_id
)
SELECT te.fiscal_year_id AS fiscal_year_id,
       te.period_no      AS period_no,
       te.project_id     AS project_id,
       SUM(COALESCE(ms.cost, 0) * te.mins / tot.total_mins) AS labor_cost
FROM te
JOIN tot ON tot.fiscal_year_id = te.fiscal_year_id
        AND tot.period_no      = te.period_no
        AND tot.user_id        = te.user_id
LEFT JOIN monthly_salaries ms ON ms.user_id        = te.user_id
                             AND ms.fiscal_year_id = te.fiscal_year_id
                             AND ms.period_no      = te.period_no
GROUP BY te.fiscal_year_id, te.period_no, te.project_id;

-- ---------------------------------------------------------------------------
-- 案件に直課された費用（年度 × 案件）
--   **仕掛品振替の仕訳そのものは除く。** 含めると振替後に「原価が減った」と見えてしまい、
--   もう一度押すたびに金額が変わる（洗い替えが成立しない）。
--   案件別損益から仕掛品振替を除くのは ADR-0072 の決定でもある（仕掛品は期間損益の話・
--   案件別損益は案件の生涯採算の話で、目的が違うので混ぜない）。
-- ---------------------------------------------------------------------------
DROP VIEW IF EXISTS v_project_direct_cost;
CREATE VIEW v_project_direct_cost AS
SELECT e.fiscal_year_id AS fiscal_year_id,
       l.project_id     AS project_id,
       SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS direct_cost
FROM journal_lines l
JOIN journal_entries e ON e.id = l.journal_entry_id
JOIN accounts a ON a.id = l.account_id
WHERE e.status = 'posted'
  AND a.account_type = 'expense'
  AND l.project_id IS NOT NULL
  AND COALESCE(e.source_type, '') NOT IN ('wip', 'wip_reversal')
GROUP BY e.fiscal_year_id, l.project_id;

-- 案件に紐づく売上（年度 × 案件）。仕掛品の判定に使う
DROP VIEW IF EXISTS v_project_revenue;
CREATE VIEW v_project_revenue AS
SELECT e.fiscal_year_id AS fiscal_year_id,
       l.project_id     AS project_id,
       SUM(CASE WHEN l.dc = 'C' THEN l.amount ELSE -l.amount END) AS revenue
FROM journal_lines l
JOIN journal_entries e ON e.id = l.journal_entry_id
JOIN accounts a ON a.id = l.account_id
WHERE e.status = 'posted'
  AND a.account_type = 'revenue'
  AND l.project_id IS NOT NULL
GROUP BY e.fiscal_year_id, l.project_id;

-- ---------------------------------------------------------------------------
-- 仕掛品の対象（年度 × 案件）
--
-- 「未完了」の判定は **検収の有無**（ADR-0072）。売上計上が検収基準（ADR-0008）なので、
-- 収益の認識と費用の繰延を同じ事象にそろえると費用収益対応の原則がそのまま成立する。
--
-- 実装では検収に **当期売上 0** の条件を重ねる。SES・SaaS は検収を作らず毎月請求するため、
-- 検収の有無だけで判定すると**毎月売上が立っている案件の原価まで繰り延べてしまう**。
-- 「売上が 1 円も立っていない」は帳簿から読める客観的事実であり、検収基準の精神と矛盾しない。
--
-- 既知の割り切り: 中間検収がある案件は当期売上が立つので対象外になる（残りの原価も繰り延べない）。
-- 部分的な繰延は進行基準に踏み込む話で ADR-0072 のスコープ外。
-- ---------------------------------------------------------------------------
DROP VIEW IF EXISTS v_wip_candidate;
CREATE VIEW v_wip_candidate AS
SELECT fy.id   AS fiscal_year_id,
       p.id    AS project_id,
       p.code  AS project_code,
       p.name  AS project_name,
       p.department_id AS department_id,
       COALESCE(dc.direct_cost, 0) AS direct_cost,
       COALESCE(la.labor_cost, 0)  AS labor_cost,
       COALESCE(dc.direct_cost, 0) + COALESCE(la.labor_cost, 0) AS wip_amount
FROM fiscal_years fy
CROSS JOIN projects p
LEFT JOIN v_project_direct_cost dc ON dc.fiscal_year_id = fy.id AND dc.project_id = p.id
LEFT JOIN (SELECT fiscal_year_id, project_id, SUM(labor_cost) AS labor_cost
           FROM v_project_labor_alloc GROUP BY fiscal_year_id, project_id) la
       ON la.fiscal_year_id = fy.id AND la.project_id = p.id
-- 対象は**受託案件だけ**。
--   社内案件（internal）は将来の収益が無いので繰り延べる根拠が無い（発生した期の費用）。
--   SES・SaaS は検収を作らず毎月請求するため、そもそも繰り延べる対象ではない
--   （初月で請求前だと売上 0 になり得るので、売上条件だけでは弾けない）。
WHERE p.project_type = 'contract'
  AND COALESCE(dc.direct_cost, 0) + COALESCE(la.labor_cost, 0) > 0
  AND COALESCE((SELECT r.revenue FROM v_project_revenue r
                WHERE r.fiscal_year_id = fy.id AND r.project_id = p.id), 0) = 0
  AND NOT EXISTS (SELECT 1 FROM acceptances a
                  JOIN sales_orders so ON so.id = a.sales_order_id
                  WHERE so.project_id = p.id
                    AND a.status = 'confirmed'
                    AND date(a.acceptance_date) <= date(fy.end_date));

-- ---------------------------------------------------------------------------
-- 年度ごとの振替状態（画面の表示と不変条件検査が同じものを読む）
--   computed_amount … いま計算し直したら振り替えるべき額
--   posted_amount   … 実際に起票済みの額（期末振替の借方合計）
--   reversal_amount … 翌期首の振戻額（貸方合計）
-- 陳腐化（computed <> posted）は、振替の後に当年度の伝票・工数が動いたときに起きる。
-- 翌期繰越（ADR-0068）とまったく同じ性質なので、同じように画面へ出して気づけるようにする。
-- ---------------------------------------------------------------------------
DROP VIEW IF EXISTS v_wip_status;
CREATE VIEW v_wip_status AS
SELECT fy.id   AS fiscal_year_id,
       fy.name AS fiscal_year_name,
       (SELECT COUNT(*) FROM v_wip_candidate c WHERE c.fiscal_year_id = fy.id) AS project_count,
       COALESCE((SELECT SUM(c.wip_amount) FROM v_wip_candidate c
                 WHERE c.fiscal_year_id = fy.id), 0) AS computed_amount,
       (SELECT COUNT(*) FROM journal_entries e
        WHERE e.source_type = 'wip' AND e.source_id = fy.id) AS posted_entries,
       COALESCE((SELECT SUM(l.amount) FROM journal_lines l
                 JOIN journal_entries e ON e.id = l.journal_entry_id
                 WHERE e.source_type = 'wip' AND e.source_id = fy.id
                   AND e.status = 'posted' AND l.dc = 'D'), 0) AS posted_amount,
       (SELECT COUNT(*) FROM journal_entries e
        WHERE e.source_type = 'wip_reversal' AND e.source_id = fy.id) AS reversal_entries,
       COALESCE((SELECT SUM(l.amount) FROM journal_lines l
                 JOIN journal_entries e ON e.id = l.journal_entry_id
                 WHERE e.source_type = 'wip_reversal' AND e.source_id = fy.id
                   AND e.status = 'posted' AND l.dc = 'C'), 0) AS reversal_amount
FROM fiscal_years fy;
