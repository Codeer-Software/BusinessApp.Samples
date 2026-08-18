-- 760: 仕掛品の「人件費が未入力の月」を数える／配賦ビューの 0 除算を塞ぐ（BUG-0367 / BUG-0373）
--
-- ① 人件費コスト（`monthly_salaries`）が未入力の月の工数は、配賦で **0 円**として扱われる。
--    仕掛品の金額が入力漏れの分だけ静かに過小になり、決算整理仕訳としてそのまま帳簿に載る。
--    金額を勝手に補うことはできない（いくらか分からない）ので、**画面で気づけるようにする**。
--    配賦画面の「⚠未配賦」と同じ考え方。
-- ② `tot.total_mins = 0` になると `cost * mins / 0` が NULL になり、その人の配賦が黙って落ちる。
--    0 分の工数行はデータとしてありうるので、明示的に除く。

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
  HAVING SUM(mins) > 0        -- ② 0 除算を作らない
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

-- 「工数はあるのに人件費コストが無い」人・月の数（年度ごと）
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
                  WHERE ms.fiscal_year_id = x.fy AND ms.period_no = x.pno AND ms.user_id = x.uid)
GROUP BY fp.fiscal_year_id;

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
                   AND e.status = 'posted' AND l.dc = 'C'), 0) AS reversal_amount,
       COALESCE((SELECT m.missing_count FROM v_missing_salary m
                 WHERE m.fiscal_year_id = fy.id), 0) AS missing_salary_count
FROM fiscal_years fy;
