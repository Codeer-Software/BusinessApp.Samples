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

// 登録番号の編集は別画面で行う（docs/13 §3-4。2026-09-02 の作り直し）。
// 新規作成の画面へ、この取引先の識別子をクエリパラメータで渡す。
void AddRegistrationButton_OnClick()
{
    var url = NavigationService.GetModuleDataUrl("PartnerInvoiceRegistration", "-");
    NavigationService.NavigateTo($"{url}?partner={Id.Value}");
}

// 保存していない変更があるまま画面を離れるときに確かめる（docs/21 §1。制限は押す前に見せる）。
// **会計の判断は無い。** 見ているのは CLB の IsModified（画面の値が読み込み時から動いたか）だけ。
// false を返すと遷移が止まる（Designer/ClaudeCodeForDesigner の Layouts の OnLocationChanging）。
bool Detail_OnLocationChanging()
{
    if (!HasUserChanges()) return true;

    // ボタンは「戻る」と書かない——ブラウザの「戻る」で出たときに、どちらへ戻るのか読めない（自己レビュー R77）。
    return MessageBox.ShowWithTitle(
        "画面を離れる確認",
        "保存していない変更があります。このまま離れると、その変更は失われます。よろしいですか？",
        "離れる", "入力を続ける") == "離れる";
}

// 利用者が触った欄があるか。
// **新規作成の画面では、何も触らなくても IsModified が真になる**——CLB が Id を「変更あり」に数え、スクリプトが入れた初期値も数える
// （qa/01 F-43。2026-09-10 に部門で実測——GetModifiedFieldNames は「Id,IsActive」を返した）。
// Id と初期値の欄しか動いていなければ「変更なし」と見る。**初期値の欄の一覧は Detail_OnAfterInitialization と揃える**（lint_design が突き合わせる）。
bool HasUserChanges()
{
    if (!IsModified) return false;
    if (!IsNewData) return true;

    // **foreach の中で値を返さない**（この関数で書いたら、確認が出ずに遷移だけが止まった。qa/01 B-11）。
    var changed = false;
    foreach (var name in this.GetModifiedFieldNames())
    {
        if (name != "Id" && name != "IsActive") changed = true;
    }

    return changed;
}
