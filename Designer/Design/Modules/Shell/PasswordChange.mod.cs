// PasswordChange.mod.cs — 利用者自身のパスワード変更（ADR-0059）
//
// このモジュールは DB に一切紐づかない（DbTable: ""）。パスワードはサーバの
// /api/password/change だけが書き込み、更新対象は Cookie 認証の NameIdentifier＝本人に固定される。
//
// なぜ AppUser を直接編集させないか:
//   AppUser は CurrentUser のソースなので UserWriteCondition を付けられない（付けると
//   マイプロフィール・LinkField 表示が壊れる＝CLB の仕様）。つまり「自分の行だけ更新できる」
//   という制約を CLB 側で表現できず、一般利用者に AppUser の書き込み経路を触らせたくない。
//   さらに「現在のパスワードの照合」はハッシュ照合なのでクライアント（WASM）では原理的に不可能。
//
// 規則の文言はサーバの GuidanceText() から取る（検証と同じ 1 か所で作られる）。

void Detail_OnAfterInit()
{
    // 表示専用モジュールの Detail はビュー専用扱いになりボタンがクリック不能になる（FB-035）
    IsViewOnly = false;

    ResultLabel.Text = "";
    GuidanceLabel.Text = "";
    NoteLabel.Text = "";
    NoteLabel.IsVisible = false;

    var result = WebApiService.Get("/api/password/policy");
    if (result.StatusCode != 200)
    {
        GuidanceLabel.Text = "パスワードの条件を取得できませんでした（変更操作は行えます）";
        return;
    }
    var data = result.JsonObject;
    GuidanceLabel.Text = $"パスワードの条件: {data.guidance}";
    var note = $"{data.note}";
    if (note != "")
    {
        NoteLabel.Text = note;
        NoteLabel.IsVisible = true;
    }
}

void Change_OnClick()
{
    ResultLabel.Color = "#dc3545";

    var current = CurrentPassword.Value;
    var next = NewPassword.Value;
    var confirm = ConfirmPassword.Value;

    // 画面だけで判定できることは通信前に返す（往復を減らす。判定の本体はサーバ側）
    if (string.IsNullOrEmpty(current))
    {
        ResultLabel.Text = "現在のパスワードを入力してください";
        return;
    }
    if (string.IsNullOrEmpty(next))
    {
        ResultLabel.Text = "新しいパスワードを入力してください";
        return;
    }
    if (next != confirm)
    {
        ResultLabel.Text = "新しいパスワードと確認用の入力が一致しません";
        return;
    }

    using var loading = LoadingService.StartLoading(0);
    var body = new JsonObject();
    body.CurrentPassword = current;
    body.NewPassword = next;
    var result = WebApiService.Post("/api/password/change", body);
    if (result.StatusCode != 200)
    {
        ResultLabel.Text = "パスワード変更サービスに接続できませんでした";
        return;
    }

    var data = result.JsonObject;
    var message = $"{data.message}";
    if ($"{data.ok}" != "True" && $"{data.ok}" != "true")
    {
        ResultLabel.Text = message;
        return;
    }

    // 成功したら入力欄を空にする（画面に平文を残さない）
    CurrentPassword.Value = "";
    NewPassword.Value = "";
    ConfirmPassword.Value = "";
    ResultLabel.Color = "#198754";
    ResultLabel.Text = $"{message}（次回のログインから新しいパスワードを使ってください）";
    Toaster.Success("パスワードを変更しました");
}
