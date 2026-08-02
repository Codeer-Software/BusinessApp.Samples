// ExpenseHome.mod.cs — 経費精算部品のトップ（ExpenseStaff / ExpenseApprover / ExpenseAccounting 共通）
// 個人のやること（承認待ち・進行中の申請・未読通知）を提供する。
// ほかの業務への切替はサイドバー先頭の「ホーム」→ 業務ポータル（PortalHome）に一元化（ADR-0042）。
// 参照するモジュールは経費精算部品内のみ（部品独立性の維持）。

void Detail_OnAfterInit()
{
    var name = "";
    if (CurrentUser != null && CurrentUser.表示名.Value != null)
    {
        name = CurrentUser.表示名.Value;
    }
    GreetingLabel.Text = $"こんにちは、{name} さん";
    DateLabel.Text = $"{DateTime.Today:yyyy年M月d日}";

    // 承認待ち件数は approval_inbox_view ベースの ApprovalInbox で数える（ADR-0016）。
    // ApprovalInbox は UserReadCondition（承認者∨経理）付きのため、権限のないユーザーでは検索しない
    // （権限外モジュールへの検索でホーム初期化が途中停止するのを防ぐ）
    var canApprove = CurrentUser.IsApprover.Value == true || CurrentUser.HasAccountingAccess.Value == true;
    var myApprovals = 0;
    if (canApprove)
    {
        var afs = new ModuleSearcher<ApprovalInbox>();
        afs.AddEquals(f => f.CurrentApprover.Value, CurrentUser.Id.Value);
        myApprovals = afs.Execute().Count;
    }
    var ers = new ModuleSearcher<ExpenseRequest>();
    ers.AddEquals(e => e.Creator.Value, CurrentUser.Id.Value);
    ers.AddEquals(e => e.SettlementStatus.Value, "applying");
    var myApplying = ers.Execute().Count;
    TodoLabel.Text = $"あなたの承認待ち: {myApprovals} 件 ／ 進行中のあなたの申請: {myApplying} 件";
}
