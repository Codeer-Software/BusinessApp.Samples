// SalesOrder.mod.cs — 受注
// 責務: 受注番号の自動採番 (SO-{yy}-{seq}) / 明細の行番号・金額(数量×単価)・合計の再計算 /
//        状態遷移はボタン一元化 (ADR-0026): open →(完了にする)→ closed →(進行中に戻す)→ open。
//        削除は検収が存在しない場合のみ・詳細画面から
// 検収 (Acceptance) の生成・売上仕訳は B4-2 で実装する

bool inLinesHandler = false;

void Detail_OnAfterInit()
{
    // 採番は機械が決める。**人に触らせない**（BUG-0426）——
    // 番号を手で直されると採番の Substring/Parse が壊れ、新規作成が全社で止まる
    OrderNo.IsViewOnly = true;

    if (this.IsNewData)
    {
        Status.Value = "open";
        OrderDate.Value = DateOnly.FromDateTime(DateTime.Today);
        OrderNo.Value = NextOrderNo();
        // 部門の初期値: 作成者の所属部（主所属が課でも伝票部門は部・ADR-0044。見積からの変換時は変換側が上書き）
        if (DepartmentRef.Value == null) { DepartmentRef.Value = CurrentUser.所属部.Value; }
    }
    SeedAmountTrace();   // 保存済み明細を「自動で入れた値」とみなせるようにする（BUG-0423）
    RecalcTotal();
    UpdateButtons();
}

// 状態に応じたボタン出し分け (ADR-0026/0027)
// P1（docs/issues/ISSUE-0002）: 検収が1件でも存在する受注は編集不可（下書き検収の自動セット額の根拠を守る）。
// 内容の追加・変更は「変更契約」として新しい見積→受注で行う
void UpdateButtons()
{
    // 部門は経理のみ変更可（2026-07-25 ユーザー要望）。一般・承認者は自部門（初期値）固定
    var isAccounting = CurrentUser.HasAccountingAccess.Value == true;
    if (!isAccounting) { DepartmentRef.IsViewOnly = true; }

    var st = Status.Value;
    if (this.IsNewData)
    {
        CloseOrderButton.IsVisible = false;
        ReopenOrderButton.IsVisible = false;
        DeleteOrderButton.IsVisible = false;
        LockedNoteLabel.IsVisible = false;
        return;
    }
    CloseOrderButton.IsVisible = (st == "open");
    ReopenOrderButton.IsVisible = (st == "closed");

    var check = new ModuleSearcher<Acceptance>();
    check.AddEquals(e => e.SalesOrderRef.Value, this.Id.Value);
    var hasAcceptance = (check.Execute().Count > 0);

    this.IsViewOnly = hasAcceptance;
    SubmitButton.IsVisible = !hasAcceptance;
    LockedNoteLabel.IsVisible = hasAcceptance;
    // 検収がある受注の削除はどうせガードされるのでボタン自体を出さない（ADR-0027）
    DeleteOrderButton.IsVisible = !hasAcceptance;
    if (hasAcceptance)
    {
        // CLB 1.3: モジュール閲覧専用時はボタンの OnClick が発火しないため、状態遷移ボタンだけ解除
        CloseOrderButton.IsViewOnly = false;
        ReopenOrderButton.IsViewOnly = false;
    }
}

// 完了にする: open → closed
void CloseOrder_OnClick()
{
    if (this.IsNewData) { Toaster.Error("先に受注を保存してください"); return; }
    if (Status.Value != "open") { Toaster.Error("進行中の受注のみ完了にできます"); return; }
    this.IsViewOnly = false;  // 検収済みロック中でも状態遷移は許可（UpdateButtons が再ロックする）
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
    this.IsViewOnly = false;  // 検収済みロック中でも状態遷移は許可（UpdateButtons が再ロックする）
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
        // 税区分は必須（ADR-0050）。新しい行には既定として課税売上 10% を入れる
        if (l.TaxCategoryRef.Value == null) l.TaxCategoryRef.Value = DefaultSalesTaxCategoryId();
        if (l.UnitPrice.Value != null)
        {
            // **手入力した金額を上書きしない**（BUG-0423）。
            // 「一式 900,000 円（単価 1,000,000 から値引き）」のように金額を直接打つのは受託見積の常套手段で、
            // 明細の金額欄は入力可のまま置いてある。無条件に数量×単価を書き戻すと、
            // フォーカスを外した瞬間に戻るどころか、**他行を編集しただけで先に入れた値引きが消える**。
            // 自動で入れた値を痕跡に控え、**金額が痕跡と一致している間だけ**追随させる
            // （請求書 Invoice が BUG-0182 で確立した型をそのまま使う）
            int auto = l.Qty.Value * l.UnitPrice.Value;
            var trace = l.AmountAutoValue.Value ?? "";
            var isUntouched = (l.Amount.Value == null) || (trace != "" && trace == $"{l.Amount.Value}");
            if (isUntouched)
            {
                l.Amount.Value = auto;
                l.AmountAutoValue.Value = $"{auto}";
            }
        }
    }
    RecalcTotal();
    inLinesHandler = false;
}

// 既存明細を開いたとき、いまの金額が数量×単価と一致していれば「自動で入れた値」とみなして痕跡を置く。
// これが無いと、保存済みの明細はすべて「手入力扱い」になり、単価を直しても金額が追随しない（BUG-0423）
void SeedAmountTrace()
{
    foreach (var row in Lines.Rows)
    {
        var l = (SalesOrderLine)row;
        if (l.Amount.Value == null) continue;
        if (l.Qty.Value == null || l.UnitPrice.Value == null) continue;
        int auto = l.Qty.Value * l.UnitPrice.Value;
        if (auto == l.Amount.Value) { l.AmountAutoValue.Value = $"{auto}"; }
    }
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
// 売上伝票の既定税区分を「マスタから」解決する（ADR-0050）。
// 「ふつうは 10%」はこの時点の制度でしかないので、コードに税区分を直書きしない。
// 税制マスタ > 税区分 の「既定として使う」で切り替えられる（tax_categories.default_for='sales'）。
long? DefaultSalesTaxCategoryId()
{
    var cs = new ModuleSearcher<TaxCategory>();
    cs.AddEquals(c => c.DefaultFor.Value, "sales");
    cs.AddEquals(c => c.IsActive.Value, true);
    var found = cs.ExecuteFirstOrDefault();
    if (found == null) return null;
    return ((TaxCategory)found).Id.Value;
}

// 受注番号採番【正典】: SO-{西暦下2桁}-{連番3桁}（BUG-0133）。
// 他モジュールからは `new SalesOrder().NextOrderNo()` で呼べる（Project.md 2026-07-26）。
// 一意の範囲は**全期間**（番号に西暦下 2 桁を含む。ddl/610 の部分ユニークインデックス）。
// **欠番は許す**（2026-08-17 ユーザー決定）。
// 未一本化: Quote.NextOrderNoForConvert が同じロジックを持つ（別作業と衝突するため今回は触っていない）
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
            // **落ちない採番**（BUG-0426）。番号を手で `Q-26-001改` のように直されると

            // 文字列降順の最大がその行になり（`'改'` は `'9'` より大きい）、

            // 以後**新規作成を開くたびに FormatException で落ちる**。番号を直すまで全社で新規が作れない。

            // 数字として読めない番号は「無かったこと」にして採番を続ける

            var tail = 0;

            if (int.TryParse(lastNo.Substring(prefix.Length), out tail)) { seq = tail + 1; }
        }
    }
    return $"{prefix}{seq:000}";
}
