// SesRunPlan.mod.cs — SES 生成プラン一覧の行スタイル（ADR-0036・RecurringRunPlan と同パターン）
// 「生成予定」(planned) の行にマーカークラスを付け、app.css の tr:has(.row-planned) が
// 行全体を黄色にする

void ListRow_OnAfterInit()
{
    if (Status.Value == "planned")
    {
        Status.ClassName = "row-planned";
    }
}
