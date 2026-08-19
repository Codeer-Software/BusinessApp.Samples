-- 売掛残高一覧: 請求書ごとの税込請求額・入金累計・残額・状態
-- void/draft は除外。日付比較は date() で正規化 (Project.md 知見)。支払期限昇順。
-- status='paid' は残額 0・入金済として扱う（少額差額の自動処理＝手数料処理済みで
-- 債権は消えているため。入金累計だけで判定すると差額分が「一部入金」に見えてしまう）。
-- 検索パラメータ:
--   @partner_id   取引先（NULL=絞り込みなし。無効化済み取引先の残高も探せるよう
--                 ドロップダウン側は IsActive で絞らない。案件・部門も同じ思想）
--   @project_id   案件（NULL=絞り込みなし）
--   @department_id 部門（NULL=絞り込みなし）
--   @state_filter 状態（exclude_paid=「入金済を除く」／それ以外は状態ラベルの完全一致）
--   @due_from / @due_to 支払期限の範囲
-- 入金累計は「消込済み（消込仕訳がある）入金」だけを数える。請求書の発行時には税込全額の
-- 未確定入金＝入金予定が自動作成される（ADR-0032）ため、単純合計にすると発行しただけの
-- 請求書が「入金済・残額 0」に見え、既定フィルタ（入金済を除く）から消える（改善候補 A-2）。
-- 「確定済み」の表現は journal_entries(source_type='receipt', source_id) の存在（ReceiptList と同じ流儀）
-- 入金の消込額はビュー **v_invoice_received**（ddl/770）から引く。
-- 入金は 1 件で複数の請求書に消し込めるので（ADR-0071）、`receipts.invoice_id` は当てにならない。
-- 同じ式が 6 か所に複製されていたのを 1 本に畳んである。
WITH rc AS (
  SELECT invoice_id, received FROM v_invoice_received
),
base AS (
  SELECT
    i.invoice_no AS invoice_no,
    i.partner_id AS partner_id,
    i.project_id AS project_id,
    i.department_id AS department_id,
    p.name AS partner_name,
    i.title AS title,
    i.issue_date AS issue_date,
    i.due_date AS due_date,
    COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0) AS total_amount,
    COALESCE(rc.received, 0) AS received_amount,
    CASE WHEN i.status = 'paid' THEN 0
         ELSE COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0) - COALESCE(rc.received, 0)
    END AS balance,
    CASE
      WHEN i.status = 'paid' THEN '入金済'
      WHEN COALESCE(rc.received, 0) >= COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0) THEN '入金済'
      WHEN i.due_date IS NOT NULL AND date(i.due_date) < date('now', 'localtime') THEN '期限超過'
      WHEN COALESCE(rc.received, 0) > 0 THEN '一部入金'
      ELSE '未入金'
    END AS state
  FROM invoices i
  LEFT JOIN partners p ON p.id = i.partner_id
  LEFT JOIN rc ON rc.invoice_id = i.id
  WHERE i.status <> 'void' AND i.status <> 'draft'
)
SELECT invoice_no, partner_name, title, issue_date, due_date,
       total_amount, received_amount, balance, state
FROM base
WHERE (@partner_id IS NULL OR partner_id = @partner_id)
  AND (@project_id IS NULL OR project_id = @project_id)
  AND (@department_id IS NULL OR department_id = @department_id)
  AND (@state_filter IS NULL OR @state_filter = ''
       OR (@state_filter = 'exclude_paid' AND state <> '入金済')
       OR state = @state_filter)
  AND (@due_from IS NULL OR date(due_date) >= date(@due_from))
  AND (@due_to IS NULL OR date(due_date) <= date(@due_to))
-- 支払期限が NULL の行を先頭に押し上げない（BUG-0139）。SQLite は NULL を最小として並べるため、
-- 旧実装では「期限が無い＝督促のしようがない請求」が一覧の一番上を占めていた。
-- 入口（`Invoice.DueDate`）は必須にしたが、移行データが混じっても並びが壊れないようにしておく
ORDER BY (due_date IS NULL), date(due_date), invoice_no
