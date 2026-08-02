// AccountingHome.mod.cs — 会計部品のトップ（Accounting フレーム）
// 未処理サマリと当月 KPI を表示する。参照するモジュールは会計部品内のみ（部品独立性の維持）。
// 旧 Home にあった仕入先請求書の期限アラートは購買部品（支払予定表）へ、資金繰りアラートは
// 経営管理部品（資金繰り予測）へ役割を移した（部品をまたぐ型参照を持たないため）。
// リスクの高い読み取り（QueryField モジュールの検索）は最後に置く（失敗しても上部は表示済み）。

void Detail_OnAfterInit()
{
    var name = "";
    if (CurrentUser != null && CurrentUser.表示名.Value != null)
    {
        name = CurrentUser.表示名.Value;
    }
    GreetingLabel.Text = $"こんにちは、{name} さん";
    DateLabel.Text = $"{DateTime.Today:yyyy年M月d日}";

    // 未処理の件数（会計部品内）
    var bls = new ModuleSearcher<BankStatementLine>();
    bls.AddEquals(b => b.Status.Value, "pending");
    var pendingBank = bls.Execute().Count;

    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.Status.Value, "draft");
    var drafts = js.Execute().Count;

    OpsLabel.Text = $"未処理: 銀行明細（未起票） {pendingBank} 件 ／ 下書き伝票 {drafts} 件";

    // 当月 KPI（AccountingKpiData = QueryField モジュールを検索して1行読む）
    var ks = new ModuleSearcher<AccountingKpiData>();
    var kpiRows = ks.Execute();
    if (kpiRows.Count > 0)
    {
        var k = (AccountingKpiData)kpiRows[0];
        var cash = k.CashBalance.Value ?? 0;
        var ar = k.ArBalance.Value ?? 0;
        var ap = k.ApBalance.Value ?? 0;
        var sales = k.MonthSales.Value ?? 0;
        var expense = k.MonthExpense.Value ?? 0;
        var profit = k.MonthProfit.Value ?? 0;
        KpiLine1.Text = $"現預金 {cash:#,0} 円 ／ 売掛金 {ar:#,0} 円 ／ 買掛金 {ap:#,0} 円";
        KpiLine2.Text = $"当月売上高 {sales:#,0} 円 ／ 当月費用 {expense:#,0} 円 ／ 当月利益 {profit:#,0} 円";
        if (profit < 0)
        {
            KpiLine2.Color = "#dc3545";
        }
    }
}
