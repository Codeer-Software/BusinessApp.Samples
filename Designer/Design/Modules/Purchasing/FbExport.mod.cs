// FbExport.mod.cs — 振込データ作成（全銀フォーマット・総合振込）D-6後続
// 未払計上済（status='accrued'）の仕入先請求書から、120桁固定長の全銀フォーマット
// （ヘッダ/データ/トレーラ/エンド）を生成してテキスト表示する。
// 実銀行への取込は検証不能のため、様式の自己検証（桁数・必須項目・使用可能文字・
// トレーラ整合）を結果に表示する（到達目標=様式検証まで。ADR-0011）。
// 半角のみで構成するため Shift-JIS バイト長 = 文字数（検証はこの前提で文字数を数える）。

void Detail_OnAfterInit()
{
    // 委託者（自社）の既定値。ddl/230 の company_profile seed と同値のデモ用ダミー。
    // 画面上で編集して生成できる（恒久的なマスタ画面は将来課題）
    if (ConsignorCode.Value == null || ConsignorCode.Value == "")
    {
        ConsignorCode.Value = "0000091001";
        ConsignorKana.Value = "ｽﾀｰﾗｲﾄｺﾝｻﾙﾃｲﾝｸﾞ(ｶ";
        HeadBankCode.Value = "0001";
        HeadBankKana.Value = "ﾐｽﾞﾎ";
        HeadBranchCode.Value = "001";
        HeadBranchKana.Value = "ﾎﾝﾃﾝ";
        HeadAccountType.Value = "1";
        HeadAccountNo.Value = "1234567";
        PayDate.Value = DateOnly.FromDateTime(DateTime.Today);
    }
    ResultLabel.Text = "";
}

