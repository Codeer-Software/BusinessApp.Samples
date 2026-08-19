-- 合計残高試算表
--
-- 【期間が空のとき】日付（自）／（至）を消して検索されたら、当年度（＝入っている方の日付、
--   どちらも空なら今日を含む会計年度）の期首／期末で補う（BUG-0274）。
--   空を「全期間」と解釈すると期首繰越が一切乗らず、貸借の崩れた表が正しい顔で出てしまう。
--   GeneralLedger.Query.sql / ProfitLoss.Query.sql と同じ「SQL 側で当年度へフォールバックする」流儀。
-- 【最下行】合計（貸借検算）行。繰越・期末は借方−貸方の純額なので、貸借が合っていれば 0 になる（BUG-0276）。
-- 【部門・案件で絞る】@department_id / @project_id はいずれも NULL＝絞り込みなし（BUG-0002）。
--   総勘定元帳には元からあり、試算表だけ日付 2 つしか無かった——同じ会計部品の中で非対称なうえ、
--   **部門別損益の入口が塞がっていた**（部門で絞った試算表が、部門別 PL のいちばん素朴な形）。
--   期首の扱いは GeneralLedger.Query.sql と同一にする:
--     ・部門で絞ったときは `opening_balances.department_id` で期首も絞る
--       （翌期繰越が (科目 × 補助科目 × 部門) の粒度で作られるため、含めるのが正しい）
--     ・**案件で絞ったときだけは期首残高を含めない**——opening_balances に案件の次元が無く、
--       期首を案件へ割り当てる術がない。0 起算であることは繰越列が 0 になることで表れる
--   絞り込みが入ると当然ながら貸借は一致しない（最下行の検算は 0 にならない）。
--   これは正しい振る舞い——部門・案件は仕訳の片側にしか付かないことがあるため
WITH fy AS (
  SELECT id, start_date, end_date FROM fiscal_years
  WHERE date(start_date) <= date(COALESCE(@date_from, @date_to, date('now', 'localtime')))
    AND date(end_date)   >= date(COALESCE(@date_from, @date_to, date('now', 'localtime')))
),
rng AS (
  SELECT
    COALESCE(date(@date_from), (SELECT date(start_date) FROM fy), '0001-01-01') AS d_from,
    COALESCE(date(@date_to),   (SELECT date(end_date)   FROM fy), '9999-12-31') AS d_to
),
ob AS (
  SELECT account_id, SUM(balance) AS bal
  FROM opening_balances
  WHERE fiscal_year_id IN (SELECT id FROM fy)
    AND (@department_id IS NULL OR department_id = @department_id)
    -- 案件で絞ったときは期首を持ち込まない（上の【部門・案件で絞る】を参照）
    AND @project_id IS NULL
  GROUP BY account_id
),
pre AS (
  SELECT l.account_id, SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS dmc
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  WHERE e.status = 'posted'
    AND date(e.entry_date) >= (SELECT date(start_date) FROM fy)
    AND date(e.entry_date) < (SELECT d_from FROM rng)
    AND (@department_id IS NULL OR l.department_id = @department_id)
    AND (@project_id    IS NULL OR l.project_id    = @project_id)
  GROUP BY l.account_id
),
sums AS (
  SELECT
    l.account_id,
    SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE 0 END) AS dsum,
    SUM(CASE WHEN l.dc = 'C' THEN l.amount ELSE 0 END) AS csum
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  WHERE e.status = 'posted'
    AND date(e.entry_date) >= (SELECT d_from FROM rng)
    AND date(e.entry_date) <= (SELECT d_to FROM rng)
    AND (@department_id IS NULL OR l.department_id = @department_id)
    AND (@project_id    IS NULL OR l.project_id    = @project_id)
  GROUP BY l.account_id
),
merged AS (
  SELECT
    a.id,
    a.code,
    a.name,
    a.dc_normal,
    COALESCE(o.bal, 0) + COALESCE(p.dmc, 0) AS open_dmc,
    COALESCE(s.dsum, 0) AS dsum,
    COALESCE(s.csum, 0) AS csum
  FROM accounts a
  LEFT JOIN ob o ON o.account_id = a.id
  LEFT JOIN pre p ON p.account_id = a.id
  LEFT JOIN sums s ON s.account_id = a.id
  WHERE COALESCE(o.bal, 0) <> 0 OR COALESCE(p.dmc, 0) <> 0
     OR COALESCE(s.dsum, 0) <> 0 OR COALESCE(s.csum, 0) <> 0
)
SELECT * FROM (
  SELECT
    m.id AS account_id_raw,   -- 元帳へのドリルダウン用（ADR-0065）。表示せず DrillButton の遷移先解決に使う
    -- 元帳へ引き継ぐ検索条件（BUG-0002）。
    -- **行スクリプトからは検索インスタンスの `SearchMin` / `SearchValue` が見えない**——
    -- クエリ一覧の行アクションは行ごとの別インスタンスで動くため、そこで検索欄を読むと必ず null になる。
    -- 実際、旧実装の `drill_from` / `drill_to` は一度も URL に乗っていなかった（実測）。
    -- **SQL の出力列に載せて DataOnlyFields で受け取る**のが、この構造で唯一確実な受け渡し。
    -- 日付は解決後の実効範囲（rng）を渡す——期間を空にして開いた人にも当年度が引き継がれる
    (SELECT d_from FROM rng) AS drill_from,
    (SELECT d_to   FROM rng) AS drill_to,
    @department_id AS drill_dept,
    @project_id    AS drill_project,
    '元帳' AS drill_label,    -- リンク文字。合計行は空にしてリンクを消す（IsVisible はリスト内のアンカーに効かない）
    m.code AS account_code,
    m.name AS account_name,
    CASE WHEN m.dc_normal = 'D' THEN m.open_dmc ELSE -m.open_dmc END AS opening_balance,
    m.dsum AS debit_total,
    m.csum AS credit_total,
    CASE WHEN m.dc_normal = 'D' THEN m.open_dmc + m.dsum - m.csum
         ELSE -m.open_dmc + m.csum - m.dsum END AS balance
  FROM merged m
  UNION ALL
  -- 合計（貸借検算）行。繰越・残高は借方−貸方の純額（＝貸借一致なら 0）、借方合計と貸方合計は一致するのが正。
  SELECT
    NULL AS account_id_raw,
    (SELECT d_from FROM rng) AS drill_from,
    (SELECT d_to   FROM rng) AS drill_to,
    @department_id AS drill_dept,
    @project_id    AS drill_project,
    '' AS drill_label,
    '' AS account_code,
    -- 部門・案件で絞ると貸借は一致しないのが正しい（片側にしか付かない仕訳がある）。
    -- **検算行の見出しでそう言う**——黙って 0 でない数字を「貸借検算」の名前で出すと、
    -- 帳簿が壊れているように見えてしまう（BUG-0002）
    CASE WHEN @department_id IS NULL AND @project_id IS NULL
         THEN '合計（貸借検算）'
         ELSE '合計（絞り込み中のため貸借は一致しません）'
    END AS account_name,
    SUM(m2.open_dmc) AS opening_balance,
    SUM(m2.dsum) AS debit_total,
    SUM(m2.csum) AS credit_total,
    SUM(m2.open_dmc + m2.dsum - m2.csum) AS balance
  FROM merged m2
)
ORDER BY CASE WHEN account_code = '' THEN 1 ELSE 0 END, account_code
