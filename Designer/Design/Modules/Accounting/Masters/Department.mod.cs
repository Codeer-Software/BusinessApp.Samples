// マスタの新規作成時の初期値。
//
// DB の既定は is_active = 1 だが、画面のチェックボックスは既定でオフになる。
// 揃えておかないと、新規作成したマスタが最初から無効になり、入力候補に出ない。
// ADR-0008 のとおり、スクリプトに書いてよいのは画面上の値の出し入れだけである。
void Detail_OnAfterInitialization()
{
    if (!IsNewData) return;

    IsActive.Value = true;
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