void Generate_OnClick()
{
    // 全銀の書式検証（数字桁数・使用可能文字）の正典は PartnerBank.mod.cs（取引先口座マスタ）。
    // 自社側（委託者・仕向）の項目も同じ基準で検証する
    var fmt = new PartnerBank();
    if (PayDate.Value == null) { Toaster.Error("振込指定日を入力してください"); return; }
    if (!fmt.IsDigitsLen(ConsignorCode.Value, 10)) { Toaster.Error("委託者コードは数字10桁で入力してください"); return; }
    if (!fmt.IsDigitsLen(HeadBankCode.Value, 4)) { Toaster.Error("仕向銀行コードは数字4桁で入力してください"); return; }
    if (!fmt.IsDigitsLen(HeadBranchCode.Value, 3)) { Toaster.Error("仕向支店コードは数字3桁で入力してください"); return; }
    if (HeadAccountType.Value == null) { Toaster.Error("口座種別を選択してください"); return; }
    if (!fmt.IsDigitsLen(HeadAccountNo.Value, 7)) { Toaster.Error("口座番号は数字7桁で入力してください"); return; }
    var consignorKanaErr = fmt.KanaError(ConsignorKana.Value);
    if (consignorKanaErr != "") { Toaster.Error($"委託者名に使用できない文字があります: {consignorKanaErr}"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 対象: 未払計上済（未払い）の仕入先請求書を支払期限順に
    var vs = new ModuleSearcher<VendorInvoice>();
    vs.AddEquals(v => v.Status.Value, "accrued");
    vs.OrderBy(v => v.DueDate.Value);
    var invoices = vs.Execute();
    if (invoices.Count == 0)
    {
        ResultLabel.Text = "対象がありません（未払計上済・未払いの仕入先請求書が 0 件）";
        FbText.Value = "";
        Toaster.Warn("対象がありません");
        return;
    }

    // 仕入先の口座情報（経理専用の PartnerBank モジュール経由。機微情報の項目分離）
    var ps = new ModuleSearcher<PartnerBank>();
    var partners = ps.Execute();

    var lines = new List<string>();
    var errors = new List<string>();
    var count = 0;
    var total = 0;

    // ヘッダレコード (120) = 1+2+1+10+40+4+4+15+3+15+1+7+17
    var header = "1" + "21" + "0"
        + PadN(ConsignorCode.Value, 10)
        + PadC(ConsignorKana.Value, 40)
        + $"{PayDate.Value:MMdd}"
        + PadN(HeadBankCode.Value, 4)
        + PadC(HeadBankKana.Value, 15)
        + PadN(HeadBranchCode.Value, 3)
        + PadC(HeadBranchKana.Value, 15)
        + HeadAccountType.Value
        + PadN(HeadAccountNo.Value, 7)
        + PadC("", 17);
    lines.Add(header);

    foreach (var im in invoices)
    {
        var inv = (VendorInvoice)im;
        PartnerBank partner = null;
        foreach (var pm in partners)
        {
            var p = (PartnerBank)pm;
            if ($"{p.Id.Value}" == $"{inv.Partner.Value}") { partner = p; break; }
        }
        var invNo = inv.InvoiceNo.Value ?? "?";
        if (partner == null)
        {
            errors.Add($"{invNo}: 仕入先が見つかりません");
            continue;
        }
        // 口座5項目の検証は PartnerBank（取引先口座マスタ）と共通のロジックで行う
        var accErr = partner.AccountValidationError();
        if (accErr != "")
        {
            errors.Add($"{invNo}: {partner.Name.Value} — {accErr}（業務マスタ > 取引先口座 で登録）");
            continue;
        }
        var amt = inv.Amount.Value ?? 0;
        if (amt <= 0)
        {
            errors.Add($"{invNo}: 金額が 0 円以下");
            continue;
        }

        // データレコード (120) = 1+4+15+3+15+4+1+7+30+10+1+10+10+1+8
        // 銀行名・支店名はコード優先の空白運用（多くの IB はコードで補完する。報告書に明記）
        var rec = "2"
            + PadN(partner.BankCode.Value, 4)
            + PadC("", 15)
            + PadN(partner.BranchCode.Value, 3)
            + PadC("", 15)
            + PadC("", 4)
            + partner.AccountTypeSel.Value
            + PadN(partner.AccountNo.Value, 7)
            + PadC(partner.PayeeKana.Value, 30)
            + PadN($"{amt}", 10)
            + "0"
            + PadC("", 10)
            + PadC("", 10)
            + "7"
            + PadC("", 8);
        lines.Add(rec);
        count = count + 1;
        total = total + amt;
    }

    // トレーラ (120) = 1+6+12+101 / エンド (120) = 1+119
    lines.Add("8" + PadN($"{count}", 6) + PadN($"{total}", 12) + PadC("", 101));
    lines.Add("9" + PadC("", 119));

    // 様式自己検証: 全レコードが 120 桁（半角のみ構成なので文字数=SJISバイト数）
    var badLen = 0;
    var lineNo = 0;
    var badLenDetail = "";
    foreach (var l in lines)
    {
        lineNo = lineNo + 1;
        if (l.Length != 120)
        {
            badLen = badLen + 1;
            badLenDetail = badLenDetail + $" 行{lineNo}={l.Length}桁";
        }
    }

    var text = "";
    foreach (var l in lines) { text = text + l + "\n"; }
    FbText.Value = text;

    var check = (badLen == 0)
        ? $"様式検証 OK（全 {lines.Count} レコードが 120 桁・トレーラ整合: 件数 {count}・合計 {total:#,0} 円）"
        : $"⚠ 様式検証 NG: 120桁でないレコードがあります →{badLenDetail}";
    var errText = "";
    if (errors.Count > 0)
    {
        errText = $" ／ 除外 {errors.Count} 件: ";
        var shown = 0;
        foreach (var e in errors)
        {
            if (shown >= 3) { errText = errText + " ほか"; break; }
            if (shown > 0) { errText = errText + " ｜ "; }
            errText = errText + e;
            shown = shown + 1;
        }
    }
    ResultLabel.Text = $"データレコード {count} 件・合計 {total:#,0} 円（振込指定日 {PayDate.Value:MM/dd}） ／ {check}{errText}";
    if (count > 0) Toaster.Success($"FBデータを生成しました（{count} 件・{total:#,0} 円）");
    else Toaster.Warn("生成できる明細がありませんでした（除外理由を確認してください）");
}

// ============ ヘルパ ============

// C項目: 左詰・後スペース（超過は切詰め）
string PadC(string s, int n)
{
    var t = s ?? "";
    if (t.Length > n) { t = t.Substring(0, n); }
    while (t.Length < n) { t = t + " "; }
    return t;
}

// N項目: 右詰・前ゼロ（超過は下位桁優先）
string PadN(string s, int n)
{
    var t = s ?? "";
    if (t.Length > n) { t = t.Substring(t.Length - n, n); }
    while (t.Length < n) { t = "0" + t; }
    return t;
}

// 数字桁数・使用可能文字の検証は PartnerBank.mod.cs（IsDigitsLen / KanaError）に共通化した
