-- 600_expense_lines.sql — 経費申請に明細行を持たせる（ADR-0066・2026-08-17）
--
-- 背景: 経費申請は 1 申請 = 1 金額で、レシート 1 枚につき申請 1 件になっていた。
-- 月末にレシートを 10 枚出す社員は申請を 10 件作り、承認者は 10 回承認する。
-- ADR-0048 で承認を積み上げ方式（課長 → 部長）にしたため押下回数はさらに倍加していた。
--
-- 方針（ADR-0066）:
--   - 費目・金額・利用日・案件・税区分は「行」が持つ
--   - ヘッダの amount / tax_amount は**新設せず**明細合計の集計列に転用する
--     （精算キュー・ポータル・資金繰り予測・申請中ビュー・支払仕訳がこの列を見ているため、
--      意味を変えるだけで下流が無修正で動く。実際に確認した参照箇所:
--        Shell/PortalTodoData.Query.sql・Shell/PortalQueueData.Query.sql・
--        Shell/PortalAlertData.Query.sql・Management/CashFlowForecastData.Query.sql・
--        ddl/440_my_application_view.sql・Expense/ExpenseSettlementQueue.mod.json）
--   - ヘッダの expense_date は「利用日」から「計上日（仕訳日付）」へ意味を変える
--     （利用日は行が持つ。仕訳の日付はヘッダで 1 つ決める必要があるため）
--
-- 移行: 既存 14 件を「1 申請 → 1 明細」で機械的に写す。単一行なので合計は一致し、
--       金額は 1 円も動かない。承認ルートも ADR-0066 の合成規則により現行と同一に帰着する。
--
-- 注意: FK 列に NOT NULL を付けない（Project.md 知見）。日付=DATE / 日時=DATETIME。
--       ヘッダ側の移動元列（expense_category_id 等）は**削除しない**——判断の経緯と
--       ロールバックの余地を残すため（docs/qa の「中身を消さない」方針と同じ）。
--       モジュールがマッピングを外すので、移行時点の値で凍結される。

CREATE TABLE IF NOT EXISTS expense_request_lines (
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    expense_request_id    INTEGER REFERENCES expense_request(id),
    line_no               INTEGER,
    used_date             DATE,                                    -- 利用日（行ごと）
    expense_category_id   INTEGER REFERENCES expense_categories(id),
    tax_category_id       INTEGER REFERENCES tax_categories(id),   -- 費目の既定を写した初期値。行で上書き可
    amount                INTEGER,                                 -- 金額（税込）
    tax_amount            INTEGER,                                 -- うち消費税（手入力。空なら税区分から計算）
    project_id            INTEGER REFERENCES projects(id),         -- 案件（任意・行ごと）
    used_at               TEXT,                                    -- 利用先（店名・レシート発行元）
    description           TEXT,                                    -- 摘要（空ならヘッダ件名を仕訳摘要に使う）
    entertainment_guest   TEXT,                                    -- 交際費 3 項目（行の費目が交際費のとき必須）
    entertainment_count   INTEGER,
    entertainment_purpose TEXT,
    is_fixed_asset        INTEGER,                                 -- 固定資産計上対象（行ごとに判定）
    asset_no              TEXT,                                    -- 資産管理番号
    receipt_file_name     TEXT,                                    -- 領収書（FileField 3 列・行ごと）
    receipt_file_size     INTEGER,
    receipt_file_guid     TEXT
);

CREATE INDEX IF NOT EXISTS idx_expense_request_lines_req ON expense_request_lines(expense_request_id);
CREATE INDEX IF NOT EXISTS idx_expense_request_lines_cat ON expense_request_lines(expense_category_id);
CREATE INDEX IF NOT EXISTS idx_expense_request_lines_prj ON expense_request_lines(project_id);

-- ---- 既存データの移行（1 申請 → 1 明細） ----
-- 税区分は費目マスタの既定を写す（現行の仕訳生成が cat.DefaultTaxCategory を使っているため、
-- 写した値で仕訳を作り直しても同じ結果になる）。
-- 利用日は現行ヘッダの expense_date（＝これまでの「利用日」）をそのまま行へ移す。
-- 冪等: 既に明細を持つ申請は対象外。

INSERT INTO expense_request_lines
    (expense_request_id, line_no, used_date, expense_category_id, tax_category_id,
     amount, tax_amount, project_id, used_at, description,
     entertainment_guest, entertainment_count, entertainment_purpose,
     is_fixed_asset, asset_no, receipt_file_name, receipt_file_size, receipt_file_guid)
SELECT e.id, 1, e.expense_date, e.expense_category_id,
       (SELECT c.default_tax_category_id FROM expense_categories c WHERE c.id = e.expense_category_id),
       e.amount, e.tax_amount, e.project_id, e.used_at, NULL,
       e.entertainment_guest, e.entertainment_count, e.entertainment_purpose,
       e.is_fixed_asset, e.asset_no, e.receipt_file_name, e.receipt_file_size, e.receipt_file_guid
  FROM expense_request e
 WHERE NOT EXISTS (SELECT 1 FROM expense_request_lines l WHERE l.expense_request_id = e.id);
