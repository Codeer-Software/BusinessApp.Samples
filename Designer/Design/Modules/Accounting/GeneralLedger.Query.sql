-- 総勘定元帳（＋補助元帳: 補助科目・部門・案件の任意絞り込み）
-- @sub_account_id / @department_id / @project_id はいずれも NULL=絞り込みなし（従来の総勘定元帳）。
-- 【残高の意味】絞り込みなし: 期首残高＋期中累計（従来どおり）。
--              絞り込みあり: opening_balances は科目単位でしか持たないため期首残高を含めず、
--              「期中発生分のみの累計」を表示する（date_from より前の期中発生分は繰越に含む）。
WITH yr AS (
  SELECT id, start_date FROM fiscal_years
  WHERE date(start_date) <= date(COALESCE(@date_from, @date_to, date('now', 'localtime')))
    AND date(end_date) >= date(COALESCE(@date_from, @date_to, date('now', 'localtime')))
),
base AS (
  SELECT
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              WHERE ob.fiscal_year_id IN (SELECT id FROM yr) AND ob.account_id = @account_id), 0)
    * (CASE WHEN @sub_account_id IS NULL AND @department_id IS NULL AND @project_id IS NULL
            THEN 1 ELSE 0 END)
    +
    COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
              FROM journal_lines l
              JOIN journal_entries e ON e.id = l.journal_entry_id
              WHERE e.status = 'posted'
                AND l.account_id = @account_id
                AND (@sub_account_id IS NULL OR l.sub_account_id = @sub_account_id)
                AND (@department_id IS NULL OR l.department_id = @department_id)
                AND (@project_id IS NULL OR l.project_id = @project_id)
                AND date(e.entry_date) >= (SELECT date(start_date) FROM yr)
                AND @date_from IS NOT NULL
                AND date(e.entry_date) < date(@date_from)), 0) AS dmc
)
SELECT
  e.id AS entry_id,   -- 伝票へのドリルダウン用（ADR-0065）。表示はせず OpenAnchor の IdVariable が読む
  e.entry_date,
  e.journal_no,
  l.line_no,
  CASE
    WHEN (SELECT COUNT(*) FROM journal_lines x WHERE x.journal_entry_id = e.id AND x.id <> l.id) = 1
      THEN (SELECT a2.name FROM journal_lines x JOIN accounts a2 ON a2.id = x.account_id
            WHERE x.journal_entry_id = e.id AND x.id <> l.id)
    ELSE '諸口'
  END AS counter_account_name,
  COALESCE(l.description, e.description, '') AS line_description,
  CASE WHEN l.dc = 'D' THEN l.amount END AS debit_amount,
  CASE WHEN l.dc = 'C' THEN l.amount END AS credit_amount,
  (SELECT dmc FROM base) * (CASE WHEN a.dc_normal = 'D' THEN 1 ELSE -1 END)
  + SUM((CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
        * (CASE WHEN a.dc_normal = 'D' THEN 1 ELSE -1 END))
      OVER (ORDER BY date(e.entry_date), e.journal_no, l.line_no
            ROWS UNBOUNDED PRECEDING) AS balance
FROM journal_lines l
JOIN journal_entries e ON e.id = l.journal_entry_id
JOIN accounts a ON a.id = l.account_id
WHERE e.status = 'posted'
  AND l.account_id = @account_id
  AND (@sub_account_id IS NULL OR l.sub_account_id = @sub_account_id)
  AND (@department_id IS NULL OR l.department_id = @department_id)
  AND (@project_id IS NULL OR l.project_id = @project_id)
  AND date(e.entry_date) >= COALESCE(date(@date_from), (SELECT date(start_date) FROM yr))
  AND (@date_to IS NULL OR date(e.entry_date) <= date(@date_to))
ORDER BY date(e.entry_date), e.journal_no, l.line_no
