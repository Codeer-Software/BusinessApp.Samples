// Partner.mod.cs — 取引先
// C-3: インボイス登録番号の実在チェック（国税庁 公表システム Web-API・サーバ経由）
// サーバの ApplicationId 未設定時はモック応答（形式チェック＋疑似結果）で動作する。

void CheckInvoice_OnClick()
{
    var no = InvoiceRegNo.Value;
    if (string.IsNullOrEmpty(no))
    {
        Toaster.Warn("登録番号（T+13桁）を入力してから確認してください");
        return;
    }

    using var loading = LoadingService.StartLoading(0);
    var result = WebApiService.Get($"/api/invoice_check?number={no}");
    if (result.StatusCode != 200)
    {
        Toaster.Error("照合サービスに接続できませんでした");
        return;
    }

    var data = result.JsonObject;
    var status = data.status;
    if (status == "ok")
    {
        Toaster.Success($"{data.message}: {data.name}（{data.registrationDate} 登録）");
    }
    else if (status == "not_found")
    {
        Toaster.Warn($"{data.message}");
    }
    else
    {
        Toaster.Error($"{data.message}");
    }
}

// マスタの有効フラグは DB 既定が 1 だが、**CLB の Boolean は新規作成で常に未チェック**になる
// （CLB-017・実測）。既定が効いていると思って保存すると、作った直後から無効なマスタができ、
// 参照側のピッカーに出てこない。新規のときだけ明示的に立てる
void Detail_OnAfterInit()
{
    if (IsNewData && IsActive.Value != true) { IsActive.Value = true; }
}
