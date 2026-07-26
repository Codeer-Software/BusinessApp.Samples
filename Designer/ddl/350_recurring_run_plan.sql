-- 350: 定期請求の実行プラン（プレビュー兼実行指示書。ADR-0034）
-- BuildPlan(対象月) が全行を洗い替えで書き込む一時テーブル。
--   ・プレビュー: 画面の一覧はこのテーブルを表示するだけ（判定ロジックを持たない）
--   ・実行: 「一括生成」は status='planned' の行を機械的に消費する（判定は BuildPlan のみ）
-- 日付列は DATE 宣言（TEXT 禁止。Project.md 2026-07-05）。FK 列に NOT NULL は付けない
CREATE TABLE IF NOT EXISTS recurring_run_plan (
    id                   INTEGER PRIMARY KEY AUTOINCREMENT,
    target_month         DATE,                -- 対象月（月初日）
    recurring_billing_id INTEGER REFERENCES recurring_billings(id),
    partner_id           INTEGER REFERENCES partners(id),
    department_id        INTEGER REFERENCES departments(id),
    billing_cycle        TEXT,                -- monthly / yearly
    plan_kind            TEXT,                -- monthly / annual / defer / none（実行時の処理種別）
    status               TEXT,                -- planned / done / excluded
    detail               TEXT,                -- 内容・理由の説明文
    invoice_amount       INTEGER,             -- 請求額（税抜。defer 行は NULL）
    tax_amount           INTEGER,             -- 消費税額
    defer_amount         INTEGER,             -- 当月の按分振替額（年払いのみ）
    cycle_start          DATE,                -- 年払い周期の起点月（月初日）
    cycle_index          INTEGER,             -- 周期内の月番号（0 起点）
    annual_invoice_id    INTEGER,             -- 按分振替のアンカー年額請求書 id（defer 行）
    invoice_no           TEXT                 -- 生成済み・関連の請求書番号
);
