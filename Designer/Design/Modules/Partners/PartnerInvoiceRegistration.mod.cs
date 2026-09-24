// 登録番号の編集画面（docs/14 §4。画面の作り直しは 2026-09-02）。
//
// 取引先は表示のみ——新規は URL の ?partner= から受け取り、既存は保存済みの値を使う。
// 不変条件の検査はサーバの関門（PartnerRegistrationSubmitGate。ADR-0008）。ここは画面の出し入れだけ。

void Detail_OnAfterInitialization()
{
    // 案内の箇条書きは、有効な登録がある取引先の新規のときだけ出す（ShowOpenRegistrationNotice）
    NoticeGuideLabel.IsVisible = false;

    if (IsNewData)
    {
        var partnerId = QueryPartnerId();
        if (partnerId == null)
        {
            // URL 直叩き。ここで止める（qa/01 F-24 のとおり守りではない。守りは関門）
            PartnerNameLabel.Text = "取引先が指定されていません。取引先の詳細の「登録番号を追加する」から入り直してください。";
            RegisterButton.IsVisible = false;
            return;
        }

        Partner.Value = partnerId;
        ShowOpenRegistrationNotice();
    }

    ShowPartnerName();
}

// 再登録の規則（取消・失効の記録がない登録があるうちは次を始められない。docs/14 §5 R-I5）を
// **押す前に**知らせる（21 §1。全部入力させてから関門で捨てさせない）。関門の代わりではない。
// **置き場は最下段・ボタンの行の直前**（21 §2-8 を Claude がこの画面へ当てはめた——docs/05 の Q-52 の ②・⑤ で諮っている）。
// もとはラベルの列（幅 140px）にあり、8 行に折り返していた。
// **過去の登録を後から入力するとき（上の登録より前に始まる登録）の案内も出す**——「再登録は先に閉じよ」だけを言うと、
// 過去の登録を足したい人がいま使っている登録を閉じてしまう（2026-09-24 の自己レビュー）。案内の文言は定義の NoticeGuideLabel が持つ（21 §2-8）。
// **箇条書きは「上の文の登録」「入力している登録」と名詞で指す**——2 項目で「その登録」が別の登録を指し、入力している登録に終わりを入れよと読めた。
// 「上の登録」だけでは、すぐ上の入力欄（空の「取消・失効年月日」）とも読めた。**赤字の 1 文も「保存してある」と言う。**
// **赤字の 1 文は「有効な」と言わない**——アプリが知っているのは「取消・失効年月日」が空であることだけで、公表サイトとは照らしていない。
// **3 項目目で、入れる日付（公表サイトの取消年月日・失効年月日をそのまま）を言う**——箇条書きに従って前の登録を閉じる人には、
// 関門の断りが一度も出ない（あとの行がまだ無いので）。押す前に言うしかない（21 §1。いずれも 2026-09-24 の自己レビュー）。
void ShowOpenRegistrationNotice()
{
    var searcher = new ModuleSearcher<PartnerInvoiceRegistration>();
    searcher.AddEquals(r => r.Partner.Value, Partner.Value);
    var rows = searcher.Execute();
    if (rows == null) { return; }
    foreach (var row in rows)
    {
        var reg = (PartnerInvoiceRegistration)row;
        if (reg.EndedOn.Value != null) { continue; }
        NoticeLabel.Text = $"この取引先には、「取消・失効年月日」が空のまま保存してある登録（{reg.ValidFrom.Value:yyyy/MM/dd} から）があります。";
        NoticeGuideLabel.IsVisible = true;
        return;
    }
}

// クエリパラメータ ?partner={取引先Id} を読む（前回プロジェクトの JournalLineDepartment と同じ形）
string QueryPartnerId()
{
    var q = NavigationService.GetUniqueQueryParameters();
    if (q == null) { return null; }
    if (!q.ContainsKey("partner")) { return null; }
    var raw = q["partner"];
    if (string.IsNullOrEmpty(raw)) { return null; }
    var id = 0;
    if (!int.TryParse(raw, out id)) { return null; }
    if (id <= 0) { return null; }
    // 正規化して返す（"007" や前後の空白をそのまま Partner.Value に入れない）
    return $"{id}";
}

void ShowPartnerName()
{
    var searcher = new ModuleSearcher<Partner>();
    searcher.AddEquals(p => p.Id.Value, Partner.Value);
    var found = searcher.ExecuteFirstOrDefault();
    if (found == null)
    {
        // 実在しない ?partner=。URL 直叩きの分岐と同じ扱いにする（対称でないと片方だけ登録できてしまう。
        // これも守りではない——守りは関門の RejectMissingPartner）
        PartnerNameLabel.Text = "取引先が見つかりません。取引先の詳細の「登録番号を追加する」から入り直してください。";
        RegisterButton.IsVisible = false;
        return;
    }

    var partner = (Partner)found;
    PartnerNameLabel.Text = $"{partner.Code.Value}　{partner.Name.Value}";
}

// SubmitButton はスクリプトを通らない（qa/01 F-02）ので ButtonField ＋ Submit() にしてある。
// 成功したときだけ取引先の詳細へ戻る——失敗（関門の拒否は ExceptionMessage で返り CLB がトーストにする。
// qa/01 F-16）とキャンセルは画面に留まる。
void RegisterButton_OnClick()
{
    if (!ValidateInput()) { return; }   // ButtonField だと IsRequired が効かない（qa/01 F-15）

    var ok = Submit();                  // 戻り値で成否分岐（qa/01 F-08）
    if (ok != true) { return; }

    Toaster.Success("登録しました。");
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("Partner", $"{Partner.Value}"));
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
        if (name != "Id" && name != "Partner") changed = true;
    }

    return changed;
}
