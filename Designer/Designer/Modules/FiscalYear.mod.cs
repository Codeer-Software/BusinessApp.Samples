// FiscalYear.mod.cs — 会計年度
// 新規年度の既定値設定と、期首日から12ヶ月の月次期間 (FiscalPeriod) を自動生成する。

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        Status.Value = "open";
    }
    UpdateOpeningTotal();
}

void OpeningBalances_OnDataChanged()
{
    UpdateOpeningTotal();
}

// 期首残高の合計（符号付き借方正）。0 なら貸借一致
void UpdateOpeningTotal()
{
    var total = 0;
    foreach (var row in OpeningBalances.Rows)
    {
        var b = (OpeningBalance)row;
        if (b.Balance.Value == null) continue;
        total += b.Balance.Value;
    }
    OpeningTotal.Value = total;
    if (total == 0)
    {
        OpeningTotal.Color = "";
    }
    else
    {
        OpeningTotal.Color = "#dc3545";
    }
}

// 翌期繰越: BS 科目の期末残高（期首+当期仕訳）＋繰越利益剰余金への当期純利益加算を
// 翌期の opening_balances に SQL 一発で洗い替え生成する（decisions/0006）
void CarryOver_OnClick()
{
    if (this.IsNewData)
    {
        Toaster.Error("年度を保存してから実行してください");
        return;
    }
    var s = new ModuleSearcher<FiscalYear>();
    s.AddGreaterThan(e => e.StartDate.Value, EndDate.Value);
    s.OrderBy(e => e.StartDate);
    s.Limit(1);
    var next = s.ExecuteFirstOrDefault();
    if (next == null)
    {
        Toaster.Error("翌期の会計年度がありません。先に翌期を作成してください");
        return;
    }
    var typedNext = (FiscalYear)next;
    var answer = MessageBox.Show($"{typedNext.Name.Value} の期首残高を作成します（既存の期首残高は洗い替えされます）。よろしいですか？", "実行", "キャンセル");
    if (answer != "実行") return;

    // ExecuteSqlField はスクリプトから直接実行できない（全メンバー ScriptHide）ため、
    // Update タイミングの CarryOverSql を「NextYearId をセットして Submit」で発火させる。
    // SQL 側は NextYearId が NULL のとき no-op ガード付き（通常の保存では何もしない）。
    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);
    NextYearId.Value = typedNext.Id.Value;
    var ret = this.Submit();
    NextYearId.Value = null;
    if (ret == false)
    {
        Toaster.Error("繰越に失敗しました");
        return;
    }
    this.Submit();
    Toaster.Success($"{typedNext.Name.Value} への繰越が完了しました");
}

void GeneratePeriods_OnClick()
{
    if (StartDate.Value == null)
    {
        StartDate.SetError("期首日を入力してください");
        return;
    }
    if (Periods.Rows.Count > 0)
    {
        MessageBox.Show("月次期間は既に存在します。生成し直す場合は既存の行を削除してから実行してください。");
        return;
    }

    using var suspend = this.SuspendNotifyStateChanged();

    var start = StartDate.Value;

    // 期末日が未入力なら 期首 + 12ヶ月 - 1日 を自動設定
    if (EndDate.Value == null)
    {
        EndDate.Value = start.AddMonths(12).AddDays(-1);
    }

    Periods.AddRows(12);
    int i = 0;
    foreach (var row in Periods.Rows)
    {
        var p = (FiscalPeriod)row;
        var s = start.AddMonths(i);
        p.PeriodNo.Value = i + 1;
        p.StartDate.Value = s;
        p.EndDate.Value = s.AddMonths(1).AddDays(-1);
        p.Status.Value = "open";
        i = i + 1;
    }
}
