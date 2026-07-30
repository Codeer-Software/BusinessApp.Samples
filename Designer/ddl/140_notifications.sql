-- 140_notifications.sql — アプリ内通知（B-9、BusinessAppSQLite）
-- Slack/メールは「口だけ実装」（作業合意）: ApprovalFlow.NotifyUser の Logger 出力が将来の連携ポイント
CREATE TABLE IF NOT EXISTS notifications (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    recipient_user INTEGER REFERENCES app_users(id),
    title TEXT,
    body TEXT,
    link_module TEXT,     -- 遷移先モジュール名（例 'ExpenseRequest'）
    link_id TEXT,         -- 遷移先レコード id
    is_read INTEGER,      -- 0/1
    created_at DATETIME
);
CREATE INDEX IF NOT EXISTS idx_notifications_recipient ON notifications(recipient_user, is_read);
