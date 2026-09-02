// マスタの新規作成時の初期値。
//
// DB の既定は is_active = 1 だが、画面のチェックボックスは既定でオフになる。
// 揃えておかないと、新規作成したマスタが最初から無効になり、入力候補に出ない。
// ADR-0008 のとおり、スクリプトに書いてよいのは画面上の値の出し入れだけである。
void Detail_OnAfterInitialization()
{
    // 保存前の取引先には登録番号を付けられない（識別子がまだ無い）。
    // IsVisible は守りではない（qa/01 F-24）——守りは関門と DB の外部キー。
    // 一覧も隠す——親の Id が空だと、クエリの「未指定なら効かない」規約に落ちて
    // **全取引先の登録が新規画面に並ぶ**（2026-09-02 のレビュー指摘）。
    AddRegistrationButton.IsVisible = !IsNewData;
    RegistrationsLabel.IsVisible = !IsNewData;
    Registrations.IsVisible = !IsNewData;

    if (!IsNewData) return;

    IsActive.Value = true;
}

// 登録番号の編集は別画面で行う（docs/07 §3-4。2026-09-02 の作り直し）。
// 新規作成の画面へ、この取引先の識別子をクエリパラメータで渡す。
void AddRegistrationButton_OnClick()
{
    var url = NavigationService.GetModuleDataUrl("PartnerInvoiceRegistration", "-");
    NavigationService.NavigateTo($"{url}?partner={Id.Value}");
}
