// PartnerBank.mod.cs — 取引先口座（振込先口座の登録・削除）
// 口座5項目（銀行コード/支店コード/口座種別/口座番号/受取人名カナ）は
// 「全て有効」か「全て空（口座未登録）」の2状態だけを許す。部分入力の口座は
// 振込データ作成（FbExport）で必ずエラーになるため、登録時点で弾く。
//
// 検証ロジックの正典は本ファイル。AccountValidationError() / IsDigitsLen() / KanaError() は
// FbExport.mod.cs からも呼ばれる（検証基準が2箇所に分かれてズレるのを防ぐ）。

void Register_OnClick()
{
    var err = AccountValidationError();
    if (err != "") { Toaster.Error(err); return; }
    var ok = this.Submit();
    if (ok == false) { Toaster.Error("登録に失敗しました"); return; }
    Toaster.Success("振込先口座を登録しました");
}

void Delete_OnClick()
{
    if (!HasAnyAccountInput()) { Toaster.Warn("口座情報は登録されていません"); return; }

    var answer = MessageBox.Show(
        $"取引先「{Name.Value}」の振込先口座情報を削除します。よろしいですか？（取引先自体は削除されません）",
        "口座削除", "キャンセル");
    if (answer != "口座削除") return;

    using var suspend = this.SuspendNotifyStateChanged();
    BankCode.Value = null;
    BranchCode.Value = null;
    AccountTypeSel.Value = null;
    AccountNo.Value = null;
    PayeeKana.Value = null;
    var ok = this.Submit();
    if (ok == false) { Toaster.Error("削除に失敗しました"); return; }
    Toaster.Success("振込先口座情報を削除しました");
}

// ============ 検証（全銀フォーマット基準の正典。FbExport からも呼ばれる） ============

// 口座5項目が「全て有効」なら ""、問題があればエラーメッセージを返す
string AccountValidationError()
{
    if (!HasAnyAccountInput()) return "口座情報が入力されていません";
    if (!IsDigitsLen(BankCode.Value, 4)) return "銀行コードは数字4桁で入力してください";
    if (!IsDigitsLen(BranchCode.Value, 3)) return "支店コードは数字3桁で入力してください";
    if (AccountTypeSel.Value == null) return "口座種別を選択してください";
    if (!IsDigitsLen(AccountNo.Value, 7)) return "口座番号は数字7桁で入力してください";
    if (PayeeKana.Value == null || PayeeKana.Value == "") return "受取人名（半角カナ）を入力してください";
    var kanaErr = KanaError(PayeeKana.Value);
    if (kanaErr != "") return $"受取人名に使用できない文字があります: {kanaErr}（半角ｶﾅ大文字・英大文字・数字で入力してください）";
    return "";
}

// 口座5項目のいずれかに入力があるか（false = 口座未登録の状態）
bool HasAnyAccountInput()
{
    if (BankCode.Value != null && BankCode.Value != "") return true;
    if (BranchCode.Value != null && BranchCode.Value != "") return true;
    if (AccountTypeSel.Value != null) return true;
    if (AccountNo.Value != null && AccountNo.Value != "") return true;
    if (PayeeKana.Value != null && PayeeKana.Value != "") return true;
    return false;
}

// 数字ちょうど n 桁か
bool IsDigitsLen(string s, int n)
{
    if (s == null || s.Length != n) return false;
    var digits = "0123456789";
    for (int i = 0; i < s.Length; i++)
    {
        if (!digits.Contains(s.Substring(i, 1))) return false;
    }
    return true;
}

// 全銀で使用可能な半角文字（カナ大文字・英大文字・数字・記号）以外が含まれれば
// 最初の不正文字を返す。空文字なら問題なし
string KanaError(string s)
{
    if (s == null || s == "") return "";
    var ok = "ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜｦﾝﾞﾟｰABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ()｢｣/-.\\";
    for (int i = 0; i < s.Length; i++)
    {
        var c = s.Substring(i, 1);
        if (!ok.Contains(c)) return c;
    }
    return "";
}
