// TimeEntryAdmin.mod.cs — 工数管理（経理の代理修正）
// 本人が入力できないケース（病気・事故・退職後の月次確定など）に経理が全員分の工数を
// 登録・修正・削除する画面。権限は経理のみ（UserRead/WriteCondition）・行制限なし。
// 一般社員向けの工数入力（TimeEntry）は本人の行のみ書き込み可（DataWriteCondition）——
// 修正窓口を中立な経理に一本化する内部統制方針（ADR 台帳 2026-07-08 合意）。

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        if (WorkDate.Value == null) { WorkDate.Value = DateOnly.FromDateTime(DateTime.Today); }
        if (Minutes.Value == null) { Minutes.Value = 480; }
    }
}
