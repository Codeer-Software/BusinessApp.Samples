-- 420_portal_thresholds.sql — 支払期限「まもなく」日数のマスタ化（ADR-0045、BusinessAppSQLite）
-- 支払予定表（PaymentSchedule）とポータルアラート（PortalAlertData）が共用する。
-- ハードコード禁止方針（CLAUDE.md §3）: 旧 SQL の「7 日」直書きを system_thresholds へ移す。
-- 再実行可（NOT EXISTS で冪等）。

INSERT INTO system_thresholds (code, name, amount, valid_from, valid_to)
SELECT 'PAY_DUE_SOON_DAYS', '支払期限の警告日数(日)', 7, NULL, NULL
WHERE NOT EXISTS (SELECT 1 FROM system_thresholds WHERE code = 'PAY_DUE_SOON_DAYS');
