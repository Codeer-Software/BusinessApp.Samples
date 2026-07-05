-- 070_approval.sql — 承認フロー一式＋経費申請（AccountingSQLite / accounting_v1.db）
-- 出典: PatternShowcaseAuth（テンプレート駆動承認ワークフローの正典）のモジュール定義から導出。
-- B-2 経費精算の基盤。日付=DATE / 日時=DATETIME 宣言（TEXT 禁止 — Project.md 知見）。
-- parent_id は TEXT（申請モジュール名＋ID の多態参照。TemporaryIdResolver が解決するため FK なし）。

CREATE TABLE IF NOT EXISTS approval_flow_template (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    description TEXT
);

CREATE TABLE IF NOT EXISTS approval_flow_template_order (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    template_id INTEGER NOT NULL REFERENCES approval_flow_template(id),
    order_no INTEGER
);

CREATE TABLE IF NOT EXISTS approval_flow_template_member (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    template_order_id INTEGER NOT NULL REFERENCES approval_flow_template_order(id),
    is_required INTEGER NOT NULL DEFAULT 0,
    approver_user_id INTEGER REFERENCES app_users(id)
);

CREATE TABLE IF NOT EXISTS approval_flow (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_module_name TEXT,
    parent_id TEXT,
    template_id INTEGER REFERENCES approval_flow_template(id),
    status TEXT,
    attempt_no INTEGER,
    creator INTEGER REFERENCES app_users(id),
    updater INTEGER REFERENCES app_users(id),
    created_at DATETIME,
    updated_at DATETIME,
    current_approver INTEGER REFERENCES app_users(id)
);

CREATE TABLE IF NOT EXISTS approval_flow_order (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    approval_flow_id INTEGER REFERENCES approval_flow(id),
    order_no INTEGER,
    status TEXT
);

CREATE TABLE IF NOT EXISTS approval_flow_member (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    approval_flow_order_id INTEGER REFERENCES approval_flow_order(id),
    approval_flow_id INTEGER REFERENCES approval_flow(id),
    is_required INTEGER NOT NULL DEFAULT 0,
    approver_user_id INTEGER REFERENCES app_users(id),
    status TEXT,
    actor_user_id INTEGER REFERENCES app_users(id),
    approved_at DATETIME,
    parent_module_name TEXT,
    parent_id TEXT
);

CREATE TABLE IF NOT EXISTS approval_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    approval_flow_id INTEGER REFERENCES approval_flow(id),
    attempt_no INTEGER,
    order_no INTEGER,
    actor_user_id INTEGER REFERENCES app_users(id),
    action TEXT,
    acted_at DATETIME,
    comment TEXT
);

CREATE TABLE IF NOT EXISTS expense_request (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    title TEXT,
    amount INTEGER,
    purpose TEXT,
    approval_flow_id INTEGER REFERENCES approval_flow(id),
    creator INTEGER REFERENCES app_users(id),
    updater INTEGER REFERENCES app_users(id),
    created_at DATETIME,
    updated_at DATETIME,
    expense_date DATE
);

CREATE INDEX IF NOT EXISTS idx_approval_flow_current ON approval_flow(current_approver);
CREATE INDEX IF NOT EXISTS idx_approval_flow_order_flow ON approval_flow_order(approval_flow_id);
CREATE INDEX IF NOT EXISTS idx_approval_flow_member_flow ON approval_flow_member(approval_flow_id);
CREATE INDEX IF NOT EXISTS idx_approval_history_flow ON approval_history(approval_flow_id);
