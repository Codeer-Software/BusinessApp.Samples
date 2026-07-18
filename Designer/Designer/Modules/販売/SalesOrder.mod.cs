// SalesOrder.mod.cs — 受注
// 責務: 受注番号の自動採番 (SO-{yy}-{seq}) / 明細の行番号・金額(数量×単価)・合計の再計算 /
//        状態遷移はボタン一元化 (ADR-0026): open →(完了にする)→ closed →(進行中に戻す)→ open。
//        削除は検収が存在しない場合のみ・詳細画面から
// 検収 (Acceptance) の生成・売上仕訳は B4-2 で実装する

bool inLinesHandler = false;

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        Status.Value = "open";
        OrderDate.Value = DateOnly.FromDateTime(DateTime.Today);
        OrderNo.Value = NextOrderNo();
    }
    RecalcTotal();
    UpdateButtons();
}

// 状態に応じたボタン出し分け (ADR-0026/0027)
void UpdateButtons()
{
    var st = Status.Value;
    if (this.IsNewData)
    {
        CloseOrderButton.IsVisible = false;
        ReopenOrderButton.IsVisible = false;
        DeleteOrderButton.IsVisible = false;
        return;
    }
    CloseOrderButton.IsVisible = (st == "open");
    ReopenOrderButton.IsVisible = (st == "closed");
    DeleteOrderButton.IsVisible = true;
}

// 完了にする: open → closed
void CloseOrder_OnClick()
{
    if (this.IsNewData) { Toaster.Error("先に受注を保存してください"); return; }
    if (Status.Value != "open") { Toaster.Error("進行中の受注のみ完了にできます"); return; }
    Status.Value = "closed";
    var ret = this.Submit();
    if (ret == false) { Toaster.Error("状態の更新に失敗しました"); return; }
    UpdateButtons();
    Toaster.Success("受注を完了にしました");
}

// 進行中に戻す: closed → open（誤操作の巻き戻し）
void ReopenOrder_OnClick()
{
    if (Status.Value != "closed") { Toaster.Error("完了した受注のみ進行中に戻せます"); return; }
    Status.Value = "open";
    var ret = this.Submit();
    if (ret == false) { Toaster.Error("状態の更新に失敗しました"); return; }
    UpdateButtons();
    Toaster.Success("受注を進行中に戻しました");
}

// 受注の削除: 検収が存在しない場合のみ（確認ダイアログ付き）。
// 削除後、元見積は「受注」状態のまま残るため、見積側の「下書きに戻す」で復活できる
void DeleteOrder_OnClick()
{
    var check = new ModuleSearcher<Acceptance>();
    check.AddEquals(e => e.SalesOrderRef.Value, this.Id.Value);
    if (check.Execute().Count > 0)
    {
        Toaster.Error("この受注には検収が存在するため削除できません（先に検収側を削除してください）");
        return;
    }
    var result = MessageBox.Show($"受注「{OrderNo.Value} {Title.Value}」を削除しますか？（元に戻せません。元見積がある場合は見積側の「下書きに戻す」で再利用できます）", "削除する", "キャンセル");
    if (result != "削除する") return;
    using var loading = LoadingService.StartLoading(0);
    this.Delete();
    Toaster.Success("受注を削除しました");
    NavigationService.NavigateTo(NavigationService.GetModuleUrl("SalesOrder"));
}

void Lines_OnDataChanged()
{
    if (inLinesHandler) return;
    inLinesHandler = true;
    var no = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (SalesOrderLine)row;
        no = no + 1;
        l.LineNo.Value = no;
        if (l.Qty.Value == null) l.Qty.Value = 1;
        if (l.UnitPrice.Value != null)
        {
            l.Amount.Value = l.Qty.Value * l.UnitPrice.Value;
        }
    }
    RecalcTotal();
    inLinesHandler = false;
}

void RecalcTotal()
{
    var total = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (SalesOrderLine)row;
        if (l.Amount.Value != null) total = total + l.Amount.Value;
    }
    TotalAmount.Value = total;
}

// 受注番号採番: SO-{西暦下2桁}-{連番3桁}。番号の文字列降順の最大から +1 (年が変われば 1 に戻る)
string NextOrderNo()
{
    var prefix = $"SO-{DateTime.Today:yy}-";
    var s = new ModuleSearcher<SalesOrder>();
    s.OrderByDescending(e => e.OrderNo.Value);
    s.Limit(1);
    var last = s.ExecuteFirstOrDefault();
    var seq = 1;
    if (last != null)
    {
        var lastNo = ((SalesOrder)last).OrderNo.Value;
        if (lastNo != null && lastNo.StartsWith(prefix))
        {
            seq = int.Parse(lastNo.Substring(prefix.Length)) + 1;
        }
    }
    return $"{prefix}{seq:000}";
}
