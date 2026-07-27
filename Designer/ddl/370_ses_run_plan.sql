-- 370: SES 精算・請求の実行プラン（プレビュー兼実行指示書。ADR-0036、350_recurring_run_plan と同設計）
-- BuildPlan(対象月) が全行を洗い替えで書き込む一時テーブル。
--   ・プレビュー: 画面の一覧はこのテーブルを表示するだけ（判定ロジックを持たない）
--   ・実行: 「一括生成」は status='planned' の行を機械的に消費する（判定は BuildPlan のみ）
-- 日付列は DATE 宣言（TEXT 禁止。Project.md 2026-07-05）。FK 列に NOT NULL は付けない
CREATE TABLE IF NOT EXISTS ses_run_plan (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    target_month   DATE,                -- 対象月（月初日）
    project_id     INTEGER REFERENCES projects(id),
    partner_id     INTEGER REFERENCES partners(id),
    actual_time    TEXT,                -- 対象月の実績時間（例: 168h30m）
    invoice_amount INTEGER,             -- 請求額（税抜）
    tax_amount     INTEGER,             -- 消費税額
    status         TEXT,                -- planned / done / excluded
    detail         TEXT,                -- 精算式・理由の説明文
    invoice_no     TEXT                 -- 生成済みの請求書番号
);
